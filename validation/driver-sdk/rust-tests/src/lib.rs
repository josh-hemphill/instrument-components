//! Shared executable SDK contracts. Synthetic implementations are test-only dependencies.
#[cfg(test)]
mod tests {
    use instrument_api::*;
    use instrument_runtime::*;
    use instrument_synthetic_driver::{candidate, descriptor, operations, BinaryFactory, Trace};
    use serde_json::Value;
    use std::collections::BTreeSet;
    use std::sync::{atomic::Ordering, Arc};
    use std::time::Duration;

    fn cases() -> Value {
        let data: Value =
            serde_json::from_str(include_str!("../../../../spec/driver-contract-cases.json"))
                .unwrap();
        assert_eq!(data["schemaVersion"], 1);
        data
    }
    fn text<'a>(v: &'a Value, key: &str) -> &'a str {
        v[key].as_str().unwrap()
    }
    fn factory(rule: &Value, id: &str, trace: Arc<Trace>) -> Arc<dyn DriverFactory> {
        let scheme = text(rule, "scheme").into();
        let rule = match text(rule, "kind") {
            "exact" => MatchRule::ExactIdentity {
                scheme,
                manufacturer: text(rule, "manufacturer").into(),
                model: text(rule, "model").into(),
            },
            "family" => MatchRule::Family {
                scheme,
                manufacturer: text(rule, "manufacturer").into(),
                model_pattern: text(rule, "model").into(),
            },
            "product" => MatchRule::ExactProduct {
                scheme,
                vendor_id: rule["vendorId"].as_u64().unwrap() as u32,
                product_id: rule["productId"].as_u64().unwrap() as u32,
            },
            "standard" => MatchRule::StandardScpi { scheme },
            _ => panic!("unknown rule"),
        };
        Arc::new(BinaryFactory {
            trace,
            descriptor: DriverDescriptor {
                id: format!("synthetic.{id}"),
                contract_major: CONTRACT_MAJOR,
                rules: vec![rule],
                operations: operations(),
            },
        })
    }

    #[test]
    fn shared_selection_cases_are_passive_and_order_independent() {
        let data = cases();
        for row in data["selectionCases"].as_array().unwrap() {
            let trace = Arc::new(Trace::default());
            let result = (|| {
                let factories = row["drivers"].as_array().unwrap().iter().map(|id| {
                    factory(
                        &data["rules"][id.as_str().unwrap()],
                        id.as_str().unwrap(),
                        trace.clone(),
                    )
                });
                let registry = DriverRegistry::new(factories)?;
                let mut c = candidate();
                c.endpoint.scheme = text(row, "scheme").into();
                c.identity.manufacturer = Some(text(row, "manufacturer").into());
                c.identity.model = Some(text(row, "model").into());
                c.identity.vendor_id = row["vendorId"].as_u64().map(|v| v as u32);
                c.identity.product_id = row["productId"].as_u64().map(|v| v as u32);
                let selection = Selection {
                    driver_id: row["explicit"].as_str().map(str::to_owned),
                    allow_standard_scpi: row["allowStandardScpi"].as_bool().unwrap_or(false),
                    preferences: row["preferences"]
                        .as_object()
                        .map(|m| {
                            m.iter()
                                .map(|(k, v)| (k.clone(), v.as_i64().unwrap() as i32))
                                .collect()
                        })
                        .unwrap_or_default(),
                };
                registry.select(&c, &selection)
            })();
            if let Some(expected) = row["expectedError"].as_str() {
                assert_eq!(
                    format!("{:?}", result.unwrap_err().kind),
                    expected,
                    "{}",
                    row["id"]
                );
            } else {
                assert_eq!(
                    result.unwrap().driver_id,
                    text(row, "expectedDriver"),
                    "{}",
                    row["id"]
                );
            }
            assert_eq!(
                trace.opens.load(Ordering::SeqCst),
                0,
                "selection must be passive"
            );
        }
    }

    #[test]
    fn shared_operation_value_error_and_lifecycle_cases() {
        for row in cases()["operationCases"].as_array().unwrap() {
            let trace = Arc::new(Trace::default());
            trace.missing_dmm.store(
                row["missingDmm"].as_bool().unwrap_or(false),
                Ordering::SeqCst,
            );
            trace.fail_close.store(
                row["failClose"].as_bool().unwrap_or(false),
                Ordering::SeqCst,
            );
            trace.unknown_inventory.store(
                row["unknownInventory"].as_bool().unwrap_or(false),
                Ordering::SeqCst,
            );
            if let Some(disabled) = row["disabledOperations"].as_array() {
                for op in disabled {
                    trace
                        .disabled_operations
                        .lock()
                        .unwrap()
                        .insert(match op.as_str().unwrap() {
                            "Shutdown" => Operation::Shutdown,
                            "Reset" => Operation::Reset,
                            _ => panic!("unknown disabled operation"),
                        });
                }
            }
            let mut factory = BinaryFactory::new(trace.clone());
            match row["envelope"].as_str() {
                Some("measureOnly") => {
                    factory.descriptor.operations =
                        [Operation::MeasureVoltageDc].into_iter().collect()
                }
                Some("withSense") => {
                    factory.descriptor.operations.insert(Operation::RemoteSense);
                }
                _ => (),
            }
            let registry =
                DriverRegistry::new([Arc::new(factory) as Arc<dyn DriverFactory>]).unwrap();
            let mode = match row["mode"].as_str() {
                Some("NativeAsync") => ExecutionMode::NativeAsync,
                Some("WorkerAsync") => ExecutionMode::WorkerAsync,
                _ => ExecutionMode::Sync,
            };
            let ownership = if row["ownership"] == "Borrowed" {
                ConnectionOwnership::Borrowed
            } else {
                ConnectionOwnership::Owned
            };
            let required: BTreeSet<_> = if row["requiredSense"].as_bool() == Some(true) {
                [Operation::RemoteSense].into_iter().collect()
            } else {
                BTreeSet::new()
            };
            let opened = registry.open(
                &candidate(),
                &Selection::default(),
                &required,
                mode,
                ownership,
            );
            if let Some(expected) = row["openError"].as_str() {
                match opened {
                    Err(e) => assert_eq!(format!("{:?}", e.kind), expected),
                    Ok(_) => panic!("expected open failure"),
                }
                assert_eq!(
                    trace.opens.load(Ordering::SeqCst),
                    row["opens"].as_u64().unwrap_or(0) as usize
                );
            } else {
                let handle = opened.unwrap();
                for step in row["steps"].as_array().unwrap() {
                    let channel = step["channel"].as_u64().unwrap_or(1) as u32;
                    let result: Result<Option<f64>> = match text(step, "action") {
                        "measure" => handle
                            .dmm()
                            .measure_voltage_dc(step["range"].as_f64())
                            .map(|m| Some(m.value)),
                        "set" => handle
                            .psu()
                            .set_voltage(channel, step["value"].as_f64().unwrap())
                            .map(|_| None),
                        "read" => handle.psu().read_voltage(channel).map(|m| Some(m.value)),
                        "output" => handle.psu().output_enable(channel, true).map(|_| None),
                        "reset" => handle.reset().map(|_| None),
                        "sense" => handle
                            .set_sense_mode(channel, SenseMode::External)
                            .map(|_| None),
                        "recover" => handle.recover().map(|_| None),
                        "close" => handle
                            .close(step["shutdown"].as_bool().unwrap_or(false))
                            .map(|_| None),
                        "fail-measure" => {
                            trace.fail_measure.store(true, Ordering::SeqCst);
                            Ok(None)
                        }
                        "fail-shutdown" => {
                            trace.fail_shutdown.store(true, Ordering::SeqCst);
                            Ok(None)
                        }
                        "fail-recover" => {
                            trace.fail_recover.store(true, Ordering::SeqCst);
                            Ok(None)
                        }
                        "fail-close" => {
                            trace.fail_close.store(true, Ordering::SeqCst);
                            Ok(None)
                        }
                        _ => panic!("unknown step"),
                    };
                    if let Some(expected) = step["error"].as_str() {
                        assert_eq!(
                            format!("{:?}", result.unwrap_err().kind),
                            expected,
                            "{} {step}",
                            row["id"]
                        );
                    } else if text(step, "action") == "measure" || text(step, "action") == "read" {
                        assert_eq!(result.unwrap(), step["value"].as_f64(), "{}", row["id"]);
                    } else {
                        result.unwrap();
                    }
                }
            }
            assert_eq!(
                trace.closes.load(Ordering::SeqCst),
                row["closes"].as_u64().unwrap() as usize,
                "{}",
                row["id"]
            );
            assert_eq!(
                trace.shutdowns.load(Ordering::SeqCst),
                row["shutdowns"].as_u64().unwrap() as usize
            );
            assert_eq!(
                trace.disposes.load(Ordering::SeqCst),
                row["disposes"]
                    .as_u64()
                    .unwrap_or(row["closes"].as_u64().unwrap()) as usize
            );
            if let Some(recoveries) = row["recoveries"].as_u64() {
                assert_eq!(
                    trace.recoveries.load(Ordering::SeqCst),
                    recoveries as usize,
                    "{}",
                    row["id"]
                );
            }
            if let Some(attempts) = row["shutdownAttempts"].as_array() {
                assert_eq!(
                    *trace.shutdown_attempts.lock().unwrap(),
                    attempts
                        .iter()
                        .map(|v| v.as_u64().unwrap() as u32)
                        .collect::<Vec<_>>(),
                    "{}",
                    row["id"]
                );
            }
            if let Some(outputs) = row["outputs"].as_object() {
                let actual = trace.outputs.lock().unwrap();
                for (channel, enabled) in outputs {
                    assert_eq!(
                        actual.get(&channel.parse::<u32>().unwrap()).copied(),
                        enabled.as_bool(),
                        "{}",
                        row["id"]
                    );
                }
            }
            if row["noIo"].as_bool() == Some(true) {
                assert!(trace.events.lock().unwrap().is_empty());
            }
        }
    }

    #[test]
    fn invalid_backend_and_contract_cleanup() {
        let trace = Arc::new(Trace::default());
        trace.missing_dmm.store(true, Ordering::SeqCst);
        let registry = DriverRegistry::new([
            Arc::new(BinaryFactory::new(trace.clone())) as Arc<dyn DriverFactory>
        ])
        .unwrap();
        assert!(matches!(
            registry.open(
                &candidate(),
                &Selection::default(),
                &BTreeSet::new(),
                ExecutionMode::Sync,
                ConnectionOwnership::Owned
            ),
            Err(DriverError {
                kind: ErrorKind::DriverContractViolation,
                ..
            })
        ));
        assert_eq!(trace.closes.load(Ordering::SeqCst), 1);
        let mut d = descriptor();
        d.contract_major += 1;
        assert!(matches!(
            DriverRegistry::new([Arc::new(BinaryFactory {
                trace,
                descriptor: d
            }) as Arc<dyn DriverFactory>]),
            Err(DriverError {
                kind: ErrorKind::IncompatibleContract,
                ..
            })
        ));
    }

    #[test]
    fn concurrent_typed_views_serialize_complete_binary_sequences() {
        let trace = Arc::new(Trace::default());
        let registry = DriverRegistry::new([
            Arc::new(BinaryFactory::new(trace.clone())) as Arc<dyn DriverFactory>
        ])
        .unwrap();
        let handle = registry
            .open(
                &candidate(),
                &Selection::default(),
                &BTreeSet::new(),
                ExecutionMode::Sync,
                ConnectionOwnership::Owned,
            )
            .unwrap();
        let workers: Vec<_> = (0..12)
            .map(|i| {
                let h = handle.clone();
                std::thread::spawn(move || {
                    if i % 2 == 0 {
                        assert_eq!(h.dmm().measure_voltage_dc(None).unwrap().value, 3.3);
                    } else {
                        h.psu().set_voltage(1, i as f64).unwrap();
                    }
                })
            })
            .collect();
        for worker in workers {
            worker.join().unwrap();
        }
        let events = trace.events.lock().unwrap();
        for (index, event) in events.iter().enumerate() {
            if event == "[a1]" {
                assert_eq!(events[index + 1], "[b2]");
            }
        }
        drop(events);
        handle.close(true).unwrap();
    }

    #[test]
    fn close_waits_for_active_operation_and_rejects_new_work() {
        let trace = Arc::new(Trace::default());
        trace.block.lock().unwrap().0 = true;
        let registry = DriverRegistry::new([
            Arc::new(BinaryFactory::new(trace.clone())) as Arc<dyn DriverFactory>
        ])
        .unwrap();
        let handle = registry
            .open(
                &candidate(),
                &Selection::default(),
                &BTreeSet::new(),
                ExecutionMode::Sync,
                ConnectionOwnership::Owned,
            )
            .unwrap();
        let h = handle.clone();
        let active = std::thread::spawn(move || h.dmm().measure_voltage_dc(None));
        let block = trace.block.lock().unwrap();
        let (block, timeout) = trace
            .wake
            .wait_timeout_while(block, Duration::from_secs(5), |b| !b.1)
            .unwrap();
        assert!(!timeout.timed_out());
        drop(block);
        let h = handle.clone();
        let queued = std::thread::spawn(move || h.psu().set_voltage(1, 7.0));
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while handle.pending_operations() != 1 {
            assert!(
                std::time::Instant::now() < deadline,
                "operation did not queue"
            );
            std::thread::yield_now();
        }
        let h = handle.clone();
        let closing = std::thread::spawn(move || h.close(true));
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while !handle.closing_requested() {
            assert!(
                std::time::Instant::now() < deadline,
                "close did not request lifecycle transition"
            );
            std::thread::yield_now();
        }
        assert_eq!(
            handle.psu().set_voltage(1, 7.0).unwrap_err().kind,
            ErrorKind::SessionClosing
        );
        let h = handle.clone();
        let close_again = std::thread::spawn(move || h.close(true));
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while handle.pending_operations() != 3 {
            assert!(
                std::time::Instant::now() < deadline,
                "queued operation and both close calls must reach gate"
            );
            std::thread::yield_now();
        }
        assert_eq!(trace.closes.load(Ordering::SeqCst), 0);
        trace.block.lock().unwrap().0 = false;
        trace.wake.notify_all();
        assert_eq!(active.join().unwrap().unwrap().value, 3.3);
        closing.join().unwrap().unwrap();
        close_again.join().unwrap().unwrap();
        assert!(matches!(
            queued.join().unwrap().unwrap_err().kind,
            ErrorKind::SessionClosing | ErrorKind::SessionClosed
        ));
        assert_eq!(trace.shutdowns.load(Ordering::SeqCst), 1);
        assert_eq!(handle.pending_operations(), 0);
        assert!(!trace
            .events
            .lock()
            .unwrap()
            .iter()
            .any(|event| event == "set:1"));
        assert_eq!(trace.closes.load(Ordering::SeqCst), 1);
        assert_eq!(
            handle.dmm().measure_voltage_dc(None).unwrap_err().kind,
            ErrorKind::SessionClosed
        );
    }
    #[test]
    fn backend_reentry_is_rejected_without_deadlock_or_io() {
        let trace = Arc::new(Trace::default());
        let registry = DriverRegistry::new([
            Arc::new(BinaryFactory::new(trace.clone())) as Arc<dyn DriverFactory>
        ])
        .unwrap();
        let handle = registry
            .open(
                &candidate(),
                &Selection::default(),
                &BTreeSet::new(),
                ExecutionMode::Sync,
                ConnectionOwnership::Owned,
            )
            .unwrap();
        let nested = handle.clone();
        *trace.on_measure.lock().unwrap() = Some(Box::new(move || nested.reset()));
        assert_eq!(
            handle.dmm().measure_voltage_dc(None).unwrap_err().kind,
            ErrorKind::DriverContractViolation
        );
        assert!(trace.events.lock().unwrap().is_empty());
        assert_eq!(
            handle.dmm().measure_voltage_dc(None).unwrap_err().kind,
            ErrorKind::SessionNeedsRecovery
        );
        handle.recover().unwrap();
        handle.close(false).unwrap();
    }

    #[test]
    fn concurrent_checked_closers_retain_first_intent_and_outcome() {
        struct ReleaseOnDrop(Arc<Trace>);
        impl Drop for ReleaseOnDrop {
            fn drop(&mut self) {
                self.0.block.lock().unwrap_or_else(|p| p.into_inner()).0 = false;
                self.0.wake.notify_all();
            }
        }
        for (first_shutdown, second_shutdown, fail_shutdown, fail_close) in [
            (false, true, false, false),
            (true, true, true, false),
            (true, false, true, false),
            (false, true, false, true),
            (false, false, false, true),
        ] {
            let trace = Arc::new(Trace::default());
            trace.fail_shutdown.store(fail_shutdown, Ordering::SeqCst);
            trace.fail_close.store(fail_close, Ordering::SeqCst);
            trace.block.lock().unwrap().0 = true;
            let _release = ReleaseOnDrop(trace.clone());
            let handle = DriverRegistry::new([Arc::new(BinaryFactory {
                trace: trace.clone(),
                descriptor: descriptor(),
            }) as Arc<dyn DriverFactory>])
            .unwrap()
            .open(
                &candidate(),
                &Selection::default(),
                &BTreeSet::new(),
                ExecutionMode::Sync,
                ConnectionOwnership::Owned,
            )
            .unwrap();
            let worker = handle.clone();
            let operation = std::thread::spawn(move || worker.dmm().measure_voltage_dc(None));
            let deadline = std::time::Instant::now() + Duration::from_secs(5);
            while !trace.block.lock().unwrap().1 {
                assert!(std::time::Instant::now() < deadline);
                std::thread::yield_now();
            }
            let worker = handle.clone();
            let first = std::thread::spawn(move || worker.close(first_shutdown));
            while !handle.closing_requested() {
                assert!(std::time::Instant::now() < deadline);
                std::thread::yield_now();
            }
            let worker = handle.clone();
            let second = std::thread::spawn(move || worker.close(second_shutdown));
            while handle.pending_operations() != 2 {
                assert!(std::time::Instant::now() < deadline);
                std::thread::yield_now();
            }
            trace.block.lock().unwrap().0 = false;
            trace.wake.notify_all();
            operation.join().unwrap().unwrap();
            let a = first.join().unwrap();
            let b = second.join().unwrap();
            if fail_shutdown || fail_close {
                let first = a.unwrap_err();
                let second = b.unwrap_err();
                assert_eq!(first.kind, ErrorKind::Shutdown);
                assert_eq!(first, second);
            } else {
                a.unwrap();
                assert_eq!(b.unwrap_err().kind, ErrorKind::Shutdown);
            }
            assert_eq!(trace.closes.load(Ordering::SeqCst), 1);
            assert_eq!(
                trace.shutdowns.load(Ordering::SeqCst),
                usize::from(first_shutdown)
            );
            if fail_shutdown || fail_close {
                assert_eq!(handle.close(true).unwrap_err().kind, ErrorKind::Shutdown);
                assert_eq!(handle.close(false).unwrap_err().kind, ErrorKind::Shutdown);
            }
            drop(handle);
            assert_eq!(trace.closes.load(Ordering::SeqCst), 1);
            assert_eq!(trace.disposes.load(Ordering::SeqCst), 1);
        }
    }

    #[test]
    fn backend_drop_reentry_is_rejected_before_completed_close_is_published() {
        use std::sync::{mpsc, Mutex};
        struct DropFactory {
            slot: Arc<Mutex<Option<DeviceHandle>>>,
            observed: Arc<Mutex<Option<ErrorKind>>>,
        }
        struct DropBackend {
            slot: Arc<Mutex<Option<DeviceHandle>>>,
            observed: Arc<Mutex<Option<ErrorKind>>>,
        }
        impl DriverFactory for DropFactory {
            fn descriptor(&self) -> DriverDescriptor {
                DriverDescriptor {
                    id: "synthetic.drop".into(),
                    contract_major: CONTRACT_MAJOR,
                    rules: descriptor().rules,
                    operations: BTreeSet::new(),
                }
            }
            fn open(&self, _: &OpenContext<'_>) -> Result<Box<dyn DeviceBackend>> {
                Ok(Box::new(DropBackend {
                    slot: self.slot.clone(),
                    observed: self.observed.clone(),
                }))
            }
        }
        impl DeviceBackend for DropBackend {
            fn operations(&self) -> BTreeSet<Operation> {
                BTreeSet::new()
            }
            fn close(&mut self) -> Result<()> {
                Ok(())
            }
        }
        impl Drop for DropBackend {
            fn drop(&mut self) {
                if let Some(handle) = self.slot.lock().unwrap().take() {
                    *self.observed.lock().unwrap() = Some(handle.close(false).unwrap_err().kind);
                }
            }
        }
        let slot = Arc::new(Mutex::new(None));
        let observed = Arc::new(Mutex::new(None));
        let registry = DriverRegistry::new([Arc::new(DropFactory {
            slot: slot.clone(),
            observed: observed.clone(),
        }) as Arc<dyn DriverFactory>])
        .unwrap();
        let handle = registry
            .open(
                &candidate(),
                &Selection::default(),
                &BTreeSet::new(),
                ExecutionMode::Sync,
                ConnectionOwnership::Owned,
            )
            .unwrap();
        *slot.lock().unwrap() = Some(handle.clone());
        let (tx, rx) = mpsc::channel();
        let worker = std::thread::spawn(move || {
            tx.send(handle.close(false)).unwrap();
        });
        rx.recv_timeout(Duration::from_secs(5))
            .expect("provider destructor reentry deadlocked")
            .unwrap();
        worker.join().unwrap();
        assert_eq!(
            *observed.lock().unwrap(),
            Some(ErrorKind::DriverContractViolation)
        );
    }
}

#[cfg(all(test, feature = "async"))]
mod asynchronous;

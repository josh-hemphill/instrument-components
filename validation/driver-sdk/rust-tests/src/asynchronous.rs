use instrument_api::*;
use instrument_runtime::*;
use instrument_synthetic_driver::*;
use serde_json::Value;
use std::{
    collections::BTreeSet,
    future::{poll_fn, Future},
    sync::{atomic::Ordering, Arc},
    task::Poll,
    time::Duration,
};

fn data() -> Value {
    let d: Value =
        serde_json::from_str(include_str!("../../../../spec/driver-async-cases.json")).unwrap();
    assert_eq!(d["schemaVersion"], 1);
    d
}
async fn until(mut predicate: impl FnMut() -> bool) {
    tokio::time::timeout(Duration::from_secs(5), async {
        while !predicate() {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
}
fn registry(factory: Arc<dyn DriverFactory>) -> DriverRegistry {
    DriverRegistry::new([factory]).unwrap()
}
enum Handle {
    Native(NativeDeviceHandle),
    Worker(WorkerDeviceHandle),
    WorkerSync(WorkerDeviceHandle),
}
impl Handle {
    async fn step(&self, step: &Value) -> Result<Option<f64>> {
        let action = step["action"].as_str().unwrap();
        let value = step["value"].as_f64().unwrap_or(0.0);
        match self {
            Self::Native(h) => match action {
                "measure" => h.measure_voltage_dc(None).await.map(|m| Some(m.value)),
                "bad-range" => h.measure_voltage_dc(Some(0.0)).await.map(|m| Some(m.value)),
                "set" => h.set_voltage(1, value).await.map(|_| None),
                "read" => h.read_voltage(1).await.map(|m| Some(m.value)),
                "output" => h.output_enable(1, true).await.map(|_| None),
                "reset" => h.reset().await.map(|_| None),
                "sense" => h.set_sense_mode(1, SenseMode::External).await.map(|_| None),
                "recover" => h.recover().await.map(|_| None),
                "close" => h
                    .close(step["shutdown"].as_bool().unwrap_or(false))
                    .await
                    .map(|_| None),
                _ => panic!("unknown action"),
            },
            Self::WorkerSync(h) => match action {
                "measure" => h.measure_voltage_dc(None).map(|m| Some(m.value)),
                "bad-range" => h.measure_voltage_dc(Some(0.0)).map(|m| Some(m.value)),
                "set" => h.set_voltage(1, value).map(|_| None),
                "read" => h.read_voltage(1).map(|m| Some(m.value)),
                "output" => h.output_enable(1, true).map(|_| None),
                "reset" => h.reset().map(|_| None),
                "sense" => h.set_sense_mode(1, SenseMode::External).map(|_| None),
                "recover" => h.recover_sync().map(|_| None),
                "close" => h
                    .close_sync(step["shutdown"].as_bool().unwrap_or(false))
                    .map(|_| None),
                _ => panic!("unknown action"),
            },
            Self::Worker(h) => match action {
                "measure" => h
                    .measure_voltage_dc_async(None)
                    .await
                    .map(|m| Some(m.value)),
                "bad-range" => h
                    .measure_voltage_dc_async(Some(0.0))
                    .await
                    .map(|m| Some(m.value)),
                "set" => h.set_voltage_async(1, value).await.map(|_| None),
                "read" => h.read_voltage_async(1).await.map(|m| Some(m.value)),
                "output" => h.output_enable_async(1, true).await.map(|_| None),
                "reset" => h.reset_async().await.map(|_| None),
                "sense" => h
                    .set_sense_mode_async(1, SenseMode::External)
                    .await
                    .map(|_| None),
                "recover" => h.recover().await.map(|_| None),
                "close" => h
                    .close(step["shutdown"].as_bool().unwrap_or(false))
                    .await
                    .map(|_| None),
                _ => panic!("unknown action"),
            },
        }
    }
}
fn configure(trace: &Trace, row: &Value) {
    *trace.measure_error.lock().unwrap() = match row["measureError"].as_str() {
        Some("SessionClosed") => Some(ErrorKind::SessionClosed),
        Some("SessionClosing") => Some(ErrorKind::SessionClosing),
        None => None,
        Some(other) => panic!("unknown injected provider error: {other}"),
    };
    for (key, flag) in [
        ("failMeasure", &trace.fail_measure),
        ("failShutdown", &trace.fail_shutdown),
        ("failClose", &trace.fail_close),
        ("missingDmm", &trace.missing_dmm),
        ("unknownInventory", &trace.unknown_inventory),
    ] {
        flag.store(row[key].as_bool().unwrap_or(false), Ordering::SeqCst);
    }
    if row["disableShutdown"].as_bool() == Some(true) {
        trace
            .disabled_operations
            .lock()
            .unwrap()
            .insert(Operation::Shutdown);
    }
}
#[tokio::test]
async fn unpolled_futures_neither_open_nor_dispatch_and_modes_are_explicit() {
    let native = Arc::new(AsyncTrace::default());
    let worker = Arc::new(WorkerTrace::default());
    let nr = registry(Arc::new(NativeFactory(native.clone())));
    let wr = registry(Arc::new(WorkerFactory(worker.clone())));
    let nc = native_candidate();
    let wc = worker_candidate();
    let selection = Selection::default();
    let required = BTreeSet::new();
    drop(nr.open_native(&nc, &selection, &required, ConnectionOwnership::Owned));
    drop(wr.open_worker(&wc, &selection, &required, ConnectionOwnership::Owned));
    assert_eq!(native.base.opens.load(Ordering::SeqCst), 0);
    assert_eq!(worker.base.opens.load(Ordering::SeqCst), 0);
    assert!(matches!(
        nr.open_worker(&nc, &selection, &required, ConnectionOwnership::Owned)
            .await,
        Err(DriverError {
            kind: ErrorKind::UnsupportedExecutionMode,
            ..
        })
    ));
    assert!(matches!(
        wr.open_native(&wc, &selection, &required, ConnectionOwnership::Owned)
            .await,
        Err(DriverError {
            kind: ErrorKind::UnsupportedExecutionMode,
            ..
        })
    ));
    let n = nr
        .open_native(&nc, &selection, &required, ConnectionOwnership::Owned)
        .await
        .unwrap();
    let w = wr
        .open_worker(&wc, &selection, &required, ConnectionOwnership::Owned)
        .await
        .unwrap();
    drop(n.measure_voltage_dc(None));
    drop(w.measure_voltage_dc_async(None));
    assert!(native.base.events.lock().unwrap().is_empty());
    assert!(worker.base.events.lock().unwrap().is_empty());
    assert!(!n.recovery_required());
    assert!(!w.recovery_required());
    n.close(false).await.unwrap();
    w.close(false).await.unwrap();
}

#[tokio::test]
async fn shared_async_cases_cover_native_and_worker() {
    for mode in 0..3 {
        let worker = mode != 0;
        for row in data()["cases"].as_array().unwrap() {
            let native = Arc::new(AsyncTrace::default());
            let local = Arc::new(WorkerTrace::default());
            let trace = if worker {
                local.base.clone()
            } else {
                native.base.clone()
            };
            configure(&trace, row);
            native
                .fail_open
                .store(row["failOpen"].as_bool().unwrap_or(false), Ordering::SeqCst);
            local
                .fail_open
                .store(row["failOpen"].as_bool().unwrap_or(false), Ordering::SeqCst);
            let factory: Arc<dyn DriverFactory> = if worker {
                Arc::new(WorkerFactory(local.clone()))
            } else {
                Arc::new(NativeFactory(native.clone()))
            };
            let reg = registry(factory);
            let ownership = if row["borrowed"].as_bool() == Some(true) {
                ConnectionOwnership::Borrowed
            } else {
                ConnectionOwnership::Owned
            };
            let result = if worker {
                reg.open_worker(
                    &worker_candidate(),
                    &Selection::default(),
                    &BTreeSet::new(),
                    ownership,
                )
                .await
                .map(|h| {
                    if mode == 2 {
                        Handle::WorkerSync(h)
                    } else {
                        Handle::Worker(h)
                    }
                })
            } else {
                reg.open_native(
                    &native_candidate(),
                    &Selection::default(),
                    &BTreeSet::new(),
                    ownership,
                )
                .await
                .map(Handle::Native)
            };
            if let Some(expected) = row["openError"].as_str() {
                match result {
                    Err(e) => assert_eq!(format!("{:?}", e.kind), expected, "{}", row["id"]),
                    Ok(_) => panic!("expected open error"),
                }
            } else {
                let h = result.unwrap();
                for step in row["steps"].as_array().unwrap() {
                    let result = h.step(step).await;
                    if let Some(expected) = step["error"].as_str() {
                        assert_eq!(
                            format!("{:?}", result.unwrap_err().kind),
                            expected,
                            "{} {step}",
                            row["id"]
                        );
                    } else {
                        let v = result.unwrap();
                        if step["action"] == "measure" || step["action"] == "read" {
                            assert_eq!(v, step["value"].as_f64());
                        }
                    }
                }
            }
            until(|| {
                trace.closes.load(Ordering::SeqCst) == row["closes"].as_u64().unwrap() as usize
            })
            .await;
            assert_eq!(
                trace.shutdowns.load(Ordering::SeqCst),
                row["shutdowns"].as_u64().unwrap() as usize
            );
            assert_eq!(
                trace.recoveries.load(Ordering::SeqCst),
                row["recoveries"].as_u64().unwrap() as usize
            );
            assert_eq!(
                trace.disposes.load(Ordering::SeqCst),
                row["disposes"].as_u64().unwrap_or(1) as usize
            );
            if row["noIo"].as_bool() == Some(true) {
                assert!(trace.events.lock().unwrap().is_empty());
            }
            if worker {
                assert_affinity(&local);
            }
        }
    }
}
fn assert_affinity(trace: &WorkerTrace) {
    let events = trace.affinity.lock().unwrap();
    let thread = events[0].1;
    assert!(events.iter().all(|(_, id)| *id == thread));
    assert!(events.iter().any(|(name, _)| name == "close"));
    assert!(events.iter().any(|(name, _)| name == "drop"));
}
#[tokio::test]
async fn native_drop_quarantines_and_queued_drop_does_not_emit_io() {
    let trace = Arc::new(AsyncTrace::default());
    trace.block_measure.store(true, Ordering::SeqCst);
    let h = registry(Arc::new(NativeFactory(trace.clone())))
        .open_native(
            &native_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let cloned = h.clone();
    let active = tokio::spawn(async move { cloned.measure_voltage_dc(None).await });
    trace.started.notified().await;
    let cloned = h.clone();
    let queued = tokio::spawn(async move { cloned.set_voltage(1, 9.0).await });
    until(|| h.pending_operations() == 1).await;
    queued.abort();
    let _ = queued.await;
    assert_eq!(h.pending_operations(), 0);
    active.abort();
    let _ = active.await;
    assert!(h.recovery_required());
    assert_eq!(trace.base.events.lock().unwrap().as_slice(), ["[a1]"]);
    h.recover().await.unwrap();
    trace.block_measure.store(false, Ordering::SeqCst);
    assert_eq!(h.measure_voltage_dc(None).await.unwrap().value, 3.3);
    h.close(true).await.unwrap();
}
#[tokio::test]
async fn native_close_waiter_drop_keeps_shutdown_owned_and_reentry_is_rejected() {
    let trace = Arc::new(AsyncTrace::default());
    let h = registry(Arc::new(NativeFactory(trace.clone())))
        .open_native(
            &native_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let nested = h.clone();
    *trace.hook.lock().unwrap() = Some(Box::new(move || {
        Box::pin(async move { nested.reset().await })
    }));
    assert_eq!(
        h.measure_voltage_dc(None).await.unwrap_err().kind,
        ErrorKind::DriverContractViolation
    );
    h.recover().await.unwrap();
    trace.block_shutdown.store(true, Ordering::SeqCst);
    let cloned = h.clone();
    let waiter = tokio::spawn(async move { cloned.close(true).await });
    trace.shutdown_started.notified().await;
    waiter.abort();
    let _ = waiter.await;
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 0);
    trace.shutdown_release.notify_one();
    h.close(true).await.unwrap();
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 1);
}
#[tokio::test]
async fn native_open_abandonment_and_completed_undelivered_handle_release() {
    let trace = Arc::new(AsyncTrace::default());
    trace.block_open.store(true, Ordering::SeqCst);
    let reg = Arc::new(registry(Arc::new(NativeFactory(trace.clone()))));
    let cloned = reg.clone();
    let open = tokio::spawn(async move {
        cloned
            .open_native(
                &native_candidate(),
                &Selection::default(),
                &BTreeSet::new(),
                ConnectionOwnership::Owned,
            )
            .await
    });
    trace.open_started.notified().await;
    open.abort();
    let _ = open.await;
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 1);
    let trace = Arc::new(AsyncTrace::default());
    let reg = registry(Arc::new(NativeFactory(trace.clone())));
    let open = tokio::spawn(async move {
        reg.open_native(
            &native_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
    });
    until(|| open.is_finished()).await;
    drop(open);
    until(|| trace.base.closes.load(Ordering::SeqCst) == 1).await;
}
#[tokio::test]
async fn worker_running_abandonment_retains_affine_owner_through_close() {
    let trace = Arc::new(WorkerTrace::default());
    trace.base.block.lock().unwrap().0 = true;
    let h = registry(Arc::new(WorkerFactory(trace.clone())))
        .open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let cloned = h.clone();
    let active = tokio::spawn(async move { cloned.measure_voltage_dc_async(None).await });
    until(|| trace.base.block.lock().unwrap().1).await;
    active.abort();
    let _ = active.await;
    assert!(h.draining());
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 0);
    let cloned = h.clone();
    let recovery = tokio::spawn(async move { cloned.recover().await });
    until(|| h.pending_operations() == 1).await;
    let cloned = h.clone();
    let close = tokio::spawn(async move { cloned.close(true).await });
    until(|| h.closing_requested()).await;
    assert!(!close.is_finished());
    assert_eq!(trace.base.recoveries.load(Ordering::SeqCst), 0);
    trace.base.block.lock().unwrap().0 = false;
    trace.base.wake.notify_all();
    close.await.unwrap().unwrap();
    assert!(matches!(
        recovery.await.unwrap().unwrap_err().kind,
        ErrorKind::SessionClosing | ErrorKind::SessionClosed
    ));
    assert_eq!(trace.base.recoveries.load(Ordering::SeqCst), 1);
    assert_affinity(&trace);
}
#[tokio::test]
async fn worker_queued_cancel_sync_frontend_and_reentry() {
    let trace = Arc::new(WorkerTrace::default());
    trace.base.block.lock().unwrap().0 = true;
    let h = registry(Arc::new(WorkerFactory(trace.clone())))
        .open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let cloned = h.clone();
    let active = tokio::spawn(async move { cloned.measure_voltage_dc_async(None).await });
    until(|| trace.base.block.lock().unwrap().1).await;
    let cloned = h.clone();
    let queued = tokio::spawn(async move { cloned.set_voltage_async(1, 8.0).await });
    until(|| h.pending_operations() == 1).await;
    queued.abort();
    let _ = queued.await;
    trace.base.block.lock().unwrap().0 = false;
    trace.base.wake.notify_all();
    active.await.unwrap().unwrap();
    until(|| h.pending_operations() == 0).await;
    assert!(!h.recovery_required());
    assert!(!trace
        .base
        .events
        .lock()
        .unwrap()
        .iter()
        .any(|e| e == "set:1"));
    h.set_voltage(1, 7.0).unwrap();
    assert_eq!(h.read_voltage_async(1).await.unwrap().value, 7.0);
    let nested = h.clone();
    *trace.base.on_measure.lock().unwrap() = Some(Box::new(move || nested.reset()));
    assert_eq!(
        h.measure_voltage_dc_async(None).await.unwrap_err().kind,
        ErrorKind::DriverContractViolation
    );
    h.recover().await.unwrap();
    h.close(true).await.unwrap();
    assert_affinity(&trace);
}
#[tokio::test]
async fn worker_abandoned_open_and_completed_undelivered_open_are_released_on_worker() {
    let trace = Arc::new(WorkerTrace::default());
    trace.block_open.lock().unwrap().0 = true;
    let reg = registry(Arc::new(WorkerFactory(trace.clone())));
    let open = tokio::spawn(async move {
        reg.open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
    });
    until(|| trace.block_open.lock().unwrap().1).await;
    open.abort();
    let _ = open.await;
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 0);
    trace.block_open.lock().unwrap().0 = false;
    trace.open_wake.notify_all();
    until(|| trace.base.closes.load(Ordering::SeqCst) == 1).await;
    until(|| {
        trace
            .affinity
            .lock()
            .unwrap()
            .iter()
            .any(|(n, _)| n == "drop")
    })
    .await;
    assert_affinity(&trace);
    let trace = Arc::new(WorkerTrace::default());
    let reg = registry(Arc::new(WorkerFactory(trace.clone())));
    let open = tokio::spawn(async move {
        reg.open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
    });
    until(|| open.is_finished()).await;
    drop(open);
    until(|| trace.base.closes.load(Ordering::SeqCst) == 1).await;
    until(|| {
        trace
            .affinity
            .lock()
            .unwrap()
            .iter()
            .any(|(n, _)| n == "drop")
    })
    .await;
    assert_affinity(&trace);
}
fn runtime() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
}

#[tokio::test]
async fn native_complete_operations_serialize_across_awaits() {
    let trace = Arc::new(AsyncTrace::default());
    let h = registry(Arc::new(NativeFactory(trace.clone())))
        .open_native(
            &native_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let tasks: Vec<_> = (0..12)
        .map(|i| {
            let h = h.clone();
            tokio::spawn(async move {
                if i % 2 == 0 {
                    assert_eq!(h.measure_voltage_dc(None).await.unwrap().value, 3.3);
                } else {
                    h.set_voltage(1, i as f64).await.unwrap();
                }
            })
        })
        .collect();
    for task in tasks {
        task.await.unwrap();
    }
    {
        let events = trace.base.events.lock().unwrap();
        assert_eq!(events.iter().filter(|e| e.as_str() == "[a1]").count(), 6);
        for (i, event) in events.iter().enumerate() {
            if event == "[a1]" {
                assert_eq!(events[i + 1], "[b2]");
            }
        }
    }
    h.close(true).await.unwrap();
}

#[tokio::test]
async fn worker_sync_and_async_share_owner_and_first_close_policy_is_not_escalated() {
    let trace = Arc::new(WorkerTrace::default());
    trace.base.block.lock().unwrap().0 = true;
    let h = registry(Arc::new(WorkerFactory(trace.clone())))
        .open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let cloned = h.clone();
    let active = tokio::spawn(async move { cloned.measure_voltage_dc_async(None).await });
    until(|| trace.base.block.lock().unwrap().1).await;
    let cloned = h.clone();
    let sync = std::thread::spawn(move || cloned.set_voltage(1, 6.0));
    until(|| h.pending_operations() == 1).await;
    trace.base.block.lock().unwrap().0 = false;
    trace.base.wake.notify_all();
    active.await.unwrap().unwrap();
    sync.join().unwrap().unwrap();
    assert_eq!(h.read_voltage_async(1).await.unwrap().value, 6.0);
    trace.base.fail_measure.store(true, Ordering::SeqCst);
    assert_eq!(
        h.measure_voltage_dc(None).unwrap_err().kind,
        ErrorKind::Timeout
    );
    h.recover_sync().unwrap();
    h.output_enable(1, true).unwrap();
    h.close_sync(true).unwrap();
    assert_affinity(&trace);
    let trace = Arc::new(WorkerTrace::default());
    trace.base.block.lock().unwrap().0 = true;
    let h = registry(Arc::new(WorkerFactory(trace.clone())))
        .open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let cloned = h.clone();
    let active = tokio::spawn(async move { cloned.measure_voltage_dc_async(None).await });
    until(|| trace.base.block.lock().unwrap().1).await;
    let cloned = h.clone();
    let weak = tokio::spawn(async move { cloned.close(false).await });
    until(|| h.closing_requested()).await;
    let cloned = h.clone();
    let strong = tokio::spawn(async move { cloned.close(true).await });
    trace.base.block.lock().unwrap().0 = false;
    trace.base.wake.notify_all();
    active.await.unwrap().unwrap();
    weak.await.unwrap().unwrap();
    assert_eq!(strong.await.unwrap().unwrap_err().kind, ErrorKind::Shutdown);
    assert_eq!(trace.base.shutdowns.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn last_worker_frontend_drop_and_worker_panic_do_not_orphan_resources_or_waiters() {
    let trace = Arc::new(WorkerTrace::default());
    trace.base.block.lock().unwrap().0 = true;
    let h = registry(Arc::new(WorkerFactory(trace.clone())))
        .open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let cloned = h.clone();
    let active = tokio::spawn(async move { cloned.measure_voltage_dc_async(None).await });
    until(|| trace.base.block.lock().unwrap().1).await;
    active.abort();
    let _ = active.await;
    drop(h);
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 0);
    trace.base.block.lock().unwrap().0 = false;
    trace.base.wake.notify_all();
    until(|| {
        trace
            .affinity
            .lock()
            .unwrap()
            .iter()
            .any(|(n, _)| n == "drop")
    })
    .await;
    assert_affinity(&trace);
    let trace = Arc::new(WorkerTrace::default());
    let h = registry(Arc::new(WorkerFactory(trace.clone())))
        .open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    *trace.base.on_measure.lock().unwrap() = Some(Box::new(|| panic!("injected worker panic")));
    assert_eq!(
        h.measure_voltage_dc_async(None).await.unwrap_err().kind,
        ErrorKind::DriverContractViolation
    );
    assert!(h.close(false).await.is_err());
    until(|| {
        trace
            .affinity
            .lock()
            .unwrap()
            .iter()
            .any(|(n, _)| n == "drop")
    })
    .await;
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 1);
    assert_affinity(&trace);
}
#[test]
fn executor_shutdown_releases_native_open_and_live_operation_resources_without_executor() {
    let trace = Arc::new(AsyncTrace::default());
    trace.block_open.store(true, Ordering::SeqCst);
    let rt = runtime();
    let reg = registry(Arc::new(NativeFactory(trace.clone())));
    rt.spawn(async move {
        reg.open_native(
            &native_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
    });
    rt.block_on(trace.open_started.notified());
    drop(rt);
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 1);
    for recovering in [false, true] {
        let trace = Arc::new(AsyncTrace::default());
        let rt = runtime();
        let h = rt
            .block_on(
                registry(Arc::new(NativeFactory(trace.clone()))).open_native(
                    &native_candidate(),
                    &Selection::default(),
                    &BTreeSet::new(),
                    ConnectionOwnership::Owned,
                ),
            )
            .unwrap();
        let cloned = h.clone();
        if recovering {
            trace.base.fail_measure.store(true, Ordering::SeqCst);
            rt.block_on(h.measure_voltage_dc(None)).unwrap_err();
            trace.block_recover.store(true, Ordering::SeqCst);
            rt.spawn(async move { cloned.recover().await });
            rt.block_on(trace.recover_started.notified());
        } else {
            trace.block_measure.store(true, Ordering::SeqCst);
            rt.spawn(async move { cloned.measure_voltage_dc(None).await });
            rt.block_on(trace.started.notified());
        }
        drop(rt);
        assert!(h.recovery_required());
        drop(h);
        assert_eq!(trace.base.closes.load(Ordering::SeqCst), 1);
    }
}
#[test]
fn executor_shutdown_before_close_poll_and_during_recovery_shutdown_resolves_waiters() {
    for phase in [0, 1, 2] {
        let trace = Arc::new(AsyncTrace::default());
        let rt = runtime();
        let h = rt
            .block_on(
                registry(Arc::new(NativeFactory(trace.clone()))).open_native(
                    &native_candidate(),
                    &Selection::default(),
                    &BTreeSet::new(),
                    ConnectionOwnership::Owned,
                ),
            )
            .unwrap();
        if phase == 1 {
            trace.base.fail_measure.store(true, Ordering::SeqCst);
            rt.block_on(h.measure_voltage_dc(None)).unwrap_err();
            trace.block_recover.store(true, Ordering::SeqCst);
        }
        if phase == 2 {
            trace.block_shutdown.store(true, Ordering::SeqCst);
        }
        let mut close = Box::pin(h.close(true));
        rt.block_on(poll_fn(|cx| match close.as_mut().poll(cx) {
            Poll::Pending => Poll::Ready(()),
            Poll::Ready(_) => panic!("close completed before owned task was polled"),
        }));
        if phase == 1 {
            rt.block_on(trace.recover_started.notified());
        }
        if phase == 2 {
            rt.block_on(trace.shutdown_started.notified());
        }
        drop(rt);
        assert_eq!(trace.base.closes.load(Ordering::SeqCst), 1);
        assert_eq!(
            runtime().block_on(close).unwrap_err().kind,
            ErrorKind::SessionClosed
        );
    }
}

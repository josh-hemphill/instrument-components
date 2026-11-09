#![cfg(test)]
use instrument_api::{catalog::*, *};
use instrument_runtime::*;
use instrument_scpi::*;
use instrument_testkit::*;
use std::{collections::BTreeSet, sync::Arc};
include!("../../generated/profiles.rs");
include!("../../generated/cases.rs");
fn candidate() -> Candidate {
    Candidate {
        endpoint: Endpoint {
            scheme: "scpi".into(),
            address: "fixture".into(),
        },
        identity: Identity::default(),
    }
}
fn selection() -> Selection {
    Selection {
        allow_standard_scpi: true,
        ..Selection::default()
    }
}
#[test]
fn shared_sync_scpi_catalogue_values_traffic_errors_and_cleanup() {
    let profile = generated_profiles::profiles().unwrap().remove(0);
    let cases = generated_cases::cases();
    assert_eq!(cases.len(), 139);
    for case in cases {
        let script = Script::new(case.script.clone());
        let registry = DriverRegistry::new([Arc::new(ScpiFactory::new(
            profile.clone(),
            "scpi",
            Arc::new(script.clone()),
        )) as Arc<dyn DriverFactory>])
        .unwrap();
        let handle = registry
            .open(
                &candidate(),
                &selection(),
                &BTreeSet::new(),
                ExecutionMode::Sync,
                ConnectionOwnership::Owned,
            )
            .unwrap();
        for (index, request) in case.calls.iter().enumerate() {
            let actual = handle.invoke(request.clone());
            if index + 1 == case.calls.len() {
                if let Some(expected) = case.expected_error {
                    assert_eq!(actual.unwrap_err().kind, expected, "{}", case.id);
                } else {
                    assert_eq!(
                        actual.unwrap(),
                        case.expected.clone().unwrap(),
                        "{}",
                        case.id
                    );
                }
            } else {
                actual.unwrap();
            }
        }
        handle.close(false).unwrap();
        script.assert_complete().unwrap();
        assert_eq!(script.closes(), 1);
        script.assert_thread_affinity().unwrap();
    }
}
#[cfg(feature = "async")]
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn shared_native_and_both_worker_frontends_match_sync() {
    let profile = generated_profiles::profiles().unwrap().remove(0);
    for mode in 0..3 {
        for case in generated_cases::cases() {
            let script = Script::new(case.script.clone());
            let factory: Arc<dyn DriverFactory> = if mode == 0 {
                Arc::new(AsyncScpiFactory::new(
                    profile.clone(),
                    "scpi",
                    Arc::new(script.clone()),
                ))
            } else {
                Arc::new(ScpiFactory::new(
                    profile.clone(),
                    "scpi",
                    Arc::new(script.clone()),
                ))
            };
            let registry = DriverRegistry::new([factory]).unwrap();
            let native = if mode == 0 {
                Some(
                    registry
                        .open_native(
                            &candidate(),
                            &selection(),
                            &BTreeSet::new(),
                            ConnectionOwnership::Owned,
                        )
                        .await
                        .unwrap(),
                )
            } else {
                None
            };
            let worker = if mode != 0 {
                Some(
                    registry
                        .open_worker(
                            &candidate(),
                            &selection(),
                            &BTreeSet::new(),
                            ConnectionOwnership::Owned,
                        )
                        .await
                        .unwrap(),
                )
            } else {
                None
            };
            for (index, request) in case.calls.iter().enumerate() {
                let actual = if let Some(h) = &native {
                    h.invoke_async(request.clone()).await
                } else if mode == 1 {
                    worker.as_ref().unwrap().invoke_async(request.clone()).await
                } else {
                    worker.as_ref().unwrap().invoke(request.clone())
                };
                if index + 1 == case.calls.len() {
                    if let Some(expected) = case.expected_error {
                        assert_eq!(
                            actual.unwrap_err().kind,
                            expected,
                            "{} mode{}",
                            case.id,
                            mode
                        );
                    } else {
                        assert_eq!(
                            actual.unwrap(),
                            case.expected.clone().unwrap(),
                            "{} mode{}",
                            case.id,
                            mode
                        );
                    }
                } else {
                    actual.unwrap();
                }
            }
            if let Some(h) = native {
                h.close(false).await.unwrap();
            } else {
                worker.unwrap().close(false).await.unwrap();
                script.assert_thread_affinity().unwrap();
            }
            script.assert_complete().unwrap();
            assert_eq!(script.closes(), 1);
        }
    }
}

#[test]
fn direct_profiles_reject_invalid_grammar_inventory_roles_and_ignored_arguments() {
    assert!(Profile::new(
        "test.invalid",
        Some(vec![1]),
        Some(vec![]),
        vec![Plan::new(
            CatalogOperation::OscilloscopeCaptureVoltageTrace,
            vec![
                Step::query(":DATA? {channel}"),
                Step {
                    command: ":PRE?".into(),
                    response: ResponseRole::Preamble,
                    when: None
                }
            ]
        )]
    )
    .is_err());
    for command in [
        ":MEAS? {unknown}",
        ":MEAS? {range:bad}",
        ":MEAS? {range",
        ":MEAS? }",
        ":MEAS?\n*RST",
    ] {
        assert!(Profile::new(
            "test.invalid",
            Some(vec![1]),
            Some(vec![]),
            vec![Plan::new(
                CatalogOperation::DmmMeasureVoltageDc,
                vec![Step::query(command)]
            )]
        )
        .is_err());
    }
    assert!(Profile::new("test.invalid", Some(vec![1, 1]), Some(vec![]), vec![]).is_err());
    assert!(Profile::new(
        "test.invalid",
        Some(vec![1]),
        Some(vec![Output::DcPowerSupply(2)]),
        vec![]
    )
    .is_err());
    assert!(Profile::new(
        "test.invalid",
        Some(vec![1]),
        None,
        vec![Plan::new(CatalogOperation::Shutdown, vec![])]
    )
    .is_err());
    assert!(Profile::new(
        "test.invalid",
        Some(vec![1]),
        Some(vec![Output::DcPowerSupply(1)]),
        vec![Plan::new(CatalogOperation::Shutdown, vec![])]
    )
    .is_err());
    assert!(Profile::new(
        "test.invalid",
        Some(vec![1]),
        Some(vec![]),
        vec![Plan::new(
            CatalogOperation::DcPowerSupplySetVoltage,
            vec![Step::write(":VOLT {volts}")]
        )]
    )
    .is_err());
    assert!(Profile::new(
        "test.invalid",
        Some(vec![1]),
        Some(vec![]),
        vec![Plan::new(
            CatalogOperation::Reset,
            vec![Step::write("*OPC?")]
        )]
    )
    .is_err());
}
fn open_profile(profile: Profile, script: &Script, ownership: ConnectionOwnership) -> DeviceHandle {
    let registry =
        DriverRegistry::new([
            Arc::new(ScpiFactory::new(profile, "scpi", Arc::new(script.clone())))
                as Arc<dyn DriverFactory>,
        ])
        .unwrap();
    registry
        .open(
            &candidate(),
            &selection(),
            &BTreeSet::new(),
            ExecutionMode::Sync,
            ownership,
        )
        .unwrap()
}
#[test]
fn burst_counts_are_positive_quantities_without_physical_channel_inventory() {
    let profile = Profile::new(
        "synthetic.counts",
        None,
        Some(vec![]),
        vec![Plan::new(
            CatalogOperation::FunctionGeneratorSetBurstCount,
            vec![Step::write(":BURS {count}")],
        )],
    )
    .unwrap();
    let script = Script::new(vec![
        ScriptStep::Write(":BURS 100".into()),
        ScriptStep::Close(Ok(())),
    ]);
    let handle = open_profile(profile, &script, ConnectionOwnership::Owned);
    instrument_api::catalog::FunctionGenerator(&handle)
        .set_burst_count(100)
        .unwrap();
    assert_eq!(
        instrument_api::catalog::FunctionGenerator(&handle)
            .set_burst_count(0)
            .unwrap_err()
            .kind,
        ErrorKind::InvalidArgument
    );
    handle.close(false).unwrap();
    script.assert_complete().unwrap();
}
#[test]
fn explicit_recovery_reset_units_borrowed_io_and_missing_range_have_no_fallback() {
    let profile = generated_profiles::profiles().unwrap().remove(0);
    let script = Script::new([
        ScriptStep::Query(":TEST:DmmMeasureVoltageDc? 2.5".into(), Ok("bad".into())),
        ScriptStep::Recover(Ok(())),
        ScriptStep::Query(":TEST:DmmMeasureVoltageDc? 2.5".into(), Ok("3.3".into())),
        ScriptStep::Query(":TEST:DmmFetch?".into(), Ok("4.2".into())),
        ScriptStep::Write(":TEST:Reset".into()),
        ScriptStep::Close(Ok(())),
    ]);
    let h = open_profile(profile, &script, ConnectionOwnership::Owned);
    let dmm = instrument_api::catalog::Dmm(&h);
    assert_eq!(
        dmm.measure_voltage_dc(Some(2.5)).unwrap_err().kind,
        ErrorKind::Parse
    );
    assert_eq!(
        dmm.measure_voltage_dc(Some(2.5)).unwrap_err().kind,
        ErrorKind::SessionNeedsRecovery
    );
    h.recover().unwrap();
    assert_eq!(dmm.measure_voltage_dc(Some(2.5)).unwrap().value, 3.3);
    assert_eq!(dmm.fetch().unwrap().unit, Unit::Volt);
    dmm.reset().unwrap();
    assert_eq!(dmm.read().unwrap_err().kind, ErrorKind::InvalidArgument);
    h.close(false).unwrap();
    script.assert_complete().unwrap();
    let profile = Profile::new(
        "synthetic.constant",
        Some(vec![1]),
        Some(vec![]),
        vec![Plan::new(
            CatalogOperation::DmmMeasureVoltageDc,
            vec![Step::query(":READ?")],
        )],
    )
    .unwrap();
    let script = Script::new([ScriptStep::Query(":READ?".into(), Ok("3.3".into()))]);
    let h = open_profile(profile, &script, ConnectionOwnership::Borrowed);
    assert_eq!(
        h.invoke(CatalogRequest::DmmMeasureVoltageDc { range: Some(2.5) })
            .unwrap_err()
            .kind,
        ErrorKind::UnsupportedOperation
    );
    assert_eq!(
        h.invoke(CatalogRequest::DmmMeasureVoltageDc { range: None })
            .unwrap()
            .into_quantity()
            .unwrap()
            .value,
        3.3
    );
    h.close(false).unwrap();
    assert_eq!(script.closes(), 0);
    script.assert_complete().unwrap();
}
#[test]
fn checked_shutdown_attempts_every_output_after_one_fails() {
    let profile = generated_profiles::profiles().unwrap().remove(0);
    let script = Script::new([
        ScriptStep::WriteFailure(
            ":TEST:DcPowerSupplyOutputEnable 1,OFF".into(),
            DriverError::new(ErrorKind::Transport, "first output failed"),
        ),
        ScriptStep::Write(":TEST:DcPowerSupplyOutputEnable 2,OFF".into()),
        ScriptStep::Write(":TEST:FunctionGeneratorOutputEnable OFF".into()),
        ScriptStep::Close(Ok(())),
    ]);
    let h = open_profile(profile, &script, ConnectionOwnership::Owned);
    assert_eq!(h.close(true).unwrap_err().kind, ErrorKind::Shutdown);
    script.assert_complete().unwrap();
    assert_eq!(script.closes(), 1);
}
#[cfg(feature = "async")]
#[tokio::test]
async fn dropped_native_catalog_query_quarantines_without_losing_cleanup_or_reply_buffers() {
    use std::{future::poll_fn, task::Poll};
    let profile = generated_profiles::profiles().unwrap().remove(0);
    let script = Script::new([
        ScriptStep::Query(":TEST:DmmMeasureVoltageDc? 2.5".into(), Ok("3.3".into())),
        ScriptStep::Recover(Ok(())),
        ScriptStep::Query(":TEST:DmmMeasureVoltageDc? 2.5".into(), Ok("4.2".into())),
        ScriptStep::Close(Ok(())),
    ]);
    let registry = DriverRegistry::new([Arc::new(AsyncScpiFactory::new(
        profile,
        "scpi",
        Arc::new(script.clone()),
    )) as Arc<dyn DriverFactory>])
    .unwrap();
    let h = registry
        .open_native(
            &candidate(),
            &selection(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let mut future = h.invoke_async(CatalogRequest::DmmMeasureVoltageDc { range: Some(2.5) });
    poll_fn(|cx| {
        assert!(matches!(future.as_mut().poll(cx), Poll::Pending));
        Poll::Ready(())
    })
    .await;
    drop(future);
    assert!(h.recovery_required());
    assert_eq!(
        h.invoke_async(CatalogRequest::DmmMeasureVoltageDc { range: Some(2.5) })
            .await
            .unwrap_err()
            .kind,
        ErrorKind::SessionNeedsRecovery
    );
    h.recover().await.unwrap();
    assert_eq!(
        h.invoke_async(CatalogRequest::DmmMeasureVoltageDc { range: Some(2.5) })
            .await
            .unwrap()
            .into_quantity()
            .unwrap()
            .value,
        4.2
    );
    h.close(false).await.unwrap();
    script.assert_complete().unwrap();
}
#[cfg(feature = "async")]
#[tokio::test]
async fn multistep_configuration_and_waveform_transactions_do_not_interleave() {
    let profile = generated_profiles::profiles().unwrap().remove(0);
    let examples = generated_cases::cases();
    let config = examples
        .iter()
        .find(|c| c.id == "SCPI-PowerMeterConfigureMeasurement")
        .unwrap();
    let waveform = examples
        .iter()
        .find(|c| c.id == "SCPI-OscilloscopeCaptureVoltageTrace")
        .unwrap();
    let steps = config
        .script
        .iter()
        .chain(waveform.script.iter())
        .filter(|s| !matches!(s, ScriptStep::Close(_)))
        .cloned()
        .chain([ScriptStep::Close(Ok(()))]);
    let script = Script::new(steps);
    let registry = DriverRegistry::new([Arc::new(AsyncScpiFactory::new(
        profile,
        "scpi",
        Arc::new(script.clone()),
    )) as Arc<dyn DriverFactory>])
    .unwrap();
    let h = registry
        .open_native(
            &candidate(),
            &selection(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    let (a, b) = tokio::join!(
        h.invoke_async(config.calls[0].clone()),
        h.invoke_async(waveform.calls[0].clone())
    );
    a.unwrap();
    b.unwrap();
    h.close(false).await.unwrap();
    script.assert_complete().unwrap();
}

#[derive(Default)]
struct OpaqueCounters {
    closes: std::sync::atomic::AtomicUsize,
    shutdowns: std::sync::atomic::AtomicUsize,
}
struct OpaqueFactory(Arc<OpaqueCounters>);
struct OpaqueBackend(Arc<OpaqueCounters>);
fn opaque_operations() -> BTreeSet<Operation> {
    [
        Operation::Catalog(CatalogOperation::DmmMeasureVoltageDc),
        Operation::Catalog(CatalogOperation::Shutdown),
    ]
    .into_iter()
    .collect()
}
impl DriverFactory for OpaqueFactory {
    fn descriptor(&self) -> DriverDescriptor {
        DriverDescriptor {
            id: "opaque.catalog".into(),
            contract_major: CONTRACT_MAJOR,
            rules: vec![MatchRule::ExactIdentity {
                scheme: "sdk".into(),
                manufacturer: "Opaque".into(),
                model: "SDK".into(),
            }],
            operations: opaque_operations(),
        }
    }
    fn execution_modes(&self) -> BTreeSet<ExecutionMode> {
        [
            ExecutionMode::Sync,
            ExecutionMode::NativeAsync,
            ExecutionMode::WorkerAsync,
        ]
        .into_iter()
        .collect()
    }
    fn open(&self, _: &OpenContext<'_>) -> Result<Box<dyn DeviceBackend>> {
        Ok(Box::new(OpaqueBackend(self.0.clone())))
    }
    #[cfg(feature = "async")]
    fn open_native<'a>(
        &'a self,
        _: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncDeviceBackend>> {
        Box::pin(async move {
            Ok(Box::new(OpaqueBackend(self.0.clone())) as Box<dyn AsyncDeviceBackend>)
        })
    }
}
impl CatalogBackend for OpaqueBackend {
    fn invoke(&mut self, request: CatalogRequest) -> Result<CatalogReply> {
        if request.operation() == CatalogOperation::Shutdown {
            self.0
                .shutdowns
                .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            Ok(CatalogReply::Unit)
        } else {
            Ok(CatalogReply::Quantity(Quantity {
                value: 3.3,
                unit: Unit::Volt,
                status: MeasurementStatus::Valid,
            }))
        }
    }
}
impl DeviceBackend for OpaqueBackend {
    fn operations(&self) -> BTreeSet<Operation> {
        opaque_operations()
    }
    fn catalog(&mut self) -> Option<&mut dyn CatalogBackend> {
        Some(self)
    }
    fn close(&mut self) -> Result<()> {
        self.0
            .closes
            .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        Ok(())
    }
}
#[cfg(feature = "async")]
impl AsyncCatalogBackend for OpaqueBackend {
    fn invoke_async(&mut self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply> {
        Box::pin(async move { CatalogBackend::invoke(self, request) })
    }
}
#[cfg(feature = "async")]
impl AsyncDeviceBackend for OpaqueBackend {
    fn operations(&self) -> BTreeSet<Operation> {
        opaque_operations()
    }
    fn catalog(&mut self) -> Option<&mut dyn AsyncCatalogBackend> {
        Some(self)
    }
    fn close(&mut self) -> Result<()> {
        DeviceBackend::close(self)
    }
}
fn opaque_candidate() -> Candidate {
    Candidate {
        endpoint: Endpoint {
            scheme: "sdk".into(),
            address: "no-transport".into(),
        },
        identity: Identity {
            manufacturer: Some("Opaque".into()),
            model: Some("SDK".into()),
            ..Identity::default()
        },
    }
}
#[test]
fn protocol_neutral_catalog_only_sync_provider_supports_checked_close() {
    let counts = Arc::new(OpaqueCounters::default());
    let registry =
        DriverRegistry::new([Arc::new(OpaqueFactory(counts.clone())) as Arc<dyn DriverFactory>])
            .unwrap();
    let h = registry
        .open(
            &opaque_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ExecutionMode::Sync,
            ConnectionOwnership::Owned,
        )
        .unwrap();
    assert_eq!(
        h.invoke(CatalogRequest::DmmMeasureVoltageDc { range: None })
            .unwrap()
            .into_quantity()
            .unwrap()
            .value,
        3.3
    );
    h.close(true).unwrap();
    assert_eq!(
        counts.shutdowns.load(std::sync::atomic::Ordering::SeqCst),
        1
    );
    assert_eq!(counts.closes.load(std::sync::atomic::Ordering::SeqCst), 1);
}
#[cfg(feature = "async")]
#[tokio::test]
async fn protocol_neutral_catalog_only_native_and_worker_providers_support_checked_close() {
    for mode in 0..3 {
        let counts = Arc::new(OpaqueCounters::default());
        let registry =
            DriverRegistry::new(
                [Arc::new(OpaqueFactory(counts.clone())) as Arc<dyn DriverFactory>],
            )
            .unwrap();
        if mode == 0 {
            let h = registry
                .open_native(
                    &opaque_candidate(),
                    &Selection::default(),
                    &BTreeSet::new(),
                    ConnectionOwnership::Owned,
                )
                .await
                .unwrap();
            assert_eq!(
                h.invoke_async(CatalogRequest::DmmMeasureVoltageDc { range: None })
                    .await
                    .unwrap()
                    .into_quantity()
                    .unwrap()
                    .value,
                3.3
            );
            h.close(true).await.unwrap();
        } else {
            let h = registry
                .open_worker(
                    &opaque_candidate(),
                    &Selection::default(),
                    &BTreeSet::new(),
                    ConnectionOwnership::Owned,
                )
                .await
                .unwrap();
            if mode == 1 {
                h.invoke_async(CatalogRequest::DmmMeasureVoltageDc { range: None })
                    .await
                    .unwrap();
            } else {
                h.invoke(CatalogRequest::DmmMeasureVoltageDc { range: None })
                    .unwrap();
            }
            h.close(true).await.unwrap();
        }
        assert_eq!(
            counts.shutdowns.load(std::sync::atomic::Ordering::SeqCst),
            1
        );
        assert_eq!(counts.closes.load(std::sync::atomic::Ordering::SeqCst), 1);
    }
}

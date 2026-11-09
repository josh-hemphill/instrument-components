use instrument_api::{catalog::*, *};
use instrument_runtime::*;
use instrument_testkit::*;
use std::{collections::BTreeSet, sync::Arc};
include!("profiles.rs");
include!("cases.rs");
fn candidate() -> Candidate {
    Candidate {
        endpoint: Endpoint {
            scheme: "scpi".into(),
            address: "fixture".into(),
        },
        identity: Identity {
            manufacturer: Some("Synthetic".into()),
            model: Some("All".into()),
            ..Identity::default()
        },
    }
}
fn selection() -> Selection {
    Selection {
        allow_standard_scpi: true,
        ..Selection::default()
    }
}
fn check(actual: Result<CatalogReply>, example: &CatalogCase, last: bool) -> Result<()> {
    if last {
        if let Some(kind) = example.expected_error {
            assert_eq!(actual.unwrap_err().kind, kind, "{}", example.id);
        } else {
            assert_eq!(actual?, example.expected.clone().unwrap(), "{}", example.id);
        }
    } else {
        actual?;
    }
    Ok(())
}
fn main() -> Result<()> {
    generated_profiles::profiles()?;
    for example in generated_cases::cases() {
        let script = Script::new(example.script.clone());
        let registry = DriverRegistry::new(generated_profiles::sync_factories(Arc::new(
            script.clone(),
        ))?)?;
        let h = registry.open(
            &candidate(),
            &selection(),
            &BTreeSet::new(),
            ExecutionMode::Sync,
            ConnectionOwnership::Owned,
        )?;
        for (i, request) in example.calls.iter().enumerate() {
            check(
                h.invoke(request.clone()),
                &example,
                i + 1 == example.calls.len(),
            )?;
        }
        h.close(false)?;
        script.assert_complete()?;
        script.assert_thread_affinity()?;
        assert_eq!(script.closes(), 1);
    }
    {
        let script = typed_script();
        let registry = DriverRegistry::new(generated_profiles::sync_factories(Arc::new(
            script.clone(),
        ))?)?;
        let h = registry.open(
            &candidate(),
            &selection(),
            &BTreeSet::new(),
            ExecutionMode::Sync,
            ConnectionOwnership::Owned,
        )?;
        typed(&h)?;
        h.close(false)?;
        script.assert_complete()?;
    }
    println!("PASS packaged SCPI sync: all eight typed classes and shared conformance cases");
    #[cfg(feature = "async")]
    tokio::runtime::Builder::new_current_thread()
        .build()
        .unwrap()
        .block_on(asynchronous())?;
    Ok(())
}
#[cfg(feature = "async")]
async fn asynchronous() -> Result<()> {
    for mode in 0..3 {
        for example in generated_cases::cases() {
            let script = Script::new(example.script.clone());
            let factory: Arc<dyn DriverFactory> = generated_profiles::combined_factories(
                Arc::new(script.clone()),
                Arc::new(script.clone()),
            )?
            .remove(0);
            let registry = DriverRegistry::new([factory])?;
            let native = if mode == 0 {
                Some(
                    registry
                        .open_native(
                            &candidate(),
                            &selection(),
                            &BTreeSet::new(),
                            ConnectionOwnership::Owned,
                        )
                        .await?,
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
                        .await?,
                )
            } else {
                None
            };
            for (i, request) in example.calls.iter().enumerate() {
                let actual = if let Some(h) = &native {
                    h.invoke_async(request.clone()).await
                } else if mode == 1 {
                    worker.as_ref().unwrap().invoke_async(request.clone()).await
                } else {
                    worker.as_ref().unwrap().invoke(request.clone())
                };
                check(actual, &example, i + 1 == example.calls.len())?;
            }
            if let Some(h) = native {
                h.close(false).await?;
            } else {
                worker.unwrap().close(false).await?;
                script.assert_thread_affinity()?;
            }
            script.assert_complete()?;
            assert_eq!(script.closes(), 1);
        }
    }
    for mode in 0..3 {
        let script = typed_script();
        let registry = DriverRegistry::new(generated_profiles::combined_factories(
            Arc::new(script.clone()),
            Arc::new(script.clone()),
        )?)?;
        if mode == 0 {
            let h = registry
                .open_native(
                    &candidate(),
                    &selection(),
                    &BTreeSet::new(),
                    ConnectionOwnership::Owned,
                )
                .await?;
            typed_async(&h).await?;
            h.close(false).await?;
        } else {
            let h = registry
                .open_worker(
                    &candidate(),
                    &selection(),
                    &BTreeSet::new(),
                    ConnectionOwnership::Owned,
                )
                .await?;
            if mode == 1 {
                typed_async(&h).await?;
            } else {
                typed(&h)?;
            }
            h.close(false).await?;
            script.assert_thread_affinity()?;
        }
        script.assert_complete()?;
    }
    println!("PASS packaged SCPI native/worker: exact values/units/errors, scripts complete, affinity/lifetime");
    Ok(())
}

fn typed(e: &impl CatalogExecutor) -> Result<()> {
    use instrument_api::catalog::{
        Counter, DcPowerSupply, Dmm, FunctionGenerator, Oscilloscope, PowerMeter, SpectrumAnalyzer,
        Switch,
    };
    assert_eq!(Dmm(e).measure_voltage_dc(Some(2.5))?.unit, Unit::Volt);
    assert_eq!(DcPowerSupply(e).read_voltage(1)?.value, 3.3);
    assert_eq!(FunctionGenerator(e).read_frequency()?.unit, Unit::Hertz);
    let wave = Oscilloscope(e).capture_voltage_trace(1)?;
    assert_eq!(wave.values.len(), 3);
    assert_eq!(wave.sample_interval_s, Some(0.5));
    assert_eq!(wave.unit, Unit::Volt);
    assert!(Switch(e).is_closed(1, 1)?);
    assert_eq!(Counter(e).measure_frequency()?.unit, Unit::Hertz);
    PowerMeter(e).configure_measurement(PowerUnit::Watt, true, true, Some(2.5), Some(2.5))?;
    assert_eq!(PowerMeter(e).read()?.unit, Unit::Watt);
    assert_eq!(SpectrumAnalyzer(e).marker_y()?.unit, Unit::Dbm);
    assert_eq!(Dmm(e).query_identity()?.model, "All");
    Ok(())
}
#[cfg(feature = "async")]
async fn typed_async(e: &impl AsyncCatalogExecutor) -> Result<()> {
    use instrument_api::catalog::{
        AsyncCounter, AsyncDcPowerSupply, AsyncDmm, AsyncFunctionGenerator, AsyncOscilloscope,
        AsyncPowerMeter, AsyncSpectrumAnalyzer, AsyncSwitch,
    };
    assert_eq!(
        AsyncDmm(e).measure_voltage_dc(Some(2.5)).await?.unit,
        Unit::Volt
    );
    assert_eq!(AsyncDcPowerSupply(e).read_voltage(1).await?.value, 3.3);
    assert_eq!(
        AsyncFunctionGenerator(e).read_frequency().await?.unit,
        Unit::Hertz
    );
    let wave = AsyncOscilloscope(e).capture_voltage_trace(1).await?;
    assert_eq!(wave.values.len(), 3);
    assert_eq!(wave.sample_interval_s, Some(0.5));
    assert_eq!(wave.unit, Unit::Volt);
    assert!(AsyncSwitch(e).is_closed(1, 1).await?);
    assert_eq!(AsyncCounter(e).measure_frequency().await?.unit, Unit::Hertz);
    AsyncPowerMeter(e)
        .configure_measurement(PowerUnit::Watt, true, true, Some(2.5), Some(2.5))
        .await?;
    assert_eq!(AsyncPowerMeter(e).read().await?.unit, Unit::Watt);
    assert_eq!(AsyncSpectrumAnalyzer(e).marker_y().await?.unit, Unit::Dbm);
    assert_eq!(AsyncDmm(e).query_identity().await?.model, "All");
    Ok(())
}
fn typed_script() -> Script {
    let examples = generated_cases::cases();
    let ids = [
        "SCPI-DmmMeasureVoltageDc",
        "SCPI-DcPowerSupplyReadVoltage",
        "SCPI-FunctionGeneratorReadFrequency",
        "SCPI-OscilloscopeCaptureVoltageTrace",
        "SCPI-SwitchIsClosed",
        "SCPI-CounterMeasureFrequency",
        "SCPI-PowerMeterRead",
        "SCPI-SpectrumAnalyzerMarkerY",
        "SCPI-QueryIdentity",
    ];
    let steps = ids
        .into_iter()
        .flat_map(|id| examples.iter().find(|c| c.id == id).unwrap().script.clone())
        .filter(|s| !matches!(s, ScriptStep::Close(_)))
        .chain([ScriptStep::Close(Ok(()))]);
    Script::new(steps)
}

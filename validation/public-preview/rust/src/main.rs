use instrument::prelude::*;
use instrument_api::Operation;
use instrument_testkit::{Script, ScriptStep};
use std::{collections::BTreeSet, sync::Arc};

fn candidate() -> Candidate {
    Candidate {
        endpoint: Endpoint {
            scheme: "scpi".into(),
            address: "preview-mock".into(),
        },
        identity: Identity {
            manufacturer: Some("Keysight Technologies".into()),
            model: Some("34401A".into()),
            ..Identity::default()
        },
    }
}
fn script() -> Arc<Script> {
    Arc::new(Script::new([
        ScriptStep::Query(
            ":MEAS:VOLT:DC? DEF,DEF".into(),
            Ok("+1.25000000E+00".into()),
        ),
        ScriptStep::Close(Ok(())),
    ]))
}
// Commissioning is an explicit host assertion, not hardware discovery.
fn factories(io: Arc<Script>, native: bool) -> Result<Vec<Arc<dyn DriverFactory>>> {
    use instrument_driver_keysight::qualified_scpi::{Binding, Commissioning, Factory, Model};
    let factory = Factory::new(
        Model::Agilent34401A,
        vec![Binding {
            endpoint: candidate().endpoint,
            commissioning: Commissioning::GpibScpiConfirmed,
        }],
        io.clone(),
    )?;
    #[cfg(feature = "async")]
    let factory = if native {
        factory.with_native_connector(io)
    } else {
        factory
    };
    #[cfg(not(feature = "async"))]
    let _ = native;
    Ok(vec![Arc::new(factory)])
}
fn required() -> BTreeSet<Operation> {
    [Operation::Catalog(CatalogOperation::DmmMeasureVoltageDc)]
        .into_iter()
        .collect()
}
fn synchronous() -> Result<()> {
    let io = script();
    let registry = DriverRegistry::new(factories(io.clone(), false)?)?;
    let c = candidate();
    assert_eq!(
        registry.select(&c, &Selection::default())?.driver_id,
        "keysight.34401a"
    );
    assert_eq!(io.opens(), 0);
    let catalog = DeviceCatalog::new(registry, vec![c.clone()])?;
    let device = catalog.device(&c.endpoint)?;
    let unsupported = [Operation::Catalog(
        CatalogOperation::DcPowerSupplyReadVoltage,
    )]
    .into_iter()
    .collect();
    assert_eq!(
        device
            .open(
                &Selection::default(),
                &unsupported,
                ConnectionOwnership::Owned
            )
            .err()
            .unwrap()
            .kind,
        ErrorKind::UnsupportedOperation
    );
    assert_eq!(io.opens(), 0);
    let handle = device.open(
        &Selection::default(),
        &required(),
        ConnectionOwnership::Owned,
    )?;
    let value = Dmm(&handle).measure_voltage_dc(None)?;
    assert_eq!(value.value, 1.25);
    assert_eq!(value.unit, Unit::Volt);
    handle.close(false)?;
    assert_eq!(
        Dmm(&handle).measure_voltage_dc(None).unwrap_err().kind,
        ErrorKind::SessionClosed
    );
    io.assert_complete()?;
    assert_eq!((io.opens(), io.closes()), (1, 1));
    println!("PASS preview Rust sync: 1.25 Volt, passive matching, pre-open requirements, checked close; no hardware");
    Ok(())
}
#[cfg(feature = "async")]
async fn asynchronous() -> Result<()> {
    for mode in 0..3 {
        let io = script();
        let factories = factories(io.clone(), true)?;
        let c = candidate();
        let catalog = DeviceCatalog::new(DriverRegistry::new(factories)?, vec![c.clone()])?;
        let device = catalog.device(&c.endpoint)?;
        let value = if mode == 0 {
            let handle = device
                .open_native(
                    &Selection::default(),
                    &required(),
                    ConnectionOwnership::Owned,
                )
                .await?;
            let value = AsyncDmm(&handle).measure_voltage_dc(None).await?;
            handle.close(false).await?;
            value
        } else {
            let handle = device
                .open_worker(
                    &Selection::default(),
                    &required(),
                    ConnectionOwnership::Owned,
                )
                .await?;
            let value = if mode == 1 {
                AsyncDmm(&handle).measure_voltage_dc(None).await?
            } else {
                Dmm(&handle).measure_voltage_dc(None)?
            };
            handle.close(false).await?;
            io.assert_thread_affinity()?;
            value
        };
        assert_eq!(value.value, 1.25);
        assert_eq!(value.unit, Unit::Volt);
        io.assert_complete()?;
        assert_eq!((io.opens(), io.closes()), (1, 1));
    }
    println!("PASS preview Rust native async, worker async and worker blocking; no hardware");
    Ok(())
}
fn main() -> Result<()> {
    synchronous()?;
    #[cfg(feature = "async")]
    tokio::runtime::Builder::new_current_thread()
        .build()
        .unwrap()
        .block_on(asynchronous())?;
    Ok(())
}

use instrument_api::*;
use instrument_runtime::*;
use instrument_synthetic_driver::{candidate, BinaryFactory, Trace};
use std::collections::BTreeSet;
use std::sync::{atomic::Ordering, Arc};

fn main() -> Result<()> {
    checked_close_regressions()?;
    let trace = Arc::new(Trace::default());
    let registry = DriverRegistry::new([
        Arc::new(BinaryFactory::new(trace.clone())) as Arc<dyn DriverFactory>
    ])?;
    let handle = registry.open(
        &candidate(),
        &Selection::default(),
        &[Operation::MeasureVoltageDc].into_iter().collect(),
        ExecutionMode::Sync,
        ConnectionOwnership::Owned,
    )?;
    assert_eq!(handle.evidence().driver_id, "synthetic.binary");
    assert_eq!(handle.dmm().measure_voltage_dc(None)?.value, 3.3);
    handle.psu().set_voltage(2, 5.0)?;
    assert_eq!(handle.psu().read_voltage(2)?.value, 5.0);
    let before = trace.events.lock().unwrap().len();
    assert_eq!(
        handle
            .set_sense_mode(1, SenseMode::External)
            .unwrap_err()
            .kind,
        ErrorKind::UnsupportedOperation
    );
    assert_eq!(trace.events.lock().unwrap().len(), before);
    assert!(matches!(
        registry.open(
            &candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ExecutionMode::NativeAsync,
            ConnectionOwnership::Owned
        ),
        Err(DriverError {
            kind: ErrorKind::UnsupportedExecutionMode,
            ..
        })
    ));
    trace.fail_measure.store(true, Ordering::SeqCst);
    assert_eq!(
        handle.dmm().measure_voltage_dc(None).unwrap_err().kind,
        ErrorKind::Timeout
    );
    assert_eq!(
        handle.dmm().measure_voltage_dc(None).unwrap_err().kind,
        ErrorKind::SessionNeedsRecovery
    );
    handle.recover()?;
    assert_eq!(handle.dmm().measure_voltage_dc(None)?.value, 3.3);
    handle.psu().output_enable(1, true)?;
    handle.psu().output_enable(2, true)?;
    handle.close(true)?;
    handle.close(true)?;
    assert_eq!(*trace.shutdown_attempts.lock().unwrap(), vec![1, 2]);
    assert!(trace
        .outputs
        .lock()
        .unwrap()
        .values()
        .all(|enabled| !enabled));
    assert_eq!(trace.closes.load(Ordering::SeqCst), 1);
    assert_eq!(trace.shutdowns.load(Ordering::SeqCst), 1);
    assert_eq!(trace.events.lock().unwrap()[..2], ["[a1]", "[b2]"]);
    let reduced = Arc::new(Trace::default());
    reduced
        .disabled_operations
        .lock()
        .unwrap()
        .insert(Operation::Shutdown);
    let reduced_registry = DriverRegistry::new([
        Arc::new(BinaryFactory::new(reduced.clone())) as Arc<dyn DriverFactory>
    ])?;
    let reduced_handle = reduced_registry.open(
        &candidate(),
        &Selection::default(),
        &BTreeSet::new(),
        ExecutionMode::Sync,
        ConnectionOwnership::Owned,
    )?;
    assert_eq!(
        reduced_handle.close(true).unwrap_err().kind,
        ErrorKind::Shutdown
    );
    assert_eq!(reduced.closes.load(Ordering::SeqCst), 1);
    assert_eq!(reduced.shutdowns.load(Ordering::SeqCst), 0);
    assert_eq!(reduced.recoveries.load(Ordering::SeqCst), 0);
    let passive = Arc::new(Trace::default());
    let mut family = BinaryFactory::new(passive.clone());
    family.descriptor.id = "synthetic.family".into();
    family.descriptor.rules = vec![MatchRule::Family {
        scheme: "fake".into(),
        manufacturer: "Acme".into(),
        model_pattern: "X*".into(),
    }];
    let family_registry = DriverRegistry::new([Arc::new(family) as Arc<dyn DriverFactory>])?;
    let mut family_candidate = candidate();
    family_candidate.identity.model = Some("X*100".into());
    assert_eq!(
        family_registry
            .select(&family_candidate, &Selection::default())?
            .driver_id,
        "synthetic.family"
    );
    assert_eq!(passive.opens.load(Ordering::SeqCst), 0);
    let empty = DriverRegistry::new([])?;
    assert_eq!(
        empty
            .select(&candidate(), &Selection::default())
            .unwrap_err()
            .kind,
        ErrorKind::DriverNotFound
    );
    println!("PASS Rust packaged provider: binary 3.3 V, PSU, unsupported mode/operation, recovery, shutdown, close, core-only selection");
    #[cfg(feature = "async")]
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
        .block_on(async_proof())?;
    Ok(())
}

fn checked_close_regressions() -> Result<()> {
    fn open(trace: Arc<Trace>) -> Result<DeviceHandle> {
        DriverRegistry::new([Arc::new(BinaryFactory::new(trace)) as Arc<dyn DriverFactory>])?.open(
            &candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ExecutionMode::Sync,
            ConnectionOwnership::Owned,
        )
    }
    let trace = Arc::new(Trace::default());
    trace.fail_shutdown.store(true, Ordering::SeqCst);
    trace.fail_close.store(true, Ordering::SeqCst);
    let h = open(trace.clone())?;
    for shutdown in [true, true, false] {
        assert_eq!(h.close(shutdown).unwrap_err().kind, ErrorKind::Shutdown);
    }
    drop(h);
    assert_eq!(trace.closes.load(Ordering::SeqCst), 1);
    assert_eq!(trace.shutdowns.load(Ordering::SeqCst), 1);
    assert_eq!(trace.disposes.load(Ordering::SeqCst), 1);
    let trace = Arc::new(Trace::default());
    let h = open(trace.clone())?;
    h.close(false)?;
    assert_eq!(h.close(true).unwrap_err().kind, ErrorKind::Shutdown);
    h.close(false)?;
    assert_eq!(trace.closes.load(Ordering::SeqCst), 1);
    assert_eq!(trace.shutdowns.load(Ordering::SeqCst), 0);
    println!(
        "PASS Rust packed checked-close outcomes: no strengthening, no lost failure, one cleanup"
    );
    Ok(())
}

#[cfg(feature = "async")]
async fn async_proof() -> Result<()> {
    use instrument_synthetic_driver::{
        native_candidate, worker_candidate, AsyncTrace, NativeFactory, WorkerFactory, WorkerTrace,
    };
    let trace = Arc::new(AsyncTrace::default());
    let registry =
        DriverRegistry::new([Arc::new(NativeFactory(trace.clone())) as Arc<dyn DriverFactory>])?;
    let h = registry
        .open_native(
            &native_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await?;
    assert_eq!(h.measure_voltage_dc(None).await?.value, 3.3);
    h.set_voltage(1, 4.2).await?;
    assert_eq!(h.read_voltage(1).await?.value, 4.2);
    h.output_enable(1, true).await?;
    *trace.base.measure_error.lock().unwrap() = Some(ErrorKind::SessionClosed);
    trace.base.fail_measure.store(true, Ordering::SeqCst);
    assert_eq!(
        h.measure_voltage_dc(None).await.unwrap_err().kind,
        ErrorKind::SessionClosed
    );
    assert!(h.recovery_required());
    h.recover().await?;
    h.close(true).await?;
    assert_eq!(trace.base.closes.load(Ordering::SeqCst), 1);
    assert!(trace.base.outputs.lock().unwrap().values().all(|v| !v));
    let local = Arc::new(WorkerTrace::default());
    let registry =
        DriverRegistry::new([Arc::new(WorkerFactory(local.clone())) as Arc<dyn DriverFactory>])?;
    let h = registry
        .open_worker(
            &worker_candidate(),
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await?;
    assert_eq!(h.measure_voltage_dc(None)?.value, 3.3);
    assert_eq!(h.measure_voltage_dc_async(None).await?.value, 3.3);
    h.set_voltage(1, 7.0)?;
    assert_eq!(h.read_voltage_async(1).await?.value, 7.0);
    *local.base.measure_error.lock().unwrap() = Some(ErrorKind::SessionClosing);
    local.base.fail_measure.store(true, Ordering::SeqCst);
    assert_eq!(
        h.measure_voltage_dc_async(None).await.unwrap_err().kind,
        ErrorKind::SessionClosing
    );
    assert!(h.recovery_required());
    h.recover_sync()?;
    h.reset()?;
    h.output_enable(1, true)?;
    h.output_enable_async(2, true).await?;
    h.close(true).await?;
    assert_eq!(local.base.closes.load(Ordering::SeqCst), 1);
    let events = local.affinity.lock().unwrap();
    assert!(events.iter().all(|(_, id)| *id == events[0].1));
    assert!(events.iter().any(|(name, _)| name == "drop"));
    println!("PASS Rust native/worker packages: async binary values, mixed worker frontends, recovery, output states, affinity, close");
    Ok(())
}

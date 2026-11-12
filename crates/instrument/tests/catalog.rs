use instrument::api::*;
use instrument::prelude::*;
use std::{
    collections::BTreeSet,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    },
};
#[derive(Default)]
struct Counts {
    opens: AtomicUsize,
    closes: AtomicUsize,
    calls: AtomicUsize,
}
struct Factory(Arc<Counts>);
fn operations() -> BTreeSet<Operation> {
    [
        CatalogOperation::DmmMeasureVoltageDc,
        CatalogOperation::DcPowerSupplyReadVoltage,
    ]
    .into_iter()
    .map(Operation::Catalog)
    .collect()
}
impl DriverFactory for Factory {
    fn descriptor(&self) -> DriverDescriptor {
        DriverDescriptor {
            id: "synthetic.opaque".into(),
            contract_major: 1,
            rules: vec![MatchRule::ExactIdentity {
                scheme: "opaque".into(),
                manufacturer: "Example".into(),
                model: "Device".into(),
            }],
            operations: operations(),
        }
    }
    fn open(&self, _: &OpenContext<'_>) -> Result<Box<dyn DeviceBackend>> {
        self.0.opens.fetch_add(1, Ordering::SeqCst);
        Ok(Box::new(Backend(self.0.clone())))
    }
    #[cfg(feature = "async")]
    fn execution_modes(&self) -> BTreeSet<ExecutionMode> {
        [
            ExecutionMode::Sync,
            ExecutionMode::NativeAsync,
            ExecutionMode::WorkerAsync,
        ]
        .into_iter()
        .collect()
    }
    #[cfg(feature = "async")]
    fn open_native<'a>(
        &'a self,
        _: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncDeviceBackend>> {
        Box::pin(async move {
            self.0.opens.fetch_add(1, Ordering::SeqCst);
            Ok(Box::new(Backend(self.0.clone())) as Box<dyn AsyncDeviceBackend>)
        })
    }
}
struct Backend(Arc<Counts>);
impl CatalogBackend for Backend {
    fn invoke(&mut self, _: CatalogRequest) -> Result<CatalogReply> {
        self.0.calls.fetch_add(1, Ordering::SeqCst);
        Ok(CatalogReply::Quantity(Quantity {
            value: 1.25,
            unit: Unit::Volt,
            status: MeasurementStatus::Valid,
        }))
    }
}
impl DeviceBackend for Backend {
    fn operations(&self) -> BTreeSet<Operation> {
        operations()
    }
    fn catalog(&mut self) -> Option<&mut dyn CatalogBackend> {
        Some(self)
    }
    fn close(&mut self) -> Result<()> {
        self.0.closes.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
}
#[cfg(feature = "async")]
impl AsyncCatalogBackend for Backend {
    fn invoke_async(&mut self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply> {
        Box::pin(async move { CatalogBackend::invoke(self, request) })
    }
}
#[cfg(feature = "async")]
impl AsyncDeviceBackend for Backend {
    fn operations(&self) -> BTreeSet<Operation> {
        operations()
    }
    fn catalog(&mut self) -> Option<&mut dyn AsyncCatalogBackend> {
        Some(self)
    }
    fn close(&mut self) -> Result<()> {
        DeviceBackend::close(self)
    }
}
fn candidate() -> Candidate {
    Candidate {
        endpoint: Endpoint {
            scheme: "opaque".into(),
            address: "fixture".into(),
        },
        identity: Identity {
            manufacturer: Some("Example".into()),
            model: Some("Device".into()),
            ..Identity::default()
        },
    }
}
fn catalog(counts: Arc<Counts>) -> DeviceCatalog {
    DeviceCatalog::new(
        DriverRegistry::new([Arc::new(Factory(counts)) as Arc<dyn DriverFactory>]).unwrap(),
        vec![candidate()],
    )
    .unwrap()
}
#[test]
fn passive_catalog_and_typed_views_share_one_opaque_connection() {
    let counts = Arc::new(Counts::default());
    let catalog = catalog(counts.clone());
    let device = catalog.device(&candidate().endpoint).unwrap();
    assert_eq!(
        device.select(&Selection::default()).unwrap().driver_id,
        "synthetic.opaque"
    );
    assert_eq!(counts.opens.load(Ordering::SeqCst), 0);
    let handle = device
        .open(
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .unwrap();
    assert_eq!(Dmm(&handle).measure_voltage_dc(None).unwrap().value, 1.25);
    assert_eq!(DcPowerSupply(&handle).read_voltage(1).unwrap().value, 1.25);
    assert_eq!(counts.opens.load(Ordering::SeqCst), 1);
    handle.close(false).unwrap();
    assert_eq!(counts.closes.load(Ordering::SeqCst), 1);
}
#[test]
fn unsupported_selection_and_required_capabilities_fail_before_open() {
    let counts = Arc::new(Counts::default());
    let catalog = catalog(counts.clone());
    let device = catalog.device(&candidate().endpoint).unwrap();
    assert!(device
        .open(
            &Selection::default(),
            &[Operation::Catalog(CatalogOperation::Shutdown)]
                .into_iter()
                .collect(),
            ConnectionOwnership::Owned
        )
        .is_err());
    assert_eq!(counts.opens.load(Ordering::SeqCst), 0);
    assert!(DeviceCatalog::new(
        DriverRegistry::new([]).unwrap(),
        vec![candidate(), candidate()]
    )
    .is_err());
    assert!(catalog
        .device(&Endpoint {
            scheme: "opaque".into(),
            address: "missing".into()
        })
        .is_err());
}
#[cfg(feature = "async")]
#[tokio::test]
async fn native_and_worker_frontends_retain_the_same_catalog_boundary() {
    let counts = Arc::new(Counts::default());
    let catalog = catalog(counts.clone());
    let device = catalog.device(&candidate().endpoint).unwrap();
    let native = device
        .open_native(
            &Selection::default(),
            &BTreeSet::new(),
            ConnectionOwnership::Owned,
        )
        .await
        .unwrap();
    assert_eq!(
        AsyncDmm(&native)
            .measure_voltage_dc(None)
            .await
            .unwrap()
            .value,
        1.25
    );
    native.close(false).await.unwrap();
    for blocking in [false, true] {
        let worker = device
            .open_worker(
                &Selection::default(),
                &BTreeSet::new(),
                ConnectionOwnership::Owned,
            )
            .await
            .unwrap();
        let q = if blocking {
            DcPowerSupply(&worker).read_voltage(1).unwrap()
        } else {
            AsyncDcPowerSupply(&worker).read_voltage(1).await.unwrap()
        };
        assert_eq!(q.unit, Unit::Volt);
        worker.close(false).await.unwrap();
    }
    assert_eq!(counts.opens.load(Ordering::SeqCst), 3);
    assert_eq!(counts.closes.load(Ordering::SeqCst), 3);
}

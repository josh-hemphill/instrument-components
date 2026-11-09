use super::*;
use std::{cell::RefCell, rc::Rc, thread::ThreadId};
use tokio::sync::Notify;

pub type AsyncHook = Box<dyn FnOnce() -> DriverFuture<'static, ()> + Send>;
#[derive(Default)]
pub struct AsyncTrace {
    pub base: Arc<Trace>,
    pub block_measure: AtomicBool,
    pub started: Notify,
    pub release: Notify,
    pub block_open: AtomicBool,
    pub open_started: Notify,
    pub open_release: Notify,
    pub block_recover: AtomicBool,
    pub recover_started: Notify,
    pub recover_release: Notify,
    pub block_shutdown: AtomicBool,
    pub shutdown_started: Notify,
    pub shutdown_release: Notify,
    pub fail_open: AtomicBool,
    pub hook: Mutex<Option<AsyncHook>>,
}
fn family_descriptor(id: &str, model: &str) -> DriverDescriptor {
    let mut d = descriptor();
    d.id = id.into();
    d.rules = vec![MatchRule::ExactIdentity {
        scheme: "fake".into(),
        manufacturer: "Acme".into(),
        model: model.into(),
    }];
    d
}
pub fn native_candidate() -> Candidate {
    let mut c = candidate();
    c.identity.model = Some("N100".into());
    c
}
pub fn worker_candidate() -> Candidate {
    let mut c = candidate();
    c.identity.model = Some("W100".into());
    c
}
pub struct NativeFactory(pub Arc<AsyncTrace>);
impl DriverFactory for NativeFactory {
    fn descriptor(&self) -> DriverDescriptor {
        family_descriptor("synthetic.native", "N100")
    }
    fn execution_modes(&self) -> BTreeSet<ExecutionMode> {
        [ExecutionMode::NativeAsync].into_iter().collect()
    }
    fn open_native<'a>(
        &'a self,
        context: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncDeviceBackend>> {
        Box::pin(async move {
            self.0.base.opens.fetch_add(1, Ordering::SeqCst);
            let mut device = NativeDevice {
                inner: BinaryDevice {
                    trace: self.0.base.clone(),
                    ownership: context.ownership,
                    voltages: BTreeMap::new(),
                    stage: false,
                },
                trace: self.0.clone(),
                closed: false,
            };
            self.0.open_started.notify_one();
            if self.0.block_open.load(Ordering::SeqCst) {
                self.0.open_release.notified().await;
            }
            if self.0.fail_open.load(Ordering::SeqCst) {
                let mut failure =
                    DriverError::new(ErrorKind::Device, "open failed after acquisition");
                if let Err(cleanup) = AsyncDeviceBackend::close(&mut device) {
                    failure.message.push_str(&format!("; cleanup: {cleanup}"));
                }
                return Err(failure);
            }
            Ok(Box::new(device) as Box<dyn AsyncDeviceBackend>)
        })
    }
}
struct NativeDevice {
    inner: BinaryDevice,
    trace: Arc<AsyncTrace>,
    closed: bool,
}
impl Drop for NativeDevice {
    fn drop(&mut self) {
        if !self.closed {
            self.closed = true;
            let _ = DeviceBackend::close(&mut self.inner);
        }
    }
}
impl AsyncDmmMeasurement for NativeDevice {
    fn measure_voltage_dc(&mut self, _range: Option<f64>) -> DriverFuture<'_, Measurement> {
        Box::pin(async move {
            let hook = self.trace.hook.lock().unwrap().take();
            if let Some(hook) = hook {
                hook().await?;
            }
            self.inner.exchange(&[0xa1])?;
            self.trace.started.notify_one();
            if self.trace.base.fail_measure.swap(false, Ordering::SeqCst) {
                return Err(DriverError::new(
                    self.trace
                        .base
                        .measure_error
                        .lock()
                        .unwrap()
                        .take()
                        .unwrap_or(ErrorKind::Timeout),
                    "injected provider failure after first write",
                ));
            }
            tokio::task::yield_now().await;
            if self.trace.block_measure.load(Ordering::SeqCst) {
                self.trace.release.notified().await;
            }
            let bytes: [u8; 8] = self
                .inner
                .exchange(&[0xb2])?
                .try_into()
                .map_err(|_| DriverError::new(ErrorKind::Parse, "binary length"))?;
            Ok(Measurement {
                value: f64::from_le_bytes(bytes),
                status: MeasurementStatus::Valid,
            })
        })
    }
}
impl AsyncPsuOutput for NativeDevice {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> DriverFuture<'_, ()> {
        Box::pin(async move { PsuOutput::set_voltage(&mut self.inner, channel, volts) })
    }
    fn read_voltage(&mut self, channel: u32) -> DriverFuture<'_, Measurement> {
        Box::pin(async move { PsuOutput::read_voltage(&mut self.inner, channel) })
    }
    fn output_enable(&mut self, channel: u32, enabled: bool) -> DriverFuture<'_, ()> {
        Box::pin(async move { PsuOutput::output_enable(&mut self.inner, channel, enabled) })
    }
}
impl AsyncReset for NativeDevice {
    fn reset(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async { Reset::reset(&mut self.inner) })
    }
}
impl AsyncSafeShutdown for NativeDevice {
    fn shutdown(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async {
            self.trace.shutdown_started.notify_one();
            if self.trace.block_shutdown.load(Ordering::SeqCst) {
                self.trace.shutdown_release.notified().await;
            }
            SafeShutdown::shutdown(&mut self.inner)
        })
    }
}
impl AsyncDeviceBackend for NativeDevice {
    fn operations(&self) -> BTreeSet<Operation> {
        DeviceBackend::operations(&self.inner)
    }
    fn dmm(&mut self) -> Option<&mut dyn AsyncDmmMeasurement> {
        if self.trace.base.missing_dmm.load(Ordering::SeqCst) {
            None
        } else {
            Some(self)
        }
    }
    fn psu(&mut self) -> Option<&mut dyn AsyncPsuOutput> {
        Some(self)
    }
    fn reset(&mut self) -> Option<&mut dyn AsyncReset> {
        Some(self)
    }
    fn shutdown(&mut self) -> Option<&mut dyn AsyncSafeShutdown> {
        Some(self)
    }
    fn recover(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async {
            self.trace.recover_started.notify_one();
            if self.trace.block_recover.load(Ordering::SeqCst) {
                self.trace.recover_release.notified().await;
            }
            DeviceBackend::recover(&mut self.inner)
        })
    }
    fn close(&mut self) -> Result<()> {
        if self.closed {
            return Ok(());
        }
        self.closed = true;
        DeviceBackend::close(&mut self.inner)
    }
}

#[derive(Default)]
pub struct WorkerTrace {
    pub base: Arc<Trace>,
    pub affinity: Mutex<Vec<(String, ThreadId)>>,
    pub block_open: Mutex<(bool, bool)>,
    pub open_wake: Condvar,
    pub fail_open: AtomicBool,
}
impl WorkerTrace {
    fn record(&self, event: &str) {
        self.affinity
            .lock()
            .unwrap()
            .push((event.into(), std::thread::current().id()));
    }
}
pub struct WorkerFactory(pub Arc<WorkerTrace>);
impl DriverFactory for WorkerFactory {
    fn descriptor(&self) -> DriverDescriptor {
        family_descriptor("synthetic.worker", "W100")
    }
    fn execution_modes(&self) -> BTreeSet<ExecutionMode> {
        [ExecutionMode::WorkerAsync].into_iter().collect()
    }
    fn open_worker(&self, context: &OpenContext<'_>) -> Result<Box<dyn WorkerBackend>> {
        self.0.record("open");
        self.0.base.opens.fetch_add(1, Ordering::SeqCst);
        let mut device = LocalDevice {
            inner: Rc::new(RefCell::new(BinaryDevice {
                trace: self.0.base.clone(),
                ownership: context.ownership,
                voltages: BTreeMap::new(),
                stage: false,
            })),
            trace: self.0.clone(),
            thread: std::thread::current().id(),
            closed: false,
        };
        {
            let mut state = self.0.block_open.lock().unwrap();
            state.1 = true;
            self.0.open_wake.notify_all();
            while state.0 {
                state = self.0.open_wake.wait(state).unwrap();
            }
        }
        if self.0.fail_open.load(Ordering::SeqCst) {
            let mut failure =
                DriverError::new(ErrorKind::Device, "worker open failed after acquisition");
            if let Err(cleanup) = WorkerBackend::close(&mut device) {
                failure.message.push_str(&format!("; cleanup: {cleanup}"));
            }
            return Err(failure);
        }
        Ok(Box::new(device))
    }
}
// This is intentionally !Send. No unsafe implementation or frontend ownership exists.
struct LocalDevice {
    inner: Rc<RefCell<BinaryDevice>>,
    trace: Arc<WorkerTrace>,
    thread: ThreadId,
    closed: bool,
}
impl LocalDevice {
    fn check(&self, name: &str) {
        assert_eq!(self.thread, std::thread::current().id());
        self.trace.record(name);
    }
}
impl Drop for LocalDevice {
    fn drop(&mut self) {
        self.check("drop");
        if !self.closed {
            self.closed = true;
            let _ = DeviceBackend::close(&mut *self.inner.borrow_mut());
            self.trace.record("close");
        }
    }
}
impl LocalDmmMeasurement for LocalDevice {
    fn measure_voltage_dc(&mut self, range: Option<f64>) -> Result<Measurement> {
        self.check("measure");
        DmmMeasurement::measure_voltage_dc(&mut *self.inner.borrow_mut(), range)
    }
}
impl LocalPsuOutput for LocalDevice {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> Result<()> {
        self.check("set");
        PsuOutput::set_voltage(&mut *self.inner.borrow_mut(), channel, volts)
    }
    fn read_voltage(&mut self, channel: u32) -> Result<Measurement> {
        self.check("read");
        PsuOutput::read_voltage(&mut *self.inner.borrow_mut(), channel)
    }
    fn output_enable(&mut self, channel: u32, enabled: bool) -> Result<()> {
        self.check("output");
        PsuOutput::output_enable(&mut *self.inner.borrow_mut(), channel, enabled)
    }
}
impl LocalReset for LocalDevice {
    fn reset(&mut self) -> Result<()> {
        self.check("reset");
        Reset::reset(&mut *self.inner.borrow_mut())
    }
}
impl LocalSafeShutdown for LocalDevice {
    fn shutdown(&mut self) -> Result<()> {
        self.check("shutdown");
        SafeShutdown::shutdown(&mut *self.inner.borrow_mut())
    }
}
impl WorkerBackend for LocalDevice {
    fn operations(&self) -> BTreeSet<Operation> {
        self.check("metadata");
        DeviceBackend::operations(&*self.inner.borrow())
    }
    fn dmm(&mut self) -> Option<&mut dyn LocalDmmMeasurement> {
        self.check("accessor");
        if self.trace.base.missing_dmm.load(Ordering::SeqCst) {
            None
        } else {
            Some(self)
        }
    }
    fn psu(&mut self) -> Option<&mut dyn LocalPsuOutput> {
        self.check("accessor");
        Some(self)
    }
    fn reset(&mut self) -> Option<&mut dyn LocalReset> {
        self.check("accessor");
        Some(self)
    }
    fn shutdown(&mut self) -> Option<&mut dyn LocalSafeShutdown> {
        self.check("accessor");
        Some(self)
    }
    fn recover(&mut self) -> Result<()> {
        self.check("recover");
        DeviceBackend::recover(&mut *self.inner.borrow_mut())
    }
    fn close(&mut self) -> Result<()> {
        self.check("close");
        if self.closed {
            return Ok(());
        }
        self.closed = true;
        DeviceBackend::close(&mut *self.inner.borrow_mut())
    }
}

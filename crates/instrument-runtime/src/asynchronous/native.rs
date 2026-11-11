use super::*;
use std::ops::{Deref, DerefMut};
use tokio::sync::{Mutex as AsyncMutex, MutexGuard as AsyncGuard};

tokio::task_local! { static NATIVE_ACTIVE: Vec<usize>; }
#[cfg(test)]
static BEFORE_UNLOCK: Mutex<Option<Box<dyn FnOnce() + Send>>> = Mutex::new(None);
struct Resource(Option<Box<dyn AsyncDeviceBackend>>);
impl Resource {
    fn release(&mut self) -> Result<()> {
        match self.0.take() {
            Some(mut backend) => {
                std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| backend.close()))
                    .unwrap_or_else(|_| {
                        Err(error(
                            ErrorKind::DriverContractViolation,
                            "resource release panicked",
                        ))
                    })
            }
            None => Ok(()),
        }
    }
}
impl Drop for Resource {
    fn drop(&mut self) {
        let _ = self.release();
    }
}
struct NativeInner {
    control: Control,
    backend: AsyncMutex<Option<Resource>>,
    // Lease release and interrupted-close cleanup share this handshake. The async
    // guard is unlocked while holding it, so abort cannot miss the final unlock.
    handoff: Mutex<()>,
    abort: AtomicBool,
    operations: BTreeSet<Operation>,
    evidence: MatchEvidence,
}
struct Lease<'a> {
    inner: &'a NativeInner,
    slot: Option<AsyncGuard<'a, Option<Resource>>>,
    dirty: bool,
}
impl Deref for Lease<'_> {
    type Target = Option<Resource>;
    fn deref(&self) -> &Self::Target {
        self.slot.as_ref().unwrap()
    }
}
impl DerefMut for Lease<'_> {
    fn deref_mut(&mut self) -> &mut Self::Target {
        self.slot.as_mut().unwrap()
    }
}
impl Drop for Lease<'_> {
    fn drop(&mut self) {
        if self.dirty {
            self.inner.control.recovery.store(true, Ordering::Release);
        }
        let _handoff = self.inner.handoff.lock().unwrap_or_else(|p| p.into_inner());
        if self.inner.abort.load(Ordering::Acquire) {
            if let Some(slot) = self.slot.as_mut() {
                drop(slot.take());
            }
        }
        #[cfg(test)]
        if let Some(hook) = BEFORE_UNLOCK.lock().unwrap().take() {
            hook();
        }
        self.inner.control.active.store(false, Ordering::Release);
        drop(self.slot.take()); // unlock BEFORE releasing the cleanup handshake
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;

    struct Backend(Arc<AtomicUsize>);
    impl AsyncDeviceBackend for Backend {
        fn operations(&self) -> BTreeSet<Operation> {
            BTreeSet::new()
        }
        fn close(&mut self) -> Result<()> {
            self.0.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }
    }
    #[test]
    fn interrupted_close_cannot_lose_cleanup_at_final_unlock() {
        let closes = Arc::new(AtomicUsize::new(0));
        let inner = Arc::new(NativeInner {
            control: Control::new(),
            backend: AsyncMutex::new(Some(Resource(Some(Box::new(Backend(closes.clone())))))),
            handoff: Mutex::new(()),
            abort: AtomicBool::new(false),
            operations: BTreeSet::new(),
            evidence: MatchEvidence {
                driver_id: "test.native".into(),
                preference: 0,
                rank: 0,
                explicit: true,
            },
        });
        let (checked, seen) = mpsc::channel();
        let (release, proceed) = mpsc::channel();
        *BEFORE_UNLOCK.lock().unwrap() = Some(Box::new(move || {
            checked.send(()).unwrap();
            proceed.recv().unwrap();
        }));
        let owned = inner.clone();
        let dropping = std::thread::spawn(move || {
            let slot = owned.backend.blocking_lock();
            drop(Lease {
                inner: &owned,
                slot: Some(slot),
                dirty: false,
            });
        });
        seen.recv().unwrap();
        let (attempting, attempted) = mpsc::channel();
        let owned = inner.clone();
        let abort = std::thread::spawn(move || {
            attempting.send(()).unwrap();
            owned.interrupt_close();
        });
        attempted.recv().unwrap();
        release.send(()).unwrap();
        dropping.join().unwrap();
        abort.join().unwrap();
        assert!(inner.backend.try_lock().unwrap().is_none());
        assert_eq!(closes.load(Ordering::SeqCst), 1);
        assert_eq!(inner.control.lifecycle.load(Ordering::Acquire), 2);
    }
}
struct Waiting<'a>(&'a Control);
impl Drop for Waiting<'_> {
    fn drop(&mut self) {
        self.0.waiting.fetch_sub(1, Ordering::AcqRel);
    }
}
impl NativeInner {
    fn id(&self) -> usize {
        self as *const _ as usize
    }
    fn reentry(&self) -> Result<()> {
        if NATIVE_ACTIVE
            .try_with(|ids| ids.contains(&self.id()))
            .unwrap_or(false)
        {
            Err(error(
                ErrorKind::DriverContractViolation,
                "backend reentered its async handle",
            ))
        } else {
            Ok(())
        }
    }
    async fn lease(&self) -> Lease<'_> {
        self.control.waiting.fetch_add(1, Ordering::AcqRel);
        let waiting = Waiting(&self.control);
        let slot = self.backend.lock().await;
        drop(waiting);
        Lease {
            inner: self,
            slot: Some(slot),
            dirty: false,
        }
    }
    fn interrupt_close(&self) {
        let _handoff = self.handoff.lock().unwrap_or_else(|p| p.into_inner());
        self.abort.store(true, Ordering::Release);
        if let Ok(mut slot) = self.backend.try_lock() {
            drop(slot.take());
        }
        self.control.finish_close(Err(error(
            ErrorKind::SessionClosed,
            "executor interrupted close; protocol shutdown unverified",
        )));
    }
}
struct CloseOwner {
    inner: Arc<NativeInner>,
    done: bool,
}
impl Drop for CloseOwner {
    fn drop(&mut self) {
        if !self.done {
            self.inner.interrupt_close();
        }
    }
}

/// Native async-only handle. Tokio must remain alive for asynchronous operations and
/// close. Dropped operations quarantine before unlock; close waiters do not own cleanup.
#[derive(Clone)]
pub struct NativeDeviceHandle {
    inner: Arc<NativeInner>,
}
impl DriverRegistry {
    pub async fn open_native(
        &self,
        candidate: &Candidate,
        selection: &Selection,
        required: &BTreeSet<Operation>,
        ownership: ConnectionOwnership,
    ) -> Result<NativeDeviceHandle> {
        let evidence = self.select(candidate, selection)?;
        let registration = &self.registrations[&evidence.driver_id];
        if !registration.modes.contains(&ExecutionMode::NativeAsync) {
            return Err(error(
                ErrorKind::UnsupportedExecutionMode,
                "native async unavailable",
            ));
        }
        if !required.is_subset(&registration.descriptor.operations) {
            return Err(error(
                ErrorKind::UnsupportedOperation,
                "required operation outside envelope",
            ));
        }
        let context = OpenContext {
            candidate,
            ownership,
            required,
        };
        let mut resource = Resource(Some(registration.factory.open_native(&context).await?));
        let backend = resource.0.as_deref_mut().unwrap();
        let operations = backend.operations();
        let validation = validate_support(
            &operations,
            &registration.descriptor.operations,
            required,
            |o| match o {
                Operation::Catalog(_) => backend.catalog().is_some(),
                Operation::MeasureVoltageDc => backend.dmm().is_some(),
                Operation::SetVoltage | Operation::ReadVoltage | Operation::OutputEnable => {
                    backend.psu().is_some()
                }
                Operation::RemoteSense => backend.remote_sense().is_some(),
                Operation::Reset => backend.reset().is_some(),
                Operation::Shutdown => backend.shutdown().is_some(),
            },
        );
        if let Err(mut failure) = validation {
            if let Err(cleanup) = resource.release() {
                failure.message.push_str(&format!("; cleanup: {cleanup}"));
            }
            return Err(failure);
        }
        Ok(NativeDeviceHandle {
            inner: Arc::new(NativeInner {
                control: Control::new(),
                backend: AsyncMutex::new(Some(resource)),
                handoff: Mutex::new(()),
                abort: AtomicBool::new(false),
                operations,
                evidence,
            }),
        })
    }
}
impl NativeDeviceHandle {
    pub fn evidence(&self) -> &MatchEvidence {
        &self.inner.evidence
    }
    pub fn operations(&self) -> &BTreeSet<Operation> {
        &self.inner.operations
    }
    pub fn recovery_required(&self) -> bool {
        self.inner.control.recovery.load(Ordering::Acquire)
    }
    pub fn closing_requested(&self) -> bool {
        self.inner.control.lifecycle.load(Ordering::Acquire) != 0
    }
    pub fn pending_operations(&self) -> usize {
        self.inner.control.waiting.load(Ordering::Acquire)
    }
    async fn run<T: Send>(
        &self,
        operation: Operation,
        action: impl for<'a> FnOnce(&'a mut dyn AsyncDeviceBackend) -> DriverFuture<'a, T>,
    ) -> Result<T> {
        self.inner.reentry()?;
        self.inner.control.check()?;
        let mut lease = self.inner.lease().await;
        self.inner.control.check()?;
        if self.recovery_required() {
            return Err(error(ErrorKind::SessionNeedsRecovery, "recovery required"));
        }
        if !self.operations().contains(&operation) {
            return Err(error(
                ErrorKind::UnsupportedOperation,
                "operation unavailable",
            ));
        }
        lease.dirty = true;
        self.inner.control.active.store(true, Ordering::Release);
        let mut active = NATIVE_ACTIVE.try_with(Clone::clone).unwrap_or_default();
        active.push(self.inner.id());
        let result = NATIVE_ACTIVE
            .scope(
                active,
                action(lease.as_mut().unwrap().0.as_deref_mut().unwrap()),
            )
            .await;
        lease.dirty = result.as_ref().err().map(uncertain).unwrap_or(false);
        result
    }
    pub async fn measure_voltage_dc(&self, range: Option<f64>) -> Result<Measurement> {
        request_valid(Operation::MeasureVoltageDc, 0, range)?;
        self.run(Operation::MeasureVoltageDc, move |b| {
            Box::pin(async move {
                measurement(
                    b.dmm()
                        .ok_or_else(|| {
                            error(ErrorKind::DriverContractViolation, "DMM accessor removed")
                        })?
                        .measure_voltage_dc(range)
                        .await?,
                )
            })
        })
        .await
    }
    pub async fn set_voltage(&self, channel: u32, volts: f64) -> Result<()> {
        request_valid(Operation::SetVoltage, channel, Some(volts))?;
        self.run(Operation::SetVoltage, move |b| {
            Box::pin(async move {
                b.psu()
                    .ok_or_else(|| {
                        error(ErrorKind::DriverContractViolation, "PSU accessor removed")
                    })?
                    .set_voltage(channel, volts)
                    .await
            })
        })
        .await
    }
    pub async fn read_voltage(&self, channel: u32) -> Result<Measurement> {
        channel_valid(channel)?;
        self.run(Operation::ReadVoltage, move |b| {
            Box::pin(async move {
                measurement(
                    b.psu()
                        .ok_or_else(|| {
                            error(ErrorKind::DriverContractViolation, "PSU accessor removed")
                        })?
                        .read_voltage(channel)
                        .await?,
                )
            })
        })
        .await
    }
    pub async fn output_enable(&self, channel: u32, enabled: bool) -> Result<()> {
        channel_valid(channel)?;
        self.run(Operation::OutputEnable, move |b| {
            Box::pin(async move {
                b.psu()
                    .ok_or_else(|| {
                        error(ErrorKind::DriverContractViolation, "PSU accessor removed")
                    })?
                    .output_enable(channel, enabled)
                    .await
            })
        })
        .await
    }
    pub async fn reset(&self) -> Result<()> {
        self.run(Operation::Reset, |b| {
            Box::pin(async move {
                b.reset()
                    .ok_or_else(|| {
                        error(ErrorKind::DriverContractViolation, "reset accessor removed")
                    })?
                    .reset()
                    .await
            })
        })
        .await
    }
    pub async fn set_sense_mode(&self, channel: u32, mode: SenseMode) -> Result<()> {
        channel_valid(channel)?;
        self.run(Operation::RemoteSense, move |b| {
            Box::pin(async move {
                b.remote_sense()
                    .ok_or_else(|| {
                        error(ErrorKind::DriverContractViolation, "sense accessor removed")
                    })?
                    .set_mode(channel, mode)
                    .await
            })
        })
        .await
    }
    pub async fn recover(&self) -> Result<()> {
        self.inner.reentry()?;
        self.inner.control.check()?;
        let mut lease = self.inner.lease().await;
        self.inner.control.check()?;
        if !self.recovery_required() {
            return Ok(());
        }
        lease.dirty = true;
        let mut active = NATIVE_ACTIVE.try_with(Clone::clone).unwrap_or_default();
        active.push(self.inner.id());
        NATIVE_ACTIVE
            .scope(
                active,
                lease.as_mut().unwrap().0.as_deref_mut().unwrap().recover(),
            )
            .await?;
        self.inner.control.recovery.store(false, Ordering::Release);
        lease.dirty = false;
        Ok(())
    }
    pub async fn close(&self, require_shutdown: bool) -> Result<()> {
        self.inner.reentry()?;
        let runtime = tokio::runtime::Handle::try_current().map_err(|_| {
            error(
                ErrorKind::UnsupportedExecutionMode,
                "native close requires Tokio",
            )
        })?;
        if self.inner.control.begin_close(require_shutdown) {
            let owner = CloseOwner {
                inner: self.inner.clone(),
                done: false,
            };
            runtime.spawn(async move {
                let mut owner = owner;
                let mut lease = owner.inner.lease().await;
                let mut failures = Vec::new();
                if let Some(mut resource) = lease.take() {
                    let backend = resource.0.as_deref_mut().unwrap();
                    let mut active = NATIVE_ACTIVE.try_with(Clone::clone).unwrap_or_default();
                    active.push(owner.inner.id());
                    NATIVE_ACTIVE
                        .scope(active, async {
                            if require_shutdown && !shutdown_supported(&owner.inner.operations) {
                                failures.push("shutdown disabled".into());
                            } else if require_shutdown {
                                let recovered =
                                    if owner.inner.control.recovery.load(Ordering::Acquire) {
                                        match backend.recover().await {
                                            Ok(()) => true,
                                            Err(e) => {
                                                failures.push(e.to_string());
                                                false
                                            }
                                        }
                                    } else {
                                        true
                                    };
                                if recovered {
                                    if !owner.inner.operations.contains(&Operation::Shutdown) {
                                        if let Err(e) = async {
                                            backend
                                                .catalog()
                                                .ok_or_else(|| {
                                                    error(
                                                        ErrorKind::DriverContractViolation,
                                                        "shutdown catalog removed",
                                                    )
                                                })?
                                                .invoke_async(CatalogRequest::Shutdown)
                                                .await?
                                                .into_unit()
                                        }
                                        .await
                                        {
                                            failures.push(e.to_string());
                                        }
                                    } else {
                                        match backend.shutdown() {
                                            Some(shutdown) => {
                                                if let Err(e) = shutdown.shutdown().await {
                                                    failures.push(e.to_string());
                                                }
                                            }
                                            None => {
                                                failures.push("shutdown accessor removed".into())
                                            }
                                        }
                                    }
                                }
                            }
                        })
                        .await;
                    if let Err(e) = resource.release() {
                        failures.push(e.to_string());
                    }
                }
                drop(lease);
                owner.inner.control.finish_close(if failures.is_empty() {
                    Ok(())
                } else {
                    Err(error(ErrorKind::Shutdown, failures.join("; ")))
                });
                owner.done = true;
            });
        }
        self.inner.control.wait_close(require_shutdown).await
    }
}

impl instrument_api::catalog::AsyncCatalogExecutor for NativeDeviceHandle {
    fn invoke_async(&self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply> {
        Box::pin(async move {
            request.validate()?;
            self.run(Operation::Catalog(request.operation()), move |b| {
                Box::pin(async move {
                    let reply = b
                        .catalog()
                        .ok_or_else(|| {
                            error(
                                ErrorKind::DriverContractViolation,
                                "catalog accessor removed",
                            )
                        })?
                        .invoke_async(request.clone())
                        .await?;
                    reply.validate(&request)?;
                    Ok(reply)
                })
            })
            .await
        })
    }
}

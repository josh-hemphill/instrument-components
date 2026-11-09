use super::*;
use std::sync::mpsc;
use std::thread::ThreadId;
use tokio::sync::oneshot;

impl CatalogExecutor for WorkerDeviceHandle {
    fn invoke(&self, request: CatalogRequest) -> Result<CatalogReply> {
        request.validate()?;
        match self.sync(Action::Catalog(Box::new(request)))? {
            Value::Catalog(v) => Ok(v),
            _ => Err(error(
                ErrorKind::DriverContractViolation,
                "invalid worker reply",
            )),
        }
    }
}
impl instrument_api::catalog::AsyncCatalogExecutor for WorkerDeviceHandle {
    fn invoke_async(&self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply> {
        Box::pin(async move {
            request.validate()?;
            match self
                .asynchronous(Action::Catalog(Box::new(request)))
                .await?
            {
                Value::Catalog(v) => Ok(v),
                _ => Err(error(
                    ErrorKind::DriverContractViolation,
                    "invalid worker reply",
                )),
            }
        })
    }
}

#[cfg(test)]
static BEFORE_DISPATCH: Mutex<Option<Box<dyn FnOnce() + Send>>> = Mutex::new(None);

#[derive(Clone)]
enum Action {
    Catalog(Box<CatalogRequest>),
    Measure(Option<f64>),
    Set(u32, f64),
    Read(u32),
    Output(u32, bool),
    Sense(u32, SenseMode),
    Reset,
    Recover,
}
impl Action {
    fn operation(&self) -> Option<Operation> {
        Some(match self {
            Self::Catalog(r) => Operation::Catalog(r.operation()),
            Self::Measure(_) => Operation::MeasureVoltageDc,
            Self::Set(..) => Operation::SetVoltage,
            Self::Read(_) => Operation::ReadVoltage,
            Self::Output(..) => Operation::OutputEnable,
            Self::Sense(..) => Operation::RemoteSense,
            Self::Reset => Operation::Reset,
            Self::Recover => return None,
        })
    }
}
enum Value {
    Catalog(CatalogReply),
    Unit,
    Measurement(Measurement),
}
enum Reply {
    Sync(mpsc::Sender<Result<Value>>),
    Async(oneshot::Sender<Result<Value>>),
}
impl Reply {
    fn abandoned(&self) -> bool {
        matches!(self, Self::Async(tx) if tx.is_closed())
    }
    fn send(self, value: Result<Value>) -> bool {
        match self {
            Self::Sync(tx) => tx.send(value).is_ok(),
            Self::Async(tx) => tx.send(value).is_ok(),
        }
    }
}
#[derive(Clone, Copy, PartialEq, Eq)]
enum Stage {
    Queued,
    Running,
    AbandonedQueued,
    AbandonedRunning,
    Completed,
}
struct Job {
    action: Action,
    reply: Reply,
    stage: Arc<Mutex<Stage>>,
}
enum Command {
    Job(Job),
    Close(bool),
}
struct WorkerControl {
    common: Control,
    thread: Mutex<Option<ThreadId>>,
}
impl WorkerControl {
    fn check(&self) -> Result<()> {
        if *self.thread.lock().unwrap_or_else(|p| p.into_inner())
            == Some(std::thread::current().id())
        {
            return Err(error(
                ErrorKind::DriverContractViolation,
                "worker backend reentered its handle",
            ));
        }
        self.common.check()
    }
}
struct Client {
    tx: mpsc::Sender<Command>,
    control: Arc<WorkerControl>,
    operations: BTreeSet<Operation>,
    evidence: MatchEvidence,
}
impl Drop for Client {
    fn drop(&mut self) {
        request_close(&self.tx, &self.control, false);
    }
}
fn request_close(tx: &mpsc::Sender<Command>, control: &WorkerControl, shutdown: bool) {
    if control.common.begin_close(shutdown) {
        control.common.waiting.fetch_add(1, Ordering::AcqRel);
        if tx.send(Command::Close(shutdown)).is_err() {
            control.common.waiting.fetch_sub(1, Ordering::AcqRel);
            control.common.finish_close(Err(error(
                ErrorKind::DriverContractViolation,
                "worker disconnected",
            )));
        }
    }
}
struct OpenOwner {
    tx: mpsc::Sender<Command>,
    control: Arc<WorkerControl>,
    delivered: bool,
}
impl Drop for OpenOwner {
    fn drop(&mut self) {
        if !self.delivered {
            request_close(&self.tx, &self.control, false);
        }
    }
}
struct ThreadOwner(Arc<WorkerControl>);
impl Drop for ThreadOwner {
    fn drop(&mut self) {
        self.0.common.waiting.store(0, Ordering::Release);
        self.0.common.active.store(false, Ordering::Release);
        self.0.common.draining.store(false, Ordering::Release);
        if self.0.common.lifecycle.load(Ordering::Acquire) != 2 {
            self.0.common.finish_close(Err(error(
                ErrorKind::DriverContractViolation,
                "worker terminated before close",
            )));
        }
    }
}
struct LocalResource(Option<Box<dyn WorkerBackend>>);
impl LocalResource {
    fn release(&mut self) -> Result<()> {
        match self.0.take() {
            Some(mut backend) => {
                std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| backend.close()))
                    .unwrap_or_else(|_| {
                        Err(error(
                            ErrorKind::DriverContractViolation,
                            "worker resource release panicked",
                        ))
                    })
            }
            None => Ok(()),
        }
    }
}
impl Drop for LocalResource {
    fn drop(&mut self) {
        let _ = self.release();
    }
}
struct Abandon {
    stage: Arc<Mutex<Stage>>,
    control: Arc<WorkerControl>,
    done: bool,
}
impl Drop for Abandon {
    fn drop(&mut self) {
        if self.done {
            return;
        }
        let mut stage = self.stage.lock().unwrap_or_else(|p| p.into_inner());
        match *stage {
            Stage::Queued => *stage = Stage::AbandonedQueued,
            Stage::Running => {
                *stage = Stage::AbandonedRunning;
                if self.control.common.lifecycle.load(Ordering::Acquire) != 2 {
                    self.control.common.draining.store(true, Ordering::Release);
                }
            }
            _ => (),
        }
    }
}

/// Both frontends dispatch owned requests to one dedicated thread. A canceled waiter
/// does not stop an SDK call; close may remain pending until that call actually returns.
#[derive(Clone)]
pub struct WorkerDeviceHandle {
    client: Arc<Client>,
}
impl DriverRegistry {
    pub async fn open_worker(
        &self,
        candidate: &Candidate,
        selection: &Selection,
        required: &BTreeSet<Operation>,
        ownership: ConnectionOwnership,
    ) -> Result<WorkerDeviceHandle> {
        let evidence = self.select(candidate, selection)?;
        let registration = &self.registrations[&evidence.driver_id];
        if !registration.modes.contains(&ExecutionMode::WorkerAsync) {
            return Err(error(
                ErrorKind::UnsupportedExecutionMode,
                "worker mode unavailable",
            ));
        }
        if !required.is_subset(&registration.descriptor.operations) {
            return Err(error(
                ErrorKind::UnsupportedOperation,
                "required operation outside envelope",
            ));
        }
        let factory = registration.factory.clone();
        let envelope = registration.descriptor.operations.clone();
        let candidate = candidate.clone();
        let required = required.clone();
        let control = Arc::new(WorkerControl {
            common: Control::new(),
            thread: Mutex::new(None),
        });
        let (tx, rx) = mpsc::channel();
        let (opened, ready) = oneshot::channel();
        let mut owner = OpenOwner {
            tx: tx.clone(),
            control: control.clone(),
            delivered: false,
        };
        let state = control.clone();
        std::thread::Builder::new()
            .name("instrument-driver".into())
            .spawn(move || {
                let _thread_owner = ThreadOwner(state.clone());
                *state.thread.lock().unwrap() = Some(std::thread::current().id());
                if state.common.lifecycle.load(Ordering::Acquire) != 0 {
                    let _ = opened.send(Err(error(ErrorKind::SessionClosing, "open abandoned")));
                    state.common.finish_close(Ok(()));
                    return;
                }
                let outcome = factory.open_worker(&OpenContext {
                    candidate: &candidate,
                    ownership,
                });
                let mut resource = match outcome {
                    Ok(backend) => LocalResource(Some(backend)),
                    Err(e) => {
                        let _ = opened.send(Err(e.clone()));
                        state.common.finish_close(Err(e));
                        return;
                    }
                };
                let backend = resource.0.as_deref_mut().unwrap();
                let operations = backend.operations();
                let validation = validate_support(&operations, &envelope, &required, |o| match o {
                    Operation::Catalog(_) => backend.catalog().is_some(),
                    Operation::MeasureVoltageDc => backend.dmm().is_some(),
                    Operation::SetVoltage | Operation::ReadVoltage | Operation::OutputEnable => {
                        backend.psu().is_some()
                    }
                    Operation::RemoteSense => backend.remote_sense().is_some(),
                    Operation::Reset => backend.reset().is_some(),
                    Operation::Shutdown => backend.shutdown().is_some(),
                });
                if let Err(mut e) = validation {
                    if let Err(cleanup) = resource.release() {
                        e.message.push_str(&format!("; cleanup: {cleanup}"));
                    }
                    let _ = opened.send(Err(e.clone()));
                    state.common.finish_close(Err(e));
                    return;
                }
                if state.common.lifecycle.load(Ordering::Acquire) != 0
                    || opened.send(Ok(operations.clone())).is_err()
                {
                    let result = resource.release();
                    state.common.finish_close(result);
                    return;
                }
                while let Ok(command) = rx.recv() {
                    state.common.waiting.fetch_sub(1, Ordering::AcqRel);
                    match command {
                        Command::Close(shutdown) => {
                            let mut failures = Vec::new();
                            let backend = resource.0.as_deref_mut().unwrap();
                            if shutdown && !shutdown_supported(&operations) {
                                failures.push("shutdown disabled".into());
                            } else if shutdown {
                                let recovered = if state.common.recovery.load(Ordering::Acquire) {
                                    match backend.recover() {
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
                                    if !operations.contains(&Operation::Shutdown) {
                                        if let Err(e) = (|| {
                                            backend
                                                .catalog()
                                                .ok_or_else(|| {
                                                    error(
                                                        ErrorKind::DriverContractViolation,
                                                        "shutdown catalog removed",
                                                    )
                                                })?
                                                .invoke(CatalogRequest::Shutdown)?
                                                .into_unit()
                                        })() {
                                            failures.push(e.to_string());
                                        }
                                    } else {
                                        match backend.shutdown() {
                                            Some(s) => {
                                                if let Err(e) = s.shutdown() {
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
                            if let Err(e) = resource.release() {
                                failures.push(e.to_string());
                            }
                            state.common.finish_close(if failures.is_empty() {
                                Ok(())
                            } else {
                                Err(error(ErrorKind::Shutdown, failures.join("; ")))
                            });
                            return;
                        }
                        Command::Job(job) => execute_job(
                            job,
                            resource.0.as_deref_mut().unwrap(),
                            &operations,
                            &state,
                        ),
                    }
                }
                let result = resource.release();
                state.common.finish_close(result);
            })
            .map_err(|e| {
                error(
                    ErrorKind::DriverContractViolation,
                    format!("worker startup: {e}"),
                )
            })?;
        let operations = ready.await.map_err(|_| {
            error(
                ErrorKind::DriverContractViolation,
                "worker open disconnected",
            )
        })??;
        owner.delivered = true;
        Ok(WorkerDeviceHandle {
            client: Arc::new(Client {
                tx,
                control,
                operations,
                evidence,
            }),
        })
    }
}
// This lock is the dispatch/cancellation linearization point for every SDK call,
// including recovery. After dispatch wins, the worker retains ownership to completion.
fn dispatch_allowed(stage: &Mutex<Stage>) -> bool {
    *stage.lock().unwrap_or_else(|p| p.into_inner()) != Stage::AbandonedRunning
}
fn execute_job(
    job: Job,
    backend: &mut dyn WorkerBackend,
    operations: &BTreeSet<Operation>,
    state: &WorkerControl,
) {
    {
        let mut stage = job.stage.lock().unwrap_or_else(|p| p.into_inner());
        if *stage == Stage::AbandonedQueued || job.reply.abandoned() {
            *stage = Stage::Completed;
            return;
        }
        *stage = Stage::Running;
    }
    state.common.active.store(true, Ordering::Release);
    let mut dispatched = false;
    let result = (|| {
        state.common.check()?;
        #[cfg(test)]
        if let Some(hook) = BEFORE_DISPATCH.lock().unwrap().take() {
            hook();
        }
        if matches!(job.action, Action::Recover) {
            if state.common.recovery.load(Ordering::Acquire) && dispatch_allowed(&job.stage) {
                dispatched = true;
                backend.recover()?;
                state.common.recovery.store(false, Ordering::Release);
            }
            return Ok(Value::Unit);
        }
        if state.common.recovery.load(Ordering::Acquire) {
            return Err(error(ErrorKind::SessionNeedsRecovery, "recovery required"));
        }
        if !operations.contains(&job.action.operation().unwrap()) {
            return Err(error(
                ErrorKind::UnsupportedOperation,
                "operation unavailable",
            ));
        }
        if !dispatch_allowed(&job.stage) {
            return Ok(Value::Unit);
        }
        dispatched = true;
        let missing = || {
            error(
                ErrorKind::DriverContractViolation,
                "capability accessor removed",
            )
        };
        match job.action {
            Action::Catalog(request) => {
                let reply = backend
                    .catalog()
                    .ok_or_else(missing)?
                    .invoke((*request).clone())?;
                reply.validate(&request)?;
                Ok(Value::Catalog(reply))
            }
            Action::Measure(range) => backend
                .dmm()
                .ok_or_else(missing)?
                .measure_voltage_dc(range)
                .and_then(measurement)
                .map(Value::Measurement),
            Action::Set(channel, value) => backend
                .psu()
                .ok_or_else(missing)?
                .set_voltage(channel, value)
                .map(|_| Value::Unit),
            Action::Read(channel) => backend
                .psu()
                .ok_or_else(missing)?
                .read_voltage(channel)
                .and_then(measurement)
                .map(Value::Measurement),
            Action::Output(channel, value) => backend
                .psu()
                .ok_or_else(missing)?
                .output_enable(channel, value)
                .map(|_| Value::Unit),
            Action::Sense(channel, mode) => backend
                .remote_sense()
                .ok_or_else(missing)?
                .set_mode(channel, mode)
                .map(|_| Value::Unit),
            Action::Reset => backend
                .reset()
                .ok_or_else(missing)?
                .reset()
                .map(|_| Value::Unit),
            Action::Recover => unreachable!(),
        }
    })();
    let mut stage = job.stage.lock().unwrap_or_else(|p| p.into_inner());
    // Completed results are committed before delivery. Abandoning an already
    // completed result does not make protocol state uncertain.
    // Preflight lifecycle errors never dispatch. A lifecycle error from a provider
    // after dispatch is uncertain, just like any other dispatched provider failure.
    if dispatched
        && (*stage == Stage::AbandonedRunning
            || result.as_ref().err().map(uncertain).unwrap_or(false))
    {
        state.common.recovery.store(true, Ordering::Release);
    }
    if !job.reply.send(result) && dispatched {
        state.common.recovery.store(true, Ordering::Release);
    }
    *stage = Stage::Completed;
    state.common.active.store(false, Ordering::Release);
    state.common.draining.store(false, Ordering::Release);
}
impl WorkerDeviceHandle {
    pub fn evidence(&self) -> &MatchEvidence {
        &self.client.evidence
    }
    pub fn operations(&self) -> &BTreeSet<Operation> {
        &self.client.operations
    }
    pub fn recovery_required(&self) -> bool {
        self.client.control.common.recovery.load(Ordering::Acquire)
    }
    pub fn draining(&self) -> bool {
        self.client.control.common.draining.load(Ordering::Acquire)
    }
    pub fn closing_requested(&self) -> bool {
        self.client.control.common.lifecycle.load(Ordering::Acquire) != 0
    }
    pub fn pending_operations(&self) -> usize {
        self.client.control.common.waiting.load(Ordering::Acquire)
    }
    fn enqueue(&self, action: Action, reply: Reply, stage: Arc<Mutex<Stage>>) -> Result<()> {
        self.client.control.check()?;
        self.client
            .control
            .common
            .waiting
            .fetch_add(1, Ordering::AcqRel);
        if self
            .client
            .tx
            .send(Command::Job(Job {
                action,
                reply,
                stage,
            }))
            .is_err()
        {
            self.client
                .control
                .common
                .waiting
                .fetch_sub(1, Ordering::AcqRel);
            return Err(error(ErrorKind::SessionClosed, "worker disconnected"));
        }
        Ok(())
    }
    fn sync(&self, action: Action) -> Result<Value> {
        let (tx, rx) = mpsc::channel();
        self.enqueue(action, Reply::Sync(tx), Arc::new(Mutex::new(Stage::Queued)))?;
        rx.recv()
            .map_err(|_| error(ErrorKind::DriverContractViolation, "worker terminated"))?
    }
    async fn asynchronous(&self, action: Action) -> Result<Value> {
        let (tx, rx) = oneshot::channel();
        let stage = Arc::new(Mutex::new(Stage::Queued));
        let mut owner = Abandon {
            stage: stage.clone(),
            control: self.client.control.clone(),
            done: false,
        };
        self.enqueue(action, Reply::Async(tx), stage)?;
        let result = rx
            .await
            .map_err(|_| error(ErrorKind::DriverContractViolation, "worker terminated"))?;
        owner.done = true;
        result
    }
    pub fn measure_voltage_dc(&self, range: Option<f64>) -> Result<Measurement> {
        request_valid(Operation::MeasureVoltageDc, 0, range)?;
        match self.sync(Action::Measure(range))? {
            Value::Measurement(v) => Ok(v),
            _ => unreachable!(),
        }
    }
    pub async fn measure_voltage_dc_async(&self, range: Option<f64>) -> Result<Measurement> {
        request_valid(Operation::MeasureVoltageDc, 0, range)?;
        match self.asynchronous(Action::Measure(range)).await? {
            Value::Measurement(v) => Ok(v),
            _ => unreachable!(),
        }
    }
    pub fn set_voltage(&self, channel: u32, volts: f64) -> Result<()> {
        request_valid(Operation::SetVoltage, channel, Some(volts))?;
        self.sync(Action::Set(channel, volts)).map(|_| ())
    }
    pub async fn set_voltage_async(&self, channel: u32, volts: f64) -> Result<()> {
        request_valid(Operation::SetVoltage, channel, Some(volts))?;
        self.asynchronous(Action::Set(channel, volts))
            .await
            .map(|_| ())
    }
    pub fn read_voltage(&self, channel: u32) -> Result<Measurement> {
        channel_valid(channel)?;
        match self.sync(Action::Read(channel))? {
            Value::Measurement(v) => Ok(v),
            _ => unreachable!(),
        }
    }
    pub async fn read_voltage_async(&self, channel: u32) -> Result<Measurement> {
        channel_valid(channel)?;
        match self.asynchronous(Action::Read(channel)).await? {
            Value::Measurement(v) => Ok(v),
            _ => unreachable!(),
        }
    }
    pub fn output_enable(&self, channel: u32, enabled: bool) -> Result<()> {
        channel_valid(channel)?;
        self.sync(Action::Output(channel, enabled)).map(|_| ())
    }
    pub async fn output_enable_async(&self, channel: u32, enabled: bool) -> Result<()> {
        channel_valid(channel)?;
        self.asynchronous(Action::Output(channel, enabled))
            .await
            .map(|_| ())
    }
    pub async fn reset_async(&self) -> Result<()> {
        self.asynchronous(Action::Reset).await.map(|_| ())
    }
    pub fn reset(&self) -> Result<()> {
        self.sync(Action::Reset).map(|_| ())
    }
    pub async fn set_sense_mode_async(&self, channel: u32, mode: SenseMode) -> Result<()> {
        channel_valid(channel)?;
        self.asynchronous(Action::Sense(channel, mode))
            .await
            .map(|_| ())
    }
    pub fn recover_sync(&self) -> Result<()> {
        self.sync(Action::Recover).map(|_| ())
    }
    pub fn set_sense_mode(&self, channel: u32, mode: SenseMode) -> Result<()> {
        channel_valid(channel)?;
        self.sync(Action::Sense(channel, mode)).map(|_| ())
    }
    pub fn close_sync(&self, shutdown: bool) -> Result<()> {
        if *self.client.control.thread.lock().unwrap() == Some(std::thread::current().id()) {
            return Err(error(ErrorKind::DriverContractViolation, "worker reentry"));
        }
        request_close(&self.client.tx, &self.client.control, shutdown);
        self.client.control.common.wait_close_sync(shutdown)
    }
    pub async fn recover(&self) -> Result<()> {
        self.asynchronous(Action::Recover).await.map(|_| ())
    }
    pub async fn close(&self, shutdown: bool) -> Result<()> {
        if *self.client.control.thread.lock().unwrap() == Some(std::thread::current().id()) {
            return Err(error(ErrorKind::DriverContractViolation, "worker reentry"));
        }
        request_close(&self.client.tx, &self.client.control, shutdown);
        self.client.control.common.wait_close(shutdown).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    static SERIAL: Mutex<()> = Mutex::new(());
    struct Backend(Arc<AtomicUsize>);
    impl WorkerBackend for Backend {
        fn operations(&self) -> BTreeSet<Operation> {
            BTreeSet::new()
        }
        fn recover(&mut self) -> Result<()> {
            self.0.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }
        fn close(&mut self) -> Result<()> {
            Ok(())
        }
    }
    struct BlockingBackend {
        calls: Arc<AtomicUsize>,
        drops: Arc<AtomicUsize>,
        started: mpsc::Sender<()>,
        release: mpsc::Receiver<()>,
    }
    impl Drop for BlockingBackend {
        fn drop(&mut self) {
            self.drops.fetch_add(1, Ordering::SeqCst);
        }
    }
    impl WorkerBackend for BlockingBackend {
        fn operations(&self) -> BTreeSet<Operation> {
            BTreeSet::new()
        }
        fn recover(&mut self) -> Result<()> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            self.started.send(()).unwrap();
            self.release
                .recv_timeout(std::time::Duration::from_secs(5))
                .unwrap();
            Ok(())
        }
        fn close(&mut self) -> Result<()> {
            Ok(())
        }
    }
    #[test]
    fn cancellation_after_recovery_dispatch_retains_backend_and_quarantines() {
        let _serial = SERIAL.lock().unwrap();
        let calls = Arc::new(AtomicUsize::new(0));
        let drops = Arc::new(AtomicUsize::new(0));
        let state = Arc::new(WorkerControl {
            common: Control::new(),
            thread: Mutex::new(None),
        });
        state.common.recovery.store(true, Ordering::Release);
        let stage = Arc::new(Mutex::new(Stage::Queued));
        let (reply, receiver) = oneshot::channel();
        let (started, seen) = mpsc::channel();
        let (release, proceed) = mpsc::channel();
        let owned = state.clone();
        let job_stage = stage.clone();
        let counter = calls.clone();
        let disposal = drops.clone();
        let worker = std::thread::spawn(move || {
            execute_job(
                Job {
                    action: Action::Recover,
                    reply: Reply::Async(reply),
                    stage: job_stage,
                },
                &mut BlockingBackend {
                    calls: counter,
                    drops: disposal,
                    started,
                    release: proceed,
                },
                &BTreeSet::new(),
                &owned,
            );
        });
        seen.recv_timeout(std::time::Duration::from_secs(5))
            .unwrap();
        drop(Abandon {
            stage,
            control: state.clone(),
            done: false,
        });
        drop(receiver);
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        assert_eq!(drops.load(Ordering::SeqCst), 0);
        assert!(state.common.draining.load(Ordering::Acquire));
        release.send(()).unwrap();
        worker.join().unwrap();
        assert_eq!(drops.load(Ordering::SeqCst), 1);
        assert!(state.common.recovery.load(Ordering::Acquire));
        assert!(!state.common.draining.load(Ordering::Acquire));
    }
    #[test]
    fn cancellation_before_recovery_dispatch_does_not_call_provider() {
        let _serial = SERIAL.lock().unwrap();
        let calls = Arc::new(AtomicUsize::new(0));
        let state = Arc::new(WorkerControl {
            common: Control::new(),
            thread: Mutex::new(None),
        });
        state.common.recovery.store(true, Ordering::Release);
        let stage = Arc::new(Mutex::new(Stage::Queued));
        let (reply, receiver) = oneshot::channel();
        let (checked, seen) = mpsc::channel();
        let (release, proceed) = mpsc::channel();
        *BEFORE_DISPATCH.lock().unwrap() = Some(Box::new(move || {
            checked.send(()).unwrap();
            proceed.recv().unwrap();
        }));
        let owned = state.clone();
        let job_stage = stage.clone();
        let counter = calls.clone();
        let worker = std::thread::spawn(move || {
            execute_job(
                Job {
                    action: Action::Recover,
                    reply: Reply::Async(reply),
                    stage: job_stage,
                },
                &mut Backend(counter),
                &BTreeSet::new(),
                &owned,
            );
        });
        seen.recv().unwrap();
        drop(Abandon {
            stage,
            control: state.clone(),
            done: false,
        });
        drop(receiver);
        release.send(()).unwrap();
        worker.join().unwrap();
        assert_eq!(calls.load(Ordering::SeqCst), 0);
        assert!(state.common.recovery.load(Ordering::Acquire));
        assert!(!state.common.draining.load(Ordering::Acquire));
    }
}

use super::*;
use std::sync::atomic::AtomicU8;
use tokio::sync::Notify;

mod native;
mod worker;
pub use native::*;
pub use worker::*;

struct CloseRecord {
    shutdown: bool,
    result: Option<Result<()>>,
}
struct Control {
    lifecycle: AtomicU8,
    recovery: AtomicBool,
    active: AtomicBool,
    draining: AtomicBool,
    waiting: AtomicUsize,
    close: Mutex<Option<CloseRecord>>,
    changed: Notify,
    sync_changed: std::sync::Condvar,
}
impl Control {
    fn new() -> Self {
        Self {
            lifecycle: AtomicU8::new(0),
            recovery: AtomicBool::new(false),
            active: AtomicBool::new(false),
            draining: AtomicBool::new(false),
            waiting: AtomicUsize::new(0),
            close: Mutex::new(None),
            changed: Notify::new(),
            sync_changed: std::sync::Condvar::new(),
        }
    }
    fn check(&self) -> Result<()> {
        match self.lifecycle.load(Ordering::Acquire) {
            1 => Err(error(ErrorKind::SessionClosing, "close requested")),
            2 => Err(error(ErrorKind::SessionClosed, "session closed")),
            _ => Ok(()),
        }
    }
    fn begin_close(&self, shutdown: bool) -> bool {
        let mut state = self.close.lock().unwrap_or_else(|p| p.into_inner());
        if state.is_some() {
            return false;
        }
        *state = Some(CloseRecord {
            shutdown,
            result: None,
        });
        self.lifecycle.store(1, Ordering::Release);
        true
    }
    fn finish_close(&self, result: Result<()>) {
        let mut state = self.close.lock().unwrap_or_else(|p| p.into_inner());
        let record = state.get_or_insert(CloseRecord {
            shutdown: false,
            result: None,
        });
        if record.result.is_none() {
            record.result = Some(result);
        }
        self.lifecycle.store(2, Ordering::Release);
        self.changed.notify_waiters();
        self.sync_changed.notify_all();
    }
    fn wait_close_sync(&self, require_shutdown: bool) -> Result<()> {
        let mut state = self.close.lock().unwrap_or_else(|p| p.into_inner());
        loop {
            if let Some(record) = state.as_ref() {
                if let Some(result) = &record.result {
                    result.clone()?;
                    if require_shutdown && !record.shutdown {
                        return Err(error(
                            ErrorKind::Shutdown,
                            "first close did not request shutdown",
                        ));
                    }
                    return Ok(());
                }
            }
            state = self
                .sync_changed
                .wait(state)
                .unwrap_or_else(|p| p.into_inner());
        }
    }
    async fn wait_close(&self, require_shutdown: bool) -> Result<()> {
        loop {
            let changed = self.changed.notified();
            tokio::pin!(changed);
            changed.as_mut().enable();
            {
                let state = self.close.lock().unwrap_or_else(|p| p.into_inner());
                if let Some(record) = state.as_ref() {
                    if let Some(result) = &record.result {
                        result.clone()?;
                        if require_shutdown && !record.shutdown {
                            return Err(error(
                                ErrorKind::Shutdown,
                                "first close did not request shutdown",
                            ));
                        }
                        return Ok(());
                    }
                }
            }
            changed.await;
        }
    }
}
fn uncertain(e: &DriverError) -> bool {
    !matches!(
        e.kind,
        ErrorKind::InvalidArgument | ErrorKind::UnsupportedOperation
    )
}
fn validate_support(
    actual: &BTreeSet<Operation>,
    envelope: &BTreeSet<Operation>,
    required: &BTreeSet<Operation>,
    mut accessor: impl FnMut(Operation) -> bool,
) -> Result<()> {
    if !actual.is_subset(envelope) || actual.iter().any(|o| !accessor(*o)) {
        return Err(error(
            ErrorKind::DriverContractViolation,
            "invalid opened capabilities",
        ));
    }
    if !required.is_subset(actual) {
        return Err(error(
            ErrorKind::UnsupportedOperation,
            "missing required capabilities",
        ));
    }
    Ok(())
}
fn request_valid(operation: Operation, channel: u32, value: Option<f64>) -> Result<()> {
    if matches!(
        operation,
        Operation::SetVoltage
            | Operation::ReadVoltage
            | Operation::OutputEnable
            | Operation::RemoteSense
    ) {
        channel_valid(channel)?;
    }
    if value
        .map(|v| !v.is_finite() || (operation == Operation::MeasureVoltageDc && v <= 0.0))
        .unwrap_or(false)
    {
        return Err(error(ErrorKind::InvalidArgument, "invalid voltage/range"));
    }
    Ok(())
}

use super::*;
use std::{future::Future, pin::Pin};

pub type DriverFuture<'a, T> = Pin<Box<dyn Future<Output = Result<T>> + Send + 'a>>;

pub trait AsyncDmmMeasurement: Send {
    fn measure_voltage_dc(&mut self, range: Option<f64>) -> DriverFuture<'_, Measurement>;
}
pub trait AsyncPsuOutput: Send {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> DriverFuture<'_, ()>;
    fn read_voltage(&mut self, channel: u32) -> DriverFuture<'_, Measurement>;
    fn output_enable(&mut self, channel: u32, enabled: bool) -> DriverFuture<'_, ()>;
}
pub trait AsyncRemoteSense: Send {
    fn set_mode(&mut self, channel: u32, mode: SenseMode) -> DriverFuture<'_, ()>;
}
pub trait AsyncReset: Send {
    fn reset(&mut self) -> DriverFuture<'_, ()>;
}
pub trait AsyncSafeShutdown: Send {
    fn shutdown(&mut self) -> DriverFuture<'_, ()>;
}

/// Native futures must stop ALL host accesses when dropped; uncertain device state
/// is quarantined by the runtime. Use a worker for calls that outlive cancellation.
/// Resource-only close is synchronous/nonblocking and must preserve borrowed I/O.
/// An abandoned open future must release everything it acquired (normally via RAII).
pub trait AsyncDeviceBackend: Send {
    fn catalog(&mut self) -> Option<&mut dyn crate::catalog::AsyncCatalogBackend> {
        None
    }
    fn operations(&self) -> BTreeSet<Operation>;
    fn dmm(&mut self) -> Option<&mut dyn AsyncDmmMeasurement> {
        None
    }
    fn psu(&mut self) -> Option<&mut dyn AsyncPsuOutput> {
        None
    }
    fn remote_sense(&mut self) -> Option<&mut dyn AsyncRemoteSense> {
        None
    }
    fn reset(&mut self) -> Option<&mut dyn AsyncReset> {
        None
    }
    fn shutdown(&mut self) -> Option<&mut dyn AsyncSafeShutdown> {
        None
    }
    fn recover(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async {
            Err(DriverError::new(
                ErrorKind::SessionNeedsRecovery,
                "no recovery strategy",
            ))
        })
    }
    fn close(&mut self) -> Result<()>;
}

/// Local capabilities deliberately do not require Send. They never cross a worker boundary.
pub trait LocalDmmMeasurement {
    fn measure_voltage_dc(&mut self, range: Option<f64>) -> Result<Measurement>;
}
pub trait LocalPsuOutput {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> Result<()>;
    fn read_voltage(&mut self, channel: u32) -> Result<Measurement>;
    fn output_enable(&mut self, channel: u32, enabled: bool) -> Result<()>;
}
pub trait LocalRemoteSense {
    fn set_mode(&mut self, channel: u32, mode: SenseMode) -> Result<()>;
}
pub trait LocalReset {
    fn reset(&mut self) -> Result<()>;
}
pub trait LocalSafeShutdown {
    fn shutdown(&mut self) -> Result<()>;
}

/// All construction, invocation and destruction stay on the owning thread, including
/// failure/abandonment. Requests/results contain owned Send data, never this backend.
pub trait WorkerBackend {
    fn catalog(&mut self) -> Option<&mut dyn crate::catalog::LocalCatalogBackend> {
        None
    }
    fn operations(&self) -> BTreeSet<Operation>;
    fn dmm(&mut self) -> Option<&mut dyn LocalDmmMeasurement> {
        None
    }
    fn psu(&mut self) -> Option<&mut dyn LocalPsuOutput> {
        None
    }
    fn remote_sense(&mut self) -> Option<&mut dyn LocalRemoteSense> {
        None
    }
    fn reset(&mut self) -> Option<&mut dyn LocalReset> {
        None
    }
    fn shutdown(&mut self) -> Option<&mut dyn LocalSafeShutdown> {
        None
    }
    fn recover(&mut self) -> Result<()> {
        Err(DriverError::new(
            ErrorKind::SessionNeedsRecovery,
            "no recovery strategy",
        ))
    }
    fn close(&mut self) -> Result<()>;
}

struct SyncWorker(Box<dyn DeviceBackend>);
impl LocalDmmMeasurement for SyncWorker {
    fn measure_voltage_dc(&mut self, range: Option<f64>) -> Result<Measurement> {
        self.0.dmm().ok_or_else(missing)?.measure_voltage_dc(range)
    }
}
impl LocalPsuOutput for SyncWorker {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> Result<()> {
        self.0
            .psu()
            .ok_or_else(missing)?
            .set_voltage(channel, volts)
    }
    fn read_voltage(&mut self, channel: u32) -> Result<Measurement> {
        self.0.psu().ok_or_else(missing)?.read_voltage(channel)
    }
    fn output_enable(&mut self, channel: u32, enabled: bool) -> Result<()> {
        self.0
            .psu()
            .ok_or_else(missing)?
            .output_enable(channel, enabled)
    }
}
impl LocalRemoteSense for SyncWorker {
    fn set_mode(&mut self, channel: u32, mode: SenseMode) -> Result<()> {
        self.0
            .remote_sense()
            .ok_or_else(missing)?
            .set_mode(channel, mode)
    }
}
impl LocalReset for SyncWorker {
    fn reset(&mut self) -> Result<()> {
        self.0.reset().ok_or_else(missing)?.reset()
    }
}
impl LocalSafeShutdown for SyncWorker {
    fn shutdown(&mut self) -> Result<()> {
        self.0.shutdown().ok_or_else(missing)?.shutdown()
    }
}
fn missing() -> DriverError {
    DriverError::new(
        ErrorKind::DriverContractViolation,
        "capability accessor removed",
    )
}
impl crate::catalog::LocalCatalogBackend for SyncWorker {
    fn invoke(
        &mut self,
        request: crate::catalog::CatalogRequest,
    ) -> Result<crate::catalog::CatalogReply> {
        self.0.catalog().ok_or_else(missing)?.invoke(request)
    }
}
impl WorkerBackend for SyncWorker {
    fn catalog(&mut self) -> Option<&mut dyn crate::catalog::LocalCatalogBackend> {
        if self.0.catalog().is_some() {
            Some(self)
        } else {
            None
        }
    }
    fn operations(&self) -> BTreeSet<Operation> {
        self.0.operations()
    }
    fn dmm(&mut self) -> Option<&mut dyn LocalDmmMeasurement> {
        if self.0.dmm().is_some() {
            Some(self)
        } else {
            None
        }
    }
    fn psu(&mut self) -> Option<&mut dyn LocalPsuOutput> {
        if self.0.psu().is_some() {
            Some(self)
        } else {
            None
        }
    }
    fn remote_sense(&mut self) -> Option<&mut dyn LocalRemoteSense> {
        if self.0.remote_sense().is_some() {
            Some(self)
        } else {
            None
        }
    }
    fn reset(&mut self) -> Option<&mut dyn LocalReset> {
        if self.0.reset().is_some() {
            Some(self)
        } else {
            None
        }
    }
    fn shutdown(&mut self) -> Option<&mut dyn LocalSafeShutdown> {
        if self.0.shutdown().is_some() {
            Some(self)
        } else {
            None
        }
    }
    fn recover(&mut self) -> Result<()> {
        self.0.recover()
    }
    fn close(&mut self) -> Result<()> {
        self.0.close()
    }
}
pub(crate) fn adapt_sync(backend: Box<dyn DeviceBackend>) -> Box<dyn WorkerBackend> {
    Box::new(SyncWorker(backend))
}

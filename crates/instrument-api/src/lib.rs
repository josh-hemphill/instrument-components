//! Experimental, statically composed device-driver contracts. No SCPI or VISA dependency.
//!
//! Native and dedicated-worker asynchronous contracts are optional via `async`.
//! Runtime-specific scheduling belongs to instrument-runtime, not these contracts.
use std::collections::BTreeSet;
use std::fmt;

/// Version of the experimental driver contract, independent of package versions.
pub const CONTRACT_MAJOR: u32 = 1;

/// Stable errors shared with the .NET driver SDK.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ErrorKind {
    InvalidArgument,
    DuplicateDriver,
    IncompatibleContract,
    DriverNotFound,
    AmbiguousDriver,
    UnsupportedOperation,
    UnsupportedExecutionMode,
    DriverContractViolation,
    Transport,
    Timeout,
    Parse,
    Device,
    SessionNeedsRecovery,
    SessionClosing,
    SessionClosed,
    Shutdown,
}

/// Protocol-neutral failure. Provider diagnostics may add context to the message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DriverError {
    pub kind: ErrorKind,
    pub message: String,
}

impl DriverError {
    pub fn new(kind: ErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
        }
    }
}

impl fmt::Display for DriverError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{:?}: {}", self.kind, self.message)
    }
}
impl std::error::Error for DriverError {}
pub type Result<T> = std::result::Result<T, DriverError>;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum ExecutionMode {
    Sync,
    NativeAsync,
    WorkerAsync,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Operation {
    Catalog(catalog::CatalogOperation),
    MeasureVoltageDc,
    SetVoltage,
    ReadVoltage,
    OutputEnable,
    RemoteSense,
    Reset,
    Shutdown,
}

/// An opaque endpoint; address parsing and connection creation belong to providers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Endpoint {
    pub scheme: String,
    pub address: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Identity {
    pub manufacturer: Option<String>,
    pub model: Option<String>,
    pub serial: Option<String>,
    pub vendor_id: Option<u32>,
    pub product_id: Option<u32>,
}

/// Passive discovery evidence. Constructing/matching a candidate performs no I/O.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Candidate {
    pub endpoint: Endpoint,
    pub identity: Identity,
}

/// Evidence ranks are fixed, not inferred from registration order or pattern length.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MatchRule {
    ExactIdentity {
        scheme: String,
        manufacturer: String,
        model: String,
    },
    ExactProduct {
        scheme: String,
        vendor_id: u32,
        product_id: u32,
    },
    Family {
        scheme: String,
        manufacturer: String,
        model_pattern: String,
    },
    /// Eligible only when the application explicitly permits standard SCPI.
    StandardScpi { scheme: String },
}

/// Immutable, application-registered driver metadata. Matching never executes provider code.
#[derive(Debug, Clone)]
pub struct DriverDescriptor {
    pub id: String,
    pub contract_major: u32,
    pub rules: Vec<MatchRule>,
    pub operations: BTreeSet<Operation>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConnectionOwnership {
    Owned,
    Borrowed,
}

/// Factories own connection creation; a byte transport is deliberately not required.
pub struct OpenContext<'a> {
    pub candidate: &'a Candidate,
    pub ownership: ConnectionOwnership,
    /// Requested operations, allowing known connection-specific rejection before IO.
    pub required: &'a BTreeSet<Operation>,
}

/// Static application references to factories root implementations for AOT.
pub trait DriverFactory: Send + Sync {
    fn descriptor(&self) -> DriverDescriptor;
    /// Frozen at registration. Opt in explicitly; selection never falls back by mode.
    fn execution_modes(&self) -> BTreeSet<ExecutionMode> {
        [ExecutionMode::Sync].into_iter().collect()
    }
    #[cfg(feature = "async")]
    fn open_native<'a>(
        &'a self,
        _context: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncDeviceBackend>> {
        Box::pin(async {
            Err(DriverError::new(
                ErrorKind::UnsupportedExecutionMode,
                "native async unavailable",
            ))
        })
    }
    /// Invoked and retained on a dedicated thread. The returned backend need not be Send.
    #[cfg(feature = "async")]
    fn open_worker(&self, context: &OpenContext<'_>) -> Result<Box<dyn WorkerBackend>> {
        self.open(context).map(asynchronous::adapt_sync)
    }
    fn open(&self, _context: &OpenContext<'_>) -> Result<Box<dyn DeviceBackend>> {
        Err(DriverError::new(
            ErrorKind::UnsupportedExecutionMode,
            "direct sync unavailable",
        ))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MeasurementStatus {
    Valid,
    Overload,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Measurement {
    pub value: f64,
    pub status: MeasurementStatus,
}

/// All values/ranges are in volts. None requests the provider's automatic range.
pub trait DmmMeasurement: Send {
    fn measure_voltage_dc(&mut self, range: Option<f64>) -> Result<Measurement>;
}

/// Physical channels are positive, one-based identifiers, not an inferred channel count.
pub trait PsuOutput: Send {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> Result<()>;
    fn read_voltage(&mut self, channel: u32) -> Result<Measurement>;
    fn output_enable(&mut self, channel: u32, enabled: bool) -> Result<()>;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SenseMode {
    Internal,
    External,
}
pub trait RemoteSense: Send {
    fn set_mode(&mut self, channel: u32, mode: SenseMode) -> Result<()>;
}
pub trait Reset: Send {
    fn reset(&mut self) -> Result<()>;
}
/// Must attempt all applicable outputs and report partial/unknown coverage as failure.
pub trait SafeShutdown: Send {
    fn shutdown(&mut self) -> Result<()>;
}

/// A backend owns shared protocol/state across capability views. Runtime serializes
/// complete method calls. Capability accessors are stable, side-effect-free and perform
/// no I/O. close releases resources once; it must preserve borrowed I/O.
/// It must not invoke application code while holding the device operation lease.
pub trait DeviceBackend: Send {
    fn operations(&self) -> BTreeSet<Operation>;
    fn catalog(&mut self) -> Option<&mut dyn catalog::CatalogBackend> {
        None
    }
    fn dmm(&mut self) -> Option<&mut dyn DmmMeasurement> {
        None
    }
    fn psu(&mut self) -> Option<&mut dyn PsuOutput> {
        None
    }
    fn remote_sense(&mut self) -> Option<&mut dyn RemoteSense> {
        None
    }
    fn reset(&mut self) -> Option<&mut dyn Reset> {
        None
    }
    fn shutdown(&mut self) -> Option<&mut dyn SafeShutdown> {
        None
    }
    /// No protocol-neutral recovery commands exist. Implementations explicitly opt in.
    fn recover(&mut self) -> Result<()> {
        Err(DriverError::new(
            ErrorKind::SessionNeedsRecovery,
            "provider has no recovery strategy",
        ))
    }
    fn close(&mut self) -> Result<()>;
}

#[cfg(feature = "async")]
mod asynchronous;
#[cfg(feature = "async")]
pub use asynchronous::*;

pub mod catalog;

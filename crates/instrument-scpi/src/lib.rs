//! Explicit-profile SCPI interpreter. No vendor registry, generic fallback or implicit discovery.
//! IEEE 488.2 common-command helpers are literals, never sent implicitly.
//! Message IO owns framing: one complete command/query body without a terminator.
//! Implementations must bound responses before allocation, return exactly one full
//! response, and retain no host accesses after native cancellation. A failed/partial
//! message requires explicit host recovery establishing a clean response boundary.
use instrument_api::{catalog::*, *};
use std::{collections::BTreeSet, sync::Arc};
mod profile;
pub use profile::*;
#[cfg(feature = "async")]
mod asynchronous;
#[cfg(feature = "async")]
pub use asynchronous::*;

/// IEEE Std 488.2 common commands. Helpers are opt-in, not device probing policy.
pub mod ieee4882 {
    pub const IDENTITY: &str = "*IDN?";
    pub const RESET: &str = "*RST";
    pub const COMPLETION: &str = "*OPC?";
    pub const TRIGGER: &str = "*TRG";
}
pub trait MessageIo: Send {
    fn write(&mut self, message: &str) -> Result<()>;
    fn query(&mut self, message: &str) -> Result<String>;
    /// Must restore a clean complete-message boundary, or fail. No default flush/retry.
    fn recover(&mut self) -> Result<()> {
        Err(DriverError::new(
            ErrorKind::SessionNeedsRecovery,
            "host must explicitly resynchronize message IO",
        ))
    }
    fn close(&mut self) -> Result<()>;
}
pub trait Connector: Send + Sync {
    fn open(&self, context: &OpenContext<'_>) -> Result<Box<dyn MessageIo>>;
}
fn error(kind: ErrorKind, text: &str) -> DriverError {
    DriverError::new(kind, text)
}
fn operations(profile: &Profile) -> BTreeSet<Operation> {
    let mut ops = profile.operations();
    for (catalog, basic) in [
        (
            CatalogOperation::DmmMeasureVoltageDc,
            Operation::MeasureVoltageDc,
        ),
        (
            CatalogOperation::DcPowerSupplySetVoltage,
            Operation::SetVoltage,
        ),
        (
            CatalogOperation::DcPowerSupplyReadVoltage,
            Operation::ReadVoltage,
        ),
        (
            CatalogOperation::DcPowerSupplyOutputEnable,
            Operation::OutputEnable,
        ),
        (
            CatalogOperation::DcPowerSupplySetSenseMode,
            Operation::RemoteSense,
        ),
        (CatalogOperation::Reset, Operation::Reset),
        (CatalogOperation::Shutdown, Operation::Shutdown),
    ] {
        if ops.contains(&Operation::Catalog(catalog)) {
            ops.insert(basic);
        }
    }
    ops
}
#[derive(Clone)]
pub struct ScpiFactory {
    descriptor: DriverDescriptor,
    profile: Profile,
    connector: Arc<dyn Connector>,
    #[cfg(feature = "async")]
    native: Option<Arc<dyn AsyncConnector>>,
}
impl ScpiFactory {
    pub fn with_rules(mut self, rules: Vec<MatchRule>) -> Self {
        self.descriptor.rules = rules;
        self
    }
    #[cfg(feature = "async")]
    pub fn with_native_connector(mut self, connector: Arc<dyn AsyncConnector>) -> Self {
        self.native = Some(connector);
        self
    }

    pub fn new(profile: Profile, scheme: &str, connector: Arc<dyn Connector>) -> Self {
        Self {
            descriptor: DriverDescriptor {
                id: profile.id().into(),
                contract_major: CONTRACT_MAJOR,
                rules: vec![MatchRule::StandardScpi {
                    scheme: scheme.into(),
                }],
                operations: operations(&profile),
            },
            profile,
            connector,
            #[cfg(feature = "async")]
            native: None,
        }
    }
}
impl DriverFactory for ScpiFactory {
    fn descriptor(&self) -> DriverDescriptor {
        self.descriptor.clone()
    }
    fn execution_modes(&self) -> BTreeSet<ExecutionMode> {
        #[cfg(feature = "async")]
        {
            let mut modes: BTreeSet<_> = [ExecutionMode::Sync, ExecutionMode::WorkerAsync]
                .into_iter()
                .collect();
            if self.native.is_some() {
                modes.insert(ExecutionMode::NativeAsync);
            }
            modes
        }
        #[cfg(not(feature = "async"))]
        {
            [ExecutionMode::Sync].into_iter().collect()
        }
    }
    #[cfg(feature = "async")]
    fn open_native<'a>(
        &'a self,
        context: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncDeviceBackend>> {
        Box::pin(async move {
            let connector = self.native.as_ref().ok_or_else(|| {
                error(
                    ErrorKind::UnsupportedExecutionMode,
                    "native connector not configured",
                )
            })?;
            asynchronous::open_device(self.profile.clone(), connector.clone(), context).await
        })
    }
    fn open(&self, context: &OpenContext<'_>) -> Result<Box<dyn DeviceBackend>> {
        Ok(Box::new(ScpiDevice {
            engine: Engine::new(self.profile.clone()),
            io: self.connector.open(context)?,
            ownership: context.ownership,
            closed: false,
        }))
    }
}
struct ScpiDevice {
    engine: Engine,
    io: Box<dyn MessageIo>,
    ownership: ConnectionOwnership,
    closed: bool,
}
impl ScpiDevice {
    fn execute(&mut self, request: CatalogRequest) -> Result<CatalogReply> {
        if self.closed {
            return Err(error(ErrorKind::SessionClosed, "SCPI resource closed"));
        }
        let prepared = self.engine.prepare(&request)?;
        let mut value = None;
        let mut preamble = None;
        let mut failures = Vec::new();
        for step in &prepared.steps {
            let result = if step.response == ResponseRole::Write {
                self.io.write(&step.command)
            } else {
                self.io
                    .query(&step.command)
                    .and_then(response_checked)
                    .and_then(|response| match step.response {
                        ResponseRole::Value => {
                            value = Some(response);
                            Ok(())
                        }
                        ResponseRole::Preamble => {
                            preamble = Some(response);
                            Ok(())
                        }
                        ResponseRole::Completion => completion(&response),
                        _ => unreachable!(),
                    })
            };
            if let Err(e) = result {
                self.engine.invalidate();
                if request.operation() != CatalogOperation::Shutdown {
                    return Err(e);
                }
                failures.push(e.to_string());
            }
        }
        if !failures.is_empty() {
            return Err(error(ErrorKind::Shutdown, &failures.join("; ")));
        }
        let result = self.engine.finish(&request, &prepared, value, preamble);
        if result.is_err() {
            self.engine.invalidate();
        }
        result
    }
    fn release(&mut self) -> Result<()> {
        if self.closed {
            return Ok(());
        }
        self.closed = true;
        if self.ownership == ConnectionOwnership::Owned {
            self.io.close()
        } else {
            Ok(())
        }
    }
}
impl Drop for ScpiDevice {
    fn drop(&mut self) {
        let _ = self.release();
    }
}
impl CatalogBackend for ScpiDevice {
    fn invoke(&mut self, request: CatalogRequest) -> Result<CatalogReply> {
        self.execute(request)
    }
}
fn measurement(reply: CatalogReply) -> Result<Measurement> {
    let q = reply.into_quantity()?;
    Ok(Measurement {
        value: q.value,
        status: q.status,
    })
}
impl DmmMeasurement for ScpiDevice {
    fn measure_voltage_dc(&mut self, range: Option<f64>) -> Result<Measurement> {
        measurement(self.execute(CatalogRequest::DmmMeasureVoltageDc { range })?)
    }
}
impl PsuOutput for ScpiDevice {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> Result<()> {
        self.execute(CatalogRequest::DcPowerSupplySetVoltage { channel, volts })?
            .into_unit()
    }
    fn read_voltage(&mut self, channel: u32) -> Result<Measurement> {
        measurement(self.execute(CatalogRequest::DcPowerSupplyReadVoltage { channel })?)
    }
    fn output_enable(&mut self, channel: u32, enabled: bool) -> Result<()> {
        self.execute(CatalogRequest::DcPowerSupplyOutputEnable { channel, enabled })?
            .into_unit()
    }
}
impl RemoteSense for ScpiDevice {
    fn set_mode(&mut self, channel: u32, mode: instrument_api::SenseMode) -> Result<()> {
        let mode = match mode {
            instrument_api::SenseMode::Internal => catalog::SenseMode::Internal,
            instrument_api::SenseMode::External => catalog::SenseMode::External,
        };
        self.execute(CatalogRequest::DcPowerSupplySetSenseMode { channel, mode })?
            .into_unit()
    }
}
impl Reset for ScpiDevice {
    fn reset(&mut self) -> Result<()> {
        self.execute(CatalogRequest::Reset)?.into_unit()
    }
}
impl SafeShutdown for ScpiDevice {
    fn shutdown(&mut self) -> Result<()> {
        self.execute(CatalogRequest::Shutdown)?.into_unit()
    }
}
impl DeviceBackend for ScpiDevice {
    fn operations(&self) -> BTreeSet<Operation> {
        operations(&self.engine.profile)
    }
    fn catalog(&mut self) -> Option<&mut dyn CatalogBackend> {
        Some(self)
    }
    fn dmm(&mut self) -> Option<&mut dyn DmmMeasurement> {
        Some(self)
    }
    fn psu(&mut self) -> Option<&mut dyn PsuOutput> {
        Some(self)
    }
    fn remote_sense(&mut self) -> Option<&mut dyn RemoteSense> {
        Some(self)
    }
    fn reset(&mut self) -> Option<&mut dyn Reset> {
        Some(self)
    }
    fn shutdown(&mut self) -> Option<&mut dyn SafeShutdown> {
        Some(self)
    }
    fn recover(&mut self) -> Result<()> {
        self.io.recover()?;
        self.engine.invalidate();
        Ok(())
    }
    fn close(&mut self) -> Result<()> {
        self.release()
    }
}

//! Synthetic device, deliberately outside the SDK workspace and production dependency graph.
use instrument_api::*;
use std::collections::{BTreeMap, BTreeSet};
use std::sync::{
    atomic::{AtomicBool, AtomicUsize, Ordering},
    Arc, Condvar, Mutex,
};

pub type MeasurementHook = Box<dyn FnOnce() -> Result<()> + Send>;

#[derive(Default)]
pub struct Trace {
    pub opens: AtomicUsize,
    pub closes: AtomicUsize,
    pub disposes: AtomicUsize,
    pub shutdowns: AtomicUsize,
    pub recoveries: AtomicUsize,
    pub fail_measure: AtomicBool,
    pub measure_error: Mutex<Option<ErrorKind>>,
    pub fail_shutdown: AtomicBool,
    pub fail_recover: AtomicBool,
    pub fail_close: AtomicBool,
    pub missing_dmm: AtomicBool,
    pub unknown_inventory: AtomicBool,
    pub disabled_operations: Mutex<BTreeSet<Operation>>,
    pub outputs: Mutex<BTreeMap<u32, bool>>,
    pub shutdown_attempts: Mutex<Vec<u32>>,
    pub events: Mutex<Vec<String>>,
    pub on_measure: Mutex<Option<MeasurementHook>>,
    pub block: Mutex<(bool, bool)>,
    pub wake: Condvar,
}

pub fn operations() -> BTreeSet<Operation> {
    [
        Operation::MeasureVoltageDc,
        Operation::SetVoltage,
        Operation::ReadVoltage,
        Operation::OutputEnable,
        Operation::Reset,
        Operation::Shutdown,
    ]
    .into_iter()
    .collect()
}

pub fn descriptor() -> DriverDescriptor {
    DriverDescriptor {
        id: "synthetic.binary".into(),
        contract_major: CONTRACT_MAJOR,
        rules: vec![MatchRule::ExactIdentity {
            scheme: "fake".into(),
            manufacturer: "Acme".into(),
            model: "X100".into(),
        }],
        operations: operations(),
    }
}

pub fn candidate() -> Candidate {
    Candidate {
        endpoint: Endpoint {
            scheme: "fake".into(),
            address: "device-1".into(),
        },
        identity: Identity {
            manufacturer: Some("Acme".into()),
            model: Some("X100".into()),
            ..Identity::default()
        },
    }
}

pub struct BinaryFactory {
    pub trace: Arc<Trace>,
    pub descriptor: DriverDescriptor,
}
impl BinaryFactory {
    pub fn new(trace: Arc<Trace>) -> Self {
        Self {
            trace,
            descriptor: descriptor(),
        }
    }
}
impl DriverFactory for BinaryFactory {
    fn descriptor(&self) -> DriverDescriptor {
        self.descriptor.clone()
    }
    fn open(&self, context: &OpenContext<'_>) -> Result<Box<dyn DeviceBackend>> {
        self.trace.opens.fetch_add(1, Ordering::SeqCst);
        Ok(Box::new(BinaryDevice {
            trace: self.trace.clone(),
            ownership: context.ownership,
            voltages: BTreeMap::new(),
            stage: false,
        }))
    }
}

struct BinaryDevice {
    trace: Arc<Trace>,
    ownership: ConnectionOwnership,
    voltages: BTreeMap<u32, f64>,
    stage: bool,
}
impl BinaryDevice {
    // A real two-message binary transaction, with a strict state machine. Any SCPI
    // fallback or sequencing error fails rather than accidentally returning a value.
    fn exchange(&mut self, message: &[u8]) -> Result<Vec<u8>> {
        self.trace
            .events
            .lock()
            .unwrap()
            .push(format!("{:02x?}", message));
        match (message, self.stage) {
            ([0xa1], false) => {
                self.stage = true;
                Ok(vec![0x06])
            }
            ([0xb2], true) => {
                self.stage = false;
                Ok(3.3_f64.to_le_bytes().to_vec())
            }
            _ => Err(DriverError::new(
                ErrorKind::Device,
                "unexpected binary protocol message",
            )),
        }
    }
}
impl DmmMeasurement for BinaryDevice {
    fn measure_voltage_dc(&mut self, _range: Option<f64>) -> Result<Measurement> {
        let callback = self.trace.on_measure.lock().unwrap().take();
        if let Some(callback) = callback {
            callback()?;
        }
        if self.exchange(&[0xa1])? != [0x06] {
            return Err(DriverError::new(ErrorKind::Parse, "bad acknowledgement"));
        }
        if self.trace.fail_measure.swap(false, Ordering::SeqCst) {
            return Err(DriverError::new(
                self.trace
                    .measure_error
                    .lock()
                    .unwrap()
                    .take()
                    .unwrap_or(ErrorKind::Timeout),
                "injected provider failure after first binary write",
            ));
        }
        {
            let mut block = self.trace.block.lock().unwrap();
            if block.0 {
                block.1 = true;
                self.trace.wake.notify_all();
            }
            while block.0 {
                let (next, timeout) = self
                    .trace
                    .wake
                    .wait_timeout(block, std::time::Duration::from_secs(15))
                    .unwrap();
                block = next;
                if timeout.timed_out() {
                    return Err(DriverError::new(
                        ErrorKind::Timeout,
                        "fixture release timed out",
                    ));
                }
            }
        }
        let bytes: [u8; 8] = self
            .exchange(&[0xb2])?
            .try_into()
            .map_err(|_| DriverError::new(ErrorKind::Parse, "bad binary length"))?;
        Ok(Measurement {
            value: f64::from_le_bytes(bytes),
            status: MeasurementStatus::Valid,
        })
    }
}
impl PsuOutput for BinaryDevice {
    fn set_voltage(&mut self, channel: u32, volts: f64) -> Result<()> {
        self.trace
            .events
            .lock()
            .unwrap()
            .push(format!("set:{channel}"));
        self.voltages.insert(channel, volts);
        Ok(())
    }
    fn read_voltage(&mut self, channel: u32) -> Result<Measurement> {
        Ok(Measurement {
            value: *self.voltages.get(&channel).unwrap_or(&0.0),
            status: MeasurementStatus::Valid,
        })
    }
    fn output_enable(&mut self, channel: u32, enabled: bool) -> Result<()> {
        if channel > 2 {
            return Err(DriverError::new(
                ErrorKind::InvalidArgument,
                "unknown physical channel",
            ));
        }
        self.trace.outputs.lock().unwrap().insert(channel, enabled);
        self.trace
            .events
            .lock()
            .unwrap()
            .push(format!("output:{channel}:{enabled}"));
        Ok(())
    }
}
impl Reset for BinaryDevice {
    fn reset(&mut self) -> Result<()> {
        self.voltages.clear();
        self.stage = false;
        Ok(())
    }
}
impl SafeShutdown for BinaryDevice {
    fn shutdown(&mut self) -> Result<()> {
        self.trace.shutdowns.fetch_add(1, Ordering::SeqCst);
        let mut failures = Vec::new();
        for channel in [1, 2] {
            self.trace.shutdown_attempts.lock().unwrap().push(channel);
            if channel == 1 && self.trace.fail_shutdown.load(Ordering::SeqCst) {
                failures.push("injected output-1 failure");
            } else {
                self.output_enable(channel, false)?;
            }
        }
        if self.trace.unknown_inventory.load(Ordering::SeqCst) {
            failures.push("physical output inventory unknown; known outputs disabled");
        }
        if failures.is_empty() {
            Ok(())
        } else {
            Err(DriverError::new(ErrorKind::Shutdown, failures.join("; ")))
        }
    }
}
impl DeviceBackend for BinaryDevice {
    fn operations(&self) -> BTreeSet<Operation> {
        operations()
            .difference(&self.trace.disabled_operations.lock().unwrap())
            .copied()
            .collect()
    }
    fn dmm(&mut self) -> Option<&mut dyn DmmMeasurement> {
        if self.trace.missing_dmm.load(Ordering::SeqCst) {
            None
        } else {
            Some(self)
        }
    }
    fn psu(&mut self) -> Option<&mut dyn PsuOutput> {
        Some(self)
    }
    fn reset(&mut self) -> Option<&mut dyn Reset> {
        Some(self)
    }
    fn shutdown(&mut self) -> Option<&mut dyn SafeShutdown> {
        Some(self)
    }
    fn recover(&mut self) -> Result<()> {
        self.trace.recoveries.fetch_add(1, Ordering::SeqCst);
        if self.trace.fail_recover.load(Ordering::SeqCst) {
            return Err(DriverError::new(
                ErrorKind::Transport,
                "injected recovery failure",
            ));
        }
        self.stage = false;
        Ok(())
    }
    fn close(&mut self) -> Result<()> {
        self.trace.closes.fetch_add(1, Ordering::SeqCst);
        if self.ownership == ConnectionOwnership::Owned {
            self.trace.disposes.fetch_add(1, Ordering::SeqCst);
        }
        if self.trace.fail_close.load(Ordering::SeqCst) {
            Err(DriverError::new(
                ErrorKind::Transport,
                "injected release failure",
            ))
        } else {
            Ok(())
        }
    }
}

#[cfg(feature = "async")]
mod asynchronous;
#[cfg(feature = "async")]
pub use asynchronous::*;

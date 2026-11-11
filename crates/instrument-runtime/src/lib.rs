//! Explicit registration and capability views; native and worker async are optional.
//! This crate does not depend on the legacy core, SCPI, VISA, or any device package.
use instrument_api::catalog::{CatalogExecutor, CatalogReply, CatalogRequest};
use instrument_api::*;
use std::cell::RefCell;
use std::collections::{BTreeMap, BTreeSet};
use std::sync::{
    atomic::{AtomicBool, AtomicUsize, Ordering},
    Arc, Mutex, MutexGuard,
};

fn shutdown_supported(operations: &BTreeSet<Operation>) -> bool {
    operations.contains(&Operation::Shutdown)
        || operations.contains(&Operation::Catalog(
            instrument_api::catalog::CatalogOperation::Shutdown,
        ))
}
fn error(kind: ErrorKind, message: impl Into<String>) -> DriverError {
    DriverError::new(kind, message)
}
thread_local! { static ACTIVE: RefCell<BTreeSet<usize>> = const { RefCell::new(BTreeSet::new()) }; }
struct Active(usize);
impl Active {
    fn check(id: usize) -> Result<()> {
        if ACTIVE.with(|a| a.borrow().contains(&id)) {
            Err(error(
                ErrorKind::DriverContractViolation,
                "backend must not reenter its device handle",
            ))
        } else {
            Ok(())
        }
    }
    fn enter(id: usize) -> Self {
        ACTIVE.with(|a| {
            a.borrow_mut().insert(id);
        });
        Self(id)
    }
}
impl Drop for Active {
    fn drop(&mut self) {
        ACTIVE.with(|a| {
            a.borrow_mut().remove(&self.0);
        });
    }
}
fn measurement(value: Measurement) -> Result<Measurement> {
    if value.status == MeasurementStatus::Valid && !value.value.is_finite() {
        Err(error(
            ErrorKind::DriverContractViolation,
            "valid measurement must be finite",
        ))
    } else {
        Ok(value)
    }
}
fn same(a: &str, b: &str) -> bool {
    a.trim().eq_ignore_ascii_case(b.trim())
}

// The single '*' wildcard is a shared contract; no regex engine/culture dependency.
fn pattern(pattern: &str, value: &str) -> bool {
    let p = pattern.trim().to_ascii_lowercase();
    let v = value.trim().to_ascii_lowercase();
    let (mut pi, mut vi, mut star, mut retry) = (0, 0, None, 0);
    let (p, v) = (p.as_bytes(), v.as_bytes());
    while vi < v.len() {
        if pi < p.len() && p[pi] != b'*' && p[pi] == v[vi] {
            pi += 1;
            vi += 1;
        } else if pi < p.len() && p[pi] == b'*' {
            star = Some(pi);
            pi += 1;
            retry = vi;
        } else if let Some(s) = star {
            retry += 1;
            vi = retry;
            pi = s + 1;
        } else {
            return false;
        }
    }
    while pi < p.len() && p[pi] == b'*' {
        pi += 1;
    }
    pi == p.len()
}

fn rank(rule: &MatchRule, c: &Candidate, standard_scpi: bool) -> Option<u8> {
    match rule {
        MatchRule::ExactIdentity {
            scheme,
            manufacturer,
            model,
        } if same(scheme, &c.endpoint.scheme)
            && c.identity
                .manufacturer
                .as_deref()
                .map(|v| same(manufacturer, v))
                == Some(true)
            && c.identity.model.as_deref().map(|v| same(model, v)) == Some(true) =>
        {
            Some(3)
        }
        MatchRule::ExactProduct {
            scheme,
            vendor_id,
            product_id,
        } if same(scheme, &c.endpoint.scheme)
            && c.identity.vendor_id == Some(*vendor_id)
            && c.identity.product_id == Some(*product_id) =>
        {
            Some(3)
        }
        MatchRule::Family {
            scheme,
            manufacturer,
            model_pattern,
        } if same(scheme, &c.endpoint.scheme)
            && c.identity
                .manufacturer
                .as_deref()
                .map(|v| same(manufacturer, v))
                == Some(true)
            && c.identity
                .model
                .as_deref()
                .map(|v| pattern(model_pattern, v))
                == Some(true) =>
        {
            Some(2)
        }
        MatchRule::StandardScpi { scheme } if standard_scpi && same(scheme, &c.endpoint.scheme) => {
            Some(1)
        }
        _ => None,
    }
}

#[derive(Debug, Clone, Default)]
pub struct Selection {
    pub driver_id: Option<String>,
    pub preferences: BTreeMap<String, i32>,
    pub allow_standard_scpi: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MatchEvidence {
    pub driver_id: String,
    pub preference: i32,
    pub rank: u8,
    pub explicit: bool,
}

struct Registration {
    descriptor: DriverDescriptor,
    factory: Arc<dyn DriverFactory>,
    modes: BTreeSet<ExecutionMode>,
}

/// Frozen registry; applications construct one with statically referenced factories.
pub struct DriverRegistry {
    registrations: BTreeMap<String, Registration>,
}

impl DriverRegistry {
    pub fn new(factories: impl IntoIterator<Item = Arc<dyn DriverFactory>>) -> Result<Self> {
        let mut registrations = BTreeMap::new();
        for factory in factories {
            let descriptor = factory.descriptor();
            if descriptor.contract_major != CONTRACT_MAJOR {
                return Err(error(ErrorKind::IncompatibleContract, &descriptor.id));
            }
            if descriptor.id.trim() != descriptor.id
                || !descriptor.id.contains('.')
                || !descriptor
                    .id
                    .as_bytes()
                    .first()
                    .map(|b| b.is_ascii_lowercase())
                    .unwrap_or(false)
                || descriptor.id.split('.').any(str::is_empty)
                || descriptor
                    .id
                    .bytes()
                    .any(|b| !(b.is_ascii_lowercase() || b.is_ascii_digit() || b"._-".contains(&b)))
                || descriptor.rules.is_empty()
            {
                return Err(error(
                    ErrorKind::InvalidArgument,
                    "driver needs a namespaced lowercase ID and matching rules",
                ));
            }
            for rule in &descriptor.rules {
                let invalid = match rule {
                    MatchRule::ExactIdentity {
                        scheme,
                        manufacturer,
                        model,
                    } => {
                        scheme.trim().is_empty()
                            || manufacturer.trim().is_empty()
                            || model.trim().is_empty()
                    }
                    MatchRule::ExactProduct { scheme, .. } | MatchRule::StandardScpi { scheme } => {
                        scheme.trim().is_empty()
                    }
                    MatchRule::Family {
                        scheme,
                        manufacturer,
                        model_pattern,
                    } => {
                        scheme.trim().is_empty()
                            || manufacturer.trim().is_empty()
                            || manufacturer.contains('*')
                            || model_pattern.trim().is_empty()
                    }
                };
                if invalid {
                    return Err(error(ErrorKind::InvalidArgument, "invalid matching rule"));
                }
            }
            let modes = factory.execution_modes();
            if modes.is_empty() {
                return Err(error(
                    ErrorKind::InvalidArgument,
                    "driver must declare an execution mode",
                ));
            }
            let id = descriptor.id.clone();
            if registrations
                .insert(
                    id.clone(),
                    Registration {
                        descriptor,
                        factory,
                        modes,
                    },
                )
                .is_some()
            {
                return Err(error(ErrorKind::DuplicateDriver, id));
            }
        }
        Ok(Self { registrations })
    }

    pub fn select(&self, candidate: &Candidate, selection: &Selection) -> Result<MatchEvidence> {
        if candidate.endpoint.scheme.trim().is_empty()
            || candidate.endpoint.address.trim().is_empty()
        {
            return Err(error(
                ErrorKind::InvalidArgument,
                "endpoint scheme/address required",
            ));
        }
        if let Some(id) = &selection.driver_id {
            let registration = self
                .registrations
                .get(id)
                .ok_or_else(|| error(ErrorKind::DriverNotFound, id))?;
            // An explicit binding overrides identity hints, but not endpoint schemes.
            let compatible = registration.descriptor.rules.iter().any(|r| match r {
                MatchRule::ExactIdentity { scheme, .. }
                | MatchRule::ExactProduct { scheme, .. }
                | MatchRule::Family { scheme, .. }
                | MatchRule::StandardScpi { scheme } => same(scheme, &candidate.endpoint.scheme),
            });
            if !compatible {
                return Err(error(
                    ErrorKind::DriverNotFound,
                    "explicit driver does not support endpoint scheme",
                ));
            }
            return Ok(MatchEvidence {
                driver_id: id.clone(),
                preference: 0,
                rank: 0,
                explicit: true,
            });
        }
        let mut matches = Vec::new();
        for (id, registration) in &self.registrations {
            if let Some(rank) = registration
                .descriptor
                .rules
                .iter()
                .filter_map(|r| rank(r, candidate, selection.allow_standard_scpi))
                .max()
            {
                matches.push(MatchEvidence {
                    driver_id: id.clone(),
                    preference: *selection.preferences.get(id).unwrap_or(&0),
                    rank,
                    explicit: false,
                });
            }
        }
        matches.sort_by(|a, b| {
            (b.preference, b.rank)
                .cmp(&(a.preference, a.rank))
                .then_with(|| a.driver_id.cmp(&b.driver_id))
        });
        let first = matches
            .first()
            .ok_or_else(|| error(ErrorKind::DriverNotFound, "no eligible driver"))?;
        let tied: Vec<_> = matches
            .iter()
            .filter(|m| (m.preference, m.rank) == (first.preference, first.rank))
            .map(|m| m.driver_id.as_str())
            .collect();
        if tied.len() > 1 {
            return Err(error(ErrorKind::AmbiguousDriver, tied.join(", ")));
        }
        Ok(first.clone())
    }

    /// Selection/passive discovery never opens or probes the endpoint.
    pub fn open(
        &self,
        candidate: &Candidate,
        selection: &Selection,
        required: &BTreeSet<Operation>,
        mode: ExecutionMode,
        ownership: ConnectionOwnership,
    ) -> Result<DeviceHandle> {
        if mode != ExecutionMode::Sync {
            return Err(error(
                ErrorKind::UnsupportedExecutionMode,
                "direct sync Open requires Sync; use open_native or open_worker for async",
            ));
        }
        let evidence = self.select(candidate, selection)?;
        let registration = &self.registrations[&evidence.driver_id];
        if !registration.modes.contains(&ExecutionMode::Sync) {
            return Err(error(
                ErrorKind::UnsupportedExecutionMode,
                "driver does not support direct sync execution",
            ));
        }
        if !required.is_subset(&registration.descriptor.operations) {
            return Err(error(
                ErrorKind::UnsupportedOperation,
                "required operation outside driver support envelope",
            ));
        }
        let mut backend = registration.factory.open(&OpenContext {
            candidate,
            ownership,
            required,
        })?;
        let operations = backend.operations();
        let invalid = operations
            .iter()
            .any(|o| matches!(o, Operation::Catalog(_)))
            && backend.catalog().is_none()
            || !operations.is_subset(&registration.descriptor.operations)
            || (operations.contains(&Operation::MeasureVoltageDc) && backend.dmm().is_none())
            || ([
                Operation::SetVoltage,
                Operation::ReadVoltage,
                Operation::OutputEnable,
            ]
            .iter()
            .any(|o| operations.contains(o))
                && backend.psu().is_none())
            || (operations.contains(&Operation::RemoteSense) && backend.remote_sense().is_none())
            || (operations.contains(&Operation::Reset) && backend.reset().is_none())
            || (operations.contains(&Operation::Shutdown) && backend.shutdown().is_none());
        if invalid || !required.is_subset(&operations) {
            let kind = if invalid {
                ErrorKind::DriverContractViolation
            } else {
                ErrorKind::UnsupportedOperation
            };
            let cleanup = backend
                .close()
                .err()
                .map(|e| format!("; cleanup: {e}"))
                .unwrap_or_default();
            return Err(error(
                kind,
                format!("invalid/insufficient opened capabilities{cleanup}"),
            ));
        }
        Ok(DeviceHandle {
            inner: Arc::new(Inner {
                closing: AtomicBool::new(false),
                closed: AtomicBool::new(false),
                waiting: AtomicUsize::new(0),
                session: Mutex::new(Session {
                    backend: Some(backend),
                    needs_recovery: false,
                }),
                operations,
                evidence,
            }),
        })
    }
}

struct Session {
    backend: Option<Box<dyn DeviceBackend>>,
    needs_recovery: bool,
}
struct Inner {
    closing: AtomicBool,
    closed: AtomicBool,
    waiting: AtomicUsize,
    session: Mutex<Session>,
    operations: BTreeSet<Operation>,
    evidence: MatchEvidence,
}
impl Drop for Inner {
    fn drop(&mut self) {
        // Resource release only, never implicit reset/output commands on Drop.
        let session = self.session.get_mut().unwrap_or_else(|p| p.into_inner());
        if let Some(mut backend) = session.backend.take() {
            let _ = backend.close();
        }
    }
}

/// Cheaply cloned typed views share one connection and whole-operation lock.
#[derive(Clone)]
pub struct DeviceHandle {
    inner: Arc<Inner>,
}

impl DeviceHandle {
    /// True as soon as close rejects new/queued work, including after close completes.
    pub fn closing_requested(&self) -> bool {
        self.inner.closing.load(Ordering::Acquire)
    }
    /// Requests waiting for the connection gate, including recovery and close.
    pub fn pending_operations(&self) -> usize {
        self.inner.waiting.load(Ordering::Acquire)
    }
    pub fn evidence(&self) -> &MatchEvidence {
        &self.inner.evidence
    }
    pub fn operations(&self) -> &BTreeSet<Operation> {
        &self.inner.operations
    }
    fn id(&self) -> usize {
        Arc::as_ptr(&self.inner) as usize
    }
    fn lifecycle(&self) -> Result<()> {
        Active::check(self.id())?;
        if self.inner.closed.load(Ordering::Acquire) {
            return Err(error(ErrorKind::SessionClosed, "session closed"));
        }
        if self.inner.closing.load(Ordering::Acquire) {
            return Err(error(ErrorKind::SessionClosing, "close requested"));
        }
        Ok(())
    }
    fn lock(&self) -> Result<MutexGuard<'_, Session>> {
        self.inner.waiting.fetch_add(1, Ordering::AcqRel);
        let acquired = self.inner.session.lock();
        self.inner.waiting.fetch_sub(1, Ordering::AcqRel);
        acquired.map_err(|_| {
            error(
                ErrorKind::DriverContractViolation,
                "provider panicked; session poisoned",
            )
        })
    }
    fn run<T>(
        &self,
        operation: Operation,
        f: impl FnOnce(&mut dyn DeviceBackend) -> Result<T>,
    ) -> Result<T> {
        self.lifecycle()?;
        let mut session = self.lock()?;
        if session.backend.is_none() {
            return Err(error(ErrorKind::SessionClosed, "session closed"));
        }
        if self.inner.closing.load(Ordering::Acquire) {
            return Err(error(ErrorKind::SessionClosing, "close requested"));
        }
        if session.needs_recovery {
            return Err(error(
                ErrorKind::SessionNeedsRecovery,
                "provider recovery required",
            ));
        }
        if !self.inner.operations.contains(&operation) {
            return Err(error(
                ErrorKind::UnsupportedOperation,
                format!("{operation:?}"),
            ));
        }
        let _active = Active::enter(self.id());
        let result = f(session.backend.as_deref_mut().unwrap());
        if let Err(e) = &result {
            if !matches!(
                e.kind,
                ErrorKind::InvalidArgument | ErrorKind::UnsupportedOperation
            ) {
                session.needs_recovery = true;
            }
        }
        result
    }
    pub fn dmm(&self) -> Dmm {
        Dmm(self.clone())
    }
    pub fn psu(&self) -> PowerSupply {
        PowerSupply(self.clone())
    }
    pub fn reset(&self) -> Result<()> {
        self.run(Operation::Reset, |b| {
            b.reset()
                .ok_or_else(|| error(ErrorKind::DriverContractViolation, "reset accessor removed"))?
                .reset()
        })
    }
    pub fn set_sense_mode(&self, channel: u32, mode: SenseMode) -> Result<()> {
        channel_valid(channel)?;
        self.run(Operation::RemoteSense, |b| {
            b.remote_sense()
                .ok_or_else(|| error(ErrorKind::DriverContractViolation, "sense accessor removed"))?
                .set_mode(channel, mode)
        })
    }
    pub fn recover(&self) -> Result<()> {
        self.lifecycle()?;
        let mut session = self.lock()?;
        if self.inner.closing.load(Ordering::Acquire) {
            return Err(error(ErrorKind::SessionClosing, "close requested"));
        }
        if !session.needs_recovery {
            return Ok(());
        }
        let _active = Active::enter(self.id());
        let backend = session
            .backend
            .as_deref_mut()
            .ok_or_else(|| error(ErrorKind::SessionClosed, "closed"))?;
        backend.recover()?;
        session.needs_recovery = false;
        Ok(())
    }
    /// Rejects queued work, waits for active operations, optionally shuts down, and
    /// always closes resources even if shutdown fails. Repeated close is idempotent.
    pub fn close(&self, require_shutdown: bool) -> Result<()> {
        Active::check(self.id())?;
        self.inner.closing.store(true, Ordering::Release);
        let mut session = self.lock()?;
        let Some(mut backend) = session.backend.take() else {
            return Ok(());
        };
        let _active = Active::enter(self.id());
        let mut failures = Vec::new();
        if require_shutdown && !shutdown_supported(&self.inner.operations) {
            failures.push("safe shutdown is not enabled for this session".into());
        } else if require_shutdown {
            let recovered = if session.needs_recovery {
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
                if !self.inner.operations.contains(&Operation::Shutdown) {
                    match backend
                        .catalog()
                        .ok_or_else(|| {
                            error(
                                ErrorKind::DriverContractViolation,
                                "shutdown catalog removed",
                            )
                        })
                        .and_then(|b| b.invoke(CatalogRequest::Shutdown))
                        .and_then(|r| r.into_unit())
                    {
                        Ok(()) => (),
                        Err(e) => failures.push(e.to_string()),
                    }
                } else {
                    match backend.shutdown() {
                        Some(shutdown) => {
                            if let Err(e) = shutdown.shutdown() {
                                failures.push(e.to_string());
                            }
                        }
                        None => failures.push("safe shutdown unsupported".into()),
                    }
                }
            }
        }
        if let Err(e) = backend.close() {
            failures.push(e.to_string());
        }
        self.inner.closed.store(true, Ordering::Release);
        if failures.is_empty() {
            Ok(())
        } else {
            Err(error(ErrorKind::Shutdown, failures.join("; ")))
        }
    }
}

fn channel_valid(channel: u32) -> Result<()> {
    if channel == 0 {
        Err(error(
            ErrorKind::InvalidArgument,
            "channel must be positive",
        ))
    } else {
        Ok(())
    }
}

pub struct Dmm(DeviceHandle);
impl Dmm {
    pub fn measure_voltage_dc(&self, range: Option<f64>) -> Result<Measurement> {
        if range.map(|r| !r.is_finite() || r <= 0.0).unwrap_or(false) {
            return Err(error(
                ErrorKind::InvalidArgument,
                "range must be finite and positive",
            ));
        }
        self.0.run(Operation::MeasureVoltageDc, |b| {
            measurement(
                b.dmm()
                    .ok_or_else(|| {
                        error(ErrorKind::DriverContractViolation, "DMM accessor removed")
                    })?
                    .measure_voltage_dc(range)?,
            )
        })
    }
}

pub struct PowerSupply(DeviceHandle);
impl PowerSupply {
    pub fn set_voltage(&self, channel: u32, volts: f64) -> Result<()> {
        channel_valid(channel)?;
        if !volts.is_finite() {
            return Err(error(ErrorKind::InvalidArgument, "voltage must be finite"));
        }
        self.0.run(Operation::SetVoltage, |b| {
            b.psu()
                .ok_or_else(|| error(ErrorKind::DriverContractViolation, "PSU accessor removed"))?
                .set_voltage(channel, volts)
        })
    }
    pub fn read_voltage(&self, channel: u32) -> Result<Measurement> {
        channel_valid(channel)?;
        self.0.run(Operation::ReadVoltage, |b| {
            measurement(
                b.psu()
                    .ok_or_else(|| {
                        error(ErrorKind::DriverContractViolation, "PSU accessor removed")
                    })?
                    .read_voltage(channel)?,
            )
        })
    }
    pub fn output_enable(&self, channel: u32, enabled: bool) -> Result<()> {
        channel_valid(channel)?;
        self.0.run(Operation::OutputEnable, |b| {
            b.psu()
                .ok_or_else(|| error(ErrorKind::DriverContractViolation, "PSU accessor removed"))?
                .output_enable(channel, enabled)
        })
    }
}

#[cfg(feature = "async")]
mod asynchronous;
#[cfg(feature = "async")]
pub use asynchronous::*;

impl CatalogExecutor for DeviceHandle {
    fn invoke(&self, request: CatalogRequest) -> Result<CatalogReply> {
        request.validate()?;
        self.run(Operation::Catalog(request.operation()), |b| {
            let reply = b
                .catalog()
                .ok_or_else(|| {
                    error(
                        ErrorKind::DriverContractViolation,
                        "catalog accessor removed",
                    )
                })?
                .invoke(request.clone())?;
            reply.validate(&request)?;
            Ok(reply)
        })
    }
}

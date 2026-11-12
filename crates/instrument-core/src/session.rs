use crate::address::ResourceAddress;
use crate::connect::ConnectOptions;
use crate::diagnostics::Diagnostics;
use crate::error::{Error, Result};
use crate::identity::{DeviceIdentity, Idn};
use crate::ieee4882::Ieee4882;
use crate::kind::InstrumentKind;
use crate::scpi::ScpiSession;
use crate::transport::DynTransport;
use std::sync::{Arc, Mutex};

/// Opens transport sessions for a device address.
pub trait SessionOpener: Send + Sync {
    fn open(&self, address: &ResourceAddress, opts: &ConnectOptions) -> Result<DynTransport>;
}

/// Active instrument session with SCPI and cached identity.
pub struct InstrumentSession {
    pub address: ResourceAddress,
    pub scpi: ScpiSession,
    identity: DeviceIdentity,
}

impl InstrumentSession {
    pub fn new(
        address: ResourceAddress,
        transport: DynTransport,
        opts: ConnectOptions,
        identity: DeviceIdentity,
    ) -> Result<Self> {
        Self::new_with_diagnostics(address, transport, opts, identity, None)
    }

    pub fn new_with_diagnostics(
        address: ResourceAddress,
        transport: DynTransport,
        opts: ConnectOptions,
        identity: DeviceIdentity,
        diagnostics: Option<Diagnostics>,
    ) -> Result<Self> {
        let mut scpi = ScpiSession::new(transport, opts)?;
        if let Some(diag) = diagnostics {
            scpi = scpi.with_diagnostics(diag);
        }
        Ok(Self {
            address,
            scpi,
            identity,
        })
    }

    pub fn address_str(&self) -> &str {
        &self.address.raw
    }

    pub fn identity(&self) -> &DeviceIdentity {
        &self.identity
    }

    pub fn scpi(&self) -> &ScpiSession {
        &self.scpi
    }

    pub fn scpi_mut(&mut self) -> &mut ScpiSession {
        &mut self.scpi
    }

    pub fn idn(&mut self) -> Result<Idn> {
        self.scpi_comm("idn", |scpi| Ieee4882::new(scpi).idn())
    }

    fn scpi_comm<T>(
        &mut self,
        command: &str,
        f: impl FnOnce(&mut ScpiSession) -> Result<T>,
    ) -> Result<T> {
        match f(&mut self.scpi) {
            Ok(v) => Ok(v),
            Err(e) => Err(e.with_comm_context(&self.address.raw, Some(command), 1)),
        }
    }

    pub fn reset(&mut self) -> Result<()> {
        self.scpi_comm("*RST", |scpi| Ieee4882::new(scpi).reset())
    }

    pub fn clear_status(&mut self) -> Result<()> {
        self.scpi_comm("*CLS", |scpi| Ieee4882::new(scpi).clear_status())
    }

    pub fn wait_complete(&mut self) -> Result<()> {
        self.scpi_comm("*OPC?", |scpi| Ieee4882::new(scpi).wait_complete())
    }

    pub fn check_errors(&mut self) -> Result<Vec<String>> {
        self.scpi_comm("SYST:ERR?", |scpi| scpi.check_errors())
    }
}

/// Reuses a single underlying session across typed views.
pub struct SessionPool {
    session: Arc<Mutex<InstrumentSession>>,
}

impl SessionPool {
    pub fn new(session: InstrumentSession) -> Self {
        Self {
            session: Arc::new(Mutex::new(session)),
        }
    }

    pub fn session(&self) -> Arc<Mutex<InstrumentSession>> {
        self.session.clone()
    }

    pub fn open_session(&self) -> Result<PooledSession> {
        Ok(PooledSession {
            session: self.session.clone(),
        })
    }
}

/// Handle to a pooled session (shared transport).
pub struct PooledSession {
    session: Arc<Mutex<InstrumentSession>>,
}

impl PooledSession {
    pub fn lock(&self) -> std::sync::MutexGuard<'_, InstrumentSession> {
        self.session.lock().unwrap()
    }
}

/// Validates that a kind is supported before opening a typed view.
pub fn ensure_kind_supported(
    address: &ResourceAddress,
    kind: InstrumentKind,
    supported: &[InstrumentKind],
) -> Result<()> {
    if supported.contains(&kind) {
        return Ok(());
    }
    Err(Error::UnsupportedKind {
        address: address.raw.clone(),
        kind,
        supported: supported.to_vec(),
    })
}

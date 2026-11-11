use crate::address::ResourceAddress;
use crate::async_transport::DynAsyncTransport;
use crate::connect::ConnectOptions;
use crate::diagnostics::Diagnostics;
use crate::error::Result;
use crate::identity::{DeviceIdentity, Idn};
use crate::ieee4882::AsyncIeee4882;
use crate::scpi::AsyncScpiSession;
use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;
use tokio::sync::Mutex;

/// Opens async transport sessions for a device address.
pub trait AsyncSessionOpener: Send + Sync {
    fn open<'a>(
        &'a self,
        address: &'a ResourceAddress,
        opts: &'a ConnectOptions,
    ) -> Pin<Box<dyn Future<Output = Result<DynAsyncTransport>> + Send + 'a>>;
}

/// Active async instrument session with SCPI and cached identity.
pub struct AsyncInstrumentSession {
    pub address: ResourceAddress,
    pub scpi: AsyncScpiSession,
    identity: DeviceIdentity,
}

impl AsyncInstrumentSession {
    pub async fn new(
        address: ResourceAddress,
        transport: DynAsyncTransport,
        opts: ConnectOptions,
        identity: DeviceIdentity,
    ) -> Result<Self> {
        Self::new_with_diagnostics(address, transport, opts, identity, None).await
    }

    pub async fn new_with_diagnostics(
        address: ResourceAddress,
        transport: DynAsyncTransport,
        opts: ConnectOptions,
        identity: DeviceIdentity,
        diagnostics: Option<Diagnostics>,
    ) -> Result<Self> {
        let mut scpi = AsyncScpiSession::new(transport, opts).await?;
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

    pub fn scpi(&self) -> &AsyncScpiSession {
        &self.scpi
    }

    pub fn scpi_mut(&mut self) -> &mut AsyncScpiSession {
        &mut self.scpi
    }

    pub async fn idn(&mut self) -> Result<Idn> {
        match AsyncIeee4882::new(&mut self.scpi).idn().await {
            Ok(v) => Ok(v),
            Err(e) => Err(e.with_comm_context(&self.address.raw, Some("idn"), 1)),
        }
    }

    pub async fn reset(&mut self) -> Result<()> {
        match AsyncIeee4882::new(&mut self.scpi).reset().await {
            Ok(v) => Ok(v),
            Err(e) => Err(e.with_comm_context(&self.address.raw, Some("*RST"), 1)),
        }
    }

    pub async fn clear_status(&mut self) -> Result<()> {
        match AsyncIeee4882::new(&mut self.scpi).clear_status().await {
            Ok(v) => Ok(v),
            Err(e) => Err(e.with_comm_context(&self.address.raw, Some("*CLS"), 1)),
        }
    }

    pub async fn wait_complete(&mut self) -> Result<()> {
        match AsyncIeee4882::new(&mut self.scpi).wait_complete().await {
            Ok(v) => Ok(v),
            Err(e) => Err(e.with_comm_context(&self.address.raw, Some("*OPC?"), 1)),
        }
    }

    pub async fn check_errors(&mut self) -> Result<Vec<String>> {
        match self.scpi.check_errors().await {
            Ok(v) => Ok(v),
            Err(e) => Err(e.with_comm_context(&self.address.raw, Some("SYST:ERR?"), 1)),
        }
    }
}

/// Reuses a single underlying async session across typed views.
pub struct AsyncSessionPool {
    session: Arc<Mutex<AsyncInstrumentSession>>,
}

impl AsyncSessionPool {
    pub fn new(session: AsyncInstrumentSession) -> Self {
        Self {
            session: Arc::new(Mutex::new(session)),
        }
    }

    pub fn session(&self) -> Arc<Mutex<AsyncInstrumentSession>> {
        self.session.clone()
    }

    pub fn open_session(&self) -> AsyncPooledSession {
        AsyncPooledSession {
            session: self.session.clone(),
        }
    }
}

/// Handle to a pooled async session (shared transport).
pub struct AsyncPooledSession {
    session: Arc<Mutex<AsyncInstrumentSession>>,
}

impl AsyncPooledSession {
    pub async fn lock(&self) -> tokio::sync::MutexGuard<'_, AsyncInstrumentSession> {
        self.session.lock().await
    }
}

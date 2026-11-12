use instrument_api::{
    Candidate, ConnectionOwnership, DriverError, Endpoint, ErrorKind, ExecutionMode, Operation,
    Result,
};
use instrument_runtime::{DeviceHandle, DriverRegistry, MatchEvidence, Selection};
use std::{collections::BTreeSet, sync::Arc};

/// Immutable candidates supplied by a host enumerator, fixture, or application.
/// Creating or selecting a catalog never probes a device or opens a connection.
#[derive(Clone)]
pub struct DeviceCatalog {
    registry: Arc<DriverRegistry>,
    candidates: Arc<[Candidate]>,
}
impl DeviceCatalog {
    pub fn new(registry: DriverRegistry, candidates: Vec<Candidate>) -> Result<Self> {
        let mut endpoints = BTreeSet::new();
        if candidates.iter().any(|c| {
            !endpoints.insert((
                c.endpoint.scheme.trim().to_ascii_lowercase(),
                c.endpoint.address.clone(),
            ))
        }) {
            return Err(DriverError::new(
                ErrorKind::InvalidArgument,
                "duplicate catalog endpoint",
            ));
        }
        Ok(Self {
            registry: Arc::new(registry),
            candidates: candidates.into(),
        })
    }
    pub fn candidates(&self) -> &[Candidate] {
        &self.candidates
    }
    pub fn device(&self, endpoint: &Endpoint) -> Result<DeviceRef> {
        let candidate = self
            .candidates
            .iter()
            .find(|c| {
                c.endpoint
                    .scheme
                    .trim()
                    .eq_ignore_ascii_case(endpoint.scheme.trim())
                    && c.endpoint.address == endpoint.address
            })
            .cloned()
            .ok_or_else(|| {
                DriverError::new(ErrorKind::DriverNotFound, "endpoint absent from catalog")
            })?;
        Ok(DeviceRef {
            registry: self.registry.clone(),
            candidate,
        })
    }
}
/// Multiple class views should borrow one returned handle. Opening again creates
/// another connection only when the application explicitly requests it.
#[derive(Clone)]
pub struct DeviceRef {
    registry: Arc<DriverRegistry>,
    candidate: Candidate,
}
impl DeviceRef {
    pub fn candidate(&self) -> &Candidate {
        &self.candidate
    }
    pub fn select(&self, selection: &Selection) -> Result<MatchEvidence> {
        self.registry.select(&self.candidate, selection)
    }
    pub fn open(
        &self,
        selection: &Selection,
        required: &BTreeSet<Operation>,
        ownership: ConnectionOwnership,
    ) -> Result<DeviceHandle> {
        self.registry.open(
            &self.candidate,
            selection,
            required,
            ExecutionMode::Sync,
            ownership,
        )
    }
    #[cfg(feature = "async")]
    pub async fn open_native(
        &self,
        selection: &Selection,
        required: &BTreeSet<Operation>,
        ownership: ConnectionOwnership,
    ) -> Result<instrument_runtime::NativeDeviceHandle> {
        self.registry
            .open_native(&self.candidate, selection, required, ownership)
            .await
    }
    #[cfg(feature = "async")]
    pub async fn open_worker(
        &self,
        selection: &Selection,
        required: &BTreeSet<Operation>,
        ownership: ConnectionOwnership,
    ) -> Result<instrument_runtime::WorkerDeviceHandle> {
        self.registry
            .open_worker(&self.candidate, selection, required, ownership)
            .await
    }
}

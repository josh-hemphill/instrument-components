//! Explicit, protocol-neutral composition. Discovery consumes passive host evidence;
//! opening invokes only registered factories. No built-in device implementations.
pub use instrument_api as api;
pub use instrument_api::catalog as classes;
pub use instrument_runtime as runtime;
pub mod catalog;
pub use catalog::{DeviceCatalog, DeviceRef};
pub mod prelude {
    pub use crate::{DeviceCatalog, DeviceRef};
    pub use instrument_api::catalog::*;
    pub use instrument_api::{
        Candidate, ConnectionOwnership, DriverFactory, Endpoint, ErrorKind, ExecutionMode,
        Identity, Result,
    };
    pub use instrument_runtime::{DeviceHandle, DriverRegistry, Selection};
}

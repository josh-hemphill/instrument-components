//! VISA-agnostic instrument control: transport, SCPI, discovery, and mock fixtures.
//!
//! This crate has no VISA dependency. Use [`Transport`] for byte I/O,
//! [`ScpiSession`] for SCPI framing, and [`ScriptedFixture`] for CI mocks.
//!
//! Enable the `async` feature for `AsyncTransport` and `AsyncScpiSession`
//! with the same framing and retry semantics as the sync path.
//!
//! For the full discovery and typed-class API, use the `instrument-components` facade.

pub mod address;
#[cfg(feature = "async")]
pub mod async_session;
#[cfg(feature = "async")]
pub mod async_transport;
pub mod classifier;
pub mod connect;
pub mod diagnostics;
pub mod enumerator;
pub mod error;
pub mod identity;
pub mod ieee4882;
pub mod kind;
pub mod mock;
pub mod probe_policy;
pub mod registry;
pub mod scpi;
pub mod session;
pub mod transport;

#[cfg(feature = "record")]
pub mod recording;

pub use address::{AddressParts, InterfaceKind, ResourceAddress};
#[cfg(feature = "async")]
pub use async_session::{
    AsyncInstrumentSession, AsyncPooledSession, AsyncSessionOpener, AsyncSessionPool,
};
#[cfg(feature = "async")]
pub use async_transport::{AsyncTransport, DynAsyncTransport, SyncAsAsyncTransport};
pub use classifier::{
    classify_deep, classify_from_address, classify_from_identity, classify_with_policy,
    ClassifiedKind, ClassifySource, DiscoveredDevice,
};
#[cfg(feature = "async")]
pub use classifier::{classify_deep_async, classify_with_policy_async};
pub use connect::{AccessMode, ConnectOptions};
pub use diagnostics::{CommsEvent, CommsEventKind, CommsObserver, DeviceHealth, Diagnostics};
pub use enumerator::{RawResource, ResourceEnumerator, StaticEnumerator};
pub use error::{Error, Result, TransportError};
pub use identity::{DeviceId, DeviceIdentity, Idn};
#[cfg(feature = "async")]
pub use ieee4882::AsyncIeee4882;
pub use ieee4882::Ieee4882;
pub use kind::InstrumentKind;
pub use mock::{MockTransport, ScriptStep, ScriptedFixture, Transcript};
pub use probe_policy::ProbePolicy;
pub use registry::ModelRegistry;
#[cfg(feature = "async")]
pub use scpi::AsyncScpiSession;
pub use scpi::{
    is_opc_supported_reply, is_syst_err_supported_reply, parse_f64, parse_f64_csv, ScpiSession,
};
pub use session::{InstrumentSession, SessionOpener, SessionPool};
pub use transport::{Transport, TransportIdentity};

#[cfg(feature = "record")]
pub use recording::RecordingTransport;

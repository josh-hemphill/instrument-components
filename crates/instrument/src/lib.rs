//! High-level instrument discovery and typed IVI-inspired classes.
//!
//! # Quick start (mock, no VISA)
//!
//! ```no_run
//! use instrument::prelude::*;
//!
//! let fixture = ScriptedFixture::builder()
//!     .idn("Acme", "DMM1", "SN1", "1.0")
//!     .kinds([InstrumentKind::Dmm])
//!     .on_query(":MEAS:VOLT:DC?", "1.0")
//!     .build();
//! let catalog = DeviceCatalog::from_fixture("mock://dmm", fixture)?;
//! # Ok::<(), instrument::Error>(())
//! ```
//!
//! # Quick start (hardware)
//!
//! Requires the `visa` feature (enabled by default).
//!
//! ```ignore
//! use instrument::prelude::*;
//! let catalog = Discovery::visa()?.scan()?;
//! ```
//!
//! # Quick start (async mock, `tokio` feature)
//!
//! ```ignore
//! use instrument::prelude::*;
//!
//! #[tokio::main]
//! async fn main() -> Result<()> {
//!     let fixture = ScriptedFixture::builder()
//!         .idn("Acme", "DMM1", "SN1", "1.0")
//!         .kinds([InstrumentKind::Dmm])
//!         .on_query(":MEAS:VOLT:DC?", "1.0")
//!         .build();
//!     let catalog = AsyncDeviceCatalog::from_fixture("mock://dmm", fixture).await?;
//!     let mut dmm = catalog.open_dmm("mock://dmm").await?;
//!     println!("{} V", dmm.measure_voltage_dc(None).await?);
//!     Ok(())
//! }
//! ```
//!
//! See the [user guide](https://josh-hemphill.github.io/instrument-components/),
//! [getting started](https://josh-hemphill.github.io/instrument-components/getting-started/),
//! and [async guide](https://josh-hemphill.github.io/instrument-components/rust/async/).

pub mod catalog;
pub mod device;
pub mod discovery;
pub mod mock_backend;
pub mod prelude;

#[cfg(feature = "tokio")]
pub mod async_catalog;
#[cfg(feature = "tokio")]
pub mod async_device;
#[cfg(feature = "tokio")]
pub mod async_discovery;

pub mod classes {
    pub mod counter;
    pub mod dc_psu;
    pub(crate) mod dialect_io;
    pub mod dmm;
    pub mod fgen;
    pub mod power_meter;
    pub mod scope;
    pub mod spectrum_analyzer;
    pub mod switch;

    #[cfg(feature = "tokio")]
    pub mod async_counter;
    #[cfg(feature = "tokio")]
    pub mod async_dc_psu;
    #[cfg(feature = "tokio")]
    pub mod async_dmm;
    #[cfg(feature = "tokio")]
    pub mod async_fgen;
    #[cfg(feature = "tokio")]
    pub mod async_power_meter;
    #[cfg(feature = "tokio")]
    pub mod async_scope;
    #[cfg(feature = "tokio")]
    pub mod async_spectrum_analyzer;
    #[cfg(feature = "tokio")]
    pub mod async_switch;

    pub use counter::Counter;
    pub use dc_psu::DcPowerSupply;
    pub use dmm::Dmm;
    pub use fgen::{FunctionGenerator, Waveform};
    pub use power_meter::{PowerMeter, PowerUnit};
    pub use scope::{Oscilloscope, VoltageTrace};
    pub use spectrum_analyzer::SpectrumAnalyzer;
    pub use switch::Switch;

    #[cfg(feature = "tokio")]
    pub use async_counter::AsyncCounter;
    #[cfg(feature = "tokio")]
    pub use async_dc_psu::AsyncDcPowerSupply;
    #[cfg(feature = "tokio")]
    pub use async_dmm::AsyncDmm;
    #[cfg(feature = "tokio")]
    pub use async_fgen::AsyncFunctionGenerator;
    #[cfg(feature = "tokio")]
    pub use async_power_meter::AsyncPowerMeter;
    #[cfg(feature = "tokio")]
    pub use async_scope::AsyncOscilloscope;
    #[cfg(feature = "tokio")]
    pub use async_spectrum_analyzer::AsyncSpectrumAnalyzer;
    #[cfg(feature = "tokio")]
    pub use async_switch::AsyncSwitch;
}

pub use catalog::DeviceCatalog;
pub use device::DeviceRef;
pub use discovery::Discovery;
pub use instrument_core::{
    self, AccessMode, CommsEvent, CommsEventKind, CommsObserver, ConnectOptions, DeviceHealth,
    DeviceId, Diagnostics, DiscoveredDevice, Error, Idn, InstrumentKind, InstrumentSession,
    MockTransport, ProbePolicy, ResourceAddress, Result, ScpiSession, ScriptedFixture, SessionPool,
    Transport, TransportIdentity,
};

#[cfg(feature = "tokio")]
pub use async_catalog::AsyncDeviceCatalog;
#[cfg(feature = "tokio")]
pub use async_device::AsyncDeviceRef;
#[cfg(feature = "tokio")]
pub use async_discovery::AsyncDiscovery;

#[cfg(feature = "tokio")]
pub use instrument_core::{
    AsyncInstrumentSession, AsyncScpiSession, AsyncSessionOpener, AsyncSessionPool, AsyncTransport,
    DynAsyncTransport,
};

#[cfg(all(feature = "visa", feature = "tokio"))]
pub use instrument_visa::{InstrumentTokioAdapter, VisaAsyncSessionOpener, VisaAsyncTransport};

#[cfg(feature = "visa")]
pub use instrument_visa::{SharedRm, VisaEnumerator, VisaSessionOpener};

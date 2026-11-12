//! Protocol-neutral typed operations for all eight instrument classes.
#[cfg(feature = "async")]
use crate::DriverFuture;
use crate::{DriverError, ErrorKind, MeasurementStatus, Operation, Result};
mod generated {
    include!("catalog_generated.rs");
}
pub use generated::*;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Unit {
    Volt,
    Ampere,
    Ohm,
    Celsius,
    Kelvin,
    /// Result dimension, never a physical unit returned in a Quantity.
    Temperature,
    Hertz,
    Second,
    Watt,
    Dbm,
    Count,
    Unknown,
    ConfiguredDmm,
    ConfiguredPower,
}
#[derive(Debug, Clone, PartialEq)]
pub struct Quantity {
    pub value: f64,
    pub unit: Unit,
    pub status: MeasurementStatus,
}
#[derive(Debug, Clone, PartialEq)]
pub struct Samples {
    pub values: Vec<f64>,
    pub unit: Unit,
    pub sample_interval_s: Option<f64>,
}
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceIdentity {
    pub manufacturer: String,
    pub model: String,
    pub serial: String,
    pub revision: String,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReplyKind {
    Unit,
    Quantity,
    Boolean,
    Count,
    Samples,
    Trace,
    Identity,
}
#[derive(Debug, Clone, PartialEq)]
pub enum Argument {
    Number(f64),
    OptionalNumber(Option<f64>),
    Channel(u32),
    Count(u32),
    Boolean(bool),
    Symbol(String),
}
#[derive(Debug, Clone, PartialEq)]
pub enum CatalogReply {
    Unit,
    Quantity(Quantity),
    Boolean(bool),
    Count(u32),
    Samples(Samples),
    Identity(DeviceIdentity),
}
fn invalid(message: &str) -> DriverError {
    DriverError::new(ErrorKind::InvalidArgument, message)
}
fn contract() -> DriverError {
    DriverError::new(
        ErrorKind::DriverContractViolation,
        "provider reply kind/unit/value violates operation contract",
    )
}
pub fn physical_unit_matches(expected: Unit, actual: Unit) -> bool {
    match expected {
        Unit::Temperature => matches!(actual, Unit::Celsius | Unit::Kelvin | Unit::Unknown),
        Unit::ConfiguredDmm => matches!(
            actual,
            Unit::Volt | Unit::Ampere | Unit::Ohm | Unit::Celsius | Unit::Kelvin | Unit::Unknown
        ),
        Unit::ConfiguredPower => matches!(actual, Unit::Watt | Unit::Dbm),
        _ => actual == expected,
    }
}
fn validate_number(value: Option<f64>, constraint: &str) -> Result<()> {
    if let Some(v) = value {
        if !v.is_finite()
            || (constraint == "positive" && v <= 0.0)
            || (constraint == "nonnegative" && v < 0.0)
            || (constraint == "percent" && !(0.0..=100.0).contains(&v))
        {
            return Err(invalid("invalid numeric argument"));
        }
    }
    Ok(())
}
fn validate_symbol(value: &str) -> Result<()> {
    if value.is_empty()
        || value.len() > 128
        || !value
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'_')
    {
        return Err(invalid("invalid semantic token"));
    }
    Ok(())
}
impl CatalogReply {
    pub fn validate(&self, request: &CatalogRequest) -> Result<()> {
        let (kind, expected) = request.result_kind();
        let unit_ok = |u| physical_unit_matches(expected, u);
        let valid = match (self, kind) {
            (Self::Unit, ReplyKind::Unit)
            | (Self::Boolean(_), ReplyKind::Boolean)
            | (Self::Count(_), ReplyKind::Count) => true,
            (Self::Quantity(q), ReplyKind::Quantity) => q.value.is_finite() && unit_ok(q.unit),
            (Self::Samples(s), ReplyKind::Samples | ReplyKind::Trace) => {
                !s.values.is_empty()
                    && s.values.len() <= 1_000_000
                    && s.values.iter().all(|v| v.is_finite())
                    && unit_ok(s.unit)
                    && s.sample_interval_s
                        .map(|v| v.is_finite() && v > 0.0)
                        .unwrap_or(true)
            }
            (Self::Identity(i), ReplyKind::Identity) => {
                !i.manufacturer.trim().is_empty() && !i.model.trim().is_empty()
            }
            _ => false,
        };
        if valid {
            Ok(())
        } else {
            Err(contract())
        }
    }
    pub fn into_unit(self) -> Result<()> {
        if self == Self::Unit {
            Ok(())
        } else {
            Err(contract())
        }
    }
    pub fn into_quantity(self) -> Result<Quantity> {
        if let Self::Quantity(v) = self {
            Ok(v)
        } else {
            Err(contract())
        }
    }
    pub fn into_boolean(self) -> Result<bool> {
        if let Self::Boolean(v) = self {
            Ok(v)
        } else {
            Err(contract())
        }
    }
    pub fn into_count(self) -> Result<u32> {
        if let Self::Count(v) = self {
            Ok(v)
        } else {
            Err(contract())
        }
    }
    pub fn into_samples(self) -> Result<Samples> {
        if let Self::Samples(v) = self {
            Ok(v)
        } else {
            Err(contract())
        }
    }
    pub fn into_trace(self) -> Result<Samples> {
        self.into_samples()
    }
    pub fn into_identity(self) -> Result<DeviceIdentity> {
        if let Self::Identity(v) = self {
            Ok(v)
        } else {
            Err(contract())
        }
    }
}
/// Called under one runtime lease; provider owns all shared state and protocol details.
pub trait CatalogBackend: Send {
    fn invoke(&mut self, request: CatalogRequest) -> Result<CatalogReply>;
}
pub trait CatalogExecutor {
    fn invoke(&self, request: CatalogRequest) -> Result<CatalogReply>;
}
#[cfg(feature = "async")]
pub trait AsyncCatalogBackend: Send {
    fn invoke_async(&mut self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply>;
}
#[cfg(feature = "async")]
pub trait AsyncCatalogExecutor {
    fn invoke_async(&self, request: CatalogRequest) -> DriverFuture<'_, CatalogReply>;
}
#[cfg(feature = "async")]
pub trait LocalCatalogBackend {
    fn invoke(&mut self, request: CatalogRequest) -> Result<CatalogReply>;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParameterKind {
    Number,
    OptionalNumber,
    Channel,
    Count,
    Boolean,
    Symbol,
    Waveform,
    PowerUnit,
    SenseMode,
}
#[derive(Debug, Clone)]
pub struct ParameterSpec {
    pub name: &'static str,
    pub kind: ParameterKind,
    pub constraint: &'static str,
}
#[derive(Debug, Clone)]
pub struct OperationMetadata {
    pub kind: &'static str,
    pub parameters: Vec<ParameterSpec>,
    pub result: ReplyKind,
    pub unit: Unit,
}

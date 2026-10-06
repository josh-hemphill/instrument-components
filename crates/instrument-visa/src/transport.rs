use crate::error::map_visa_error;
use instrument_core::address::InterfaceKind;
use instrument_core::connect::ConnectOptions;
use instrument_core::error::{Error, Result, TransportError};
use instrument_core::transport::{Transport, TransportIdentity};
use std::io::{Read, Write};
use std::time::Duration;
use visa_rs::enums::attribute::{
    AttrManfId, AttrManfName, AttrModelCode, AttrModelName, AttrTmoValue, AttrUsbSerialNum,
    HasAttribute, SpecAttr,
};
use visa_rs::prelude::Instrument;

/// Converts a duration to VISA timeout milliseconds (platform-specific `ViUInt32` width).
pub(crate) fn visa_timeout_ms(timeout: Duration) -> visa_rs::vs::ViUInt32 {
    timeout.as_millis().min(visa_rs::vs::ViUInt32::MAX as u128) as visa_rs::vs::ViUInt32
}

/// VISA instrument session transport.
pub struct VisaTransport {
    instrument: Instrument,
    identity: TransportIdentity,
}

impl VisaTransport {
    pub fn new(instrument: Instrument) -> Self {
        Self {
            instrument,
            identity: TransportIdentity::default(),
        }
    }

    pub fn with_identity(mut self, identity: TransportIdentity) -> Self {
        self.identity = identity;
        self
    }

    pub fn instrument(&self) -> &Instrument {
        &self.instrument
    }
}

impl Transport for VisaTransport {
    fn write(&mut self, data: &[u8]) -> Result<()> {
        (&self.instrument).write_all(data).map_err(|e| {
            if e.kind() == std::io::ErrorKind::TimedOut {
                Error::Timeout
            } else {
                Error::Transport(TransportError::Io(e.to_string()))
            }
        })
    }

    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        match (&self.instrument).read(buf) {
            Ok(0) => Err(Error::Transport(TransportError::Closed)),
            Ok(n) => Ok(n),
            Err(e) if e.kind() == std::io::ErrorKind::TimedOut => Err(Error::Timeout),
            Err(e) => Err(Error::Transport(TransportError::Io(e.to_string()))),
        }
    }

    fn clear(&mut self) -> Result<()> {
        self.instrument.clear().map_err(map_visa_error)
    }

    fn set_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        let attr = AttrTmoValue::new_checked(visa_timeout_ms(timeout))
            .ok_or_else(|| Error::Parse("invalid VISA timeout value".into()))?;
        self.instrument.set_attr(attr).map_err(map_visa_error)
    }

    fn identity(&self) -> TransportIdentity {
        self.identity.clone()
    }

    fn configure(&mut self, opts: &ConnectOptions) -> Result<()> {
        self.set_read_timeout(opts.io_timeout())
    }
}

/// Reads VISA attributes for identity hints without SCPI.
pub fn read_identity_from_instrument(instr: &Instrument) -> TransportIdentity {
    let manf_id = AttrManfId::get_from(instr)
        .ok()
        .map(|a| a.into_inner() as u32);
    let model_code = AttrModelCode::get_from(instr)
        .ok()
        .map(|a| a.into_inner() as u32);
    let manf_name = AttrManfName::get_from(instr)
        .ok()
        .map(|a| a.into_inner().to_string_lossy().into_owned());
    let model_name = AttrModelName::get_from(instr)
        .ok()
        .map(|a| a.into_inner().to_string_lossy().into_owned());
    let serial = AttrUsbSerialNum::get_from(instr)
        .ok()
        .map(|a| a.into_inner().to_string_lossy().into_owned());

    TransportIdentity {
        manufacturer: manf_name,
        model: model_name,
        serial,
        interface: InterfaceKind::Unknown,
        manf_id,
        model_code,
    }
}

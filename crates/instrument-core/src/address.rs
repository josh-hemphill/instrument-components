use crate::error::{Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};

/// VISA interface kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
#[non_exhaustive]
pub enum InterfaceKind {
    Usb,
    Gpib,
    Tcpip,
    Serial,
    Vxi,
    Pxi,
    #[default]
    Unknown,
}

/// Parsed components of a VISA resource string.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct AddressParts {
    pub board: Option<u32>,
    pub primary_address: Option<u32>,
    pub secondary_address: Option<u32>,
    pub vid: Option<String>,
    pub pid: Option<String>,
    pub serial: Option<String>,
    pub host: Option<String>,
    pub port: Option<u16>,
    pub lane: Option<String>,
}

/// Typed VISA resource address with canonical dedup key.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ResourceAddress {
    pub interface: InterfaceKind,
    pub raw: String,
    pub components: AddressParts,
    dedup_key: u64,
}

impl ResourceAddress {
    /// Parses a VISA resource string into a typed address.
    pub fn parse(raw: &str) -> Result<Self> {
        let trimmed = raw.trim();
        if trimmed.is_empty() {
            return Err(Error::InvalidAddress("empty address".into()));
        }

        let upper = trimmed.to_uppercase();
        let (interface, components) = if upper.starts_with("USB") {
            parse_usb(trimmed)?
        } else if upper.starts_with("GPIB") {
            parse_gpib(trimmed)?
        } else if upper.starts_with("TCPIP") || upper.starts_with("TCP") {
            parse_tcpip(trimmed)?
        } else if upper.starts_with("ASRL") {
            parse_serial(trimmed)?
        } else if upper.starts_with("VXI") {
            (InterfaceKind::Vxi, AddressParts::default())
        } else if upper.starts_with("PXI") {
            (InterfaceKind::Pxi, AddressParts::default())
        } else {
            (InterfaceKind::Unknown, AddressParts::default())
        };

        let mut addr = Self {
            interface,
            raw: trimmed.to_string(),
            components,
            dedup_key: 0,
        };
        addr.dedup_key = addr.compute_dedup_key();
        Ok(addr)
    }

    /// Returns the canonical dedup key for merge operations.
    pub fn dedup_key(&self) -> u64 {
        self.dedup_key
    }

    fn compute_dedup_key(&self) -> u64 {
        let mut hasher = DefaultHasher::new();
        self.interface.hash(&mut hasher);
        self.raw.to_uppercase().hash(&mut hasher);
        hasher.finish()
    }
}

fn parse_usb(raw: &str) -> Result<(InterfaceKind, AddressParts)> {
    // USB0::0x0957::0x0607::SN123::INSTR
    let parts: Vec<&str> = raw.split("::").collect();
    let vid = parts.get(1).map(|s| normalize_hex_id(s));
    let pid = parts.get(2).map(|s| normalize_hex_id(s));
    let serial = parts.get(3).map(|s| s.to_string());
    Ok((
        InterfaceKind::Usb,
        AddressParts {
            vid,
            pid,
            serial,
            ..Default::default()
        },
    ))
}

fn parse_gpib(raw: &str) -> Result<(InterfaceKind, AddressParts)> {
    let parts: Vec<&str> = raw.split("::").collect();
    let board = parts
        .first()
        .and_then(|s| s.strip_prefix("GPIB"))
        .and_then(|s| s.parse().ok());
    let primary_address = parts.get(1).and_then(|s| s.parse().ok());
    let secondary_address = parts.get(2).and_then(|s| s.parse().ok());
    Ok((
        InterfaceKind::Gpib,
        AddressParts {
            board,
            primary_address,
            secondary_address,
            ..Default::default()
        },
    ))
}

fn parse_tcpip(raw: &str) -> Result<(InterfaceKind, AddressParts)> {
    let parts: Vec<&str> = raw.split("::").collect();
    let host = parts.get(1).map(|s| s.to_string());
    let port = if parts.len() == 4 && parts[3].eq_ignore_ascii_case("SOCKET") {
        parts[2].parse().ok()
    } else {
        None
    };
    Ok((
        InterfaceKind::Tcpip,
        AddressParts {
            host,
            port,
            ..Default::default()
        },
    ))
}

fn parse_serial(raw: &str) -> Result<(InterfaceKind, AddressParts)> {
    let board = raw
        .split("::")
        .next()
        .and_then(|s| s.strip_prefix("ASRL"))
        .and_then(|s| s.parse().ok());
    Ok((
        InterfaceKind::Serial,
        AddressParts {
            board,
            ..Default::default()
        },
    ))
}

fn normalize_hex_id(s: &str) -> String {
    s.trim()
        .trim_start_matches("0x")
        .trim_start_matches("0X")
        .to_uppercase()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_usb_address() {
        let addr = ResourceAddress::parse("USB0::0x0957::0x0607::MY123::INSTR").unwrap();
        assert_eq!(addr.interface, InterfaceKind::Usb);
        assert_eq!(addr.components.vid.as_deref(), Some("0957"));
        assert_eq!(addr.components.pid.as_deref(), Some("0607"));
    }

    #[test]
    fn dedup_is_case_insensitive() {
        let a = ResourceAddress::parse("usb0::0x0957::0x0607::SN::INSTR").unwrap();
        let b = ResourceAddress::parse("USB0::0x0957::0x0607::SN::INSTR").unwrap();
        assert_eq!(a.dedup_key(), b.dedup_key());
    }
}

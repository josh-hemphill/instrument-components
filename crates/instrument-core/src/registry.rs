use crate::kind::InstrumentKind;
use serde::Deserialize;
use std::collections::HashMap;
use std::path::Path;

/// Model registry entry — hint only, not authoritative.
#[derive(Debug, Clone, Deserialize)]
struct RegistryFile {
    #[serde(default)]
    entry: Vec<ModelEntry>,
    #[serde(default)]
    usb_entry: Vec<UsbEntry>,
}

#[derive(Debug, Clone, Deserialize)]
struct ModelEntry {
    manufacturer: String,
    model: String,
    kinds: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
struct UsbEntry {
    vid: String,
    pid: String,
    manufacturer: Option<String>,
    model: Option<String>,
    kinds: Vec<String>,
}

/// IVI-registry-inspired model → kinds lookup (hints only).
#[derive(Debug, Default, Clone)]
pub struct ModelRegistry {
    by_model: HashMap<String, Vec<InstrumentKind>>,
    by_usb: HashMap<(String, String), UsbHint>,
    runtime: HashMap<String, Vec<InstrumentKind>>,
}

#[derive(Debug, Clone)]
pub struct UsbHint {
    pub manufacturer: Option<String>,
    pub model: Option<String>,
    pub kinds: Vec<InstrumentKind>,
}

impl ModelRegistry {
    pub fn embedded() -> Self {
        Self::default()
    }

    pub fn from_toml(content: &str) -> std::result::Result<Self, String> {
        let file: RegistryFile =
            toml::from_str(content).map_err(|e| format!("registry parse: {e}"))?;
        let mut registry = Self::default();
        for entry in file.entry {
            let key = normalize_key(&entry.manufacturer, &entry.model);
            registry.by_model.insert(key, parse_kinds(&entry.kinds));
        }
        for entry in file.usb_entry {
            let key = (entry.vid.to_uppercase(), entry.pid.to_uppercase());
            registry.by_usb.insert(
                key,
                UsbHint {
                    manufacturer: entry.manufacturer,
                    model: entry.model,
                    kinds: parse_kinds(&entry.kinds),
                },
            );
        }
        Ok(registry)
    }

    pub fn load_path(path: impl AsRef<Path>) -> Result<Self, String> {
        let content =
            std::fs::read_to_string(path.as_ref()).map_err(|e| format!("read registry: {e}"))?;
        Self::from_toml(&content)
    }

    pub fn merge(&mut self, other: &Self) {
        for (k, v) in &other.by_model {
            self.by_model.entry(k.clone()).or_insert_with(|| v.clone());
        }
        for (k, v) in &other.by_usb {
            self.by_usb.entry(k.clone()).or_insert_with(|| v.clone());
        }
        for (k, v) in &other.runtime {
            self.runtime.insert(k.clone(), v.clone());
        }
    }

    pub fn add_runtime(&mut self, manufacturer: &str, model: &str, kinds: Vec<InstrumentKind>) {
        let key = normalize_key(manufacturer, model);
        self.runtime.insert(key, kinds);
    }

    pub fn lookup_model(&self, manufacturer: &str, model: &str) -> Option<Vec<InstrumentKind>> {
        let key = normalize_key(manufacturer, model);
        self.runtime
            .get(&key)
            .or_else(|| self.by_model.get(&key))
            .cloned()
    }

    pub fn lookup_usb(&self, vid: &str, pid: &str) -> Option<UsbHint> {
        let key = (vid.to_uppercase(), pid.to_uppercase());
        self.by_usb.get(&key).cloned()
    }
}

fn normalize_key(manufacturer: &str, model: &str) -> String {
    format!(
        "{}|{}",
        manufacturer.trim().to_lowercase(),
        model.trim().to_lowercase()
    )
}

fn parse_kinds(labels: &[String]) -> Vec<InstrumentKind> {
    labels
        .iter()
        .filter_map(|l| InstrumentKind::from_str_label(l))
        .collect()
}

// Use minimal toml parsing - we need toml crate
// Add toml to instrument-core dependencies

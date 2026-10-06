#[cfg(feature = "async")]
mod async_probes;
mod probes;

use crate::address::ResourceAddress;
use crate::identity::{DeviceId, DeviceIdentity, Idn};
use crate::kind::InstrumentKind;
use crate::probe_policy::ProbePolicy;
use crate::registry::ModelRegistry;
use crate::scpi::ScpiSession;
use crate::transport::TransportIdentity;
pub use probes::valid_probe_reply;
use probes::{
    probe_any, COUNTER_READONLY_COMMANDS, DMM_ACQUISITION_COMMANDS, DMM_READONLY_COMMANDS,
    FGEN_READONLY_COMMANDS, PROBE_TIMEOUT, PSU_READONLY_COMMANDS, PWRMETER_READONLY_COMMANDS,
    SCOPE_READONLY_COMMANDS, SPECAN_READONLY_COMMANDS, SWITCH_READONLY_COMMANDS,
};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[cfg(feature = "async")]
use crate::scpi::AsyncScpiSession;
#[cfg(feature = "async")]
use async_probes::probe_any_async;

/// Source layer for a classification result.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ClassifySource {
    ResourceParse,
    VisaAttributes,
    ModelRegistry,
    ScpiIdn,
    CapabilityProbe,
    UserOverride,
}

/// A classified instrument kind with confidence.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ClassifiedKind {
    pub kind: InstrumentKind,
    pub confidence: u8,
    pub source: ClassifySource,
}

/// A discovered device in the catalog.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DiscoveredDevice {
    pub address: ResourceAddress,
    pub identity: DeviceIdentity,
    pub supported_kinds: Vec<InstrumentKind>,
    pub classification: Vec<ClassifiedKind>,
    pub reachable: bool,
    pub error: Option<String>,
}

impl DiscoveredDevice {
    /// Returns a stable identity for instrument replacement workflows.
    pub fn device_id(&self) -> DeviceId {
        DeviceId::from_identity(&self.identity, &self.address.raw)
    }
}

/// Classifies kinds from parsed resource address (layer 1).
pub fn classify_from_address(
    address: &ResourceAddress,
    registry: &ModelRegistry,
) -> (DeviceIdentity, Vec<ClassifiedKind>) {
    let mut identity = DeviceIdentity::default();
    let mut kinds = Vec::new();

    if let (Some(vid), Some(pid)) = (
        address.components.vid.as_deref(),
        address.components.pid.as_deref(),
    ) {
        if let Some(hint) = registry.lookup_usb(vid, pid) {
            identity.manufacturer = hint.manufacturer.clone();
            identity.model = hint.model.clone();
            for kind in &hint.kinds {
                kinds.push(ClassifiedKind {
                    kind: *kind,
                    confidence: 40,
                    source: ClassifySource::ModelRegistry,
                });
            }
        }
    }

    if identity.serial.is_none() {
        identity.serial = address.components.serial.clone();
    }

    if kinds.is_empty() {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Unknown,
            confidence: 10,
            source: ClassifySource::ResourceParse,
        });
    }

    (identity, kinds)
}

/// Classifies kinds from transport identity hints (layer 2).
pub fn classify_from_transport_hint(
    hint: &TransportIdentity,
    registry: &ModelRegistry,
) -> (DeviceIdentity, Vec<ClassifiedKind>) {
    let identity = DeviceIdentity {
        manufacturer: hint.manufacturer.clone(),
        model: hint.model.clone(),
        serial: hint.serial.clone(),
        firmware: None,
        options: None,
    };
    let mut kinds = Vec::new();

    if let (Some(m), Some(model)) = (&hint.manufacturer, &hint.model) {
        if let Some(registry_kinds) = registry.lookup_model(m, model) {
            for kind in registry_kinds {
                kinds.push(ClassifiedKind {
                    kind,
                    confidence: 45,
                    source: ClassifySource::ModelRegistry,
                });
            }
        }
    }

    if kinds.is_empty() && hint.manf_id.is_some() {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Unknown,
            confidence: 15,
            source: ClassifySource::VisaAttributes,
        });
    }

    (identity, kinds)
}

/// Classifies kinds from SCPI *IDN? (layer 4).
pub fn classify_from_identity(
    idn: &Idn,
    registry: &ModelRegistry,
) -> (DeviceIdentity, Vec<ClassifiedKind>) {
    let identity = DeviceIdentity::from_idn(idn);
    let mut kinds = Vec::new();

    if let Some(registry_kinds) = registry.lookup_model(&idn.manufacturer, &idn.model) {
        for kind in registry_kinds {
            kinds.push(ClassifiedKind {
                kind,
                confidence: 60,
                source: ClassifySource::ModelRegistry,
            });
        }
    }

    kinds.push(ClassifiedKind {
        kind: InstrumentKind::Unknown,
        confidence: 30,
        source: ClassifySource::ScpiIdn,
    });

    (identity, kinds)
}

/// Capability probing (layer 5) using the given policy.
pub fn classify_with_policy(session: &mut ScpiSession, policy: ProbePolicy) -> Vec<ClassifiedKind> {
    match policy {
        ProbePolicy::None => Vec::new(),
        ProbePolicy::ReadOnly => classify_readonly_probes(session),
        ProbePolicy::Full => {
            let mut kinds = classify_readonly_probes(session);
            kinds.extend(classify_acquisition_probes(session));
            kinds
        }
    }
}

/// Full capability probing including acquisition-triggering queries.
pub fn classify_deep(session: &mut ScpiSession) -> Vec<ClassifiedKind> {
    classify_with_policy(session, ProbePolicy::Full)
}

fn classify_readonly_probes(session: &mut ScpiSession) -> Vec<ClassifiedKind> {
    let mut kinds = Vec::new();

    if probe_any(session, DMM_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Dmm,
            confidence: 80,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any(session, PSU_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::DcPowerSupply,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any(session, FGEN_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::FunctionGenerator,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any(session, SCOPE_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Oscilloscope,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any(session, SWITCH_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Switch,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any(session, COUNTER_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Counter,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any(session, PWRMETER_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::PowerMeter,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any(session, SPECAN_READONLY_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::SpectrumAnalyzer,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    kinds
}

fn classify_acquisition_probes(session: &mut ScpiSession) -> Vec<ClassifiedKind> {
    let mut kinds = Vec::new();

    if probe_any(session, DMM_ACQUISITION_COMMANDS, PROBE_TIMEOUT) {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Dmm,
            confidence: 90,
            source: ClassifySource::CapabilityProbe,
        });
    }

    kinds
}

/// Async capability probing (layer 5) using the given policy.
#[cfg(feature = "async")]
pub async fn classify_with_policy_async(
    session: &mut AsyncScpiSession,
    policy: ProbePolicy,
) -> Vec<ClassifiedKind> {
    match policy {
        ProbePolicy::None => Vec::new(),
        ProbePolicy::ReadOnly => classify_readonly_probes_async(session).await,
        ProbePolicy::Full => {
            let mut kinds = classify_readonly_probes_async(session).await;
            kinds.extend(classify_acquisition_probes_async(session).await);
            kinds
        }
    }
}

/// Full async capability probing including acquisition-triggering queries.
#[cfg(feature = "async")]
pub async fn classify_deep_async(session: &mut AsyncScpiSession) -> Vec<ClassifiedKind> {
    classify_with_policy_async(session, ProbePolicy::Full).await
}

#[cfg(feature = "async")]
async fn classify_readonly_probes_async(session: &mut AsyncScpiSession) -> Vec<ClassifiedKind> {
    let mut kinds = Vec::new();

    if probe_any_async(session, DMM_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Dmm,
            confidence: 80,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any_async(session, PSU_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::DcPowerSupply,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any_async(session, FGEN_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::FunctionGenerator,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any_async(session, SCOPE_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Oscilloscope,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any_async(session, SWITCH_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Switch,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any_async(session, COUNTER_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Counter,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any_async(session, PWRMETER_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::PowerMeter,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    if probe_any_async(session, SPECAN_READONLY_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::SpectrumAnalyzer,
            confidence: 85,
            source: ClassifySource::CapabilityProbe,
        });
    }

    kinds
}

#[cfg(feature = "async")]
async fn classify_acquisition_probes_async(session: &mut AsyncScpiSession) -> Vec<ClassifiedKind> {
    let mut kinds = Vec::new();

    if probe_any_async(session, DMM_ACQUISITION_COMMANDS, PROBE_TIMEOUT).await {
        kinds.push(ClassifiedKind {
            kind: InstrumentKind::Dmm,
            confidence: 90,
            source: ClassifySource::CapabilityProbe,
        });
    }

    kinds
}

/// Merges classification layers into final supported kinds.
pub fn merge_classifications(
    layers: impl IntoIterator<Item = Vec<ClassifiedKind>>,
    user_override: Option<&[InstrumentKind]>,
) -> (Vec<InstrumentKind>, Vec<ClassifiedKind>) {
    if let Some(override_kinds) = user_override {
        let classified: Vec<_> = override_kinds
            .iter()
            .map(|kind| ClassifiedKind {
                kind: *kind,
                confidence: 100,
                source: ClassifySource::UserOverride,
            })
            .collect();
        return (override_kinds.to_vec(), classified);
    }

    let mut by_kind: HashMap<InstrumentKind, ClassifiedKind> = HashMap::new();
    let mut all = Vec::new();

    for layer in layers {
        for entry in layer {
            all.push(entry.clone());
            by_kind
                .entry(entry.kind)
                .and_modify(|existing| {
                    if entry.confidence > existing.confidence {
                        *existing = entry.clone();
                    }
                })
                .or_insert(entry);
        }
    }

    let mut supported: Vec<_> = by_kind
        .values()
        .filter(|k| k.kind != InstrumentKind::Unknown && k.confidence >= 40)
        .map(|k| k.kind)
        .collect();
    supported.sort_by_key(|k| format!("{k:?}"));
    supported.dedup();

    if supported.is_empty() {
        supported.push(InstrumentKind::Unknown);
    }

    (supported, all)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::registry::ModelRegistry;

    #[test]
    fn registry_hint_for_usb_vid_pid() {
        let registry = ModelRegistry::embedded();
        let addr = ResourceAddress::parse("USB0::0x0957::0x0607::SN::INSTR").unwrap();
        let (_, kinds) = classify_from_address(&addr, &registry);
        assert!(kinds.iter().any(|k| k.kind == InstrumentKind::Dmm));
    }

    #[test]
    fn probe_policy_none_skips_capability_queries() {
        use crate::connect::ConnectOptions;
        use crate::mock::{MockTransport, ScriptStep};
        use crate::probe_policy::ProbePolicy;

        let transport = MockTransport::from_script(vec![
            ScriptStep::Write {
                data: ":SENS:FUNC?\n".into(),
            },
            ScriptStep::Read {
                data: "VOLT\n".into(),
            },
        ]);
        let mut session = ScpiSession::new(Box::new(transport), ConnectOptions::default()).unwrap();
        let kinds = super::classify_with_policy(&mut session, ProbePolicy::None);
        assert!(kinds.is_empty());
    }

    #[test]
    fn user_override_wins() {
        let (kinds, _) = merge_classifications(
            vec![vec![ClassifiedKind {
                kind: InstrumentKind::Unknown,
                confidence: 10,
                source: ClassifySource::ResourceParse,
            }]],
            Some(&[InstrumentKind::Dmm]),
        );
        assert_eq!(kinds, vec![InstrumentKind::Dmm]);
    }

    #[test]
    fn scope_readonly_probe_succeeds() {
        use crate::connect::ConnectOptions;
        use crate::mock::{MockTransport, ScriptStep};
        use probes::{probe_any, PROBE_TIMEOUT, SCOPE_READONLY_COMMANDS};

        let transport = MockTransport::from_script(vec![
            ScriptStep::Write {
                data: ":TIMebase:SCALe?\n".into(),
            },
            ScriptStep::Read {
                data: "1e-3\n".into(),
            },
        ]);
        let mut session = ScpiSession::new(Box::new(transport), ConnectOptions::default()).unwrap();
        assert!(probe_any(
            &mut session,
            SCOPE_READONLY_COMMANDS,
            PROBE_TIMEOUT
        ));
    }

    #[test]
    fn switch_readonly_probe_succeeds() {
        use crate::connect::ConnectOptions;
        use crate::mock::{MockTransport, ScriptStep};
        use probes::{probe_any, PROBE_TIMEOUT, SWITCH_READONLY_COMMANDS};

        let transport = MockTransport::from_script(vec![
            ScriptStep::Write {
                data: ":ROUTe:CLOS?\n".into(),
            },
            ScriptStep::Read { data: "0\n".into() },
        ]);
        let mut session = ScpiSession::new(Box::new(transport), ConnectOptions::default()).unwrap();
        assert!(probe_any(
            &mut session,
            SWITCH_READONLY_COMMANDS,
            PROBE_TIMEOUT
        ));
    }

    #[test]
    fn counter_readonly_probe_succeeds() {
        use crate::connect::ConnectOptions;
        use crate::mock::{MockTransport, ScriptStep};
        use probes::{probe_any, COUNTER_READONLY_COMMANDS, PROBE_TIMEOUT};

        let transport = MockTransport::from_script(vec![
            ScriptStep::Write {
                data: ":COUNter:DATA?\n".into(),
            },
            ScriptStep::Read {
                data: "42\n".into(),
            },
        ]);
        let mut session = ScpiSession::new(Box::new(transport), ConnectOptions::default()).unwrap();
        assert!(probe_any(
            &mut session,
            COUNTER_READONLY_COMMANDS,
            PROBE_TIMEOUT
        ));
    }
}

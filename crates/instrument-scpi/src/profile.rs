use instrument_api::{catalog::*, *};
use std::collections::{BTreeMap, BTreeSet};

pub const MAX_RESPONSE_BYTES: usize = 1_048_576;
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Output {
    DcPowerSupply(u32),
    FunctionGenerator,
}
/// Application-supplied supported channels and complete output inventory.
/// None means unknown; an empty output list is an explicit outputless device.
#[derive(Debug, Clone)]
pub struct InventoryBinding {
    pub channels: Option<Vec<u32>>,
    pub outputs: Option<Vec<Output>>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResponseRole {
    Write,
    Value,
    Completion,
    Preamble,
}
#[derive(Debug, Clone)]
pub struct Step {
    pub command: String,
    pub response: ResponseRole,
    pub when: Option<String>,
}
impl Step {
    pub fn write(command: &str) -> Self {
        Self {
            command: command.into(),
            response: ResponseRole::Write,
            when: None,
        }
    }
    pub fn query(command: &str) -> Self {
        Self {
            command: command.into(),
            response: ResponseRole::Value,
            when: None,
        }
    }
}
#[derive(Debug, Clone)]
pub struct Plan {
    pub operation: CatalogOperation,
    pub steps: Vec<Step>,
    pub tokens: BTreeMap<String, BTreeMap<String, String>>,
    pub defaults: BTreeMap<String, String>,
    /// Physical result-unit declaration for a dimension with source-dependent units.
    pub unit: Option<Unit>,
    pub choices: Vec<PlanChoice>,
}
/// A finite, nonrecursive optional-argument presence branch. All wire data is supplied.
#[derive(Debug, Clone)]
pub struct PlanChoice {
    pub presence: BTreeMap<String, bool>,
    pub steps: Vec<Step>,
    pub tokens: BTreeMap<String, BTreeMap<String, String>>,
    pub defaults: BTreeMap<String, String>,
    pub unit: Option<Unit>,
}
impl PlanChoice {
    pub fn flat_plan(&self, operation: CatalogOperation) -> Plan {
        Plan {
            operation,
            steps: self.steps.clone(),
            tokens: self.tokens.clone(),
            defaults: self.defaults.clone(),
            unit: self.unit,
            choices: Vec::new(),
        }
    }
}
impl Plan {
    pub fn new(operation: CatalogOperation, steps: Vec<Step>) -> Self {
        Self {
            operation,
            steps,
            tokens: BTreeMap::new(),
            defaults: BTreeMap::new(),
            unit: None,
            choices: Vec::new(),
        }
    }
}
#[derive(Debug, Clone)]
pub struct Profile {
    id: String,
    channels: Option<Vec<u32>>,
    outputs: Option<Vec<Output>>,
    plans: BTreeMap<CatalogOperation, Plan>,
}
fn fail(kind: ErrorKind, message: &str) -> DriverError {
    DriverError::new(kind, message)
}
fn invalid(message: &str) -> DriverError {
    fail(ErrorKind::DriverContractViolation, message)
}
fn safe_message(s: &str) -> bool {
    !s.trim().is_empty() && s.len() <= 65_536 && s.bytes().all(|c| (32..127).contains(&c))
}
fn safe_token(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 128
        && s.bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'_' | b'-' | b'.' | b'+'))
}
pub fn placeholders(template: &str) -> Result<BTreeSet<String>> {
    let mut result = BTreeSet::new();
    let mut rest = template;
    while let Some(start) = rest.find('{') {
        if rest[..start].contains('}') {
            return Err(invalid("unmatched template brace"));
        }
        rest = &rest[start + 1..];
        let end = rest
            .find('}')
            .ok_or_else(|| invalid("unmatched template brace"))?;
        let key = &rest[..end];
        if key.is_empty()
            || !key.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'_')
            || !key.as_bytes()[0].is_ascii_alphabetic()
        {
            return Err(invalid("invalid template placeholder"));
        }
        result.insert(key.into());
        rest = &rest[end + 1..];
    }
    if rest.contains('}') {
        return Err(invalid("unmatched template brace"));
    }
    Ok(result)
}
impl Profile {
    pub fn new(
        id: &str,
        channels: Option<Vec<u32>>,
        outputs: Option<Vec<Output>>,
        plans: Vec<Plan>,
    ) -> Result<Self> {
        if id.is_empty()
            || id.len() > 128
            || !id.contains('.')
            || id.split('.').any(str::is_empty)
            || !id.as_bytes()[0].is_ascii_lowercase()
            || !id.bytes().all(|c| {
                c.is_ascii_lowercase() || c.is_ascii_digit() || matches!(c, b'_' | b'.' | b'-')
            })
        {
            return Err(invalid("invalid profile id"));
        }
        for list in [&channels].into_iter().flatten() {
            if list.len() > 4096
                || list.contains(&0)
                || list.iter().copied().collect::<BTreeSet<_>>().len() != list.len()
            {
                return Err(invalid("invalid or duplicate channel inventory"));
            }
        }
        if let Some(list) = &outputs {
            if list.len() > 4096
                || list.iter().copied().collect::<BTreeSet<_>>().len() != list.len()
                || list.iter().any(|output| match output {
                    Output::DcPowerSupply(c) => {
                        *c == 0 || !channels.as_ref().is_some_and(|all| all.contains(c))
                    }
                    Output::FunctionGenerator => false,
                })
            {
                return Err(invalid("invalid/duplicate output inventory"));
            }
        }
        if plans.is_empty() {
            return Err(invalid("profile requires an operation plan"));
        }
        let mut indexed = BTreeMap::new();
        for plan in plans {
            let metadata = plan.operation.metadata();
            if indexed.contains_key(&plan.operation) || plan.steps.len() > 32 {
                return Err(invalid("duplicate operation or excessive plan length"));
            }
            if !plan.choices.is_empty() {
                if !plan.steps.is_empty()
                    || !plan.tokens.is_empty()
                    || !plan.defaults.is_empty()
                    || plan.unit.is_some()
                {
                    return Err(invalid("choice plans cannot also contain a flat plan"));
                }
                validate_choices(id, &channels, &outputs, &plan)?;
                indexed.insert(plan.operation, plan);
                continue;
            }
            if let Some(unit) = plan.unit {
                if metadata.result != ReplyKind::Quantity
                    || !physical_unit_matches(metadata.unit, unit)
                    || matches!(metadata.unit, Unit::ConfiguredDmm | Unit::ConfiguredPower)
                {
                    return Err(invalid(
                        "result unit declaration violates operation dimension",
                    ));
                }
            }
            let parameters: BTreeMap<_, _> = metadata
                .parameters
                .iter()
                .map(|p| (p.name, p.kind))
                .collect();
            let mut used = BTreeSet::new();
            let mut values = 0;
            let mut preambles = 0;
            for step in &plan.steps {
                if !safe_message(&step.command) {
                    return Err(invalid("invalid SCPI message body"));
                }
                let queries = step.command.bytes().filter(|c| *c == b'?').count();
                if (step.response == ResponseRole::Write && queries != 0)
                    || (step.response != ResponseRole::Write && queries != 1)
                {
                    return Err(invalid("message/query response role mismatch"));
                }
                if let Some(when) = &step.when {
                    if parameters.get(when.as_str()) != Some(&ParameterKind::OptionalNumber)
                        || matches!(step.response, ResponseRole::Value | ResponseRole::Preamble)
                    {
                        return Err(invalid("invalid conditional step"));
                    }
                }
                for key in placeholders(&step.command)? {
                    if !parameters.contains_key(key.as_str()) {
                        return Err(invalid("unknown operation placeholder"));
                    }
                    used.insert(key);
                }
                if step.response == ResponseRole::Value {
                    values += 1;
                }
                if step.response == ResponseRole::Preamble {
                    if values > 0 {
                        return Err(invalid("preamble must precede trace data"));
                    }
                    preambles += 1;
                }
            }
            for parameter in &metadata.parameters {
                if parameter.kind != ParameterKind::OptionalNumber && !used.contains(parameter.name)
                {
                    return Err(invalid("required argument ignored by plan"));
                }
                if parameter.kind == ParameterKind::Channel && channels.is_none() {
                    return Err(invalid("channel operations require explicit inventory"));
                }
            }
            for (name, map) in &plan.tokens {
                if !matches!(
                    parameters.get(name.as_str()),
                    Some(
                        ParameterKind::Boolean
                            | ParameterKind::Symbol
                            | ParameterKind::Waveform
                            | ParameterKind::PowerUnit
                            | ParameterKind::SenseMode
                    )
                ) || map.is_empty()
                    || map
                        .iter()
                        .any(|(key, value)| !safe_token(key) || !safe_token(value))
                {
                    return Err(invalid("invalid semantic token mapping"));
                }
                let kind = parameters[name.as_str()];
                if map.keys().any(|key| {
                    kind.semantic_values()
                        .is_some_and(|domain| !domain.contains(&key.as_str()))
                        || kind == ParameterKind::Symbol
                            && (key.is_empty()
                                || !key.chars().all(|c| c.is_ascii_alphanumeric() || c == '_'))
                }) {
                    return Err(invalid("token key is outside the typed semantic domain"));
                }
            }
            for parameter in &metadata.parameters {
                if matches!(
                    parameter.kind,
                    ParameterKind::Boolean
                        | ParameterKind::Symbol
                        | ParameterKind::Waveform
                        | ParameterKind::PowerUnit
                        | ParameterKind::SenseMode
                ) && !plan.tokens.contains_key(parameter.name)
                {
                    return Err(invalid(
                        "semantic arguments require explicit wire token mapping",
                    ));
                }
            }
            for (name, default) in &plan.defaults {
                if parameters.get(name.as_str()) != Some(&ParameterKind::OptionalNumber)
                    || !safe_token(default)
                {
                    return Err(invalid("invalid optional numeric default"));
                }
                if !matches!(default.as_str(), "AUTO" | "DEF" | "MIN" | "MAX") {
                    let value =
                        parse_number(default).map_err(|_| invalid("invalid numeric default"))?;
                    let c = metadata
                        .parameters
                        .iter()
                        .find(|p| p.name == name)
                        .unwrap()
                        .constraint;
                    if c == "positive" && value <= 0.0
                        || c == "nonnegative" && value < 0.0
                        || c == "percent" && !(0.0..=100.0).contains(&value)
                    {
                        return Err(invalid("numeric default violates argument constraint"));
                    }
                }
            }
            let local = plan.operation == CatalogOperation::DcPowerSupplyChannelCount;
            let shutdown = plan.operation == CatalogOperation::Shutdown;
            if local && outputs.is_none() {
                return Err(invalid("channel count requires known inventory"));
            }
            if shutdown && outputs.is_none() {
                return Err(invalid("shutdown requires complete output inventory"));
            }
            if local && !plan.steps.is_empty() {
                return Err(invalid("channel-count metadata must perform no IO"));
            }
            if plan.operation == CatalogOperation::SpectrumAnalyzerWaitOpc
                && !plan
                    .steps
                    .iter()
                    .any(|s| s.response == ResponseRole::Completion)
            {
                return Err(invalid(
                    "completion operation requires an explicit completion query",
                ));
            }
            if !local && !shutdown && plan.steps.is_empty() {
                return Err(invalid("empty operation plan"));
            }
            if matches!(
                metadata.result,
                ReplyKind::Quantity | ReplyKind::Boolean | ReplyKind::Samples | ReplyKind::Identity
            ) && values != 1
            {
                return Err(invalid("operation requires exactly one value query"));
            }
            if metadata.result == ReplyKind::Trace && (values != 1 || preambles != 1) {
                return Err(invalid("trace requires one preamble and one data query"));
            }
            if matches!(metadata.result, ReplyKind::Unit | ReplyKind::Count) && values != 0 {
                return Err(invalid("unexpected value query"));
            }
            if metadata.result != ReplyKind::Trace && preambles != 0 {
                return Err(invalid("unexpected preamble query"));
            }
            indexed.insert(plan.operation, plan);
        }
        if let Some(outputs) = &outputs {
            if indexed.values().any(|p| {
                p.operation.metadata().kind == "DcPowerSupply"
                    && p.operation
                        .metadata()
                        .parameters
                        .iter()
                        .any(|a| a.kind == ParameterKind::Channel)
            }) && !outputs
                .iter()
                .any(|o| matches!(o, Output::DcPowerSupply(_)))
            {
                return Err(invalid(
                    "PSU channel operations contradict complete output inventory",
                ));
            }
            if indexed.contains_key(&CatalogOperation::FunctionGeneratorOutputEnable)
                && !outputs.contains(&Output::FunctionGenerator)
            {
                return Err(invalid(
                    "generator output operation contradicts complete inventory",
                ));
            }
            if indexed.contains_key(&CatalogOperation::Shutdown) {
                for output in outputs {
                    let op = match output {
                        Output::DcPowerSupply(_) => CatalogOperation::DcPowerSupplyOutputEnable,
                        Output::FunctionGenerator => {
                            CatalogOperation::FunctionGeneratorOutputEnable
                        }
                    };
                    if !indexed.contains_key(&op) {
                        return Err(invalid(
                            "shutdown lacks a declared output's disable capability",
                        ));
                    }
                }
                if indexed.contains_key(&CatalogOperation::DcPowerSupplyOutputEnable)
                    && !outputs
                        .iter()
                        .any(|o| matches!(o, Output::DcPowerSupply(_)))
                    || indexed.contains_key(&CatalogOperation::FunctionGeneratorOutputEnable)
                        && !outputs.contains(&Output::FunctionGenerator)
                {
                    return Err(invalid(
                        "output-capable profile cannot claim zero corresponding outputs",
                    ));
                }
            }
        }
        let profile = Self {
            id: id.into(),
            channels,
            outputs,
            plans: indexed,
        };
        if profile.plans.contains_key(&CatalogOperation::Shutdown) {
            Engine::new(profile.clone())
                .prepare(&CatalogRequest::Shutdown)
                .map_err(|_| invalid("shutdown cannot prepare every declared output disable"))?;
        }
        Ok(profile)
    }
    pub fn id(&self) -> &str {
        &self.id
    }
    pub fn channel_ids(&self) -> Option<&[u32]> {
        self.channels.as_deref()
    }
    pub fn output_channel_ids(&self) -> Option<&[Output]> {
        self.outputs.as_deref()
    }
    /// Immutable snapshots; callers rebind by constructing and validating a new Profile.
    pub fn plans(&self) -> impl Iterator<Item = &Plan> {
        self.plans.values()
    }
    /// Reconstruct an immutable, validated profile before opening any IO.
    /// Shutdown actions are explicit driver-owned stop/open-all actions, not Reset.
    pub fn bind_inventory(
        &self,
        binding: &InventoryBinding,
        shutdown_steps: Option<Vec<Step>>,
    ) -> Result<Self> {
        let mut plans: Vec<_> = self
            .plans()
            .filter(|p| {
                !matches!(
                    p.operation,
                    CatalogOperation::Shutdown | CatalogOperation::DcPowerSupplyChannelCount
                )
            })
            .cloned()
            .collect();
        let psu = plans
            .iter()
            .any(|p| p.operation.metadata().kind == "DcPowerSupply");
        if psu && binding.outputs.as_ref().is_some_and(Vec::is_empty) {
            return Err(invalid(
                "PSU capabilities cannot declare zero physical outputs",
            ));
        }
        if binding.outputs.is_some() && psu {
            plans.push(Plan::new(
                CatalogOperation::DcPowerSupplyChannelCount,
                Vec::new(),
            ));
        }
        if binding.outputs.is_some() {
            if let Some(steps) = shutdown_steps {
                plans.push(Plan::new(CatalogOperation::Shutdown, steps));
            }
        }
        Self::new(
            &self.id,
            binding.channels.clone(),
            binding.outputs.clone(),
            plans,
        )
    }
    pub fn operations(&self) -> BTreeSet<Operation> {
        self.plans.keys().map(|o| Operation::Catalog(*o)).collect()
    }
}
fn validate_choices(
    id: &str,
    channels: &Option<Vec<u32>>,
    outputs: &Option<Vec<Output>>,
    root: &Plan,
) -> Result<()> {
    let metadata = root.operation.metadata();
    let optional: Vec<_> = metadata
        .parameters
        .iter()
        .filter(|p| p.kind == ParameterKind::OptionalNumber)
        .map(|p| p.name)
        .collect();
    if optional.is_empty() || optional.len() > 8 || root.choices.len() > 16 {
        return Err(invalid("invalid or excessive optional plan choices"));
    }
    for choice in &root.choices {
        if choice
            .presence
            .keys()
            .any(|key| !optional.contains(&key.as_str()))
        {
            return Err(invalid("presence key must name an optional argument"));
        }
        Profile::new(
            id,
            channels.clone(),
            outputs.clone(),
            vec![choice.flat_plan(root.operation)],
        )?;
    }
    for bits in 0..(1usize << optional.len()) {
        let mask: BTreeMap<_, _> = optional
            .iter()
            .enumerate()
            .map(|(i, n)| (*n, bits & (1 << i) != 0))
            .collect();
        let matched: Vec<_> = root
            .choices
            .iter()
            .filter(|c| c.presence.iter().all(|(n, v)| mask[n.as_str()] == *v))
            .collect();
        if matched.len() > 1 {
            return Err(invalid("overlapping optional plan choices"));
        }
        if let Some(choice) = matched.first() {
            let mut used = BTreeSet::new();
            for step in &choice.steps {
                if step.when.as_ref().is_some_and(|name| !mask[name.as_str()]) {
                    continue;
                }
                for name in placeholders(&step.command)? {
                    if mask.get(name.as_str()) == Some(&false)
                        && !choice.defaults.contains_key(&name)
                    {
                        return Err(invalid(
                            "absent optional placeholder has no explicit default",
                        ));
                    }
                    used.insert(name);
                }
            }
            if metadata.parameters.iter().any(|p| {
                (p.kind != ParameterKind::OptionalNumber || mask[p.name]) && !used.contains(p.name)
            }) {
                return Err(invalid(
                    "selected choice ignores a required or supplied argument",
                ));
            }
        }
    }
    Ok(())
}
fn selected_plan(root: &Plan, args: &BTreeMap<&str, Argument>) -> Result<Plan> {
    if root.choices.is_empty() {
        return Ok(root.clone());
    }
    let choice = root
        .choices
        .iter()
        .find(|c| {
            c.presence.iter().all(|(name, present)| {
                matches!(
                    args.get(name.as_str()),
                    Some(Argument::OptionalNumber(Some(_)))
                ) == *present
            })
        })
        .ok_or_else(|| {
            fail(
                ErrorKind::UnsupportedOperation,
                "optional argument combination is not supported",
            )
        })?;
    Ok(choice.flat_plan(root.operation))
}
#[derive(Debug, Clone)]
pub(crate) struct Prepared {
    pub steps: Vec<Step>,
    pub unit: Unit,
    pub kind: ReplyKind,
}
pub(crate) struct Engine {
    pub profile: Profile,
    dmm: Option<Unit>,
    power: Option<Unit>,
}
impl Engine {
    pub fn new(profile: Profile) -> Self {
        Self {
            profile,
            dmm: None,
            power: None,
        }
    }
    pub fn invalidate(&mut self) {
        self.dmm = None;
        self.power = None;
    }
    pub fn prepare(&self, request: &CatalogRequest) -> Result<Prepared> {
        request.validate()?;
        let plan = self
            .profile
            .plans
            .get(&request.operation())
            .ok_or_else(|| fail(ErrorKind::UnsupportedOperation, "operation not in profile"))?;
        let args: BTreeMap<_, _> = request.arguments().into_iter().collect();
        let plan = selected_plan(plan, &args)?;
        if request.operation() == CatalogOperation::Shutdown
            && self.profile.outputs.as_ref().is_some_and(|o| !o.is_empty())
        {
            let mut steps = Vec::new();
            for output in self.profile.outputs.as_ref().unwrap() {
                let disable = match output {
                    Output::DcPowerSupply(channel) => CatalogRequest::DcPowerSupplyOutputEnable {
                        channel: *channel,
                        enabled: false,
                    },
                    Output::FunctionGenerator => {
                        CatalogRequest::FunctionGeneratorOutputEnable { enabled: false }
                    }
                };
                steps.extend(self.prepare(&disable)?.steps);
            }
            // Explicit class-specific stop/open-all actions follow every output disable.
            steps.extend(plan.steps.clone());
            return Ok(Prepared {
                steps,
                unit: Unit::Unknown,
                kind: ReplyKind::Unit,
            });
        }
        let (kind, unit) = request.result_kind();
        let unit = match unit {
            Unit::Temperature => plan.unit.unwrap_or(Unit::Unknown),
            Unit::ConfiguredDmm => self.dmm.ok_or_else(|| {
                fail(
                    ErrorKind::InvalidArgument,
                    "configure DMM measurement function before reading",
                )
            })?,
            Unit::ConfiguredPower => self.power.ok_or_else(|| {
                fail(
                    ErrorKind::InvalidArgument,
                    "configure power units before reading",
                )
            })?,
            u => u,
        };
        let mut used = BTreeSet::new();
        let mut steps = Vec::new();
        for step in &plan.steps {
            if step.when.as_ref().is_some_and(|n| {
                matches!(args.get(n.as_str()), Some(Argument::OptionalNumber(None)))
            }) {
                continue;
            }
            let mut command = step.command.clone();
            for name in placeholders(&step.command)? {
                let arg = &args[name.as_str()];
                let text = match arg {
                    Argument::Number(v) | Argument::OptionalNumber(Some(v)) => format_number(*v)?,
                    Argument::Count(v) => v.to_string(),
                    Argument::OptionalNumber(None) => {
                        plan.defaults.get(&name).cloned().ok_or_else(|| {
                            fail(
                                ErrorKind::UnsupportedOperation,
                                "profile has no default for absent argument",
                            )
                        })?
                    }
                    Argument::Channel(c) => {
                        if !self
                            .profile
                            .channels
                            .as_ref()
                            .is_some_and(|all| all.contains(c))
                            || request.operation().metadata().kind == "DcPowerSupply"
                                && self
                                    .profile
                                    .outputs
                                    .as_ref()
                                    .is_some_and(|all| !all.contains(&Output::DcPowerSupply(*c)))
                        {
                            return Err(fail(
                                ErrorKind::InvalidArgument,
                                "unknown physical channel",
                            ));
                        }
                        c.to_string()
                    }
                    Argument::Boolean(v) => plan
                        .tokens
                        .get(&name)
                        .and_then(|map| map.get(if *v { "true" } else { "false" }))
                        .cloned()
                        .ok_or_else(|| {
                            fail(ErrorKind::UnsupportedOperation, "boolean mode not mapped")
                        })?,
                    Argument::Symbol(v) => plan
                        .tokens
                        .get(&name)
                        .and_then(|map| map.get(v))
                        .cloned()
                        .ok_or_else(|| {
                            fail(ErrorKind::UnsupportedOperation, "semantic mode not mapped")
                        })?,
                };
                command = command.replace(&format!("{{{name}}}"), &text);
                used.insert(name);
            }
            steps.push(Step {
                command,
                response: step.response,
                when: None,
            });
        }
        if args
            .iter()
            .any(|(n, a)| !matches!(a, Argument::OptionalNumber(None)) && !used.contains(*n))
        {
            return Err(fail(
                ErrorKind::UnsupportedOperation,
                "profile cannot honor supplied argument",
            ));
        }
        Ok(Prepared { steps, unit, kind })
    }
    pub fn finish(
        &mut self,
        request: &CatalogRequest,
        prepared: &Prepared,
        value: Option<String>,
        preamble: Option<String>,
    ) -> Result<CatalogReply> {
        let raw = value.unwrap_or_default();
        let reply = match prepared.kind {
            ReplyKind::Unit => CatalogReply::Unit,
            ReplyKind::Count => CatalogReply::Count(
                self.profile
                    .outputs
                    .as_ref()
                    .map(|v| {
                        v.iter()
                            .filter(|o| matches!(o, Output::DcPowerSupply(_)))
                            .count() as u32
                    })
                    .ok_or_else(|| {
                        fail(ErrorKind::UnsupportedOperation, "unknown channel inventory")
                    })?,
            ),
            ReplyKind::Quantity => {
                let number = parse_number(&raw)?;
                CatalogReply::Quantity(Quantity {
                    value: number,
                    unit: prepared.unit,
                    status: if number.abs() >= 9.0e36 {
                        MeasurementStatus::Overload
                    } else {
                        MeasurementStatus::Valid
                    },
                })
            }
            ReplyKind::Boolean => {
                CatalogReply::Boolean(match raw.trim().to_ascii_uppercase().as_str() {
                    "1" | "+1" | "ON" | "CLOSED" => true,
                    "0" | "+0" | "OFF" | "OPEN" => false,
                    _ => return Err(fail(ErrorKind::Parse, "invalid boolean response")),
                })
            }
            ReplyKind::Identity => {
                let fields: Vec<_> = raw.trim().split(',').map(str::trim).collect();
                if fields.len() != 4 || fields[0].is_empty() || fields[1].is_empty() {
                    return Err(fail(
                        ErrorKind::Parse,
                        "identity requires four comma-separated fields",
                    ));
                }
                CatalogReply::Identity(DeviceIdentity {
                    manufacturer: fields[0].into(),
                    model: fields[1].into(),
                    serial: fields[2].into(),
                    revision: fields[3].into(),
                })
            }
            ReplyKind::Samples | ReplyKind::Trace => {
                let values = parse_csv(&raw)?;
                let dt = if prepared.kind == ReplyKind::Trace {
                    let p = parse_csv(preamble.as_deref().unwrap_or(""))?;
                    if p.len() < 5 || p[4] <= 0.0 {
                        return Err(fail(ErrorKind::Parse, "invalid ASCII waveform preamble"));
                    }
                    Some(p[4])
                } else {
                    None
                };
                CatalogReply::Samples(Samples {
                    values,
                    unit: prepared.unit,
                    sample_interval_s: dt,
                })
            }
        };
        reply.validate(request)?;
        if request.operation() == CatalogOperation::Reset {
            self.invalidate();
        }
        if let Some(unit) = request.configured_unit() {
            let unit = if unit == Unit::Temperature {
                prepared.unit
            } else {
                unit
            };
            if unit == Unit::Watt || unit == Unit::Dbm {
                self.power = Some(unit);
            } else {
                self.dmm = Some(unit);
            }
        }
        Ok(reply)
    }
}
pub fn parse_number(raw: &str) -> Result<f64> {
    let v: f64 = raw
        .trim()
        .parse()
        .map_err(|_| fail(ErrorKind::Parse, "invalid numeric response"))?;
    if !v.is_finite() {
        Err(fail(ErrorKind::Parse, "nonfinite numeric response"))
    } else {
        Ok(v)
    }
}
fn parse_csv(raw: &str) -> Result<Vec<f64>> {
    if raw.trim().is_empty() || raw.len() > MAX_RESPONSE_BYTES {
        return Err(fail(ErrorKind::Parse, "empty/oversize CSV"));
    }
    raw.trim().split(',').map(parse_number).collect()
}
pub fn format_number(value: f64) -> Result<String> {
    if !value.is_finite() {
        return Err(fail(ErrorKind::InvalidArgument, "nonfinite number"));
    }
    if value == 0.0 {
        return Ok("0".into());
    }
    let raw = value.to_string().to_ascii_lowercase();
    let (mantissa, exponent) = raw
        .split_once('e')
        .map(|(m, e)| (m, e.parse::<i32>().unwrap()))
        .unwrap_or((&raw, 0));
    let negative = mantissa.starts_with('-');
    let body = mantissa.trim_start_matches('-');
    let point = body.find('.').unwrap_or(body.len()) as i32;
    let digits: String = body.chars().filter(|c| *c != '.').collect();
    let first = digits.find(|c| c != '0').unwrap_or(0);
    let significant = digits[first..].trim_end_matches('0');
    let exp = exponent + point - first as i32 - 1;
    let mut result = if negative {
        "-".to_owned()
    } else {
        String::new()
    };
    result.push_str(&significant[..1]);
    if significant.len() > 1 {
        result.push('.');
        result.push_str(&significant[1..]);
    }
    if exp != 0 {
        result.push('e');
        result.push_str(&exp.to_string());
    }
    Ok(result)
}
pub(crate) fn response_checked(response: String) -> Result<String> {
    if response.len() > MAX_RESPONSE_BYTES || response.contains('\0') {
        Err(fail(
            ErrorKind::Parse,
            "invalid or oversized response message",
        ))
    } else {
        Ok(response)
    }
}
pub(crate) fn completion(raw: &str) -> Result<()> {
    if matches!(raw.trim(), "1" | "+1") {
        Ok(())
    } else {
        Err(fail(ErrorKind::Parse, "completion response must be 1"))
    }
}

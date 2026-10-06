use crate::address::{InterfaceKind, ResourceAddress};
use crate::error::{Error, Result};
use crate::identity::Idn;
use crate::kind::InstrumentKind;
use crate::transport::{Transport, TransportIdentity};
use indexmap::IndexMap;
use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// A single step in a mock transport script.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum ScriptStep {
    Write { data: String },
    Read { data: String },
    Clear,
}

/// Scripted request/response transport for deterministic CI.
#[derive(Debug, Clone)]
pub struct MockTransport {
    script: Vec<ScriptStep>,
    steps: Arc<Mutex<Vec<ScriptStep>>>,
    step_index: Arc<Mutex<usize>>,
    identity: TransportIdentity,
    fail_writes_remaining: Arc<Mutex<u32>>,
    fail_reads_remaining: Arc<Mutex<u32>>,
    pending_read: Arc<Mutex<VecDeque<u8>>>,
}

impl MockTransport {
    pub fn from_script(steps: Vec<ScriptStep>) -> Self {
        Self {
            script: steps.clone(),
            steps: Arc::new(Mutex::new(steps)),
            step_index: Arc::new(Mutex::new(0)),
            identity: TransportIdentity::default(),
            fail_writes_remaining: Arc::new(Mutex::new(0)),
            fail_reads_remaining: Arc::new(Mutex::new(0)),
            pending_read: Arc::new(Mutex::new(VecDeque::new())),
        }
    }

    /// Returns a fresh transport replaying the same script (for new sessions).
    pub fn reopen(&self) -> Self {
        let mut t = Self::from_script(self.script.clone());
        t.identity = self.identity.clone();
        *t.fail_writes_remaining.lock().unwrap() = *self.fail_writes_remaining.lock().unwrap();
        *t.fail_reads_remaining.lock().unwrap() = *self.fail_reads_remaining.lock().unwrap();
        t
    }

    pub fn script(&self) -> &[ScriptStep] {
        &self.script
    }

    pub fn with_identity(mut self, identity: TransportIdentity) -> Self {
        self.identity = identity;
        self
    }

    /// Fails the next N write attempts with Timeout (for retry testing).
    pub fn fail_writes(self, count: u32) -> Self {
        *self.fail_writes_remaining.lock().unwrap() = count;
        self
    }

    /// Fails the next N read attempts with Timeout without consuming a script step.
    pub fn fail_reads(self, count: u32) -> Self {
        *self.fail_reads_remaining.lock().unwrap() = count;
        self
    }

    fn peek_step(&self) -> Result<ScriptStep> {
        let idx = self.step_index.lock().unwrap();
        let steps = self.steps.lock().unwrap();
        if *idx >= steps.len() {
            return Err(Error::MockExhausted);
        }
        Ok(steps[*idx].clone())
    }

    fn next_step(&self) -> Result<ScriptStep> {
        let mut idx = self.step_index.lock().unwrap();
        let steps = self.steps.lock().unwrap();
        if *idx >= steps.len() {
            return Err(Error::MockExhausted);
        }
        let step = steps[*idx].clone();
        *idx += 1;
        Ok(step)
    }
}

impl Transport for MockTransport {
    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut fails = self.fail_writes_remaining.lock().unwrap();
        if *fails > 0 {
            *fails -= 1;
            return Err(Error::Timeout);
        }
        drop(fails);

        match self.next_step()? {
            ScriptStep::Write { data: expected } => {
                let actual = String::from_utf8_lossy(data).to_string();
                if normalize_cmd(&actual) != normalize_cmd(&expected) {
                    return Err(Error::MockMismatch { expected, actual });
                }
                Ok(())
            }
            other => Err(Error::MockMismatch {
                expected: format!("write, got {other:?}"),
                actual: String::from_utf8_lossy(data).to_string(),
            }),
        }
    }

    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        let mut fails = self.fail_reads_remaining.lock().unwrap();
        if *fails > 0 {
            *fails -= 1;
            return Err(Error::Timeout);
        }
        drop(fails);

        if buf.is_empty() {
            return Ok(0);
        }
        let mut pending = self.pending_read.lock().unwrap();
        if pending.is_empty() {
            if !matches!(self.peek_step()?, ScriptStep::Read { .. }) {
                return Err(Error::Timeout);
            }
            if let ScriptStep::Read { data } = self.next_step()? {
                pending.extend(data.into_bytes());
            }
        }
        let n = pending.len().min(buf.len());
        for byte in &mut buf[..n] {
            *byte = pending.pop_front().unwrap();
        }
        Ok(n)
    }

    fn clear(&mut self) -> Result<()> {
        match self.next_step()? {
            ScriptStep::Clear => Ok(()),
            other => Err(Error::MockMismatch {
                expected: "clear".into(),
                actual: format!("{other:?}"),
            }),
        }
    }

    fn set_read_timeout(&mut self, _timeout: Duration) -> Result<()> {
        Ok(())
    }

    fn identity(&self) -> TransportIdentity {
        self.identity.clone()
    }
}

#[cfg(feature = "async")]
impl crate::async_transport::AsyncTransport for MockTransport {
    fn write<'a>(
        &'a mut self,
        data: &'a [u8],
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<()>> + Send + 'a>> {
        Box::pin(async move { Transport::write(self, data) })
    }

    fn read<'a>(
        &'a mut self,
        buf: &'a mut [u8],
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<usize>> + Send + 'a>> {
        Box::pin(async move { Transport::read(self, buf) })
    }

    fn clear<'a>(
        &'a mut self,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<()>> + Send + 'a>> {
        Box::pin(async move { Transport::clear(self) })
    }

    fn set_read_timeout<'a>(
        &'a mut self,
        timeout: Duration,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<()>> + Send + 'a>> {
        Box::pin(async move { Transport::set_read_timeout(self, timeout) })
    }

    fn apply_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        Transport::set_read_timeout(self, timeout)
    }

    fn identity(&self) -> TransportIdentity {
        Transport::identity(self)
    }
}

fn normalize_cmd(s: &str) -> String {
    s.trim().to_uppercase()
}

/// High-level fixture builder for common instrument patterns.
#[derive(Debug, Default)]
pub struct ScriptedFixture {
    idn: Idn,
    kinds: Vec<InstrumentKind>,
    query_handlers: IndexMap<String, String>,
    write_handlers: Vec<String>,
}

impl ScriptedFixture {
    pub fn builder() -> ScriptedFixtureBuilder {
        ScriptedFixtureBuilder::default()
    }

    pub fn into_transport(self) -> MockTransport {
        let mut steps = Vec::new();

        for cmd in &self.write_handlers {
            let data = if cmd.ends_with('\n') {
                cmd.clone()
            } else {
                format!("{cmd}\n")
            };
            steps.push(ScriptStep::Write { data });
        }

        for (query, response) in &self.query_handlers {
            steps.push(ScriptStep::Write {
                data: format!("{query}\n"),
            });
            let resp = if response.ends_with('\n') {
                response.clone()
            } else {
                format!("{response}\n")
            };
            steps.push(ScriptStep::Read { data: resp });
        }

        let identity = TransportIdentity {
            manufacturer: Some(self.idn.manufacturer.clone()),
            model: Some(self.idn.model.clone()),
            serial: Some(self.idn.serial.clone()),
            interface: InterfaceKind::Unknown,
            manf_id: None,
            model_code: None,
        };

        MockTransport::from_script(steps).with_identity(identity)
    }

    pub fn kinds(&self) -> &[InstrumentKind] {
        &self.kinds
    }

    pub fn idn(&self) -> &Idn {
        &self.idn
    }
}

#[derive(Debug, Default)]
pub struct ScriptedFixtureBuilder {
    idn: Idn,
    kinds: Vec<InstrumentKind>,
    query_handlers: IndexMap<String, String>,
    write_handlers: Vec<String>,
}

impl ScriptedFixtureBuilder {
    pub fn idn(mut self, manufacturer: &str, model: &str, serial: &str, firmware: &str) -> Self {
        self.idn = Idn {
            manufacturer: manufacturer.into(),
            model: model.into(),
            serial: serial.into(),
            firmware: firmware.into(),
        };
        self
    }

    pub fn kinds(mut self, kinds: impl IntoIterator<Item = InstrumentKind>) -> Self {
        self.kinds = kinds.into_iter().collect();
        self
    }

    pub fn on_query(mut self, query: &str, response: &str) -> Self {
        self.query_handlers
            .insert(query.to_string(), response.to_string());
        self
    }

    /// Expects a write-only command (no response read).
    pub fn on_write(mut self, command: &str) -> Self {
        self.write_handlers.push(command.to_string());
        self
    }

    /// Registers `*IDN?` response for discovery probing.
    pub fn with_idn_probe(mut self) -> Self {
        self.query_handlers
            .insert("*IDN?".into(), self.idn.format_response());
        self
    }

    pub fn build(self) -> ScriptedFixture {
        ScriptedFixture {
            idn: self.idn,
            kinds: self.kinds,
            query_handlers: self.query_handlers,
            write_handlers: self.write_handlers,
        }
    }
}

/// Builds a mock address for fixture-based catalogs.
pub fn mock_address(name: &str) -> Result<ResourceAddress> {
    ResourceAddress::parse(&format!("mock://{name}"))
}

/// Serializable transcript for fixture storage and replay.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Transcript {
    pub steps: Vec<ScriptStep>,
}

impl Transcript {
    pub fn from_steps(steps: Vec<ScriptStep>) -> Self {
        Self { steps }
    }

    pub fn to_json(&self) -> Result<String> {
        serde_json::to_string_pretty(self).map_err(|e| Error::Parse(e.to_string()))
    }

    pub fn from_json(json: &str) -> Result<Self> {
        serde_json::from_str(json).map_err(|e| Error::Parse(e.to_string()))
    }
}

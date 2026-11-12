use crate::error::Result;
use crate::mock::ScriptStep;
use crate::transport::Transport;
use std::time::Duration;
use tracing::debug;

/// Records I/O on a wrapped transport for replay as a MockTransport script.
#[derive(Debug)]
pub struct RecordingTransport<T: Transport> {
    inner: T,
    pub steps: Vec<ScriptStep>,
}

impl<T: Transport> RecordingTransport<T> {
    pub fn new(inner: T) -> Self {
        Self {
            inner,
            steps: Vec::new(),
        }
    }

    pub fn into_script(self) -> Vec<ScriptStep> {
        self.steps
    }

    pub fn into_inner(self) -> T {
        self.inner
    }
}

impl<T: Transport> Transport for RecordingTransport<T> {
    fn write(&mut self, data: &[u8]) -> Result<()> {
        debug!(cmd = %String::from_utf8_lossy(data), "scpi write");
        self.steps.push(ScriptStep::Write {
            data: String::from_utf8_lossy(data).to_string(),
        });
        self.inner.write(data)
    }

    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        let n = self.inner.read(buf)?;
        let data = String::from_utf8_lossy(&buf[..n]).to_string();
        debug!(response = %data, "scpi read");
        self.steps.push(ScriptStep::Read { data });
        Ok(n)
    }

    fn clear(&mut self) -> Result<()> {
        self.steps.push(ScriptStep::Clear);
        self.inner.clear()
    }

    fn set_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        self.inner.set_read_timeout(timeout)
    }

    fn reconnect(&mut self) -> Result<()> {
        self.inner.reconnect()
    }

    fn identity(&self) -> crate::transport::TransportIdentity {
        self.inner.identity()
    }

    fn configure(&mut self, opts: &crate::connect::ConnectOptions) -> Result<()> {
        self.inner.configure(opts)
    }
}

pub use crate::mock::Transcript;

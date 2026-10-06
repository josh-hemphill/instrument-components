use super::framing::extract_response;
use super::protocol::{
    is_no_error_reply, is_opc_supported_reply, is_syst_err_supported_reply, max_write_attempts,
    normalize_command, parse_f64, SessionCapabilities,
};
use crate::connect::ConnectOptions;
use crate::diagnostics::{CommsEventKind, Diagnostics};
use crate::error::{Error, Result};
use crate::ieee4882::Ieee4882;
use crate::transport::{DynTransport, Transport};
use std::thread;
use std::time::{Duration, Instant};

/// SCPI session over a boxed transport.
pub struct ScpiSession {
    transport: DynTransport,
    opts: ConnectOptions,
    capabilities: SessionCapabilities,
    read_buffer: Vec<u8>,
    diagnostics: Option<Diagnostics>,
    pending_command: Option<String>,
    pending_error_reply: Option<String>,
    skip_block_terminator: bool,
}

impl ScpiSession {
    pub fn new(mut transport: DynTransport, opts: ConnectOptions) -> Result<Self> {
        transport.configure(&opts)?;
        let mut session = Self {
            transport,
            opts,
            capabilities: SessionCapabilities::default(),
            read_buffer: Vec::with_capacity(4096),
            diagnostics: None,
            pending_command: None,
            pending_error_reply: None,
            skip_block_terminator: false,
        };
        if session.opts.reset_on_connect {
            let _ = Ieee4882::new(&mut session).clear_status();
            let _ = Ieee4882::new(&mut session).reset();
            let _ = session.restore_io_timeout();
        }
        Ok(session)
    }

    pub fn with_diagnostics(mut self, diagnostics: Diagnostics) -> Self {
        self.diagnostics = Some(diagnostics);
        self
    }

    pub fn transport(&self) -> &dyn Transport {
        self.transport.as_ref()
    }

    pub fn options(&self) -> &ConnectOptions {
        &self.opts
    }

    /// Drains pending bytes from the transport read buffer.
    pub fn flush(&mut self) -> Result<()> {
        let short = Duration::from_millis(50);
        self.transport.set_read_timeout(short)?;
        let result = self.drain_read_buffer();
        let _ = self.restore_io_timeout();
        result
    }

    fn drain_read_buffer(&mut self) -> Result<()> {
        let mut chunk = [0u8; 256];
        loop {
            match self.transport.read(&mut chunk) {
                Ok(0) => break,
                Ok(_) => continue,
                Err(Error::Timeout) => break,
                Err(e) => return Err(e),
            }
        }
        self.read_buffer.clear();
        Ok(())
    }

    /// Writes a command without expecting a response.
    pub fn write(&mut self, command: &str) -> Result<()> {
        self.write_with_retry(command, false)
    }

    /// Sends a query and returns the response string.
    pub fn query(&mut self, command: &str) -> Result<String> {
        self.query_with_timeout(command, self.effective_read_timeout())
    }

    pub fn query_with_timeout(&mut self, command: &str, timeout: Duration) -> Result<String> {
        let max_attempts = max_write_attempts(true, self.opts.retries);
        let mut attempts = 0;
        loop {
            attempts += 1;
            self.write_with_retry(command, true)?;
            match self.read_response(timeout) {
                Ok(bytes) => {
                    return Ok(String::from_utf8_lossy(&bytes).trim().to_string());
                }
                Err(Error::Timeout) if attempts < max_attempts => {
                    let _ = self.flush();
                    if self.opts.reconnect_on_failure {
                        self.try_reconnect();
                    }
                    thread::sleep(self.opts.retry_backoff * attempts);
                }
                Err(Error::Timeout) => {
                    let _ = self.flush();
                    return Err(Error::Timeout);
                }
                Err(e) => return Err(e),
            }
        }
    }

    fn write_with_retry(&mut self, command: &str, idempotent: bool) -> Result<()> {
        let payload = normalize_command(command, &self.opts.terminator);
        let data = payload.as_bytes();

        let mut attempts = 0;
        let max_attempts = max_write_attempts(idempotent, self.opts.retries);

        loop {
            attempts += 1;
            let started = Instant::now();
            self.pending_command = Some(command.to_string());
            match self.transport.write(data) {
                Ok(()) => {
                    self.record_success(CommsEventKind::WriteOk, Some(command), attempts, started);
                    return Ok(());
                }
                Err(Error::Timeout) if attempts < max_attempts => {
                    self.record_failure(
                        CommsEventKind::Timeout,
                        Some(command),
                        attempts,
                        started,
                        "write timeout",
                    );
                    if self.opts.reconnect_on_failure {
                        self.try_reconnect();
                    }
                    thread::sleep(self.opts.retry_backoff);
                }
                Err(Error::Timeout) => {
                    self.record_failure(
                        CommsEventKind::Timeout,
                        Some(command),
                        attempts,
                        started,
                        "write timeout",
                    );
                    return Err(Error::Timeout);
                }
                Err(e) => {
                    self.record_failure(
                        CommsEventKind::WriteFailed,
                        Some(command),
                        attempts,
                        started,
                        &e.to_string(),
                    );
                    return Err(e);
                }
            }
        }
    }

    fn read_response(&mut self, timeout: Duration) -> Result<Vec<u8>> {
        self.transport.set_read_timeout(timeout)?;
        let result = self.read_framed_response(timeout);
        let _ = self.restore_io_timeout();
        result
    }

    fn read_framed_response(&mut self, timeout: Duration) -> Result<Vec<u8>> {
        let deadline = Instant::now() + timeout;
        self.read_buffer.clear();
        let command = self.pending_command.clone();

        let mut chunk = [0u8; 1024];
        loop {
            let started = Instant::now();
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(Error::Timeout);
            }
            self.transport.set_read_timeout(remaining)?;
            match self.transport.read(&mut chunk) {
                Ok(0) => {
                    if !self.read_buffer.is_empty() {
                        if let Ok((payload, _)) =
                            extract_response(&self.read_buffer, &self.opts.terminator)
                        {
                            self.record_success(
                                CommsEventKind::ReadOk,
                                command.as_deref(),
                                1,
                                started,
                            );
                            self.skip_block_terminator = self.read_buffer.starts_with(b"#")
                                && self.read_buffer.get(1) != Some(&b'0');
                            return Ok(payload);
                        }
                    }
                    self.record_failure(
                        CommsEventKind::Timeout,
                        command.as_deref(),
                        1,
                        started,
                        "zero-byte read",
                    );
                    return Err(Error::Timeout);
                }
                Ok(n) => {
                    for &byte in &chunk[..n] {
                        if self.skip_block_terminator && matches!(byte, b'\r' | b'\n') {
                            continue;
                        }
                        self.skip_block_terminator = false;
                        self.read_buffer.push(byte);
                    }
                    if let Ok((payload, _)) =
                        extract_response(&self.read_buffer, &self.opts.terminator)
                    {
                        self.record_success(CommsEventKind::ReadOk, command.as_deref(), 1, started);
                        self.skip_block_terminator = self.read_buffer.starts_with(b"#")
                            && self.read_buffer.get(1) != Some(&b'0');
                        return Ok(payload);
                    }
                }
                Err(Error::Timeout) => {
                    if !self.read_buffer.is_empty() {
                        if let Ok((payload, _)) =
                            extract_response(&self.read_buffer, &self.opts.terminator)
                        {
                            self.record_success(
                                CommsEventKind::ReadOk,
                                command.as_deref(),
                                1,
                                started,
                            );
                            self.skip_block_terminator = self.read_buffer.starts_with(b"#")
                                && self.read_buffer.get(1) != Some(&b'0');
                            return Ok(payload);
                        }
                    }
                    self.record_failure(
                        CommsEventKind::Timeout,
                        command.as_deref(),
                        1,
                        started,
                        "read timeout",
                    );
                    return Err(Error::Timeout);
                }
                Err(e) => {
                    self.record_failure(
                        CommsEventKind::ReadFailed,
                        command.as_deref(),
                        1,
                        started,
                        &e.to_string(),
                    );
                    return Err(e);
                }
            }
        }
    }

    fn record_success(
        &self,
        kind: CommsEventKind,
        command: Option<&str>,
        attempt: u32,
        started: Instant,
    ) {
        if let Some(diag) = &self.diagnostics {
            diag.record_success(kind, command, attempt, started.elapsed());
        }
    }

    fn record_failure(
        &self,
        kind: CommsEventKind,
        command: Option<&str>,
        attempt: u32,
        started: Instant,
        detail: &str,
    ) {
        if let Some(diag) = &self.diagnostics {
            diag.record_failure(kind, command, attempt, started.elapsed(), detail);
        }
    }

    fn record_reconnect(&self) {
        if let Some(diag) = &self.diagnostics {
            diag.record_success(CommsEventKind::Reconnect, None, 1, Duration::ZERO);
        }
    }

    fn try_reconnect(&mut self) {
        if self.transport.reconnect().is_ok() {
            self.record_reconnect();
        }
    }

    fn effective_read_timeout(&self) -> Duration {
        self.opts.per_op_timeout.unwrap_or(self.opts.read_timeout)
    }

    /// Restores the session I/O timeout after a short probe or flush.
    fn restore_io_timeout(&mut self) -> Result<()> {
        self.transport.set_read_timeout(self.opts.io_timeout())
    }

    /// Probes and caches whether SYST:ERR? is supported.
    pub fn probe_syst_err(&mut self) -> bool {
        if let Some(v) = self.capabilities.syst_err {
            return v;
        }
        match self.query_with_timeout("SYST:ERR?", Duration::from_millis(500)) {
            Ok(resp) => {
                let supported = is_syst_err_supported_reply(&resp);
                self.capabilities.syst_err = Some(supported);
                // A zero reply describes the queue at probe time, not at the next check.
                if supported && !is_no_error_reply(&resp) {
                    self.pending_error_reply = Some(resp);
                }
                supported
            }
            Err(_) => false,
        }
    }

    /// Timeout is inconclusive and must not be cached as unsupported.
    pub fn probe_opc(&mut self) -> bool {
        if let Some(v) = self.capabilities.opc {
            return v;
        }
        match self.query_with_timeout("*OPC?", Duration::from_millis(500)) {
            Ok(resp) => {
                let supported = is_opc_supported_reply(&resp);
                self.capabilities.opc = Some(supported);
                supported
            }
            Err(_) => false,
        }
    }

    /// Drains the error queue, including any entry consumed by an earlier probe.
    pub fn check_errors(&mut self) -> Result<Vec<String>> {
        if self.capabilities.syst_err == Some(false) {
            return Ok(Vec::new());
        }
        let mut errors = Vec::new();
        loop {
            let resp = match self.pending_error_reply.take() {
                Some(reply) => reply,
                None => self.query("SYST:ERR?")?,
            };
            if !is_syst_err_supported_reply(&resp) {
                if self.capabilities.syst_err.is_none() {
                    self.capabilities.syst_err = Some(false);
                    return Ok(errors);
                }
                return Err(Error::Parse(format!("invalid error queue reply '{resp}'")));
            }
            self.capabilities.syst_err = Some(true);
            if is_no_error_reply(&resp) {
                break;
            }
            errors.push(resp);
            if errors.len() >= 50 {
                break;
            }
        }
        Ok(errors)
    }

    pub(crate) fn query_completion(&mut self, timeout: Duration) -> Result<String> {
        self.ensure_opc_available()?;
        self.write_with_retry("*OPC?", false)?;
        match self.read_response(timeout) {
            Ok(bytes) => Ok(String::from_utf8_lossy(&bytes).trim().into()),
            Err(Error::Timeout) => {
                let _ = self.flush();
                Err(Error::Timeout)
            }
            Err(err) => Err(err),
        }
    }

    pub(crate) fn ensure_opc_available(&self) -> Result<()> {
        if self.capabilities.opc == Some(false) {
            return Err(Error::Unsupported(
                "operation completion requires *OPC? support",
            ));
        }
        Ok(())
    }

    /// Parses a numeric SCPI response.
    pub fn parse_f64(response: &str) -> Result<f64> {
        parse_f64(response)
    }
}

use super::framing::extract_response;
use super::protocol::{
    is_opc_supported_reply, is_syst_err_supported_reply, max_write_attempts, normalize_command,
    parse_f64, SessionCapabilities,
};
use crate::async_transport::{AsyncTransport, DynAsyncTransport};
use crate::connect::ConnectOptions;
use crate::diagnostics::{CommsEventKind, Diagnostics};
use crate::error::{Error, Result};
use crate::ieee4882::AsyncIeee4882;
use std::time::{Duration, Instant};

/// Restores I/O timeout on drop so cancel cannot leave a short VISA timeout.
struct IoTimeoutRestoreGuard<'a> {
    transport: &'a mut DynAsyncTransport,
    timeout: Duration,
}

impl Drop for IoTimeoutRestoreGuard<'_> {
    fn drop(&mut self) {
        let _ = self.transport.apply_read_timeout(self.timeout);
    }
}

/// Async SCPI session over a boxed transport.
pub struct AsyncScpiSession {
    transport: DynAsyncTransport,
    opts: ConnectOptions,
    capabilities: SessionCapabilities,
    read_buffer: Vec<u8>,
    diagnostics: Option<Diagnostics>,
    pending_command: Option<String>,
    pending_error_reply: Option<String>,
    skip_block_terminator: bool,
}

impl AsyncScpiSession {
    pub async fn new(mut transport: DynAsyncTransport, opts: ConnectOptions) -> Result<Self> {
        transport.configure(&opts).await?;
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
            let _ = AsyncIeee4882::new(&mut session).clear_status().await;
            let _ = AsyncIeee4882::new(&mut session).reset().await;
            let _ = session.restore_io_timeout().await;
        }
        Ok(session)
    }

    pub fn with_diagnostics(mut self, diagnostics: Diagnostics) -> Self {
        self.diagnostics = Some(diagnostics);
        self
    }

    pub fn transport(&self) -> &dyn AsyncTransport {
        self.transport.as_ref()
    }

    pub fn options(&self) -> &ConnectOptions {
        &self.opts
    }

    /// Drains pending bytes from the transport read buffer.
    pub async fn flush(&mut self) -> Result<()> {
        let short = Duration::from_millis(50);
        let restore_to = self.opts.io_timeout();
        self.transport.set_read_timeout(short).await?;
        let guard = IoTimeoutRestoreGuard {
            transport: &mut self.transport,
            timeout: restore_to,
        };
        drain_read_buffer(&mut *guard.transport, &mut self.read_buffer).await
    }

    /// Writes a command without expecting a response.
    pub async fn write(&mut self, command: &str) -> Result<()> {
        self.write_with_retry(command, false).await
    }

    /// Sends a query and returns the response string.
    pub async fn query(&mut self, command: &str) -> Result<String> {
        self.query_with_timeout(command, self.effective_read_timeout())
            .await
    }

    pub async fn query_with_timeout(&mut self, command: &str, timeout: Duration) -> Result<String> {
        let max_attempts = max_write_attempts(true, self.opts.retries);
        let mut attempts = 0;
        loop {
            attempts += 1;
            self.write_with_retry(command, true).await?;
            match self.read_response(timeout).await {
                Ok(bytes) => {
                    return Ok(String::from_utf8_lossy(&bytes).trim().to_string());
                }
                Err(Error::Timeout) if attempts < max_attempts => {
                    let _ = self.flush().await;
                    if self.opts.reconnect_on_failure {
                        self.try_reconnect().await;
                    }
                    tokio::time::sleep(self.opts.retry_backoff * attempts).await;
                }
                Err(Error::Timeout) => {
                    let _ = self.flush().await;
                    return Err(Error::Timeout);
                }
                Err(e) => return Err(e),
            }
        }
    }

    async fn write_with_retry(&mut self, command: &str, idempotent: bool) -> Result<()> {
        let payload = normalize_command(command, &self.opts.terminator);
        let data = payload.as_bytes();

        let mut attempts = 0;
        let max_attempts = max_write_attempts(idempotent, self.opts.retries);

        loop {
            attempts += 1;
            let started = Instant::now();
            self.pending_command = Some(command.to_string());
            match self.transport.write(data).await {
                Ok(()) => {
                    record_success(
                        &self.diagnostics,
                        CommsEventKind::WriteOk,
                        Some(command),
                        attempts,
                        started,
                    );
                    return Ok(());
                }
                Err(Error::Timeout) if attempts < max_attempts => {
                    record_failure(
                        &self.diagnostics,
                        CommsEventKind::Timeout,
                        Some(command),
                        attempts,
                        started,
                        "write timeout",
                    );
                    if self.opts.reconnect_on_failure {
                        self.try_reconnect().await;
                    }
                    tokio::time::sleep(self.opts.retry_backoff).await;
                }
                Err(Error::Timeout) => {
                    record_failure(
                        &self.diagnostics,
                        CommsEventKind::Timeout,
                        Some(command),
                        attempts,
                        started,
                        "write timeout",
                    );
                    return Err(Error::Timeout);
                }
                Err(e) => {
                    record_failure(
                        &self.diagnostics,
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

    async fn read_response(&mut self, timeout: Duration) -> Result<Vec<u8>> {
        let restore_to = self.opts.io_timeout();
        self.transport.set_read_timeout(timeout).await?;
        let guard = IoTimeoutRestoreGuard {
            transport: &mut self.transport,
            timeout: restore_to,
        };
        read_framed_response(
            &mut *guard.transport,
            &mut self.read_buffer,
            &mut self.skip_block_terminator,
            &self.opts,
            self.pending_command.as_deref(),
            &self.diagnostics,
            timeout,
        )
        .await
    }

    fn effective_read_timeout(&self) -> Duration {
        self.opts.per_op_timeout.unwrap_or(self.opts.read_timeout)
    }

    /// Restores the session I/O timeout after a short probe or flush.
    async fn restore_io_timeout(&mut self) -> Result<()> {
        self.transport
            .set_read_timeout(self.opts.io_timeout())
            .await
    }

    async fn try_reconnect(&mut self) {
        if self.transport.reconnect().await.is_ok() {
            record_reconnect(&self.diagnostics);
        }
    }

    /// Probes and caches whether SYST:ERR? is supported.
    pub async fn probe_syst_err(&mut self) -> bool {
        if let Some(v) = self.capabilities.syst_err {
            return v;
        }
        match self
            .query_with_timeout("SYST:ERR?", Duration::from_millis(500))
            .await
        {
            Ok(resp) => {
                let supported = is_syst_err_supported_reply(&resp);
                self.capabilities.syst_err = Some(supported);
                if supported {
                    self.pending_error_reply = Some(resp);
                }
                supported
            }
            Err(_) => false,
        }
    }

    /// Timeout is inconclusive and must not be cached as unsupported.
    pub async fn probe_opc(&mut self) -> bool {
        if let Some(v) = self.capabilities.opc {
            return v;
        }
        match self
            .query_with_timeout("*OPC?", Duration::from_millis(500))
            .await
        {
            Ok(resp) => {
                let supported = is_opc_supported_reply(&resp);
                self.capabilities.opc = Some(supported);
                supported
            }
            Err(_) => false,
        }
    }

    /// Drains the error queue, including any entry consumed by an earlier probe.
    pub async fn check_errors(&mut self) -> Result<Vec<String>> {
        if self.capabilities.syst_err == Some(false) {
            return Ok(Vec::new());
        }
        let mut errors = Vec::new();
        loop {
            let resp = match self.pending_error_reply.take() {
                Some(reply) => reply,
                None => self.query("SYST:ERR?").await?,
            };
            if !is_syst_err_supported_reply(&resp) {
                if self.capabilities.syst_err.is_none() {
                    self.capabilities.syst_err = Some(false);
                    return Ok(errors);
                }
                return Err(Error::Parse(format!("invalid error queue reply '{resp}'")));
            }
            self.capabilities.syst_err = Some(true);
            if resp
                .split(',')
                .next()
                .and_then(|s| s.trim().parse::<i32>().ok())
                == Some(0)
            {
                break;
            }
            errors.push(resp);
            if errors.len() >= 50 {
                break;
            }
        }
        Ok(errors)
    }

    pub(crate) async fn query_completion(&mut self, timeout: Duration) -> Result<String> {
        self.ensure_opc_available()?;
        self.write_with_retry("*OPC?", false).await?;
        match self.read_response(timeout).await {
            Ok(bytes) => Ok(String::from_utf8_lossy(&bytes).trim().into()),
            Err(Error::Timeout) => {
                let _ = self.flush().await;
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

async fn drain_read_buffer(
    transport: &mut DynAsyncTransport,
    read_buffer: &mut Vec<u8>,
) -> Result<()> {
    let mut chunk = [0u8; 256];
    loop {
        match transport.read(&mut chunk).await {
            Ok(0) => break,
            Ok(_) => continue,
            Err(Error::Timeout) => break,
            Err(e) => return Err(e),
        }
    }
    read_buffer.clear();
    Ok(())
}

async fn read_framed_response(
    transport: &mut DynAsyncTransport,
    read_buffer: &mut Vec<u8>,
    skip_block_terminator: &mut bool,
    opts: &ConnectOptions,
    command: Option<&str>,
    diagnostics: &Option<Diagnostics>,
    timeout: Duration,
) -> Result<Vec<u8>> {
    let deadline = Instant::now() + timeout;
    read_buffer.clear();
    let mut chunk = [0u8; 1024];
    loop {
        let started = Instant::now();
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(Error::Timeout);
        }
        transport.set_read_timeout(remaining).await?;
        match transport.read(&mut chunk).await {
            Ok(0) => {
                if !read_buffer.is_empty() {
                    if let Ok((payload, _)) = extract_response(read_buffer, &opts.terminator) {
                        record_success(diagnostics, CommsEventKind::ReadOk, command, 1, started);
                        *skip_block_terminator =
                            read_buffer.starts_with(b"#") && read_buffer.get(1) != Some(&b'0');
                        return Ok(payload);
                    }
                }
                record_failure(
                    diagnostics,
                    CommsEventKind::Timeout,
                    command,
                    1,
                    started,
                    "zero-byte read",
                );
                return Err(Error::Timeout);
            }
            Ok(n) => {
                for &byte in &chunk[..n] {
                    if *skip_block_terminator && matches!(byte, b'\r' | b'\n') {
                        continue;
                    }
                    *skip_block_terminator = false;
                    read_buffer.push(byte);
                }
                if let Ok((payload, _)) = extract_response(read_buffer, &opts.terminator) {
                    record_success(diagnostics, CommsEventKind::ReadOk, command, 1, started);
                    *skip_block_terminator =
                        read_buffer.starts_with(b"#") && read_buffer.get(1) != Some(&b'0');
                    return Ok(payload);
                }
            }
            Err(Error::Timeout) => {
                if !read_buffer.is_empty() {
                    if let Ok((payload, _)) = extract_response(read_buffer, &opts.terminator) {
                        record_success(diagnostics, CommsEventKind::ReadOk, command, 1, started);
                        *skip_block_terminator =
                            read_buffer.starts_with(b"#") && read_buffer.get(1) != Some(&b'0');
                        return Ok(payload);
                    }
                }
                record_failure(
                    diagnostics,
                    CommsEventKind::Timeout,
                    command,
                    1,
                    started,
                    "read timeout",
                );
                return Err(Error::Timeout);
            }
            Err(e) => {
                record_failure(
                    diagnostics,
                    CommsEventKind::ReadFailed,
                    command,
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
    diagnostics: &Option<Diagnostics>,
    kind: CommsEventKind,
    command: Option<&str>,
    attempt: u32,
    started: Instant,
) {
    if let Some(diag) = diagnostics {
        diag.record_success(kind, command, attempt, started.elapsed());
    }
}

fn record_failure(
    diagnostics: &Option<Diagnostics>,
    kind: CommsEventKind,
    command: Option<&str>,
    attempt: u32,
    started: Instant,
    detail: &str,
) {
    if let Some(diag) = diagnostics {
        diag.record_failure(kind, command, attempt, started.elapsed(), detail);
    }
}

fn record_reconnect(diagnostics: &Option<Diagnostics>) {
    if let Some(diag) = diagnostics {
        diag.record_success(CommsEventKind::Reconnect, None, 1, Duration::ZERO);
    }
}

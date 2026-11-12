use std::time::Duration;

/// VISA access mode (maps to visa-rs AccessMode when using visa backend).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AccessMode {
    pub exclusive_lock: bool,
    pub shared_lock: bool,
}

impl AccessMode {
    pub const NO_LOCK: Self = Self {
        exclusive_lock: false,
        shared_lock: false,
    };

    pub const SHARED_LOCK: Self = Self {
        exclusive_lock: false,
        shared_lock: true,
    };

    pub const EXCLUSIVE_LOCK: Self = Self {
        exclusive_lock: true,
        shared_lock: false,
    };
}

/// Options when opening an instrument session.
#[derive(Debug, Clone)]
pub struct ConnectOptions {
    pub open_timeout: Duration,
    pub read_timeout: Duration,
    pub write_timeout: Duration,
    pub per_op_timeout: Option<Duration>,
    /// Maximum response payload bytes, excluding SCPI framing. None leaves the raw adapter unbounded.
    pub max_response_bytes: Option<usize>,
    pub terminator: String,
    pub access_mode: AccessMode,
    pub reset_on_connect: bool,
    pub retries: u32,
    pub retry_backoff: Duration,
    /// When true, timed-out writes/queries call [`crate::transport::Transport::reconnect`].
    ///
    /// VISA backends return [`crate::error::Error::Unsupported`]; `try_reconnect` swallows
    /// that so write/query retry still runs. TCP reconnect needs a SOCKET transport
    /// (not VISA). Default stays true so a future reconnectable transport is opted in.
    pub reconnect_on_failure: bool,
}

impl Default for ConnectOptions {
    fn default() -> Self {
        Self {
            open_timeout: Duration::from_secs(5),
            read_timeout: Duration::from_secs(10),
            write_timeout: Duration::from_secs(10),
            per_op_timeout: None,
            max_response_bytes: None,
            terminator: "\n".to_string(),
            access_mode: AccessMode::NO_LOCK,
            reset_on_connect: false,
            retries: 2,
            retry_backoff: Duration::from_millis(100),
            reconnect_on_failure: true,
        }
    }
}

impl ConnectOptions {
    pub fn with_read_timeout(mut self, timeout: Duration) -> Self {
        self.read_timeout = timeout;
        self
    }

    pub fn with_per_op_timeout(mut self, timeout: Duration) -> Self {
        self.per_op_timeout = Some(timeout);
        self
    }

    /// Single I/O timeout for backends (VISA) that cannot split read vs write.
    pub fn io_timeout(&self) -> Duration {
        self.per_op_timeout
            .unwrap_or_else(|| self.read_timeout.max(self.write_timeout))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn io_timeout_uses_max_of_read_and_write() {
        let mut opts = ConnectOptions {
            read_timeout: Duration::from_secs(2),
            write_timeout: Duration::from_secs(7),
            ..ConnectOptions::default()
        };
        assert_eq!(opts.io_timeout(), Duration::from_secs(7));
        opts.per_op_timeout = Some(Duration::from_millis(50));
        assert_eq!(opts.io_timeout(), Duration::from_millis(50));
    }
}

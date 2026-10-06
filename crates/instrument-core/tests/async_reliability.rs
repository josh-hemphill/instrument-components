use instrument_core::async_transport::AsyncTransport;
use instrument_core::connect::ConnectOptions;
use instrument_core::error::{Error, Result, TransportError};
use instrument_core::ieee4882::AsyncIeee4882;
use instrument_core::mock::{MockTransport, ScriptStep};
use instrument_core::transport::{Transport, TransportIdentity};
use instrument_core::{AsyncScpiSession, SyncAsAsyncTransport};
use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

struct TimeoutSpy {
    last: Arc<Mutex<Option<Duration>>>,
}

impl Transport for TimeoutSpy {
    fn write(&mut self, _data: &[u8]) -> Result<()> {
        Ok(())
    }

    fn read(&mut self, _buf: &mut [u8]) -> Result<usize> {
        Err(Error::Transport(TransportError::Closed))
    }

    fn clear(&mut self) -> Result<()> {
        Ok(())
    }

    fn set_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        *self.last.lock().expect("timeout spy") = Some(timeout);
        Ok(())
    }
}

struct ReconnectProbe {
    reconnects: Arc<Mutex<u32>>,
    remaining_timeouts: u32,
    zero_byte: bool,
    payload: Option<Vec<u8>>,
}

impl Transport for ReconnectProbe {
    fn write(&mut self, _data: &[u8]) -> Result<()> {
        Ok(())
    }

    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        if self.zero_byte {
            return Ok(0);
        }
        if self.remaining_timeouts > 0 {
            self.remaining_timeouts -= 1;
            return Err(Error::Timeout);
        }
        if let Some(payload) = self.payload.take() {
            let n = payload.len().min(buf.len());
            buf[..n].copy_from_slice(&payload[..n]);
            return Ok(n);
        }
        Err(Error::Transport(TransportError::Closed))
    }

    fn clear(&mut self) -> Result<()> {
        Ok(())
    }

    fn set_read_timeout(&mut self, _timeout: Duration) -> Result<()> {
        Ok(())
    }

    fn reconnect(&mut self) -> Result<()> {
        *self.reconnects.lock().expect("reconnect counter") += 1;
        Ok(())
    }
}

struct PendingReadAsync {
    last: Arc<Mutex<Option<Duration>>>,
}

impl AsyncTransport for PendingReadAsync {
    fn write<'a>(
        &'a mut self,
        _data: &'a [u8],
    ) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>> {
        Box::pin(async { Ok(()) })
    }

    fn read<'a>(
        &'a mut self,
        _buf: &'a mut [u8],
    ) -> Pin<Box<dyn Future<Output = Result<usize>> + Send + 'a>> {
        Box::pin(std::future::pending())
    }

    fn set_read_timeout<'a>(
        &'a mut self,
        timeout: Duration,
    ) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>> {
        Box::pin(async move {
            *self.last.lock().expect("timeout spy") = Some(timeout);
            Ok(())
        })
    }

    fn apply_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        *self.last.lock().expect("timeout spy") = Some(timeout);
        Ok(())
    }

    fn identity(&self) -> TransportIdentity {
        TransportIdentity::default()
    }
}

fn retry_opts() -> ConnectOptions {
    ConnectOptions {
        retries: 1,
        retry_backoff: Duration::from_millis(1),
        reconnect_on_failure: false,
        ..Default::default()
    }
}

#[tokio::test]
async fn query_retries_after_timeout_then_succeeds() {
    let transport = MockTransport::from_script(vec![
        ScriptStep::Write {
            data: ":MEAS:VOLT:DC?\n".into(),
        },
        ScriptStep::Read {
            data: "1.0\n".into(),
        },
        ScriptStep::Write {
            data: ":MEAS:VOLT:DC?\n".into(),
        },
        ScriptStep::Read {
            data: "1.0\n".into(),
        },
    ])
    .fail_writes(1);

    let mut opts = ConnectOptions::default();
    opts.retries = 1;
    opts.retry_backoff = Duration::from_millis(1);

    let mut session = AsyncScpiSession::new(Box::new(transport), opts)
        .await
        .unwrap();
    let volts = session.query(":MEAS:VOLT:DC?").await.unwrap();
    assert_eq!(volts.trim(), "1.0");
}

#[tokio::test]
async fn query_retries_read_timeout_flushes_stale_then_succeeds() {
    let transport = MockTransport::from_script(vec![
        ScriptStep::Write {
            data: ":MEAS:VOLT:DC?\n".into(),
        },
        ScriptStep::Read {
            data: "1.0\n".into(),
        },
        ScriptStep::Write {
            data: ":MEAS:VOLT:DC?\n".into(),
        },
        ScriptStep::Read {
            data: "3.3\n".into(),
        },
    ])
    .fail_reads(1);

    let mut session = AsyncScpiSession::new(Box::new(transport), retry_opts())
        .await
        .unwrap();
    let volts = session.query(":MEAS:VOLT:DC?").await.unwrap();
    assert_eq!(volts.trim(), "3.3");
}

#[tokio::test]
async fn query_read_retries_exhausted_is_timeout() {
    let transport = MockTransport::from_script(vec![
        ScriptStep::Write {
            data: ":MEAS:VOLT:DC?\n".into(),
        },
        ScriptStep::Write {
            data: ":MEAS:VOLT:DC?\n".into(),
        },
    ])
    // Two query attempts plus a flush read after each timed-out framed read.
    .fail_reads(4);

    let mut session = AsyncScpiSession::new(Box::new(transport), retry_opts())
        .await
        .unwrap();
    let err = session.query(":MEAS:VOLT:DC?").await.unwrap_err();
    assert!(matches!(err, Error::Timeout));
}

#[tokio::test]
async fn probe_opc_failure_restores_io_timeout() {
    let last = Arc::new(Mutex::new(None));
    let opts = ConnectOptions {
        read_timeout: Duration::from_secs(9),
        write_timeout: Duration::from_secs(4),
        reconnect_on_failure: false,
        ..Default::default()
    };
    let expected = opts.io_timeout();

    let mut session = AsyncScpiSession::new(
        Box::new(SyncAsAsyncTransport::new(TimeoutSpy { last: last.clone() })),
        opts,
    )
    .await
    .unwrap();
    assert!(!session.probe_opc().await);
    assert_eq!(*last.lock().expect("timeout spy"), Some(expected));
}

#[tokio::test]
async fn probe_opc_undefined_header_is_unsupported() {
    let transport = MockTransport::from_script(vec![
        ScriptStep::Write {
            data: "*OPC?\n".into(),
        },
        ScriptStep::Read {
            data: "-113,\"Undefined header\"\n".into(),
        },
    ]);
    let mut session = AsyncScpiSession::new(Box::new(transport), retry_opts())
        .await
        .unwrap();
    assert!(!session.probe_opc().await);
    assert!(matches!(
        AsyncIeee4882::new(&mut session).wait_complete().await,
        Err(Error::Unsupported(_))
    ));
}

#[tokio::test]
async fn zero_byte_read_is_timeout_without_spin() {
    let mut opts = retry_opts();
    opts.retries = 0;
    opts.read_timeout = Duration::from_secs(10);
    let mut session = AsyncScpiSession::new(
        Box::new(SyncAsAsyncTransport::new(ReconnectProbe {
            reconnects: Arc::new(Mutex::new(0)),
            remaining_timeouts: 0,
            zero_byte: true,
            payload: None,
        })),
        opts,
    )
    .await
    .unwrap();
    let started = Instant::now();
    let err = session.query("*IDN?").await.unwrap_err();
    assert!(matches!(err, Error::Timeout));
    assert!(
        started.elapsed() < Duration::from_secs(2),
        "zero-byte read spun instead of failing closed"
    );
}

#[tokio::test]
async fn dropped_query_restores_io_timeout() {
    let last = Arc::new(Mutex::new(None));
    let opts = ConnectOptions {
        read_timeout: Duration::from_secs(2),
        write_timeout: Duration::from_secs(7),
        reconnect_on_failure: false,
        retries: 0,
        ..Default::default()
    };
    let expected = opts.io_timeout();
    let mut session =
        AsyncScpiSession::new(Box::new(PendingReadAsync { last: last.clone() }), opts)
            .await
            .unwrap();

    let timed_out = tokio::time::timeout(Duration::from_millis(50), session.query("*IDN?")).await;
    assert!(timed_out.is_err(), "query future should still be pending");
    assert_eq!(*last.lock().expect("timeout spy"), Some(expected));
}

#[tokio::test]
async fn zero_byte_read_does_not_reconnect() {
    let reconnects = Arc::new(Mutex::new(0));
    let mut opts = retry_opts();
    opts.retries = 0;
    opts.reconnect_on_failure = true;
    let mut session = AsyncScpiSession::new(
        Box::new(SyncAsAsyncTransport::new(ReconnectProbe {
            reconnects: reconnects.clone(),
            remaining_timeouts: 0,
            zero_byte: true,
            payload: None,
        })),
        opts,
    )
    .await
    .unwrap();
    let err = session.query("*IDN?").await.unwrap_err();
    assert!(matches!(err, Error::Timeout));
    assert_eq!(*reconnects.lock().expect("reconnect counter"), 0);
}

#[tokio::test]
async fn query_read_timeout_reconnects_once_then_succeeds() {
    let reconnects = Arc::new(Mutex::new(0));
    let mut opts = retry_opts();
    opts.reconnect_on_failure = true;
    let mut session = AsyncScpiSession::new(
        Box::new(SyncAsAsyncTransport::new(ReconnectProbe {
            reconnects: reconnects.clone(),
            remaining_timeouts: 2,
            zero_byte: false,
            payload: Some(b"3.3\n".to_vec()),
        })),
        opts,
    )
    .await
    .unwrap();
    let volts = session.query(":MEAS:VOLT:DC?").await.unwrap();
    assert_eq!(volts.trim(), "3.3");
    assert_eq!(*reconnects.lock().expect("reconnect counter"), 1);
}

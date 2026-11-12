use instrument_core::connect::ConnectOptions;
use instrument_core::error::{Error, Result};
use instrument_core::scpi::ScpiSession;
use instrument_core::transport::Transport;
use std::sync::{Arc, Mutex};
use std::time::Duration;

#[derive(Default)]
struct State {
    reads: usize,
    writes: usize,
    timeout: Duration,
    short_applied: bool,
}

struct StreamingTransport {
    state: Arc<Mutex<State>>,
    timeout_first_read: bool,
    quiet_after: Option<usize>,
    timeout_at_quiet: bool,
    delay: Duration,
    fail_short_timeout: bool,
    fail_restore: bool,
}

impl Transport for StreamingTransport {
    fn write(&mut self, _: &[u8]) -> Result<()> {
        self.state.lock().unwrap().writes += 1;
        Ok(())
    }
    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        let mut state = self.state.lock().unwrap();
        state.reads += 1;
        if self.timeout_first_read && state.reads == 1 {
            return Err(Error::Timeout);
        }
        assert!(state.reads < 100, "finite fake-stream guard reached");
        if self.quiet_after.is_some_and(|n| state.reads > n) {
            return if self.timeout_at_quiet {
                Err(Error::Timeout)
            } else {
                Ok(0)
            };
        }
        std::thread::sleep(self.delay);
        buf[0] = b'x';
        Ok(1)
    }
    fn clear(&mut self) -> Result<()> {
        Ok(())
    }
    fn set_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        let mut state = self.state.lock().unwrap();
        if self.fail_restore && state.short_applied && timeout >= Duration::from_secs(1) {
            return Err(Error::Transport(
                instrument_core::error::TransportError::Io("restore failed".into()),
            ));
        }
        state.timeout = timeout;
        if timeout <= Duration::from_millis(50) {
            state.short_applied = true;
        }
        if self.fail_short_timeout && timeout < Duration::from_secs(3) {
            return Err(Error::Timeout);
        }
        Ok(())
    }
}

fn fixture(query: bool, retries: u32) -> (StreamingTransport, Arc<Mutex<State>>, ConnectOptions) {
    let state = Arc::new(Mutex::new(State::default()));
    let transport = StreamingTransport {
        state: state.clone(),
        timeout_first_read: query,
        quiet_after: None,
        timeout_at_quiet: false,
        delay: Duration::from_millis(2),
        fail_short_timeout: false,
        fail_restore: false,
    };
    let opts = ConnectOptions {
        read_timeout: Duration::from_secs(3),
        write_timeout: Duration::from_secs(3),
        retries,
        retry_backoff: Duration::ZERO,
        reconnect_on_failure: false,
        ..ConnectOptions::default()
    };
    (transport, state, opts)
}

#[test]
fn continuous_sync_drain_is_bounded_and_failed_cleanup_does_not_retry() {
    for query in [false, true] {
        for retries in [0, 2] {
            let (transport, state, opts) = fixture(query, retries);
            let mut session = ScpiSession::new(Box::new(transport), opts).unwrap();
            let result = if query {
                session.query("READ?").map(|_| ())
            } else {
                session.flush()
            };
            assert!(matches!(result, Err(Error::Timeout)));
            let state = state.lock().unwrap();
            assert!(state.reads < 100);
            assert_eq!(state.writes, usize::from(query));
            assert_eq!(state.timeout, Duration::from_secs(3));
        }
    }
}

#[cfg(feature = "async")]
#[tokio::test]
async fn continuous_async_drain_is_bounded_and_failed_cleanup_does_not_retry() {
    use instrument_core::async_transport::SyncAsAsyncTransport;
    use instrument_core::scpi::AsyncScpiSession;
    for query in [false, true] {
        for retries in [0, 2] {
            let (transport, state, opts) = fixture(query, retries);
            let mut session =
                AsyncScpiSession::new(Box::new(SyncAsAsyncTransport::new(transport)), opts)
                    .await
                    .unwrap();
            let result = if query {
                session.query("READ?").await.map(|_| ())
            } else {
                session.flush().await
            };
            assert!(matches!(result, Err(Error::Timeout)));
            let state = state.lock().unwrap();
            assert!(state.reads < 100);
            assert_eq!(state.writes, usize::from(query));
            assert_eq!(state.timeout, Duration::from_secs(3));
        }
    }
}

#[test]
fn sync_quiet_budget_and_timeout_configuration_errors_remain_distinct() {
    for case in 0..4 {
        let (mut transport, state, opts) = fixture(false, 0);
        transport.quiet_after = Some(2);
        transport.timeout_at_quiet = case != 0;
        if case == 2 {
            transport.delay = Duration::from_millis(45);
        }
        if case == 3 {
            transport.fail_short_timeout = true;
        }
        let result = ScpiSession::new(Box::new(transport), opts).unwrap().flush();
        if case < 2 {
            assert!(result.is_ok());
        } else {
            assert!(matches!(result, Err(Error::Timeout)));
        }
        assert_eq!(state.lock().unwrap().timeout, Duration::from_secs(3));
    }
}

#[cfg(feature = "async")]
#[tokio::test]
async fn async_quiet_budget_and_timeout_configuration_errors_remain_distinct() {
    use instrument_core::async_transport::SyncAsAsyncTransport;
    use instrument_core::scpi::AsyncScpiSession;
    for case in 0..4 {
        let (mut transport, state, opts) = fixture(false, 0);
        transport.quiet_after = Some(2);
        transport.timeout_at_quiet = case != 0;
        if case == 2 {
            transport.delay = Duration::from_millis(45);
        }
        if case == 3 {
            transport.fail_short_timeout = true;
        }
        let result = AsyncScpiSession::new(Box::new(SyncAsAsyncTransport::new(transport)), opts)
            .await
            .unwrap()
            .flush()
            .await;
        if case < 2 {
            assert!(result.is_ok());
        } else {
            assert!(matches!(result, Err(Error::Timeout)));
        }
        assert_eq!(state.lock().unwrap().timeout, Duration::from_secs(3));
    }
}

#[test]
fn failed_sync_timeout_restoration_is_not_successful_cleanup() {
    for query in [false, true] {
        let (mut transport, state, opts) = fixture(query, 2);
        transport.quiet_after = Some(if query { 2 } else { 1 });
        transport.timeout_at_quiet = true;
        transport.fail_restore = true;
        let mut session = ScpiSession::new(Box::new(transport), opts).unwrap();
        let result = if query {
            session.query("READ?").map(|_| ())
        } else {
            session.flush()
        };
        assert!(matches!(result, Err(Error::Transport(_))));
        assert_eq!(state.lock().unwrap().writes, usize::from(query));
    }
}

#[cfg(feature = "async")]
#[tokio::test]
async fn failed_async_timeout_restoration_is_not_successful_cleanup() {
    use instrument_core::async_transport::SyncAsAsyncTransport;
    use instrument_core::scpi::AsyncScpiSession;
    for query in [false, true] {
        let (mut transport, state, opts) = fixture(query, 2);
        transport.quiet_after = Some(if query { 2 } else { 1 });
        transport.timeout_at_quiet = true;
        transport.fail_restore = true;
        let mut session =
            AsyncScpiSession::new(Box::new(SyncAsAsyncTransport::new(transport)), opts)
                .await
                .unwrap();
        let result = if query {
            session.query("READ?").await.map(|_| ())
        } else {
            session.flush().await
        };
        assert!(matches!(result, Err(Error::Transport(_))));
        assert_eq!(state.lock().unwrap().writes, usize::from(query));
    }
}

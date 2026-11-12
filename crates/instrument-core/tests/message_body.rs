use instrument_core::{
    connect::ConnectOptions,
    error::{Error, Result},
    mock::{MockTransport, ScriptStep},
    scpi::ScpiSession,
    transport::Transport,
};
use std::sync::{
    atomic::{AtomicUsize, Ordering},
    Arc,
};
use std::time::Duration;

fn options() -> ConnectOptions {
    ConnectOptions {
        retries: 0,
        terminator: "\r\n".into(),
        ..Default::default()
    }
}

#[test]
fn plain_text_body_rejects_binary_wrappers_without_changing_scpi_query() {
    for wire in [
        "#110\r\n",
        "#211+1.25000E+0\r\n",
        "#00\r\n",
        "0\r\n1\r\n",
        "0\n",
    ] {
        for split in [false, true] {
            let mut steps = vec![ScriptStep::Write {
                data: "READ?\r\n".into(),
            }];
            if split {
                steps.push(ScriptStep::Read {
                    data: wire[..1].into(),
                });
                steps.push(ScriptStep::Read {
                    data: wire[1..].into(),
                });
            } else {
                steps.push(ScriptStep::Read { data: wire.into() });
            }
            let mut session =
                ScpiSession::new(Box::new(MockTransport::from_script(steps)), options()).unwrap();
            assert!(matches!(session.query_body("READ?"), Err(Error::Parse(_))));
        }
        let mut session = ScpiSession::new(
            Box::new(MockTransport::from_script(vec![
                ScriptStep::Write {
                    data: "READ?\r\n".into(),
                },
                ScriptStep::Read { data: wire.into() },
            ])),
            options(),
        )
        .unwrap();
        assert!(!session.query("READ?").unwrap().contains('#'));
    }
}

#[cfg(feature = "async")]
#[tokio::test]
async fn native_plain_text_body_rejects_binary_wrappers() {
    use instrument_core::{AsyncScpiSession, SyncAsAsyncTransport};
    for wire in [
        "#110\r\n",
        "#211+1.25000E+0\r\n",
        "#00\r\n",
        "0\r\n1\r\n",
        "0\n",
    ] {
        let transport = MockTransport::from_script(vec![
            ScriptStep::Write {
                data: "READ?\r\n".into(),
            },
            ScriptStep::Read {
                data: wire[..1].into(),
            },
            ScriptStep::Read {
                data: wire[1..].into(),
            },
        ]);
        let mut session =
            AsyncScpiSession::new(Box::new(SyncAsAsyncTransport::new(transport)), options())
                .await
                .unwrap();
        assert!(matches!(
            session.query_body("READ?").await,
            Err(Error::Parse(_))
        ));
    }
}
fn wire() -> MockTransport {
    MockTransport::from_script(vec![
        ScriptStep::Write {
            data: "READ?\r\n".into(),
        },
        ScriptStep::Read {
            data: " \t1.25 \r\n".into(),
        },
    ])
}
#[test]
fn complete_body_preserves_payload_whitespace_and_removes_only_framing() {
    let mut session = ScpiSession::new(Box::new(wire()), options()).unwrap();
    assert_eq!(session.query_body("READ?").unwrap(), " \t1.25 ");
}
#[cfg(feature = "async")]
#[tokio::test]
async fn native_complete_body_matches_sync() {
    use instrument_core::{AsyncScpiSession, SyncAsAsyncTransport};
    let mut session = AsyncScpiSession::new(Box::new(SyncAsAsyncTransport::new(wire())), options())
        .await
        .unwrap();
    assert_eq!(session.query_body("READ?").await.unwrap(), " \t1.25 ");
}
struct TimeoutWire(Arc<AtomicUsize>);
impl Transport for TimeoutWire {
    fn write(&mut self, _: &[u8]) -> Result<()> {
        self.0.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
    fn read(&mut self, _: &mut [u8]) -> Result<usize> {
        self.0.fetch_add(1, Ordering::SeqCst);
        Err(Error::Timeout)
    }
    fn set_read_timeout(&mut self, _: Duration) -> Result<()> {
        Ok(())
    }
    fn clear(&mut self) -> Result<()> {
        panic!("No automatic clear")
    }
}
#[test]
fn timeout_does_not_retry_or_drain_and_retry_policy_rejects_before_io() {
    let calls = Arc::new(AtomicUsize::new(0));
    let mut session = ScpiSession::new(Box::new(TimeoutWire(calls.clone())), options()).unwrap();
    assert!(matches!(session.query_body("READ?"), Err(Error::Timeout)));
    assert_eq!(calls.load(Ordering::SeqCst), 2);
    let mut config = options();
    config.retries = 1;
    let mut session = ScpiSession::new(Box::new(TimeoutWire(calls.clone())), config).unwrap();
    assert!(matches!(
        session.query_body("READ?"),
        Err(Error::Unsupported(_))
    ));
    assert_eq!(calls.load(Ordering::SeqCst), 2);
}

struct StalledWire {
    calls: Arc<AtomicUsize>,
    timeout: Duration,
    partial: bool,
}
impl Transport for StalledWire {
    fn write(&mut self, _: &[u8]) -> Result<()> {
        self.calls.fetch_add(1, Ordering::SeqCst);
        Ok(())
    }
    fn read(&mut self, buffer: &mut [u8]) -> Result<usize> {
        self.calls.fetch_add(1, Ordering::SeqCst);
        if self.partial {
            self.partial = false;
            buffer[0] = b'+';
            return Ok(1);
        }
        let (_sender, receiver) = std::sync::mpsc::channel::<usize>();
        receiver
            .recv_timeout(self.timeout)
            .map_err(|_| Error::Timeout)
    }
    fn set_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        self.timeout = timeout;
        Ok(())
    }
    fn clear(&mut self) -> Result<()> {
        panic!("No automatic clear")
    }
}
#[test]
fn independent_deadline_expires_on_stalled_incomplete_body_without_retry_or_drain() {
    for partial in [false, true] {
        let calls = Arc::new(AtomicUsize::new(0));
        let config = ConnectOptions {
            per_op_timeout: Some(Duration::from_millis(20)),
            ..options()
        };
        let mut session = ScpiSession::new(
            Box::new(StalledWire {
                calls: calls.clone(),
                timeout: Duration::ZERO,
                partial,
            }),
            config,
        )
        .unwrap();
        let started = std::time::Instant::now();
        assert!(matches!(session.query_body("READ?"), Err(Error::Timeout)));
        assert!(
            started.elapsed() >= Duration::from_millis(15)
                && started.elapsed() < Duration::from_secs(5)
        );
        assert_eq!(calls.load(Ordering::SeqCst), if partial { 3 } else { 2 });
    }
}

#[cfg(feature = "async")]
mod native_deadlines {
    use super::*;
    use instrument_core::{async_transport::AsyncTransport, AsyncScpiSession};
    use std::{future::Future, pin::Pin};
    struct Wire {
        calls: Arc<AtomicUsize>,
        timeout: Duration,
        partial: bool,
    }
    impl AsyncTransport for Wire {
        fn write<'a>(
            &'a mut self,
            _: &'a [u8],
        ) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>> {
            Box::pin(async move {
                self.calls.fetch_add(1, Ordering::SeqCst);
                Ok(())
            })
        }
        fn read<'a>(
            &'a mut self,
            buffer: &'a mut [u8],
        ) -> Pin<Box<dyn Future<Output = Result<usize>> + Send + 'a>> {
            Box::pin(async move {
                self.calls.fetch_add(1, Ordering::SeqCst);
                if self.partial {
                    self.partial = false;
                    buffer[0] = b'+';
                    return Ok(1);
                }
                tokio::time::timeout(self.timeout, std::future::pending::<usize>())
                    .await
                    .map_err(|_| Error::Timeout)
            })
        }
        fn set_read_timeout<'a>(
            &'a mut self,
            timeout: Duration,
        ) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>> {
            Box::pin(async move {
                self.timeout = timeout;
                Ok(())
            })
        }
        fn clear<'a>(&'a mut self) -> Pin<Box<dyn Future<Output = Result<()>> + Send + 'a>> {
            panic!("No automatic clear")
        }
    }
    #[tokio::test]
    async fn independent_native_deadline_expires_pending_empty_and_incomplete_body() {
        for partial in [false, true] {
            let calls = Arc::new(AtomicUsize::new(0));
            let config = ConnectOptions {
                per_op_timeout: Some(Duration::from_millis(20)),
                ..options()
            };
            let mut session = AsyncScpiSession::new(
                Box::new(Wire {
                    calls: calls.clone(),
                    timeout: Duration::ZERO,
                    partial,
                }),
                config,
            )
            .await
            .unwrap();
            let started = std::time::Instant::now();
            assert!(matches!(
                session.query_body("READ?").await,
                Err(Error::Timeout)
            ));
            assert!(
                started.elapsed() >= Duration::from_millis(15)
                    && started.elapsed() < Duration::from_secs(5)
            );
            assert_eq!(calls.load(Ordering::SeqCst), if partial { 3 } else { 2 });
        }
    }
}

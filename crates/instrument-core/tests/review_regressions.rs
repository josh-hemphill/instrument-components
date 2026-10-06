use instrument_core::connect::ConnectOptions;
use instrument_core::error::{Error, Result};
use instrument_core::ieee4882::Ieee4882;
use instrument_core::mock::{MockTransport, ScriptStep};
use instrument_core::scpi::ScpiSession;
use instrument_core::transport::Transport;
use serde_json::Value;
use std::collections::VecDeque;
use std::time::Duration;

fn vectors() -> Value {
    serde_json::from_str(
        &std::fs::read_to_string(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../../spec/review-regressions.json"),
        )
        .unwrap(),
    )
    .unwrap()
}
fn opts() -> ConnectOptions {
    ConnectOptions {
        retries: 0,
        reconnect_on_failure: false,
        ..Default::default()
    }
}
fn error_transport(v: &Value) -> MockTransport {
    MockTransport::from_script(
        v["replies"]
            .as_array()
            .unwrap()
            .iter()
            .flat_map(|reply| {
                vec![
                    ScriptStep::Write {
                        data: "SYST:ERR?\n".into(),
                    },
                    ScriptStep::Read {
                        data: format!("{}\n", reply.as_str().unwrap()),
                    },
                ]
            })
            .collect(),
    )
}

#[test]
fn shared_error_queues_preserve_first_entry_with_or_without_probe() {
    for probe_first in [false, true] {
        for v in vectors()["errorQueues"].as_array().unwrap() {
            let mut session = ScpiSession::new(Box::new(error_transport(v)), opts()).unwrap();
            if probe_first {
                session.probe_syst_err();
            }
            let expected: Vec<String> = v["expected"]
                .as_array()
                .unwrap()
                .iter()
                .map(|v| v.as_str().unwrap().into())
                .collect();
            assert_eq!(session.check_errors().unwrap(), expected, "{}", v["id"]);
        }
    }
}

#[test]
fn shared_probe_shapes_and_socket_addresses() {
    for v in vectors()["probeReplies"].as_array().unwrap() {
        assert_eq!(
            instrument_core::classifier::valid_probe_reply(
                v["command"].as_str().unwrap(),
                v["reply"].as_str().unwrap()
            ),
            v["valid"].as_bool().unwrap()
        );
    }
    for v in vectors()["socketAddresses"].as_array().unwrap() {
        assert_eq!(
            instrument_core::ResourceAddress::parse(v["address"].as_str().unwrap())
                .unwrap()
                .components
                .port,
            v["port"].as_u64().map(|p| p as u16)
        );
    }
}

struct ChunkTransport {
    chunks: VecDeque<Vec<u8>>,
    timeout: Duration,
    require_completion_timeout: bool,
}
impl ChunkTransport {
    fn block() -> Self {
        Self {
            chunks: vectors()["blockChunks"]
                .as_array()
                .unwrap()
                .iter()
                .map(|s| s.as_str().unwrap().as_bytes().to_vec())
                .collect(),
            timeout: Duration::ZERO,
            require_completion_timeout: false,
        }
    }
    fn completion(reply: &str) -> Self {
        Self {
            chunks: VecDeque::from([reply.as_bytes().to_vec()]),
            timeout: Duration::ZERO,
            require_completion_timeout: true,
        }
    }
}
impl Transport for ChunkTransport {
    fn write(&mut self, _: &[u8]) -> Result<()> {
        Ok(())
    }
    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        if self.require_completion_timeout {
            assert!(
                self.timeout <= Duration::from_secs(30) && self.timeout > Duration::from_secs(29)
            );
        }
        let reply = self.chunks.pop_front().ok_or(Error::Timeout)?;
        buf[..reply.len()].copy_from_slice(&reply);
        Ok(reply.len())
    }
    fn clear(&mut self) -> Result<()> {
        Ok(())
    }
    fn set_read_timeout(&mut self, timeout: Duration) -> Result<()> {
        self.timeout = timeout;
        Ok(())
    }
}

#[test]
fn fragmented_block_terminator_does_not_contaminate_next_query() {
    let mut session = ScpiSession::new(Box::new(ChunkTransport::block()), opts()).unwrap();
    assert_eq!(session.query("BLOCK?").unwrap(), "abcde");
    assert_eq!(session.query("NEXT?").unwrap(), "3.3");
}

#[test]
fn completion_uses_full_timeout_and_rejects_invalid_reply() {
    let mut session =
        ScpiSession::new(Box::new(ChunkTransport::completion("+1\n")), opts()).unwrap();
    Ieee4882::new(&mut session).wait_complete().unwrap();
    let mut session =
        ScpiSession::new(Box::new(ChunkTransport::completion("0\n")), opts()).unwrap();
    assert!(matches!(
        Ieee4882::new(&mut session).wait_complete(),
        Err(Error::Unsupported(_))
    ));
}

#[cfg(feature = "async")]
#[tokio::test]
async fn async_shared_queues_and_fragmented_block() {
    use instrument_core::{AsyncScpiSession, SyncAsAsyncTransport};
    for probe_first in [false, true] {
        for v in vectors()["errorQueues"].as_array().unwrap() {
            let mut session = AsyncScpiSession::new(Box::new(error_transport(v)), opts())
                .await
                .unwrap();
            if probe_first {
                session.probe_syst_err().await;
            }
            let expected: Vec<String> = v["expected"]
                .as_array()
                .unwrap()
                .iter()
                .map(|v| v.as_str().unwrap().into())
                .collect();
            assert_eq!(
                session.check_errors().await.unwrap(),
                expected,
                "{}",
                v["id"]
            );
        }
    }
    let mut session = AsyncScpiSession::new(
        Box::new(SyncAsAsyncTransport::new(ChunkTransport::block())),
        opts(),
    )
    .await
    .unwrap();
    assert_eq!(session.query("BLOCK?").await.unwrap(), "abcde");
    assert_eq!(session.query("NEXT?").await.unwrap(), "3.3");
    let mut session = AsyncScpiSession::new(
        Box::new(SyncAsAsyncTransport::new(ChunkTransport::completion(
            "+1\n",
        ))),
        opts(),
    )
    .await
    .unwrap();
    instrument_core::ieee4882::AsyncIeee4882::new(&mut session)
        .wait_complete()
        .await
        .unwrap();
}

#[test]
fn large_mock_reply_is_not_truncated() {
    let value = "x".repeat(1500);
    let transport = MockTransport::from_script(vec![
        ScriptStep::Write {
            data: "DATA?\n".into(),
        },
        ScriptStep::Read {
            data: format!("{value}\n"),
        },
    ]);
    let mut session = ScpiSession::new(Box::new(transport), opts()).unwrap();
    assert_eq!(session.query("DATA?").unwrap(), value);
}

struct ProbeTransport {
    output_only: bool,
    command: String,
}
impl Transport for ProbeTransport {
    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.command = String::from_utf8_lossy(data).into();
        Ok(())
    }
    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        let reply = if self.output_only && self.command.contains("OUTP?") {
            "1\n"
        } else {
            "-113,\"Undefined header\"\n"
        };
        buf[..reply.len()].copy_from_slice(reply.as_bytes());
        Ok(reply.len())
    }
    fn clear(&mut self) -> Result<()> {
        Ok(())
    }
    fn set_read_timeout(&mut self, _: Duration) -> Result<()> {
        Ok(())
    }
}
#[test]
fn error_only_and_output_only_responders_have_no_capabilities() {
    for output_only in [false, true] {
        let mut session = ScpiSession::new(
            Box::new(ProbeTransport {
                output_only,
                command: String::new(),
            }),
            opts(),
        )
        .unwrap();
        assert!(instrument_core::classifier::classify_with_policy(
            &mut session,
            instrument_core::probe_policy::ProbePolicy::ReadOnly
        )
        .is_empty());
    }
}

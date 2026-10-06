use instrument_core::classifier::classify_with_policy;
#[cfg(feature = "async")]
use instrument_core::classifier::classify_with_policy_async;
use instrument_core::connect::ConnectOptions;
use instrument_core::error::Result;
use instrument_core::kind::InstrumentKind;
use instrument_core::mock::{MockTransport, ScriptStep};
use instrument_core::probe_policy::ProbePolicy;
use instrument_core::transport::Transport;
#[cfg(feature = "async")]
use instrument_core::AsyncScpiSession;
use instrument_core::ScpiSession;
use std::time::Duration;

fn options() -> ConnectOptions {
    ConnectOptions {
        retries: 0,
        reconnect_on_failure: false,
        ..Default::default()
    }
}
fn errors(zero: &str) -> MockTransport {
    MockTransport::from_script(vec![
        ScriptStep::Write {
            data: "SYST:ERR?\n".into(),
        },
        ScriptStep::Read {
            data: format!("{zero}\n"),
        },
        ScriptStep::Write {
            data: "BAD\n".into(),
        },
        ScriptStep::Write {
            data: "SYST:ERR?\n".into(),
        },
        ScriptStep::Read {
            data: "-113,\"Undefined header\"\n".into(),
        },
        ScriptStep::Write {
            data: "SYST:ERR?\n".into(),
        },
        ScriptStep::Read {
            data: "0,\"No error\"\n".into(),
        },
    ])
}

#[test]
fn probe_before_command_must_not_hide_new_errors() {
    for zero in ["0,\"No error\"", "+0,\"No error\"", "  +0 , \"No error\"  "] {
        let mut session = ScpiSession::new(Box::new(errors(zero)), options()).unwrap();
        assert!(session.probe_syst_err());
        session.write("BAD").unwrap();
        assert_eq!(
            session.check_errors().unwrap(),
            vec!["-113,\"Undefined header\""]
        );
    }
}

#[cfg(feature = "async")]
#[tokio::test]
async fn async_probe_before_command_must_not_hide_new_errors() {
    for zero in ["0,\"No error\"", "+0,\"No error\"", "  +0 , \"No error\"  "] {
        let mut session = AsyncScpiSession::new(Box::new(errors(zero)), options())
            .await
            .unwrap();
        assert!(session.probe_syst_err().await);
        session.write("BAD").await.unwrap();
        assert_eq!(
            session.check_errors().await.unwrap(),
            vec!["-113,\"Undefined header\""]
        );
    }
}

#[derive(Default)]
struct SignedVoltage {
    command: String,
}
impl Transport for SignedVoltage {
    fn write(&mut self, bytes: &[u8]) -> Result<()> {
        self.command = String::from_utf8_lossy(bytes).trim().into();
        Ok(())
    }
    fn read(&mut self, buf: &mut [u8]) -> Result<usize> {
        let reply = if self.command.trim_start_matches(':') == "MEAS:VOLT:DC?" {
            "-3.3\n"
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
fn negative_voltage_still_identifies_dmm() {
    let mut session = ScpiSession::new(Box::new(SignedVoltage::default()), options()).unwrap();
    assert!(classify_with_policy(&mut session, ProbePolicy::Full)
        .iter()
        .any(|k| k.kind == InstrumentKind::Dmm));
}

#[cfg(feature = "async")]
#[tokio::test]
async fn async_negative_voltage_still_identifies_dmm() {
    let mut session = AsyncScpiSession::new(
        Box::new(instrument_core::SyncAsAsyncTransport::new(
            SignedVoltage::default(),
        )),
        options(),
    )
    .await
    .unwrap();
    assert!(classify_with_policy_async(&mut session, ProbePolicy::Full)
        .await
        .iter()
        .any(|k| k.kind == InstrumentKind::Dmm));
}

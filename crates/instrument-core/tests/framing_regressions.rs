use instrument_core::{
    connect::ConnectOptions,
    error::Error,
    mock::{MockTransport, ScriptStep},
    scpi::ScpiSession,
};

fn malformed() -> Vec<String> {
    let cases: serde_json::Value =
        serde_json::from_str(include_str!("framing-cases.json")).unwrap();
    cases["cases"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|c| c["error"] == "parse")
        .map(|c| c["wire"].as_str().unwrap().to_owned())
        .collect()
}
fn transport(wire: String) -> MockTransport {
    MockTransport::from_script(vec![
        ScriptStep::Write {
            data: ":READ?\n".into(),
        },
        ScriptStep::Read { data: wire },
    ])
}
#[test]
fn sync_sessions_propagate_malformed_headers_without_reading_again() {
    for wire in malformed() {
        let mut session =
            ScpiSession::new(Box::new(transport(wire.clone())), ConnectOptions::default()).unwrap();
        assert!(
            matches!(session.query(":READ?"), Err(Error::Parse(_))),
            "{wire:?}"
        );
    }
}
#[cfg(feature = "async")]
#[tokio::test]
async fn async_sessions_propagate_malformed_headers_without_reading_again() {
    use instrument_core::{AsyncScpiSession, SyncAsAsyncTransport};
    for wire in malformed() {
        let mut session = AsyncScpiSession::new(
            Box::new(SyncAsAsyncTransport::new(transport(wire.clone()))),
            ConnectOptions::default(),
        )
        .await
        .unwrap();
        assert!(
            matches!(session.query(":READ?").await, Err(Error::Parse(_))),
            "{wire:?}"
        );
    }
}

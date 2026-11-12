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

fn limited_transport(case: &serde_json::Value, split: usize) -> (MockTransport, ConnectOptions) {
    let term = case["terminator"].as_str().unwrap_or("\n");
    let wire = case["wire"].as_str().unwrap();
    let mut steps = vec![ScriptStep::Write {
        data: format!("READ?{term}"),
    }];
    if split > 0 {
        steps.push(ScriptStep::Read {
            data: wire[..split].into(),
        });
    }
    steps.push(ScriptStep::Read {
        data: wire[split..].into(),
    });
    (
        MockTransport::from_script(steps),
        ConnectOptions {
            max_response_bytes: Some(case["limit"].as_u64().unwrap() as usize),
            terminator: term.into(),
            retries: 2,
            ..ConnectOptions::default()
        },
    )
}

#[test]
fn sync_sessions_enforce_shared_payload_limits_before_reading_oversize_bodies() {
    let data: serde_json::Value =
        serde_json::from_str(include_str!("response-limit-cases.json")).unwrap();
    for case in data["cases"].as_array().unwrap() {
        if case["error"] == "timeout" {
            continue;
        }
        for (split, _) in case["wire"].as_str().unwrap().char_indices() {
            // Without a terminator, each available ASCII read defines a response.
            if split > 0
                && case["terminator"] == ""
                && !case["wire"].as_str().unwrap().starts_with('#')
            {
                continue;
            }
            let (transport, opts) = limited_transport(case, split);
            let mut session = ScpiSession::new(Box::new(transport), opts).unwrap();
            let result = session.query("READ?");
            if case["error"] == "parse" {
                assert!(
                    matches!(result, Err(Error::Parse(_))),
                    "{case:?}: {result:?}"
                );
            } else {
                assert_eq!(result.unwrap(), case["payload"].as_str().unwrap().trim());
            }
        }
    }
}

#[cfg(feature = "async")]
#[tokio::test]
async fn async_sessions_enforce_shared_payload_limits_before_reading_oversize_bodies() {
    use instrument_core::{AsyncScpiSession, SyncAsAsyncTransport};
    let data: serde_json::Value =
        serde_json::from_str(include_str!("response-limit-cases.json")).unwrap();
    for case in data["cases"].as_array().unwrap() {
        if case["error"] == "timeout" {
            continue;
        }
        for (split, _) in case["wire"].as_str().unwrap().char_indices() {
            // Without a terminator, each available ASCII read defines a response.
            if split > 0
                && case["terminator"] == ""
                && !case["wire"].as_str().unwrap().starts_with('#')
            {
                continue;
            }
            let (transport, opts) = limited_transport(case, split);
            let mut session =
                AsyncScpiSession::new(Box::new(SyncAsAsyncTransport::new(transport)), opts)
                    .await
                    .unwrap();
            let result = session.query("READ?").await;
            if case["error"] == "parse" {
                assert!(
                    matches!(result, Err(Error::Parse(_))),
                    "{case:?}: {result:?}"
                );
            } else {
                assert_eq!(result.unwrap(), case["payload"].as_str().unwrap().trim());
            }
        }
    }
}

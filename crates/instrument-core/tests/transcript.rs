use instrument_core::{ConnectOptions, MockTransport, ScpiSession, Transcript};
#[test]
fn synthetic_transcript_is_strict_and_round_trips_without_device_data() {
    let transcript=Transcript::from_json(r#"{"steps":[{"op":"write","data":"*IDN?\n"},{"op":"read","data":"Example,Device,SN,1\n"}]}"#).unwrap();
    let mut session = ScpiSession::new(
        Box::new(MockTransport::from_script(transcript.steps)),
        ConnectOptions::default(),
    )
    .unwrap();
    assert_eq!(session.query("*IDN?").unwrap(), "Example,Device,SN,1");
}

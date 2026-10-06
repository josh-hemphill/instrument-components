use instrument::prelude::*;
use instrument_core::{DeviceIdentity, Transcript};

fn load_transcript(name: &str) -> Transcript {
    let path = format!("{}/../../fixtures/{name}", env!("CARGO_MANIFEST_DIR"));
    let json = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("read {path}: {e}"));
    Transcript::from_json(&json).unwrap()
}

fn session_from_fixture(name: &str, manufacturer: &str, model: &str) -> InstrumentSession {
    let transcript = load_transcript(name);
    let identity = DeviceIdentity {
        manufacturer: Some(manufacturer.into()),
        model: Some(model.into()),
        serial: Some("SN".into()),
        firmware: Some("1.0".into()),
        options: None,
    };
    InstrumentSession::new(
        ResourceAddress::parse("TCPIP0::127.0.0.1::inst0::INSTR").unwrap(),
        Box::new(MockTransport::from_script(transcript.steps)),
        ConnectOptions::default(),
        identity,
    )
    .unwrap()
}

#[test]
fn smu2602_measure_voltage_dc() {
    let session = session_from_fixture("smu2602.json", "Keithley Instruments", "2602B");
    let mut dmm = Dmm::new(session);
    let volts = dmm.measure_voltage_dc(None).unwrap();
    assert!((volts - 3.3).abs() < 1e-9);
}

#[test]
fn scope_ds1054z_capture_trace() {
    let session = session_from_fixture("scope_ds1054z.json", "Rigol Technologies", "DS1054Z");
    let mut scope = Oscilloscope::new(session);
    scope.set_timebase_scale(1e-3).unwrap();
    let trace = scope.capture_voltage_trace(1).unwrap();
    assert_eq!(trace.samples, vec![1.0, 2.0, 3.0]);
    assert!((trace.sample_interval_s - 1e-6).abs() < 1e-12);
}

#[test]
fn switch_34970a_is_closed() {
    let session = session_from_fixture("switch_34970a.json", "Keysight Technologies", "34970A");
    let mut sw = Switch::new(session);
    assert!(sw.is_closed(1, 2).unwrap());
}

#[test]
fn counter_53230a_measure_frequency() {
    let session = session_from_fixture("counter_53230a.json", "Keysight Technologies", "53230A");
    let mut counter = Counter::new(session);
    let freq = counter.measure_frequency().unwrap();
    assert!((freq - 1000.0).abs() < 1e-9);
}

#[test]
fn dmm_dmm6500_measure_voltage_dc() {
    // Live *IDN? often reports model "MODEL DMM6500", not exact "DMM6500".
    let session = session_from_fixture("dmm_dmm6500.json", "Keithley Instruments", "MODEL DMM6500");
    let mut dmm = Dmm::new(session);
    let dialect_volts = dmm.measure_voltage_dc(None).unwrap();
    assert!((dialect_volts - 1.2345).abs() < 1e-9);
    // Ranged measure cannot fill the constant vendor template, so this is
    // generic :MEAS:VOLT:DC? 10 — not complete DMM6500 SCPI.
    let ranged_volts = dmm.measure_voltage_dc(Some(10.0)).unwrap();
    assert!((ranged_volts - 10.001).abs() < 1e-9);
}

#[test]
fn psu_n6705c_set_and_read_voltage() {
    // N6705 units often IDN as Agilent Technologies, not Keysight.
    let session = session_from_fixture("psu_n6705c.json", "Agilent Technologies", "N6705C");
    let mut psu = DcPowerSupply::new(session);
    assert_eq!(psu.channel_count(), 4);
    assert!(matches!(
        psu.sense_enable(1, true),
        Err(instrument_core::Error::Unsupported(_))
    ));
    psu.set_voltage(1, 3.3).unwrap();
    psu.output_enable(1, true).unwrap();
    let volts = psu.read_voltage(1).unwrap();
    assert!((volts - 3.3).abs() < 1e-9);
}

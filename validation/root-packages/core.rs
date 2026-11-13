use instrument_core::{MockTransport, ScriptStep, Transport};
fn script() -> MockTransport {
    MockTransport::from_script(vec![
        ScriptStep::Write {
            data: "READ?\n".into(),
        },
        ScriptStep::Read {
            data: "1.25\n".into(),
        },
    ])
}
fn main() -> instrument_core::Result<()> {
    let mut io = script();
    io.write(b"READ?\n")?;
    let mut buffer = [0; 32];
    let count = io.read(&mut buffer)?;
    assert_eq!(&buffer[..count], b"1.25\n");
    assert_eq!(instrument_core::parse_f64("1.25")?, 1.25);
    #[cfg(feature = "async")]
    tokio::runtime::Builder::new_current_thread()
        .build()
        .unwrap()
        .block_on(async {
            use instrument_core::{AsyncTransport, SyncAsAsyncTransport};
            let mut io = SyncAsAsyncTransport::new(script());
            io.write(b"READ?\n").await?;
            let mut buffer = [0; 32];
            let count = io.read(&mut buffer).await?;
            assert_eq!(&buffer[..count], b"1.25\n");
            Ok::<(), instrument_core::Error>(())
        })?;
    println!("PASS packed root core mock transport, no device implementation or hardware");
    Ok(())
}

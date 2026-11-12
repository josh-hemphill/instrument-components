# Async I/O (opt-in)

> Historical 0.1-era async walkthrough. The current breaking 0.2 preview uses explicit driver factories and protocol-neutral handles; these examples are retained as history. Use the [current getting-started guide](../docs-site/docs/getting-started.md) and [initial testing notes](../docs-site/docs/initial-testing.md) for published alpha.1 and prepared corrections.

Enable the `tokio` feature for a **full async API** that mirrors the sync stack: `AsyncScpiSession`, `AsyncDiscovery`, `AsyncDeviceCatalog`, and typed classes (`AsyncDmm`, `AsyncDcPowerSupply`, `AsyncFunctionGenerator`, `AsyncOscilloscope`, `AsyncSwitch`, `AsyncCounter`).

Sync remains the default. Async is additive behind `tokio`.

## Enable

```toml
[dependencies]
instrument-components = { version = "0.1", features = ["visa", "tokio"] }
tokio = { version = "1", features = ["rt-multi-thread", "macros"] }
```

The published crates.io `0.1.0` tag does not include this async API. Use git/`latest` until `0.2.0`.

Feature chain:

```
instrument-components/tokio
  → instrument-core/async

instrument-components/visa + tokio
  → instrument-visa/tokio → visa-rs/tokio
```

Default (`features = ["visa"]` only) does **not** pull in tokio.

## Mock quick start (no VISA)

```rust
use instrument::prelude::*;

#[tokio::main]
async fn main() -> Result<()> {
    let fixture = ScriptedFixture::builder()
        .idn("Acme", "DMM1", "SN1", "1.0")
        .kinds([InstrumentKind::Dmm])
        .on_query(":MEAS:VOLT:DC?", "1.0")
        .build();

    let catalog = AsyncDeviceCatalog::from_fixture("mock://dmm", fixture).await?;
    let mut dmm = catalog.open_dmm("mock://dmm").await?;
    let volts = dmm.measure_voltage_dc(None).await?;
    println!("{volts} V");
    Ok(())
}
```

```bash
cargo run -p instrument-components --example mock_fixture_ci_async --features tokio --no-default-features
```

## Hardware quick start

VISA session **open is sync** (visa-rs limitation); read/write after open are async.

```rust
use instrument::prelude::*;

#[tokio::main]
async fn main() -> Result<()> {
    let catalog = AsyncDiscovery::visa()?.scan().await?;
    let mut dmm = catalog
        .open_dmm(&catalog.devices_by_kind(InstrumentKind::Dmm)[0].address.raw)
        .await?;
    let volts = dmm.measure_voltage_dc(None).await?;
    println!("{volts} V");
    Ok(())
}
```

## Async type map

| Sync | Async |
|---|---|
| `Transport` | `AsyncTransport` |
| `ScpiSession` | `AsyncScpiSession` |
| `InstrumentSession` | `AsyncInstrumentSession` |
| `Discovery::scan()` | `AsyncDiscovery::scan().await` |
| `DeviceCatalog` | `AsyncDeviceCatalog` |
| `DeviceRef` | `AsyncDeviceRef` |
| `Dmm` | `AsyncDmm` |
| `DcPowerSupply` | `AsyncDcPowerSupply` |
| `FunctionGenerator` | `AsyncFunctionGenerator` |
| `Oscilloscope` | `AsyncOscilloscope` |
| `Switch` | `AsyncSwitch` |
| `Counter` | `AsyncCounter` |
| `VisaTransport` | `VisaAsyncTransport` |
| `VisaSessionOpener` | `VisaAsyncSessionOpener` |

## Low-level adapter

`InstrumentTokioAdapter` from visa-rs is still re-exported for raw byte I/O without SCPI framing:

```rust
use instrument::prelude::*;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

async fn raw_idn(adapter: &mut InstrumentTokioAdapter) -> std::io::Result<()> {
    adapter.write_all(b"*IDN?\n").await?;
    let mut buf = vec![0u8; 256];
    let n = adapter.read(&mut buf).await?;
    println!("{}", String::from_utf8_lossy(&buf[..n]));
    Ok(())
}
```

Prefer `AsyncScpiSession` / typed classes for framing, retries, and diagnostics.

## Cross-compile

```toml
instrument-components = { version = "0.1", features = ["visa", "cross-compile"] }
```

```bash
cargo build --features cross-compile --target x86_64-pc-windows-gnu
```

Cross-compile fixes VISA enum **repr** for the target arch. You still need a target VISA import library at link time (`LIB_VISA_PATH`).

## Testing

```bash
cargo test -p instrument-core --features async
cargo test -p instrument-components --features tokio --no-default-features
```

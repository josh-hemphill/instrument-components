# Getting started (Rust)

> Historical 0.1-era Rust getting started. The current breaking 0.2 preview uses explicit driver factories and protocol-neutral handles; these examples are retained as history. Use the [current getting-started guide](../docs-site/docs/getting-started.md) and [initial testing notes](../docs-site/docs/initial-testing.md) for published alpha.1 and prepared corrections.

Contributor-oriented Rust walkthrough. Dual-language published docs: [docs-site getting started](https://josh-hemphill.github.io/instrument-components/getting-started/). C#: [dotnet-getting-started.md](dotnet-getting-started.md).

## Prerequisites

- Rust 1.70+ (`rustup update stable`)
- For hardware: NI-VISA or Keysight IO Libraries installed

## Step 1: Add the dependency

**Mock / CI (no VISA):**

```toml
[dependencies]
instrument-components = { version = "0.1", default-features = false }
```

**Hardware (VISA default):**

```toml
[dependencies]
instrument-components = "0.1"
```

## Step 2: Mock path (recommended first)

```rust
use instrument::prelude::*;

fn main() -> Result<()> {
    let fixture = ScriptedFixture::builder()
        .idn("Keysight Technologies", "34401A", "SN1", "1.0")
        .kinds([InstrumentKind::Dmm])
        .on_query("*IDN?", "Keysight Technologies,34401A,SN1,1.0")
        .on_query(":MEAS:VOLT:DC?", "1.234")
        .build();

    let catalog = DeviceCatalog::from_fixture("mock://dmm-1", fixture)?;
    let volts = catalog.open_dmm("mock://dmm-1")?.measure_voltage_dc(None)?;
    println!("{volts} V");
    Ok(())
}
```

Run: `cargo run` (no extra features needed).

## Step 3: Discover real instruments

```rust
use instrument::prelude::*;

fn main() -> Result<()> {
    let catalog = Discovery::visa()?.scan()?;
    for dev in catalog.devices() {
        println!(
            "{} @ {} — {:?}",
            dev.identity.model.as_deref().unwrap_or("?"),
            dev.address.raw,
            dev.supported_kinds,
        );
    }
    Ok(())
}
```

USB and GPIB devices are found automatically. Add TCPIP manually:

```rust
Discovery::visa()?
    .manual_address("TCPIP0::192.168.0.42::INSTR")
    .scan()?;
```

## Step 4: Open a typed class

```rust
let catalog = Discovery::visa()?.scan()?;
let dmms = catalog.devices_by_kind(InstrumentKind::Dmm);
if let Some(dev) = dmms.first() {
    let mut dmm = catalog.open_dmm(&dev.address.raw)?;
    let v = dmm.measure_voltage_dc(None)?;
    println!("{v} V");
}
```

## Step 5: Assign instruments in an app

Use stable `DeviceId` so replacing hardware does not break saved config:

```rust
let device_id = dev.device_id();
// persist device_id in your app config
let dmm = catalog.reconnect_by_identity(&device_id)?.open_dmm()?;
```

## Environment variables (VISA linking)

| Variable | Purpose |
|---|---|
| `LIB_VISA_PATH` | Directory containing `visa64.lib` / `visa.lib` |
| `LIB_VISA_NAME` | Library name (`visa64`, `visa32`, `visa`) |

## Optional: Async path (`tokio` feature)

Add `features = ["tokio"]` (and `tokio` runtime dep). Use `AsyncDeviceCatalog`, `AsyncDiscovery`, and `.await` on typed class methods:

```toml
[dependencies]
instrument-components = { version = "0.1", default-features = false, features = ["tokio"] }
tokio = { version = "1", features = ["rt-multi-thread", "macros"] }
```

```rust
use instrument::prelude::*;

#[tokio::main]
async fn main() -> Result<()> {
    let fixture = ScriptedFixture::builder()
        .idn("Keysight Technologies", "34401A", "SN1", "1.0")
        .kinds([InstrumentKind::Dmm])
        .on_query(":MEAS:VOLT:DC?", "1.234")
        .build();
    let catalog = AsyncDeviceCatalog::from_fixture("mock://dmm-1", fixture).await?;
    let volts = catalog.open_dmm("mock://dmm-1").await?.measure_voltage_dc(None).await?;
    println!("{volts} V");
    Ok(())
}
```

See [async.md](async.md) for hardware async discovery.

## Next steps

- [Discovery guide](discovery.md) — probe policy and classification
- [Diagnostics](diagnostics.md) — comms health for technicians
- [Examples](examples.md) — runnable `cargo run --example` commands

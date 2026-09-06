# instrument-components — dual-native test-equipment control

[![CI](https://github.com/josh-hemphill/instrument-components/actions/workflows/ci.yml/badge.svg)](https://github.com/josh-hemphill/instrument-components/actions/workflows/ci.yml)

High-level **Rust** and **C#** control for VISA instruments: auto-discovery, IVI-inspired typed classes, and mock fixtures for CI. Shared SCPI, dialect, and classifier tables keep the two native implementations aligned.

**Rust:** `cargo add instrument-components` then `use instrument::prelude::*` (the library name stays `instrument`).  
**C#:** project-reference `InstrumentComponents` (and `InstrumentComponents.Visa` for hardware) — see [dotnet/](dotnet/).

## What you get

- Auto-discovery of USB, GPIB, and serial VISA resources
- Typed instrument classes with SI units (volts, amps, hertz, seconds): DMM, DC power supply, function generator, oscilloscope, switch, counter, power meter, spectrum analyzer
- Mock transport for hardware-free CI in both languages
- Per-device comms health and optional push diagnostics
- Stable `DeviceId` for instrument replacement workflows

## Choose your path

| I want to… | Start here |
|---|---|
| Test in CI without hardware | [Mock quick start](#mock-quick-start) · [C# mock](dotnet/README.md#mock-quick-start-ci-no-visa) |
| Talk to real USB/GPIB gear | [Hardware quick start](#hardware-quick-start) · [C# hardware](dotnet/README.md#hardware-quick-start-windows-or-linux--visa) |
| Build an app with device pickers | [docs/discovery.md](docs/discovery.md) |
| Use async VISA I/O (opt-in) | [docs/async.md](docs/async.md) · [C# async](docs/visa-async-csharp.md) |

## Mock quick start

No VISA installation required. Rust:

```toml
[dependencies]
instrument-components = { version = "0.1", default-features = false }
```

```rust
use instrument::prelude::*;

let fixture = ScriptedFixture::builder()
    .idn("Acme Corp", "SMU2602", "SN001", "1.0")
    .kinds([InstrumentKind::Dmm, InstrumentKind::DcPowerSupply])
    .on_query(":MEAS:VOLT:DC?", "3.300")
    .build();

let catalog = DeviceCatalog::from_fixture("mock://smu-1", fixture)?;
let dmm = catalog.open_dmm("mock://smu-1")?;
let volts = dmm.measure_voltage_dc(None)?;
println!("{volts} V");
```

C# (same fixture, no VISA):

```bash
dotnet run --project dotnet/examples/MockFixtureCi
```

## Hardware quick start

1. Install [NI-VISA](https://www.ni.com/en-us/support/downloads/drivers/download.ni-visa.html) or [Keysight IO Libraries](https://www.keysight.com/us/en/lib/software-detail/computer-software/io-libraries-suite-downloads-2175637.html).
2. Add the dependency (VISA enabled by default):

```toml
[dependencies]
instrument-components = "0.1"
```

3. Discover and measure:

```rust
use instrument::prelude::*;

let catalog = Discovery::visa()?.scan()?;
catalog.print_summary();

let dmm = catalog.open_dmm(&catalog.devices_by_kind(InstrumentKind::Dmm)[0].address.raw)?;
let volts = dmm.measure_voltage_dc(None)?;
println!("{volts} V");
```

TCPIP/LXI devices usually need a manual address:

```rust
let catalog = Discovery::visa()?
    .manual_address("TCPIP0::192.168.0.42::INSTR")
    .scan()?;
```

See [docs/getting-started.md](docs/getting-started.md) for the full walkthrough.

## Feature flags

| Feature | Default | Description |
|---|---|---|
| `visa` | yes | VISA backend via `instrument-visa` |
| `record` | no | Record real I/O into mock scripts |
| `tokio` | no | Full async API: `AsyncScpiSession`, `AsyncDiscovery`, typed classes |
| `cross-compile` | no | Cross-compile enum repr for `visa-rs` |
| `default-features = false` | — | Mock/CI only; no native VISA link |

```toml
# CI / mock only
instrument-components = { version = "0.1", default-features = false }

# Async VISA (opt-in; use git/`latest` until 0.2.0 is published)
instrument-components = { version = "0.1", features = ["visa", "tokio"] }
tokio = { version = "1", features = ["rt-multi-thread", "macros"] }
```

## Async quick start

Same mock path as above, with `.await` and `AsyncDeviceCatalog`:

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
        .idn("Acme", "DMM1", "SN1", "1.0")
        .kinds([InstrumentKind::Dmm])
        .on_query(":MEAS:VOLT:DC?", "1.0")
        .build();
    let catalog = AsyncDeviceCatalog::from_fixture("mock://dmm", fixture).await?;
    let mut dmm = catalog.open_dmm("mock://dmm").await?;
    println!("{} V", dmm.measure_voltage_dc(None).await?);
    Ok(())
}
```

crates.io `0.1.0` predates the async API and later instrument classes. Until `0.2.0` is published, take those features from git/`latest`.

## Documentation

Published site (MkDocs): [https://josh-hemphill.github.io/instrument-components/](https://josh-hemphill.github.io/instrument-components/)

- [Getting started](docs/getting-started.md) — Rust step-by-step setup (published dual-language walkthrough: [docs-site](https://josh-hemphill.github.io/instrument-components/))
- [Architecture](docs/architecture.md) — crate layout and mental model
- [Discovery](docs/discovery.md) — probe policy, device assignment
- [Diagnostics](docs/diagnostics.md) — health polling and observers
- [Async I/O](docs/async.md) — tokio feature and async API
- [API overview](docs/api-overview.md) — main types reference
- [Examples](docs/examples.md) — runnable examples index
- [Roadmap](docs/roadmap.md) — dual-native reliability plan
- [Dual-native plan](docs/dual-native-plan.md) — CI, session honesty, shared contracts
- [Parity checklist](docs/parity-checklist.md) — Rust ↔ C# scenario matrix
- [.NET getting started](docs/dotnet-getting-started.md) — C# quick start (first-class, not a port afterthought)
- [OpenTAP / HardwareTest consumer](docs/opentap-consumer.md) — contributor planning for session injection. User guide: [C# OpenTAP pack](https://josh-hemphill.github.io/instrument-components/csharp/opentap/).
- [Contributing](CONTRIBUTING.md)

API reference on [docs.rs](https://docs.rs/instrument-components) (after first publish). Local docs site: `docs-site/` (`pip install -r requirements.txt && mkdocs serve`).

## .NET / NuGet

Native C# packages live under [`dotnet/`](dotnet/) — first-class, same contracts as Rust. Install from NuGet:

```bash
dotnet add package InstrumentComponents          # mock/CI — no VISA
dotnet add package InstrumentComponents.Visa     # Windows + IviFoundation.Visa
```

See [dotnet/README.md](dotnet/README.md) for mock and hardware quick starts.

## Workspace crates

| Crate | crates.io | Role |
|---|---|---|
| `instrument-components` | facade | Discovery, typed classes, re-exports |
| `instrument-core` | backend | Transport, SCPI, mocks — no VISA |
| `instrument-visa` | backend | NI-VISA / Keysight VISA via visa-rs |

## Testing

```bash
# Mock path (no VISA) — runs in CI
cargo test --workspace --no-default-features

# Async mock path
cargo test -p instrument-core --features async
cargo test -p instrument-components --features tokio --no-default-features

# Hardware (local or self-hosted, requires VISA + a DMM)
cargo test -p instrument-components --features visa -- --ignored
# One-resource DMM smoke (used by .github/workflows/hardware.yml)
INSTRUMENT_RESOURCE='USB0::0x0957::0x0607::SN::INSTR' \
  cargo test -p instrument-components --features visa --test hardware dmm_measure_voltage_dc_smoke -- --ignored --nocapture --exact
```

## License

Licensed under either of [Apache License, Version 2.0](LICENSE-APACHE) or [MIT License](LICENSE-MIT) at your option.

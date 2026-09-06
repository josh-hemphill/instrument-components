# instrument-components

High-level control for VISA / SCPI instruments in **Rust** and **C#**: auto-discovery, IVI-inspired typed classes, and mock fixtures for CI — without waiting on a vendor IVI class driver.

## What you get

- Auto-discovery of USB, GPIB, and serial VISA resources
- Typed instrument classes with SI units (V, A, Hz, s): DMM, DC power supply, function generator, oscilloscope, switch, counter, power meter, spectrum analyzer
- Mock transport for hardware-free CI
- Stable `DeviceId` for instrument replacement workflows
- Opt-in async APIs (true async in Rust via visa-rs; sync bridge in C# today)

## Choose your path

| I want to… | Start here |
|---|---|
| First measure (mock, no VISA) | [Getting started](getting-started.md) |
| Discover real instruments | [Discovery](discovery.md) |
| Use a typed class | [Instrument classes](classes/dmm.md) |
| Async I/O | [Rust async](rust/async.md) · [C# async](csharp/async.md) |
| Author OpenTAP plans | [C# OpenTAP pack](csharp/opentap.md) |
| See what's implemented | [Capability matrix](capability-matrix.md) |

## Install

=== "Rust"

    ```toml
    # Mock / CI
    instrument-components = { version = "0.1", default-features = false }

    # Hardware (VISA default)
    instrument-components = "0.1"
    ```

    Import: `use instrument::prelude::*;`

=== "C#"

    Package publishing is deferred; reference projects from this repo for now:

    ```bash
    dotnet add reference path/to/InstrumentComponents.csproj
    # Hardware also needs:
    dotnet add reference path/to/InstrumentComponents.Visa.csproj
    ```

## Mock → volts

=== "Rust"

    --8<-- "snippets/rust/mock-dmm.md"

=== "C#"

    --8<-- "snippets/csharp/mock-dmm.md"

## Source & API reference

- GitHub: [josh-hemphill/instrument-components](https://github.com/josh-hemphill/instrument-components)
- Rust API docs: [docs.rs/instrument-components](https://docs.rs/instrument-components) (after publish)
- Agent-oriented notes (roadmap, parity) live in the repo `docs/` folder

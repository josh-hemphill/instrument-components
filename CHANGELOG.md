# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Contributor contract for HardwareTest / OpenTAP consumption (`docs/opentap-consumer.md`): session injection, OpenTAP pack with all eight instrument types, and explicit non-goals
- C# `IScpiIo` message session plus `InstrumentSession.FromIo` so a host can inject Write/Query without wrapping VISA as `ITransport`
- `QueryIdn` / `OutputOff` / `Reset` on all eight typed classes (`IInstrumentIdentity`, `IInstrumentShutdown`)
- OpenTAP pack `InstrumentComponents.OpenTap` with all eight instrument types, injected sessions, and pack-safe VisaAddress discovery
- Thin OpenTAP function steps that publish Phase I `Sample` / `Scalar` (and Identity) tables for every class
- Golden OpenTAP public-contract catalogs and real `.TapPlan` round-trips for all eight instruments and 17 function steps (no broker / VISA)
- Shared OpenTAP `InstrumentBoundStep<T>` with Editor validation, units, and formatted step names for all 17 function steps
- OpenTAP operation catalog (`spec/opentap-operations.json`) covering the shipped 17 steps as a stable TapPlan contract
- Shared OpenTAP sample-loop and optional-limit primitives so scalar steps apply inclusive limits consistently
- Generated OpenTAP DMM steps (AC/current/resistance, configure, INIT/FETC/READ/*TRG) from `spec/opentap-operations.json`
- Generated OpenTAP Base-capability steps for the remaining classes (PSU set/read/OVP, FGen duty/burst, scope timebase/trigger/run, switch `IsClosed`, counter gate/totalize, power-meter configure/initiate/fetch, spec-an center/span/RBW/trace/sweep)
- OpenTAP generated-step TapPlan setting round-trips and mixed-plan Phase I value coverage across all eight classes

### Changed

- Roadmap and OpenTAP consumer docs: Stream F is merged (#11); G/H are independent. The OpenTAP contract stays contributor planning in `docs/` (not in MkDocs yet).

### Fixed

- Queries retry the write+read pair after a timed-out read, flushing stale data first
- Framed reads do not reconnect; query retry flushes first, then reconnects once
- C# zero-byte reads time out without a second failure record or reconnect
- `*OPC?` / `SYST:ERR?` probes cache support only for real OPC (`1`/`+1`) and SCPI error-queue (`code,message`) replies
- Framed reads treat a successful 0-byte `read` as timeout instead of spinning
- Rust async sessions restore I/O timeout if the query/flush future is dropped (cancel)
- `reconnect_on_failure` documents that VISA reconnect is unsupported; retry still runs
- Canonical GitHub and GitHub Pages URLs are `josh-hemphill/instrument-components` (not the misspelled `instrument-componenets-rs`)
- Docs and examples depend on `0.1` plus git/`latest` for unreleased async APIs, not a non-existent `0.2` crate
- Generated dialect and SCPI tables now match `cargo fmt` so CI's format check passes
- Discovery connect options now apply to sessions opened from the resulting catalog
- Reconnect diagnostics are recorded only when reconnect succeeds
- C# SCPI number parse/format is culture-invariant
- C# `ProbeSystErr` treats transport failure as unsupported
- C# `ResetOnConnect` issues `*CLS`/`*RST` like Rust
- C# VISA `SharedLock` fails closed (`Ivi.Visa` has no shared-lock access mode)
- C# `VisaTransport` disposes the underlying VISA session
- C# `VisaSessionOpener.Open` preserves `InstrumentUnsupportedException` for `SharedLock` (map access mode before the VISA open try)
- C# `SyncAsAsyncTransport` disposes the inner sync transport so async sessions close native VISA handles
- SCPI sessions restore I/O timeout after short probe/flush/query timeouts (ResetOnConnect no longer leaves a 500ms VISA timeout)
- C# async SCPI cleanup restores I/O timeout without the caller cancellation token, so a cancelled flush/query cannot leave a short VISA timeout
- C# I/O timeout restore is best-effort: it cannot fail `ResetOnConnect` session create or hide a successful probe/query

### Added

- Shared dual-native contracts under `spec/` (`scpi-vectors.json`, `classifier-cases.json`, `vendors/*.json`) loaded by Rust and C# tests
- Transcript fixtures now drive typed-class actions and assert measured values
- Golden DMM/PSU transcripts (`fixtures/dmm_dmm6500.json`, `fixtures/psu_n6705c.json`) plus Keithley DMM6500 and Keysight N6705C vendor dialects
- DMM6500 / N6705C dialect globs match live `*IDN?` shapes (`MODEL DMM6500`, Agilent N6705C)
- Self-hosted `Hardware smoke` workflow (`workflow_dispatch`) that measures DC voltage on one DMM via `INSTRUMENT_RESOURCE`
- Hardware smoke logs the resolved dialect id, asserts `keithley_dmm6500` for DMM6500 IDNs, and rejects overload-scale readings
- C# examples: `DmmMeasure`, `ManualTcpip`, `DiscoverAsync`
- Dual-native reliability plan (`docs/dual-native-plan.md`)
- Release workflow requires the .NET CI workflow before crates.io or NuGet publish
- Full async API behind `tokio` feature: `AsyncScpiSession`, `AsyncInstrumentSession`, `AsyncDiscovery`, `AsyncDeviceCatalog`, `AsyncDeviceRef`
- Async typed classes: `AsyncDmm`, `AsyncDcPowerSupply`, `AsyncFunctionGenerator`, `AsyncOscilloscope`, `AsyncSwitch`, `AsyncCounter`, `AsyncPowerMeter`, `AsyncSpectrumAnalyzer`
- Sync typed classes: `Oscilloscope`, `Switch`, `Counter`, `PowerMeter`, `SpectrumAnalyzer` (in addition to DMM / PSU / FGen)
- Class depth APIs: DMM AC/Ω/temp + INIT/FETC/READ/*TRG; PSU OVP/sense/channel count; scope trigger/measure; FGen duty/burst; switch path labels; counter gate/channel
- Dialect profiles + codegen (`tools/gen-dialects.ts`) for vendor SCPI variance including power meter and spectrum analyzer
- Curated model registry growth (≥40 models across classes) with PowerMeter / SpectrumAnalyzer hints
- Readonly capability probes for PowerMeter and SpectrumAnalyzer
- `VisaAsyncTransport` and `VisaAsyncSessionOpener` for async VISA I/O
- `MockTransport` async impl and `MockAsyncSessionOpener` for CI
- Shared SCPI protocol helpers (`scpi/protocol.rs`) used by sync and async sessions
- Shared TOML tables + codegen for SCPI commands and capability probes (Rust + C#)
- Examples: `mock_fixture_ci_async`, `discover_async`; C# examples under `dotnet/examples/`
- Async integration tests and CI tokio job coverage
- Docs: `docs/roadmap.md`, `docs/parity-checklist.md`, `docs/capability-matrix.md`, `docs/dotnet-getting-started.md`, `docs/visa-async-csharp.md`

### Changed

- Remaining catalog classes (DMM, PSU, FGen, oscilloscope, switch, counter) emit dialect-resolved SCPI, falling back to `scpi_commands` / `ScpiCommands` when the profile has no usable template
- Dialect codegen unescapes TOML `\"` so quoted SCPI (counter `channel_select`) matches the instrument string
- Dialect fallback ignores extra optional vars when required placeholders are filled; leftover `{ident}` on no-arg `try_command` falls back like C#
- Spectrum analyzer and power meter classes emit SCPI from the resolved dialect profile (Rigol DSA `:TRAC?` vs generic `:TRAC:DATA?`)
- Vendor dialect profiles are matched before catch-all `generic_*` rows; glob patterns like `*U20*` match a substring
- `tools/gen-dialects.ts` loads `spec/vendors/*.json` hardware dialects after CI TOML fixtures and before `generic_*`
- C# `InstrumentComponents.Visa` targets `net8.0` (Linux-capable builds via `IviFoundation.Visa`; runtime still needs a vendor VISA install)

## [0.1.0] - 2026-06-06

### Added

- `instrument-components` facade with `instrument` lib name for VISA instrument control
- `instrument-core`: `Transport`, `ScpiSession`, mock fixtures, layered classifier, diagnostics
- `instrument-visa`: NI-VISA / Keysight backend via visa-rs 0.7.0-alpha.1
- Auto-discovery for USB, GPIB, serial; manual TCPIP addresses
- Typed classes: `Dmm`, `DcPowerSupply`, `FunctionGenerator` (SI units)
- `ProbePolicy` for tiered capability probing during discovery
- `DeviceHealth` polling and `CommsObserver` push diagnostics
- Stable `DeviceId` for instrument replacement workflows
- Feature flags: `visa` (default), `tokio`, `cross-compile`, `record`
- Examples: discover, dmm_measure, mock_fixture_ci, assign_instruments
- Documentation in `docs/` and CONTRIBUTING guide

### Notes

- Depends on `visa-rs` 0.7.0-alpha.1 (pre-release)
- Async APIs shipped in Unreleased (enable `tokio`); originally noted as planned for a later minor

[0.1.0]: https://github.com/josh-hemphill/instrument-components/releases/tag/v0.1.0

# Rust ↔ C# parity checklist

Shared scenarios both implementations must cover. Update this table when adding tests or closing gaps.

| Scenario | Rust | C# | Notes |
|---|---|---|---|
| Mock DMM measure | `crates/instrument/tests/mock_catalog.rs` | `MockCatalogTests.FixtureDmmMeasure` | |
| Async mock DMM measure | `crates/instrument/tests/async_mock_catalog.rs` | `MockCatalogTests.AsyncFixtureDmmMeasure` | |
| Multi-session same device | `crates/instrument/tests/mock_catalog.rs` | `MockCatalogTests.MultiSessionSameDevice` | |
| Static discovery classify | `crates/instrument/tests/discovery_static.rs` | `DiscoveryStaticTests` | |
| Async static discovery | `crates/instrument/tests/async_discovery_static.rs` | `AsyncDiscoveryStaticTests` | |
| SCPI query retry after timeout | `crates/instrument-core/tests/reliability.rs` | `ReliabilityTests` | Write retry (`fail_writes`) |
| SCPI query read retry + flush | same | same | `fail_reads` drains stale, retry returns fresh |
| Async SCPI query retry | `crates/instrument-core/tests/async_reliability.rs` | `AsyncReliabilityTests` | Write + read retry |
| Honest `*OPC?` / `SYST:ERR?` probes | `reliability.rs` | `ReliabilityTests` | `-113` / `OK` are not supported |
| Zero-byte read fail-closed | same | same | No 1 ms spin loop |
| Async query drop restores I/O timeout | `async_reliability.rs` | C# `CancelledFlushStillRestoresIoTimeout` | Rust Drop guard; C# `finally` |
| Diagnostics observer + health | `crates/instrument-core/tests/diagnostics.rs` | `DiagnosticsTests` | |
| Async diagnostics | `crates/instrument-core/tests/async_diagnostics.rs` | `AsyncDiagnosticsTests` | |
| Transcript `smu2602` | `crates/instrument/tests/transcript_behavior.rs` | `TranscriptBehaviorTests` | `fixtures/smu2602.json` — asserts 3.3 V |
| Transcript scope | same | same | `fixtures/scope_ds1054z.json` — asserts samples + interval |
| Transcript switch | same | same | `fixtures/switch_34970a.json` — asserts closed |
| Transcript counter | same | same | `fixtures/counter_53230a.json` — asserts 1000 Hz |
| Transcript DMM6500 | same | same | `fixtures/dmm_dmm6500.json` — dialect `:SENS:FUNC`; ranged measure falls back |
| Transcript N6705C | same | same | `fixtures/psu_n6705c.json` — channel-list SCPI; `channel_count` is 4 |
| Shared SCPI vectors | `crates/instrument-core/tests/shared_contracts.rs` | `SharedContractTests` | `spec/scpi-vectors.json` |
| Shared classifier cases | same | same | `spec/classifier-cases.json` |
| New kinds (scope/switch/counter) | `crates/instrument/tests/new_instrument_kinds.rs` | `NewInstrumentKindTests` | |
| Class depth Dmm/Psu | `crates/instrument/tests/mock_catalog.rs` | `MockCatalogTests` | AC/Ω/temp, INIT/FETC, OVP/sense |
| Class depth Scope/Fgen | `new_instrument_kinds.rs` | `NewInstrumentKindTests` | Trigger/measure, burst/duty |
| Dialect fallback (DMM range, FGen freq, scope timebase) | `mock_catalog.rs`, `async_mock_catalog.rs`, `dialect_io` tests | `MockCatalogTests`, `DialectCommandTests` | Generic profile has no `{range}` / `read_frequency` / `read_timebase_scale` |
| Dialect-wins (non-generic remaining classes) | `mock_catalog.rs`, `async_mock_catalog.rs` | `MockCatalogTests` | CI fixtures `ci_dmm_dialect_wins` / `ci_psu_dialect_wins` (`TestDialect*`) |
| PowerMeter + SpectrumAnalyzer | `new_instrument_kinds.rs` | `NewInstrumentKindTests` | Open + configure/read / sweep |
| SCPI framing | `instrument-core` framing unit tests | `FramingTests` | |
| Address parse | core address tests | `AddressTests` | |
| Classifier / registry | core classifier + registry | `ClassifierTests`, registry hints in `NewInstrumentKindTests` | |
| Hardware discover smoke | `crates/instrument/tests/hardware.rs` (`#[ignore]`) | `HardwareTests.DiscoverRealDevices` (skipped) | Local VISA only |
| Hardware DMM smoke | same, `dmm_measure_voltage_dc_smoke` | `HardwareTests.DmmMeasureVoltageDcSmoke` | Self-hosted `Hardware smoke` workflow; logs dialect id; `|V|<1e6`; DMM6500 asserts `keithley_dmm6500` |
| Mock example runnable | `examples/mock_fixture_ci` | `dotnet/examples/MockFixtureCi` | |
| Async mock example | `examples/mock_fixture_ci_async` | `dotnet/examples/MockFixtureCiAsync` | |
| Discover example | `examples/discover` | `dotnet/examples/Discover` | Needs VISA |
| DMM measure example | `examples/dmm_measure` | `dotnet/examples/DmmMeasure` | Needs VISA |
| Manual TCPIP example | `examples/manual_tcpip` | `dotnet/examples/ManualTcpip` | Needs VISA |
| Async discover example | `examples/discover_async` | `dotnet/examples/DiscoverAsync` | Needs VISA |

## Known intentional differences

| Topic | Rust | C# |
|---|---|---|
| Async catalog API | Separate `AsyncDeviceCatalog` (`tokio`) | Same `DeviceCatalog` with `*Async` methods |
| Cancellation | Futures only | `CancellationToken` throughout |
| VISA async I/O | `visa-rs` tokio adapter | Sync bridge (`SyncAsAsyncTransport`) — see [visa-async-csharp.md](visa-async-csharp.md) |
| VISA package TFM | Cross-platform where `visa-rs` links | `net8.0` (Windows or Linux + vendor VISA) |
| Recording transport | `record` feature | Always available |

## Regenerating shared tables

```bash
node tools/gen-shared-tables.ts
node dotnet/tools/gen-registry.ts
```

CI fails if generated outputs drift from committed files.

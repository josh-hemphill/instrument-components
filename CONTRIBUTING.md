# Contributing

Thank you for contributing to instrument-components.

Dual-native work (Rust + C#) follows [docs/dual-native-plan.md](docs/dual-native-plan.md). Shared TOML is the contract; do not land behavior in only one language.

## Development setup

```bash
git clone https://github.com/josh-hemphill/instrument-components.git
cd instrument-components
cargo test --workspace --no-default-features
```

## Project layout

| Crate | Path | Role |
|---|---|---|
| `instrument-components` | `crates/instrument` | Public facade (lib name: `instrument`) |
| `instrument-core` | `crates/instrument-core` | Transport, SCPI, mocks — no VISA |
| `instrument-visa` | `crates/instrument-visa` | visa-rs backend |

## Test matrix

| Command | When |
|---|---|
| `cargo test --workspace --no-default-features` | CI gate — mock path, no VISA |
| `cargo test -p instrument-core --features async` | Async SCPI + mock transport |
| `cargo test -p instrument-components --features tokio --no-default-features` | Async facade + catalog |
| `cargo test -p instrument-components --features visa -- --ignored` | Local hardware tests (discover + IDN + DMM smoke) |
| `INSTRUMENT_RESOURCE=... cargo test -p instrument-components --features visa --test hardware dmm_measure_voltage_dc_smoke -- --ignored --nocapture --exact` | Self-hosted DMM smoke (Rust) |
| `INSTRUMENT_RESOURCE=... dotnet test dotnet/tests/InstrumentComponents.Visa.Tests --filter "FullyQualifiedName~DmmMeasureVoltageDcSmoke"` | Self-hosted DMM smoke (C#) |
| `INSTRUMENT_RESOURCE=... dotnet test dotnet/tests/InstrumentComponents.OpenTap.Visa.Tests --filter "FullyQualifiedName~OpenTapVisaDmmMeasureVoltageDcSmoke"` | Self-hosted OpenTAP VISA companion smoke |
| `dotnet test dotnet/tests/InstrumentComponents.OpenTap.Visa.Tests --filter "Category!=Hardware"` | OpenTAP VISA companion (no instruments) |
| `cargo check -p instrument-components --features visa,tokio` | Async + VISA compiles |
| `cargo clippy -p instrument-core --features async -- -D warnings` | Async clippy |
| `cargo check -p instrument-visa --features cross-compile --target x86_64-pc-windows-gnu` | Cross-compile repr check |
| `dotnet test dotnet/tests/InstrumentComponents.Tests` | C# mock path |
| `dotnet test dotnet/tests/InstrumentComponents.Visa.Tests --filter "Category!=Hardware"` | C# VISA package (no instruments) |

CI does **not** link against VISA (no NI-VISA on GitHub-hosted runners). Hardware verification is local or the `Hardware smoke` workflow (`workflow_dispatch`, runner labels `self-hosted` + `visa`). The release workflow requires **both** the Rust `CI` and `.NET` workflows. Do not treat a green GitHub-hosted PR as hardware evidence.

## Code style

```bash
cargo fmt --all
cargo clippy --workspace --no-default-features -- -D warnings
```

## Adding instrument models

Edit `crates/instrument-core/data/model_registry.toml`:

```toml
[[entry]]
manufacturer = "Keysight Technologies"
model = "34401A"
kinds = ["Dmm"]

[[usb_entry]]
vid = "0957"
pid = "0607"
manufacturer = "Keysight Technologies"
model = "34401A"
kinds = ["Dmm"]
```

Then regenerate the C# embed:

```bash
deno run --allow-read --allow-write dotnet/tools/gen-registry.ts
```

Registry entries are **hints only** — capability probes and `*IDN?` can override.

## Shared SCPI / probe tables

Shared TOML is the source of truth for generic commands and CI dialect fixtures. Hardware vendor dialects for DMM/PSU live under `spec/vendors/`. Never hand-edit generated files (`scpi_commands.rs`, `ScpiCommands.cs`, `dialect.rs`, `DialectRegistry.cs`, `probes.rs`, `CapabilityProbes.cs`, `model_registry.json`).

Edit TOML under `crates/instrument-core/data/`, then:

```bash
deno run --allow-read --allow-write --allow-run=rustfmt tools/gen-shared-tables.ts
deno run --allow-read --allow-write --allow-run=rustfmt tools/gen-dialects.ts
deno run --allow-read --allow-write dotnet/tools/gen-registry.ts
```

This regenerates Rust `probes.rs` / `scpi_commands.rs` / `dialect.rs` and C# `CapabilityProbes.cs` / `ScpiCommands.cs` / `DialectRegistry.cs`. Codegen runs `rustfmt` on the Rust outputs and fails if a `generic_*` dialect profile drifts from `scpi_commands.toml` (`spec/generic-scpi-map.json`). CI fails on drift.

Typed classes that have a dialect profile (spectrum analyzer, power meter) emit the resolved dialect command, not a hardcoded generic string.

## Golden vectors

New class methods that emit SCPI need a row in `spec/scpi-vectors.json` (exact command string) or a shared transcript under `fixtures/` that drives the typed class and asserts **values**. Classifier changes need a row in `spec/classifier-cases.json`. Both languages load the same files — see `spec/README.md`.

## Pull requests

- Include tests for behavior changes (mock path preferred).
- Keep diffs focused — no drive-by refactors.
- Update `CHANGELOG.md` under `[Unreleased]` for user-visible changes.

## Publishing (maintainers)

Publish order on crates.io:

1. `instrument-core`
2. `instrument-visa`
3. `instrument-components`

Set `CARGO_REGISTRY_TOKEN` and `NUGET_API_KEY` in GitHub repository secrets. Tag `v*` triggers the release workflow, which waits on Rust **and** .NET CI.

Pre-first-publish: `cargo publish -p instrument-core --dry-run --allow-dirty` validates packaging. Dependent crates require `instrument-core` on crates.io before their dry-run verify step succeeds.

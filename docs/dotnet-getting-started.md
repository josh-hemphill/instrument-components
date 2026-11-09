# .NET getting started

C# implementation of instrument-components — discover and drive SCPI instruments **without waiting for a vendor IVI.NET class driver**. Dual-native with the Rust crates; shared TOML is the contract.

Unlike IVI Shared Components (contracts that vendors must implement per model), this library talks SCPI over VISA (or a mock), classifies devices from `*IDN?` / probes / a shared model registry, and exposes SI-unit typed classes (DMM, PSU, FGen, oscilloscope, switch, counter, power meter, spectrum analyzer).

Package publishing is deferred; use project references from this repo for now.

## Mock → volts (no VISA, ~1 minute)

```bash
cd dotnet
dotnet run --project examples/MockFixtureCi
```

Or async:

```bash
dotnet run --project examples/MockFixtureCiAsync
```

## Hardware (Windows or Linux + vendor VISA)

1. Install NI-VISA, Keysight IO Libraries, or another VISA that provides the VISA.NET shared components expected by [IviFoundation.Visa](https://www.nuget.org/packages/IviFoundation.Visa).
2. Run discovery:

```bash
dotnet run --project examples/Discover
```

3. Assign roles by stable `DeviceId`:

```bash
dotnet run --project examples/AssignInstruments
```

Other hardware examples: `examples/DmmMeasure`, `examples/ManualTcpip`, `examples/DiscoverAsync`.

## Library usage

```csharp
using InstrumentComponents.Catalog;
using InstrumentComponents.Kind;
using InstrumentComponents.Mock;

var fixture = ScriptedFixture.Builder()
    .Idn("Acme Corp", "SMU2602", "SN001", "1.0")
    .Kinds(InstrumentKind.Dmm, InstrumentKind.DcPowerSupply)
    .OnQuery(":MEAS:VOLT:DC?", "3.300")
    .Build();

var catalog = DeviceCatalog.FromFixture("mock://smu-1", fixture);
var dmm = catalog.OpenDmm("mock://smu-1");
Console.WriteLine(dmm.MeasureVoltageDc());
```

Hardware entry point:

```csharp
using InstrumentComponents.Visa;

var catalog = VisaDiscovery.Create().Scan();
catalog.PrintSummary();
```

## Async notes

Core async APIs take `CancellationToken` and return `Task` / `ValueTask`.

**VISA async** (`VisaAsyncTransport`) is currently a **sync bridge** (thread-pool offload via `SyncAsAsyncTransport`), not vendor APM. See [visa-async-csharp.md](visa-async-csharp.md).

## Shared tables

SCPI command strings and capability-probe lists are generated from TOML under `crates/instrument-core/data/`:

```bash
node tools/gen-shared-tables.ts
node dotnet/tools/gen-registry.ts
```

## More

- [dotnet/README.md](../dotnet/README.md) — package layout
- [parity-checklist.md](parity-checklist.md) — Rust ↔ C# scenarios
- [roadmap.md](roadmap.md) — phased work

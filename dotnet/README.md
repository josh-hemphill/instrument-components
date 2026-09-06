# InstrumentComponents (.NET)

C# packages for [instrument-components](../README.md) over vendor-neutral [IviFoundation.Visa](https://www.nuget.org/packages/IviFoundation.Visa). Same discovery, SCPI, and typed-class contracts as the Rust crates.

See [docs/dotnet-getting-started.md](../docs/dotnet-getting-started.md) for the IVI-alternative pitch and full walkthrough.

## Packages

| Package | Role |
|---|---|
| `InstrumentComponents` | Discovery, typed classes (DMM, PSU, FGen, oscilloscope, switch, counter, power meter, spectrum analyzer), mocks — no VISA runtime (`net8.0`) |
| `InstrumentComponents.OpenTap` | OpenTAP plugin: eight instrument types + typed function steps. Host injects `IScpiIo`; no VISA. |
| `InstrumentComponents.OpenTap.Visa` | Optional companion: registers a VISA `IOpenTapScpiIoProvider` for TUI-without-host. Not in the TapPackage. |
| `InstrumentComponents.Visa` | VISA transport via IviFoundation.Visa (`net8.0`; Windows **or** Linux with a vendor VISA install) |

## Mock quick start (CI, no VISA)

```bash
dotnet run --project examples/MockFixtureCi
```

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
var volts = dmm.MeasureVoltageDc();
Console.WriteLine($"{volts} V");
```

## OpenTAP mock plan (no VISA)

```bash
dotnet run --project examples/OpenTapMockPlan
```

See the [C# OpenTAP pack](../docs-site/docs/csharp/opentap.md) guide.

## Hardware quick start (Windows or Linux + VISA)

1. Install [NI-VISA](https://www.ni.com/en-us/support/downloads/drivers/download.ni-visa.html), [Keysight IO Libraries](https://www.keysight.com/us/en/lib/software-detail/computer-software/io-libraries-suite-downloads-2175637.html), or another stack that provides VISA.NET compatible with `IviFoundation.Visa` 8.x.
2. Reference both projects (NuGet publish deferred):

```bash
dotnet run --project examples/Discover
```

```csharp
using InstrumentComponents.Visa;

var catalog = VisaDiscovery.Create().Scan();
catalog.PrintSummary();

var dmm = catalog.OpenDmm(catalog.DevicesByKind(InstrumentComponents.Kind.InstrumentKind.Dmm)[0].Address.Raw);
var volts = dmm.MeasureVoltageDc();
```

## Async quick start

```csharp
var catalog = DeviceCatalog.FromFixture("mock://dmm", fixture);
var dmm = await catalog.Device("mock://dmm").OpenDmmAsync();
var volts = await dmm.MeasureVoltageDcAsync();
```

**Note:** `VisaAsyncTransport` is a sync bridge (thread-pool offload), not vendor APM. Details: [docs/visa-async-csharp.md](../docs/visa-async-csharp.md).

## Examples

| Project | Requires VISA |
|---|---|
| `examples/MockFixtureCi` | No |
| `examples/MockFixtureCiAsync` | No |
| `examples/Discover` | Yes |
| `examples/DiscoverAsync` | Yes |
| `examples/DmmMeasure` | Yes |
| `examples/ManualTcpip` | Yes |
| `examples/AssignInstruments` | Yes |

## Testing

```bash
cd dotnet
dotnet test tests/InstrumentComponents.Tests
dotnet test tests/InstrumentComponents.Visa.Tests --filter "Category!=Hardware"
dotnet test tests/InstrumentComponents.OpenTap.Tests
dotnet test tests/InstrumentComponents.OpenTap.Visa.Tests --filter "Category!=Hardware"
```

## Registry / shared-table drift check

```bash
deno run --allow-read --allow-write dotnet/tools/gen-registry.ts
deno run --allow-read --allow-write tools/gen-shared-tables.ts
git diff --exit-code
```

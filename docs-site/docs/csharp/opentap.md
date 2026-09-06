# OpenTAP pack (C#)

`InstrumentComponents.OpenTap` is an OpenTAP plugin with **all eight** SCPI instrument types and explicit typed `TestStep` classes. The host injects an already-open message session, or registers an `IOpenTapScpiIoProvider`. The pack never opens a vendor VISA resource manager.

## Install

NuGet publishing is deferred. From this repo:

```bash
dotnet add reference path/to/InstrumentComponents.OpenTap.csproj
```

The pack references `OpenTAP` 9.32 and `InstrumentComponents` only — not `InstrumentComponents.Visa`.

## Session injection

Call `AttachSession` (or the `IScpiIo` constructor) before `Instrument.Open`:

```csharp
using InstrumentComponents.OpenTap;
using InstrumentComponents.Scpi;

var io = new DelegateScpiIo(write, query); // wrap the host's Write/Query
var dmm = new DmmInstrument { VisaAddress = "TCPIP0::192.0.2.10::inst0::INSTR" };
dmm.AttachSession(io);
dmm.Open();
```

`VisaAddress` is pack-safe discovery metadata. Without an attached session **and** without a provider, `Open()` throws and does not call IVI.

Optional process-wide provider (for TUI-without-host; HardwareTest should keep injecting):

```csharp
OpenTapScpiIo.Provider = new MyHostScpiIoProvider(); // IOpenTapScpiIoProvider
var dmm = new DmmInstrument { VisaAddress = "TCPIP0::192.0.2.10::inst0::INSTR" };
dmm.Open(); // provider.Open(VisaAddress, timeout) then IDN
```

`AttachSession` / the `IScpiIo` constructor still win over the provider. Close disposes provider-created I/O and leaves host-injected I/O alone.

## Plan-author steps

Steps are **explicit types** (stable TapPlan names), not a generic "call method" broker.

| Kind | Examples |
|---|---|
| Utility | `IdentityQueryStep`, `SafeShutdownStep` |
| Composite | `PsuConfigureOutputStep`, `FgenConfigureOutputStep`, `ScopeCaptureTraceStep` |
| Generated invoke | `DmmMeasureVoltageAcStep`, `PsuSetVoltageStep`, `ScopeRunStep`, … |

Generated wrappers come from `spec/opentap-operations.json`. Do not rename `stepTypeName` values — they are TapPlan contract.

Scalar measure steps inherit optional inclusive `LimitLow` / `LimitHigh` (unset = no fail).

## Phase I results

| Table | Columns |
|---|---|
| `Sample` | Channel, Index, Value |
| `Scalar` | Name, Value, Unit, LimitLow, LimitHigh |
| `Identity` | Idn, DutSerial |

## Mock plan (no VISA)

--8<-- "snippets/csharp/opentap-mock-plan.md"

```bash
dotnet run --project dotnet/examples/OpenTapMockPlan
```

## TapPackage

The plugin library stays `net8.0`. Creating a `.TapPackage` needs a **net9** runtime for the OpenTAP `tap` CLI:

```bash
dotnet build dotnet/src/InstrumentComponents.OpenTap/InstrumentComponents.OpenTap.csproj -c Release -p:CreateOpenTapPackage=true
```

That emits `InstrumentComponents.OpenTap.0.1.0.TapPackage` (plugin DLL + `InstrumentComponents.dll`, no VISA). Install with `tap package install`.

## HardwareTest

The bench shell owns broker, gate, and operator UI. This pack is the plugin that shell consumes. Contributor layering notes: [`docs/opentap-consumer.md`](https://github.com/josh-hemphill/instrument-components/blob/latest/docs/opentap-consumer.md) in the repo.

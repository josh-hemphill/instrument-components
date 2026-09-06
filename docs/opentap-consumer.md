# OpenTAP / HardwareTest consumer contract

Contributor planning for how this library supports
[dotnet-avalonia-hardwaretest-template](https://github.com/josh-hemphill/dotnet-avalonia-hardwaretest-template)
without becoming a second operator shell. C# only; Rust stays dual-native for
session, dialects, and mocks. Stream F is already merged
([#11](https://github.com/josh-hemphill/instrument-components/pull/11)). G and H
are independent of this track.

This file is contributor planning in `docs/`. The user-facing pack guide is
[`docs-site/docs/csharp/opentap.md`](../docs-site/docs/csharp/opentap.md).

**Status:** pack ships in `dotnet/src/InstrumentComponents.OpenTap` with injected
`IScpiIo`, eight instrument types, handwritten composites, and generated invoke
steps from `spec/opentap-operations.json`.

## One-line role

This library owns **SCPI session + typed capabilities + protocol mocks**, and
ships **all eight instrument classes** as an OpenTAP pack so TUI authors can
write plans without growing HardwareTest `Plugins.Basic`. HardwareTest owns
**broker, gate, Avalonia, mixins, operator dialogs, program catalog**. The pack
is a *plugin the shell consumes*, not a bench application.

## Layering (who owns what)

```text
HardwareTest Avalonia shell
  Instruments page, Run board, Presentation widgets, operator prompts
        │
HardwareTest.Core
  IVisaBroker / VisaSessionGate / IBenchOperationCoordinator
  (process-wide mock↔real, Safety Stop preemption)
        │ injects already-open Write/Query session (AttachSession / ctor)
        ▼
OpenTAP pack (THIS repo) — all 8 instrument types + thin function steps
  DmmInstrument, DcPowerSupplyInstrument, … : ScpiInstrument
        │
InstrumentComponents (this repo, no OpenTAP)
  IScpiIo / InstrumentSession / Dmm / DcPowerSupply / Oscilloscope / …
  ScriptedFixture protocol mocks
        │
InstrumentComponents.Visa  — NOT referenced by the OpenTAP pack
  Ivi.Visa open / enumerate  (HardwareTest.Core already does this for the bench)
```

HardwareTest’s `VisaDmmInstrument` today opens through `IVisaBroker` and talks
raw SCPI (`*IDN?`, `CONF:VOLT:DC`, `READ?`). The intended end state is: product
plans reference **this pack**; HardwareTest injects a broker-backed session so
the pack never calls `GlobalResourceManager.Open`.

## Determinations (expanded)

### 1. Session: own Open / Write / Query (timeout, dispose)

**What HardwareTest needs.** A session object this library fully owns:

| Member | Why |
| --- | --- |
| Open (or construct-from-injected) | Wrapper `Instrument.Open` must not call IVI |
| `Write(command)` | SCPI set / configure / output-off / `*RST` |
| `Query(command)` | `*IDN?`, measure, output state |
| Timeout | Maps onto HardwareTest `IoTimeoutMilliseconds` / `ConnectOptions` |
| Dispose | `Instrument.Close` / plan teardown |

**What we must not add here.** `IVisaBroker`, `VisaSessionGate`,
`IBenchOperationCoordinator`, Avalonia types, or `HardwareTest.Core`. Those
serialize Instruments-page I/O with plan I/O and Safety Stop. HardwareTest
keeps them. We accept an **already-opened** message session.

**Injection shape.** HardwareTest `IVisaSession` is **string** Write/Query (IVI
`FormattedIO`). Our `ITransport` is a **byte stream**. Wrapping `IVisaSession`
as `ITransport` would double-frame (their FormattedIO already appends `\n`;
`ScpiSession` normalizes terminators again) and cannot implement `Read` because
`IVisaSession` has no read-only API.

Shipped C# seam:

```csharp
public interface IScpiIo : IDisposable
{
    TimeSpan IoTimeout { get; set; }
    void Write(string command);
    string Query(string command);
}
```

- Native path: `ScpiSession` implements `IScpiIo` (current framing, retry, probes).
- HardwareTest path: adapter `IVisaSession` → `IScpiIo` (pass-through strings,
  timeout, sync-over-async at the wrapper). Typed classes keep using
  `InstrumentSession`.
- `InstrumentSession` constructible from `IScpiIo` **or** `ITransport`.
- OpenTAP wrapper: default ctor for XML load; `ctor(IScpiIo)` for tests;
  `AttachSession(IScpiIo)` for Host after `TestPlan.Load`. `Open()` uses the
  attached session; it does not open VISA.

No process-global broker clone inside this library. Tests and Host pass the
session in. `TestPlan.Load` / TUI round-trip must succeed **without** a broker:
default ctor + writable `VisaAddress` + stable type names. Live I/O happens
when the host attaches a session and OpenTAP calls `Instrument.Open` during
execute.

TUI/Editor execute without HardwareTest: `Open()` throws a clear
“no SCPI session attached” error unless a host registered
`OpenTapScpiIo.Provider`. v1 does **not** open IVI from the main pack.
The optional `InstrumentComponents.OpenTap.Visa` companion (not loaded by
HardwareTest, not in the TapPackage) can register that provider for
standalone TUI Find/Open.

### 2. OpenTAP pack: ship every instrument class

The pack is a planned C# deliverable (not required for Rust, NuGet core, or
dual-native G/H; F is already merged). **v1 ships all eight `InstrumentKind`
types**, not a DMM-only preview. That is the easiest consume path: TUI authors
pick DMM / PSU / FGen / scope / switch / counter / power meter / spectrum
analyzer from one package, bind steps, save a TapPlan, drop it on the bench.

| Rule | Detail |
| --- | --- |
| One `Instrument` **resource** per physical device | An SMU that is DMM+PSU is **one** plan resource, not two slots |
| One OpenTAP **type** per `InstrumentKind` | Eight concrete types so TUI “Add instrument” lists every class we already support |
| Writable `VisaAddress` | HardwareTest `InstrumentResourceAccess` prefers this name, then `ResourceName`, then `Address` |
| `IDeviceDiscovery` for `VisaAddress` | So **Discover OpenTAP** can list addresses. Must not call `Ivi.Visa` from the pack |
| Nested extra capabilities | Methods or `[EmbedProperties]` on that one resource — not a second `Instrument` |
| Stable type names + package version | TapPlan XML stores `type` + package. Rename/break version → TUI and `TestPlan.Load` fail even with no broker |

**Why both “one resource per device” and “eight types”.** Types are the TUI
catalog (easiest pick). Resources are plan instances. A Keysight E36313A is
one `DcPowerSupplyInstrument`. A Keithley 2602B used as DMM+PSU is still
**one** resource: pick the primary type (usually PSU) and use nested DMM
methods / `[EmbedProperties]` for the meter view. Do not add a second
`DmmInstrument` with the same `VisaAddress` as the authoring model.

Host **may** de-dupe injected sessions by `VisaAddress` if a plan still has two
typed instruments on one address (shared `IScpiIo`, `Open()` is idempotent
enough to tolerate). That is a safety net, not the documented authoring path.

**Discovery without IVI in the pack.** HardwareTest already enumerates via Core
`VisaResourceDiscovery` **and** `PluginManager.GetPlugins<IDeviceDiscovery>`.
Pack `IDeviceDiscovery` uses a host-registered `ResourceEnumerator` on OpenTAP
`SessionLocal` (OpenTAP session scope, not a second broker we invent). If none
is registered, Detect returns empty — Instruments **VISA** column still works.
Do not reference `InstrumentComponents.Visa` from the pack.

### 3. Capability surfaces (every class)

Identity and output-off/reset must be callable on **every** pack instrument so
HardwareTest `IdentityCheckStep` / `SafeShutdownStep` can bind to the shared
base without `HardwareDmm`. Product plans should prefer **this pack’s** identity
and shutdown steps so they do not depend on `Plugins.Basic` at all.

| Surface | Who | Behavior |
| --- | --- | --- |
| Identity | `ScpiInstrument.QueryIdn()` → `InstrumentSession.Idn()` | `*IDN?` on every type |
| Reset | `ScpiInstrument.Reset()` | `*RST` + wait-complete |
| OutputOff / safe idle | per class, then `Reset` from shutdown step | See table below |
| Measurement / source | existing typed classes | Pack instruments expose the same methods (SI units) |

| Class | `OutputOff()` | Notes |
| --- | --- | --- |
| DMM | no-op | No output stage |
| DC power supply | `OutputEnable(ch, false)` for `1..ChannelCount` | Primary shutdown |
| Function generator | `OutputEnable(false)` | |
| Oscilloscope | `Stop()` | Idle acquisition |
| Switch | `OpenAll()` | |
| Counter | no-op beyond reset | No output stage |
| Power meter | no-op beyond reset | No output stage |
| Spectrum analyzer | `SweepContinuous(false)` | Stop free-run |

**Mocks.** Protocol-level `ScriptedFixture` / `MockTransport` / golden
transcripts stay in this library. HardwareTest `MockVisaSession` is a demo
sine-wave stub; it must not grow a second dialect table. HardwareTest only
**wraps** our fixtures (adapter to `IScpiIo`). Pack tests drive the eight
instruments through `ScriptedFixture`, not through HardwareTest.

### 4. Function steps: ship a thin set for every class

Easiest consume path includes steps, not only resources. v1 ships **thin
function steps** in the same pack so a TapPlan can Identity → configure →
measure/readback → SafeShutdown without `Plugins.Basic` measure steps.

HardwareTest Phase I publish contract — do not invent a parallel one:

| Table | Columns |
| --- | --- |
| `Sample` | `Channel`, `Index`, `Value` |
| `Scalar` | `Name`, `Value`, `Unit`, `LimitLow`, `LimitHigh` |

Do **not** ship Presentation mixins, result sidecars, or operator dialogs.
Authors attach HardwareTest `PresentationMixin` / `AnnotationMixin` in TUI.
Do not add `DialogStep`, WinForms, or WPF prompts.

DUT / serial stamping stays HardwareTest (`HardwareDut`). Our identity step
publishes `Identity` with `Idn` (and empty DutSerial if we include the column
for listener compatibility). HardwareTest Host may need to recognize this
pack’s identity step as well as `IdentityCheckStep` — that is HardwareTest
work.

### 5. Packaging

- Versioned `.TapPackage` (`CreateOpenTapPackage`).
- Package id (locked): `InstrumentComponents.OpenTap`.
- `Dependencies`: **OpenTAP** (match HardwareTest: `9.32.2` until we bump both)
  + bundled `InstrumentComponents`.
- **Not** `InstrumentComponents.Visa`, **not** `HardwareTest.Core`.
- Product plans depend on **this pack**, not on adding types to
  `HardwareTest.OpenTap.Plugins.Basic`.
- Document `AttachSession` / ctor injection in the pack README (same repo
  `dotnet/` folder).

Package id and **public type FullNames** are part of the TapPlan contract.
Treat renames as breaking; bump the OpenTAP package version in lockstep with
the assembly that contains the instrument types.

### 6. Hard nos

| Do not | Why |
| --- | --- |
| Reference `HardwareTest.Core` | Shell/broker stay in HardwareTest; this library stays reusable |
| Open `Ivi.Visa` / `GlobalResourceManager` from the OpenTAP pack | HardwareTest architecture tests forbid IVI in plugins; broker is the only IVI open on the bench |
| Put “needs DMM+PSU” (or any instrument inventory) in `program.json` | That sidecar is HardwareTest catalog (display name, DUT family, session requirements, `selectionIncludesCleanup`). Hardware needs belong in the TapPlan resources + Instruments slot overrides |
| Clone Instruments UI, Presentation, or operator interaction | Avalonia shell owns those |
| A second process-wide broker/locator inside this library | Injection from HardwareTest Host |
| Ship only DMM “to start” | Consuming tests need the full class list in TUI from v1 |

## Locked pack types

Namespace `InstrumentComponents.OpenTap`. Display group `Instrument Components`.
These FullNames are the TapPlan contract — do not rename after first package
publish.

### Instruments (all in v1)

| OpenTAP type | Wraps | `[Display]` name |
| --- | --- | --- |
| `ScpiInstrument` (abstract) | `InstrumentSession` + `IScpiIo` | (not added directly) |
| `DmmInstrument` | `Dmm` | DMM |
| `DcPowerSupplyInstrument` | `DcPowerSupply` | DC Power Supply |
| `FunctionGeneratorInstrument` | `FunctionGenerator` | Function Generator |
| `OscilloscopeInstrument` | `Oscilloscope` | Oscilloscope |
| `SwitchInstrument` | `Switch` | Switch |
| `CounterInstrument` | `Counter` | Counter |
| `PowerMeterInstrument` | `PowerMeter` | Power Meter |
| `SpectrumAnalyzerInstrument` | `SpectrumAnalyzer` | Spectrum Analyzer |

Shared on `ScpiInstrument` (visible in TUI / HardwareTest Instruments):

```text
VisaAddress              string, writable
IoTimeoutMilliseconds    int, default 5000, clamp 100..120_000
QueryIdn()               string
Reset()                  void
OutputOff()              abstract / virtual per class
AttachSession(IScpiIo)   not a Display property (host/tests)
```

Nested `[EmbedProperties]` (not extra resources): `Identity` (manufacturer /
model / serial / firmware, filled after Open) and, where it helps the Editor
grid, an `Output` group (enabled, channel). Prefer methods that already exist
on the typed classes over duplicating every knob as a property. Steps own
“what to do this run”; instruments own “which box + timeout + session”.

Each concrete type also exposes the wrapped class as `Dmm` / `Supply` / … so
custom product steps can call the full API without waiting for a pack step.

Multi-kind extra views on the **same** resource (SMU): methods `AsDmm()`,
`AsDcPowerSupply()`, … that construct the typed view on the shared
`InstrumentSession`. They throw `UnsupportedKindException` when the session
identity/classifier does not list that kind — same rule as `DeviceRef.OpenDmm`.
Do not surface those as additional `Instrument` subclasses on the plan.

### Function steps (all in v1)

Display group `Instrument Components`. Bind the matching concrete instrument
(or `ScpiInstrument` for identity/shutdown). Publish Phase I tables only.

| Step type | Instrument | Action | Publish |
| --- | --- | --- | --- |
| `IdentityQueryStep` | `ScpiInstrument` | `QueryIdn()` | `Identity` (`Idn`, `DutSerial`) |
| `SafeShutdownStep` | `ScpiInstrument` | `OutputOff()` + `Reset()` | none (log) |
| `DmmMeasureVoltageDcStep` | `DmmInstrument` | configure optional range; N samples | `Sample` Channel/Index/Value |
| `DmmMeasureScalarStep` | `DmmInstrument` | one reading + optional limits | `Scalar` |
| `PsuConfigureOutputStep` | `DcPowerSupplyInstrument` | V, I limit, enable | `Scalar` (setpoints) |
| `PsuReadbackStep` | `DcPowerSupplyInstrument` | `ReadVoltage` / `ReadCurrent` | `Sample` or `Scalar` |
| `FgenConfigureOutputStep` | `FunctionGeneratorInstrument` | waveform, Hz, Vpp, offset, enable | `Scalar` |
| `ScopeMeasureVppStep` | `OscilloscopeInstrument` | `MeasureVpp` | `Scalar` |
| `ScopeMeasureFrequencyStep` | `OscilloscopeInstrument` | `MeasureFrequency` | `Scalar` |
| `ScopeCaptureTraceStep` | `OscilloscopeInstrument` | ASCII `CaptureVoltageTrace` | `Sample` (Index=i, Value=volts) |
| `SwitchCloseRouteStep` | `SwitchInstrument` | `CloseRoute` | none |
| `SwitchOpenRouteStep` | `SwitchInstrument` | `OpenRoute` | none |
| `SwitchOpenAllStep` | `SwitchInstrument` | `OpenAll` | none |
| `CounterMeasureFrequencyStep` | `CounterInstrument` | `MeasureFrequency` | `Scalar` |
| `CounterMeasurePeriodStep` | `CounterInstrument` | `MeasurePeriod` | `Scalar` |
| `PowerMeterReadStep` | `PowerMeterInstrument` | `Read` | `Scalar` |
| `SpectrumAnalyzerMarkerPeakStep` | `SpectrumAnalyzerInstrument` | peak + `MarkerX`/`MarkerY` | `Scalar` (Hz and dBm) |

Channel / Index / Name / Unit / LimitLow / LimitHigh are step properties with
`[Display]`, not mixins. Presentation role (`timeseries` / `scalar` /
`passband`) is a HardwareTest mixin attached in TUI.

Do not ship Acquire-loops with operator prompts, MeanGte, Repeat, or HangForever
— those stay HardwareTest demo/shell concerns. A DMM step may take `SampleCount`
+ `IntervalMs` because that is measurement, not operator chrome.

### Project layout

```text
dotnet/src/InstrumentComponents.OpenTap/
  InstrumentComponents.OpenTap.csproj   # OpenTAP 9.32.2, ref InstrumentComponents only
  ScpiInstrument.cs
  DmmInstrument.cs
  DcPowerSupplyInstrument.cs
  FunctionGeneratorInstrument.cs
  OscilloscopeInstrument.cs
  SwitchInstrument.cs
  CounterInstrument.cs
  PowerMeterInstrument.cs
  SpectrumAnalyzerInstrument.cs
  Discovery/ScpiVisaAddressDiscovery.cs
  Steps/*.cs
dotnet/tests/InstrumentComponents.OpenTap.Tests/
  LoadWithoutBrokerTests.cs             # serialize → TestPlan.Load, no IScpiIo
  SessionInjectionTests.cs
  ArchitectureTests.cs                  # forbid Ivi.Visa / IviFoundation.Visa
  StepPublishTests.cs                   # Sample/Scalar column names
  FixtureMeasureTests.cs                # ScriptedFixture per class
```

CI: add a `dotnet test` project on GitHub-hosted runners (mock fixtures, no
VISA). Building `.TapPackage` in CI is in-scope so HardwareTest can consume a
CI artifact; publishing to a feed is separate from NuGet `0.2.0`.

## Authoring path (easiest consume)

1. Install `InstrumentComponents.OpenTap` (TUI/`tap package install`, or
   HardwareTest plugin dir / appliance bake).
2. In OpenTAP TUI or Editor: add instruments from **Instrument Components**
   (all eight listed). Set `VisaAddress` (or leave blank for bench slot
   override).
3. Add pack steps; assign instrument properties. Attach HardwareTest
   Presentation/Annotation mixins if the plan will run on the operator UI.
4. Save `.TapPlan`. HardwareTest `program.json` beside it is display/DUT/session
   flags only — not “needs DMM+PSU”.
5. Validate with HardwareTest `--validate-plan`. On the bench, Instruments
   rebinds `VisaAddress`; Host `AttachSession` before execute.

Combo SMU: one `DcPowerSupplyInstrument`, PSU steps + custom or DMM methods via
`AsDmm()`. Separate physical DMM and PSU: two resources, two addresses.

## Current repo vs the contract

| Need | Today | Gap |
| --- | --- | --- |
| Message-level `IScpiIo` | `ScpiSession.Write` / `Query` + timeouts + dispose | No interface; `InstrumentSession` only constructs from `ITransport` |
| Injected session into typed classes | `new Dmm(InstrumentSession)` works | No `AttachSession`; opening still goes through `ISessionOpener` → VISA |
| Identity / reset on session | `InstrumentSession.Idn()` / `Reset()` | Not on typed classes or pack instruments |
| Output-off | PSU `OutputEnable`; FGen `OutputEnable`; switch `OpenAll`; scope `Stop` | No portable `OutputOff()` on each class |
| Protocol mocks | `ScriptedFixture`, `MockTransport`, `spec/` + `fixtures/` | Need identity+shutdown+measure fixtures per class for pack tests |
| OpenTAP instruments | none | New project; **all eight types in v1** |
| Function steps | none | Thin Phase I steps per class (table above) |
| Writable `VisaAddress` | n/a | On `ScpiInstrument` |
| `IDeviceDiscovery` | `VisaEnumerator` in `.Visa` (IVI) | Pack-safe enumerator via OpenTAP `SessionLocal` |
| `.TapPackage` | NuGet ids only (`InstrumentComponents` 0.1.0) | `InstrumentComponents.OpenTap` package metadata |

`InstrumentComponents.Visa` opening IVI is **correct** for library consumers
and examples. The restriction is **plugin / pack** code that HardwareTest will
load, not the Visa package itself.

## Recommended path (this repo)

Independent of dual-native G (transcripts) / H (hardware smoke). F (dialect
remaining classes) is already merged. Do not block G/H. C# APIs below should
stay thin enough that Rust does not need equivalents until a non-OpenTAP
consumer wants `IScpiIo`.

### P1 — Session injection seam (`InstrumentComponents` only)

1. Introduce `IScpiIo` (Write / Query / timeout / dispose).
2. Implement it on `ScpiSession` (or a thin adapter over it).
3. Allow `InstrumentSession` to wrap an injected `IScpiIo` without `ITransport`.
4. Document a HardwareTest adapter: `IVisaSession` → `IScpiIo` (no framing of
   our own on that path).
5. Tests: mock `IScpiIo` drives `Dmm.MeasureVoltageDc` without `ITransport`;
   injected timeout is honored.

### P2 — Portable identity / shutdown on typed classes

1. Add `QueryIdn()` / `Reset()` on each of the eight typed classes (delegate to
   session).
2. Add `OutputOff()` per the class table above.
3. OpenTAP-free interfaces `IInstrumentIdentity` / `IInstrumentShutdown` so
   HardwareTest can bind without pack types if needed.
4. Scripted fixtures + tests for `*IDN?`, `*RST`, and output-off per class.

### P3 — OpenTAP pack: all eight instruments

`dotnet/src/InstrumentComponents.OpenTap/`:

1. `ScpiInstrument` with `VisaAddress`, timeout, default ctor, `ctor(IScpiIo)`,
   `AttachSession`, `Open`/`Close` (inject-only).
2. All eight concrete instruments wrapping the typed classes; nested Identity;
   `AsDmm()` / `AsDcPowerSupply()` / … extra views.
3. `IDeviceDiscovery` for `VisaAddress` (empty without registered enumerator).
4. Architecture test: pack `.cs` / `.csproj` must not mention `Ivi.Visa`,
   `GlobalResourceManager`, or `IviFoundation.Visa`.
5. Golden TapPlan per type: serialize → `TestPlan.Load` with no broker → type
   names round-trip.
6. CI builds the `.TapPackage`.

Keep pack type namespaces stable from the first published package.

### P4 — Function steps for every class

Same project as P3 (can land in the same PR series once P3 types exist). Column
names golden-tested. No Presentation/operator types.

P3 and P4 are the consume path; do not ship a DMM-only pack “to learn OpenTAP”
and add the rest later — TapPlan type names would churn and HardwareTest would
keep growing `Plugins.Basic`.

## HardwareTest-side work (out of scope here)

- `AttachSession` after `TestPlan.Load`, wrapping `IVisaSession` as `IScpiIo`.
  Optional: de-dupe by `VisaAddress`. No `VisaBrokerHost` clone inside this
  library.
- Retarget demo `IdentityCheckStep` / `SafeShutdownStep` to `ScpiInstrument`
  **or** leave them as demos and run product plans on this pack’s steps.
- Recognize this pack’s `Identity` publish / identity step for DUT stamping.
- Stop growing `VisaDmmInstrument` / `Plugins.Basic` for product SCPI.
- `program.json` stays display/DUT/session flags; never instrument inventory.
- Provision `InstrumentComponents.OpenTap` via existing offline package /
  plugin-dir / appliance bake.
- Optionally register a `ResourceEnumerator` on OpenTAP `SessionLocal` so pack
  `IDeviceDiscovery` fills **Discover OpenTAP**.

## Decision log

| Topic | Decision |
| --- | --- |
| Become a HardwareTest fork? | No |
| Broker in this library? | No — inject session (`AttachSession` / ctor) |
| OpenTAP pack | Yes — planned C# plugin, not optional-subset |
| Which instruments in v1? | **All eight** `InstrumentKind` types |
| IVI in the pack? | No |
| IVI in `InstrumentComponents.Visa`? | Yes, remains the native backend |
| Message vs byte injection? | Message (`IScpiIo`); do not wrap `IVisaSession` as `ITransport` |
| One vs many OpenTAP **resources** per box? | One resource; extra kinds are nested views |
| One vs many OpenTAP **types**? | Eight types (TUI catalog) + abstract base |
| Steps in v1? | Yes — thin Phase I steps for every class |
| `program.json` in this repo? | No |
| Standalone TUI VISA open in v1? | No — fail closed without injected session |

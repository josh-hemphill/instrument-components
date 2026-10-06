# Async I/O (C#)

Core async APIs take `CancellationToken` and return `Task` / `ValueTask`. Typed async classes (`AsyncDmm`, etc.) mirror the sync surface with `*Async` method names.

## Mock quick start

```csharp
using InstrumentComponents.Catalog;
using InstrumentComponents.Kind;
using InstrumentComponents.Mock;

var fixture = ScriptedFixture.Builder()
    .Idn("Acme", "DMM1", "SN1", "1.0")
    .Kinds(InstrumentKind.Dmm)
    .OnQuery(":MEAS:VOLT:DC?", "1.0")
    .Build();

var catalog = DeviceCatalog.FromFixture("mock://dmm", fixture);
var dmm = await catalog.Device("mock://dmm").OpenDmmAsync();
var volts = await dmm.MeasureVoltageDcAsync();
Console.WriteLine($"{volts} V");
```

```bash
dotnet run --project examples/MockFixtureCiAsync
```

## VISA async honesty

[`VisaAsyncTransport`](https://github.com/josh-hemphill/instrument-components/blob/latest/dotnet/src/InstrumentComponents.Visa/VisaAsyncTransport.cs) wraps sync `VisaTransport` in `SyncAsAsyncTransport`:

- `WriteAsync` / `ReadAsync` run blocking VISA I/O on the thread pool.
- `CancellationToken` cancels queued bridge work before it starts; it does **not** cancel an in-flight native VISA call the way true APM would.
- This is intentional until vendor APM proves reliable across Keysight and NI on Windows **and** Linux.

Do **not** advertise “true async VISA I/O” for the C# package yet. Rust already has true async via visa-rs `InstrumentTokioAdapter` — see [Rust async](../rust/async.md).

## Hardware

```csharp
using InstrumentComponents.Visa;

var catalog = await VisaDiscovery.Create().ScanAsync();
var dmm = await catalog
    .Device(catalog.DevicesByKind(InstrumentComponents.Kind.InstrumentKind.Dmm)[0].Address.Raw)
    .OpenDmmAsync();
var volts = await dmm.MeasureVoltageDcAsync();
```

## Related

- [C# VISA](visa.md) — packages and platform notes
- Agent spike notes: repo `docs/visa-async-csharp.md`

A running native call owns its read/write buffer until it finishes under the VISA
timeout. Its task remains pending until then. Complete session queries serialize
the write/read pair so concurrent callers cannot exchange replies. Dispose waits
for an active call before closing the native session.

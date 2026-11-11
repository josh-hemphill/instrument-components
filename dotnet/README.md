# Instrument Components for .NET

The breaking 0.2 preview uses explicit factories, passive candidate catalogs and shared protocol-neutral handles. The SDK supports .NET 8 and 10 Native AOT on Windows/Linux x64. OpenTAP host compatibility is separate.

See the [getting started guide](../docs-site/docs/getting-started.md), [async guide](../docs-site/docs/csharp/async.md) and [OpenTAP guide](../docs-site/docs/csharp/opentap.md). Run `dotnet test dotnet/InstrumentComponents.sln -c Release` for software-only tests. The main build needs no external device packages or hardware. `MockFixtureCi`, `MockFixtureCiAsync` and `OpenTapMockPlan` demonstrate opaque in-memory composition.

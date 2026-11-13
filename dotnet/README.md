# Instrument Components for .NET

The **0.3.0-alpha.1** breaking preview uses explicit factories, passive candidate catalogs and shared protocol-neutral handles. Pin the facade, SDK and selected external device packages to the same exact prerelease. Check the matching release pages and registries for availability before installing.

The SDK and device packages have software checks for .NET 8 and 10 Native AOT on Windows/Linux x64. OpenTAP is a separate managed host. VISA provider activation and physical device behavior require bench evidence; no hardware has been validated.

Read the versioned [getting started and upgrade guide](https://github.com/josh-hemphill/instrument-components/blob/driver-sdk-v0.3.0-alpha.1/docs-site/docs/getting-started.md), [async guide](https://github.com/josh-hemphill/instrument-components/blob/driver-sdk-v0.3.0-alpha.1/docs-site/docs/csharp/async.md), and [OpenTAP guide](https://github.com/josh-hemphill/instrument-components/blob/driver-sdk-v0.3.0-alpha.1/docs-site/docs/csharp/opentap.md). The 34401A uses an explicit commissioned factory; the retired generic registration belongs to the 0.2 API.

Run `dotnet test dotnet/InstrumentComponents.sln -c Release` for software tests. Core builds require no external device implementations or hardware. `MockFixtureCi`, `MockFixtureCiAsync` and `OpenTapMockPlan` demonstrate opaque in-memory composition. TestKit is a development fixture, and device implementations remain in independently distributed vendor repositories.

# .NET 10 package consumer smoke

This example uses NuGet package references rather than project references. It asserts the .NET 10 runtime, resolves the VISA assembly without opening a native resource manager, and verifies sync/async measurements against a mock instrument.

From the repository root with the .NET 10 SDK:

```bash
dotnet pack dotnet/src/InstrumentComponents/InstrumentComponents.csproj -c Release -o /tmp/instrument-packages
dotnet pack dotnet/src/InstrumentComponents.Visa/InstrumentComponents.Visa.csproj -c Release -o /tmp/instrument-packages
dotnet restore dotnet/examples/PackageConsumerSmoke/PackageConsumerSmoke.csproj --source /tmp/instrument-packages --source https://api.nuget.org/v3/index.json --packages /tmp/instrument-consumer-cache
dotnet run --project dotnet/examples/PackageConsumerSmoke/PackageConsumerSmoke.csproj -c Release --no-restore
```

Use fresh output/cache directories for each validation run. For a different package version, pass `-p:InstrumentComponentsPackageVersion=VERSION` to restore and run. CI executes this check on Linux and Windows.

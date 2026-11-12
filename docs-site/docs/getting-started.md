# Getting started

Choose the device packages your application needs and register their exported factories explicitly. The root facade provides an immutable catalog of passive candidates. It neither enumerates hardware nor sends identification queries. Your host supplies candidates, identity evidence and the appropriate connector; opening invokes the selected factory.

## Install the public preview

The SDK and all five device families are published as **0.2.0-alpha.1** on crates.io and NuGet. Pin that exact prerelease while evaluating this breaking API; `0.1` examples use the retired API.

For Rust, use Rust 1.85 or newer and add the facade plus the device package you need:

```sh
cargo add instrument-components@=0.2.0-alpha.1 --no-default-features
cargo add instrument-driver-keysight@=0.2.0-alpha.1
```

For C#, use a .NET 8 or .NET 10 console project:

```sh
dotnet add package InstrumentComponents --version 0.2.0-alpha.1
dotnet add package InstrumentComponents.Drivers.Keysight --version 0.2.0-alpha.1
```

Replace Keysight with the family you need. Multiple device packages can be registered in one application. The facade has no bundled drivers. A VISA runtime is needed only when your application chooses a VISA connector; ordinary package restore and the mock example below require no hardware, credentials or VISA installation.

## Run without hardware

The complete [public-package example](https://github.com/josh-hemphill/instrument-components/tree/feat/driver-sdk-extraction/validation/public-preview) contains Rust and C# console projects with version-only dependencies. Download that directory, then run from its root:

```sh
cargo run --manifest-path rust/Cargo.toml --release
cargo run --manifest-path rust/Cargo.toml --release --features async
dotnet run --project dotnet/Preview.csproj -c Release
```

It registers the published Keysight driver against TestKit's strict in-memory connector and measures **1.25 Volt**. It also checks that selection and unsupported required capabilities perform no I/O, that close releases the connection once, and that native async and worker frontends produce the same result. TestKit is a development fixture; replace it with your host's connector for real equipment. The example sends no hardware traffic and does not validate a physical 34401A.

From the main checkout, run the stronger first-install check into an empty directory outside the checkout:

```sh
python tools/validate-public-preview.py --output /tmp/instrument-preview --framework net8.0
```

On Windows, choose an empty output path such as `C:\Temp\instrument-preview`. The check restores only public packages into fresh caches, runs Rust default/async, publishes and executes the C# app with Native AOT, and records package/binary/log hashes. Use `--framework net10.0` for .NET 10. Native compilation needs Clang and the platform development libraries on Linux, or Visual Studio C++ build tools on Windows. CI covers both x64 hosts and frameworks.

The main checkout also contains protocol-neutral custom-provider examples:

```sh
dotnet run --project dotnet/examples/MockFixtureCi
dotnet run --project dotnet/examples/MockFixtureCiAsync
cargo test -p instrument-components --all-features
```

These examples use an opaque in-memory backend. They prove that the facade does not require SCPI. TestKit and synthetic drivers are development dependencies, excluded from production package closures.

## Package boundary

| Responsibility | Rust | .NET |
|---|---|---|
| Protocol-neutral contracts and typed views | instrument-api | InstrumentComponents.Abstractions |
| Registration and serialized session runtime | instrument-runtime | InstrumentComponents.Runtime |
| Optional data-driven SCPI backend | instrument-scpi | InstrumentComponents.Scpi |
| Passive application catalog | instrument-components (library `instrument`) | InstrumentComponents |
| Development scripts and fault injection | instrument-testkit | InstrumentComponents.TestKit |

Device packages are `instrument-driver-{vendor}` and `InstrumentComponents.Drivers.{Vendor}`, maintained in the separate [Keysight](https://github.com/josh-hemphill/instrument-components-keysight/tree/driver-v0.2.0-alpha.1), [Keithley](https://github.com/josh-hemphill/instrument-components-keithley/tree/driver-v0.2.0-alpha.1), [Rigol](https://github.com/josh-hemphill/instrument-components-rigol/tree/driver-v0.2.0-alpha.1), [Siglent](https://github.com/josh-hemphill/instrument-components-siglent/tree/driver-v0.2.0-alpha.1) and [Tektronix](https://github.com/josh-hemphill/instrument-components-tektronix/tree/driver-v0.2.0-alpha.1) repositories. These links select the published source tags; the unmerged default branches still contain repository scaffolds. All existing model hints are migrated as unvalidated source evidence.

## C# composition

With a host-supplied `connector` and passive `candidate`, compose explicitly:

```csharp
using InstrumentComponents.Catalog;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
using Vendor = InstrumentComponents.Drivers.Keysight.Drivers;

var registry = new DriverRegistry(Vendor.Factories(connector));
var catalog = new DeviceCatalog(registry, new[] { candidate });
using var handle = catalog.Device(candidate.Endpoint).Open();
var dmm = new Dmm(handle);
// Invoke only operations advertised by handle.Operations.
var value = dmm.MeasureVoltageDc(null);
Console.WriteLine($"{value.Value} {value.Unit}");
```

Opening again requests another connection. Share one handle among class views for multifunction devices. A 2602B's DMM and PSU views therefore share serialization, configuration, units and lifecycle. Never infer supported operations from a model name alone. Request required capabilities at open to reject incompatible connection bindings before transport IO.

## Rust composition

```rust,ignore
use instrument::prelude::*;
use std::collections::{BTreeMap, BTreeSet};

let factories = instrument_driver_keysight::sync_factories(connector, &BTreeMap::new())?;
let registry = DriverRegistry::new(factories)?;
let catalog = DeviceCatalog::new(registry, vec![candidate.clone()])?;
let handle = catalog.device(&candidate.endpoint)?.open(
    &Selection::default(), &BTreeSet::new(), ConnectionOwnership::Owned,
)?;
let value = Dmm(&handle).measure_voltage_dc(None)?;
handle.close(false)?;
```

The connector and candidate are application inputs, not globals. No runtime assembly scanning, library loading or reflection activation is involved. See [async execution](rust/async.md) and [discovery](discovery.md) for lifecycle and matching rules.

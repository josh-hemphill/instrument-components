# Getting started

Choose the device packages your application needs and register their exported factories explicitly. The root facade provides an immutable catalog of passive candidates. It neither enumerates hardware nor sends identification queries. Your host supplies candidates, identity evidence and the appropriate connector; opening invokes the selected factory.

For a hardware-free example from this checkout:

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

Device packages are `instrument-driver-{vendor}` and `InstrumentComponents.Drivers.{Vendor}`, maintained in the separate [Keysight](https://github.com/josh-hemphill/instrument-components-keysight), [Keithley](https://github.com/josh-hemphill/instrument-components-keithley), [Rigol](https://github.com/josh-hemphill/instrument-components-rigol), [Siglent](https://github.com/josh-hemphill/instrument-components-siglent) and [Tektronix](https://github.com/josh-hemphill/instrument-components-tektronix) repositories. Preview sources and draft PRs are available; public registry availability is a separate release gate. Pin the exact preview SDK and driver versions. All existing model hints are migrated as unvalidated source evidence.

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

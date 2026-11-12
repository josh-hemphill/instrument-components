# DC power supply

Use the `DcPowerSupply` typed view over an already opened protocol-neutral handle. Rust views borrow the handle; C# views retain its shared reference. Async views use the corresponding async handle. A view does not open another connection or introduce device-specific fallback behavior.

Inspect the handle's operation set and request required operations at open. Device packages own literal commands, supported arguments, channel domains and any model exceptions. An unsupported operation fails without issuing IO; class membership alone does not guarantee every method.

Measurement results preserve units and status. See [capabilities](../capability-matrix.md), [composition](../getting-started.md) and [errors](../csharp/errors.md). The authoritative operation signatures are generated from `spec/driver-operations.json` in the main repository. External package provenance links source behavior to exact transcript cases. Hardware confirmation remains pending.

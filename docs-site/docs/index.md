# Instrument Components

Compose instrument applications from vendor-neutral contracts and explicit device packages. Rust and C# share an operation catalog, capability model and lifecycle rules. Device commands and exceptions belong in external driver repositories.

The breaking **0.3.0-alpha.1** preview targets Linux x64 and Windows x64. Native AOT applies to the .NET SDK and statically composed applications; the OpenTAP host has separate requirements. Software/transcript tests do not establish hardware behavior. Early packages must pass release gates before bench evidence is collected.

Start with [Getting started](getting-started.md), then [Discovery and registration](discovery.md). Use [the capability matrix](capability-matrix.md) to understand which operations require explicit inventory or device evidence.

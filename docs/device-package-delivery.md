# Device package delivery

All five repositories now exist under `josh-hemphill`: `instrument-components-{keysight,keithley,rigol,siglent,tektronix}`. Each has a draft implementation PR and independently packaged Rust/.NET sources. Core production dependencies contain no external devices. Old constructor compatibility is intentionally dropped in 0.2.

Implemented: static explicit factories, external model/USB hints, source-derived profiles, optional-argument plans, immutable model/endpoint bindings, honest output capabilities, shared generated Rust/C# wire cases, version-only production dependencies, generated-source and packed-consumer validation. The main facade and OpenTAP consume protocol-neutral handles. Generic SCPI interpretation and optional standards transports remain in main.

Validation layers must be reported separately:

1. Source and generation checks; neutral lifecycle/unit/cancellation tests; exact independent external wire transcripts.
2. Checksummed release-form artifact bootstrap into fresh Cargo/NuGet feeds; native Linux/Windows x64 .NET 8/10 execution, Rust default/async, four frontends and negative trimming diagnostic gate.
3. Static all-five application composition with versioned dependencies and identity/capability/multifunction checks.
4. Approved public preview publication and fresh public-registry consumer validation. Bootstrap evidence does not pass this gate.
5. Physical bench evidence after previews, with firmware/transport/package hashes, wiring, units, acceptance ranges and logs. No hardware validation has been performed.

The batch publisher helper in `tools/publisher-setup` removes repeated policy entry. NuGet needs one normal account sign-in. Existing crates need a temporary trusted-publishing configuration token. New crate names require first publication before their policies can be created; that publication remains a separately reviewed action. SDK packages release before facade/integration packages, then external packages, then public composition validation.

Use the task's current source/evidence index and CI run IDs to assess which gates actually ran. Workflow definitions alone are not execution evidence; do not promote stale runs on earlier source revisions as final cutover validation.

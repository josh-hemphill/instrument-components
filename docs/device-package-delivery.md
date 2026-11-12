# Device package delivery

All five repositories now exist under `josh-hemphill`: `instrument-components-{keysight,keithley,rigol,siglent,tektronix}`. Each has an open, unmerged implementation PR and independently packaged Rust/.NET sources. Core production dependencies contain no external devices. Old constructor compatibility is intentionally dropped in 0.2.

Implemented: static explicit factories, external model/USB hints, source-derived profiles, optional-argument plans, immutable model/endpoint bindings, honest output capabilities, shared generated Rust/C# wire cases, version-only production dependencies, generated-source and packed-consumer validation. The main facade and OpenTAP consume protocol-neutral handles. Generic SCPI interpretation and optional standards transports remain in main.

Validation layers must be reported separately:

1. Source and generation checks; neutral lifecycle/unit/cancellation tests; exact independent external wire transcripts.
2. Checksummed release-form artifact bootstrap into fresh Cargo/NuGet feeds; native Linux/Windows x64 .NET 8/10 execution, Rust default/async, four frontends and negative trimming diagnostic gate.
3. Static all-five application composition with versioned dependencies and identity/capability/multifunction checks.
4. Approved public preview publication and fresh public-registry consumer validation. Bootstrap evidence does not pass this gate.
5. Physical bench evidence after previews, with firmware/transport/package hashes, wiring, units, acceptance ranges and logs. No hardware validation has been performed.

The batch publisher helper in `tools/publisher-setup` removes repeated policy entry. NuGet needs one normal account sign-in. Existing crates need a temporary trusted-publishing configuration token. New crate names require first publication before their policies can be created; that publication remains a separately reviewed action. SDK packages release before facade/integration packages, then external packages, then public composition validation.

Use the task's current source/evidence index and CI run IDs to assess which gates actually ran. Workflow definitions alone are not execution evidence; do not promote stale runs on earlier source revisions as final cutover validation.

Current software acceptance includes all20 independent vendor native cells and
all four statically composed all-five cells, with actual IL2026 and IL3050
failures verified. The 74 OpenTAP steps have exact nondefault request/value/unit
oracles and execute saved/reloaded plans; a deliberately wrong forwarded range
fails those oracles. `python tools/device-extraction-index.py
--require-complete-evidence` checks the committed execution/package/log joins
offline. That record preserves the original software and artifact-bootstrap
results. The separate public-release record below covers the subsequent
publication and public package consumers; it does not relabel the historical
bootstrap evidence. Physical bench evidence remains unperformed.


## Published alpha and cloud handoff

The coordinated `0.2.0-alpha.1` release is complete: twelve Rust crates and
thirteen NuGet packages are public, including the SDK, facade/integrations and
all five independent drivers. The Python SDK tool wheel and OpenTAP package
are attached to the main repository's GitHub prereleases. Exact release tags,
source commits, public archive hashes and successful workflow/job URLs are in
[`spec/public-release-evidence.json`](../spec/public-release-evidence.json).

All twenty vendor public-consumer cells passed on Linux/Windows x64 with .NET
8/10, using fresh public Cargo/NuGet caches. All four all-five application
composition cells also passed. Their proof archives were checked against
GitHub artifact digests, native binary and execution-log hashes, and the actual
public package hashes. Each composition cell consumed the ten intended Cargo
and ten NuGet packages. The later bench self-test stages the standards
transport from source; it remains a separate software test without hardware IO.

All twelve Rust publisher mappings and seven NuGet policy mappings are verified.
The nine new Rust mappings were created and read back after their initial
publication. The six temporary `CARGO_BOOTSTRAP_TOKEN` secrets were removed and
the account owner confirmed temporary token revocation. The existing root NuGet
`InstrumentComponent*` policy and unrelated repository credentials were
preserved. New Rust crates use OIDC on their next release; that first subsequent
OIDC publication has not yet been exercised. The three existing root crates and
all NuGet releases successfully used OIDC in this release.

Release tags remain fixed at the source commits in the evidence record. The
implementation PRs remain open and unmerged: main PR 37 uses
`feat/driver-sdk-extraction`; each vendor PR 1 uses
`feat/independent-device-drivers`. Continue cloud development on those branches,
or merge them through the normal review process. Vendor default branches still
contain the initial repository scaffolds. A subsequent release must use a new
package version and matching tag; published versions cannot be overwritten.
Update exact SDK dependency pins when changing the SDK version. No temporary
publishing token is required for the configured OIDC workflows.

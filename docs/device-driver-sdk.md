# Device driver SDK preview

The independently versioned SDK implements synchronous, native async and dedicated
worker device composition in Rust and .NET. Its production packages are independent
of `instrument-core` / `InstrumentComponents`, VISA and vendor implementations.
Version `0.2.0-alpha.1` is a published breaking alpha preview. Physical hardware validation remains unperformed.
Internal Cargo and NuGet package dependencies pin that exact preview revision;
package proofs inspect the real emitted dependency ranges without strengthening them.
The facade and OpenTAP now use explicitly registered protocol-neutral handles. The old device-aware facade has been removed.

The November 2026 decisions permit a breaking API and preview publication before
hardware validation. All current model hints and all eight classes remain in the
final cutover scope. Required deployment targets are Linux x64 and Windows x64,
with .NET 8 and .NET 10 Native AOT. See [delivery and setup](device-package-delivery.md).

| Role | Rust | .NET |
|---|---|---|
| Public contracts | `instrument-api` | `InstrumentComponents.Abstractions` |
| Selection and shared handles | `instrument-runtime` | `InstrumentComponents.Runtime` |
| Explicit-profile SCPI interpreter | `instrument-scpi` | `InstrumentComponents.Scpi` |
| Development conformance helpers | `instrument-testkit` | `InstrumentComponents.TestKit` |
| Synthetic binary package proof | `validation/driver-sdk/rust-provider` | `validation/driver-sdk/dotnet-provider` |

The new production packages have no dependency on the legacy packages, VISA,
device models or the synthetic implementation. Applications reference selected
driver packages and pass statically referenced factories to `DriverRegistry`.
There is no global registry, reflection activation, assembly scan or runtime
managed-code loading. Normal trait/interface dispatch supports build-time AOT.

## Implemented contract

`DriverFactory` / `IDriverFactory` exposes immutable registration metadata and
opens an arbitrary backend. A backend is not required to supply a SCPI session or
byte transport. An opaque endpoint, passive identity evidence and owned/borrowed
lifetime policy are passed to the factory. Matching and selection issue no I/O.
Factories must be safe to call from multiple callers and clean up any resources
they acquire if opening fails.

Matching uses ASCII case folding and trimmed identity fields in both languages.
Exact manufacturer+model or vendor+product evidence ranks 3, family manufacturer
plus `*` model patterns rank 2, explicitly enabled standard SCPI ranks 1. Higher
application preference tiers win before ranks. Equal best driver IDs produce
`AmbiguousDriver`, independent of registration order. Duplicate IDs and unsupported
contract majors fail registry creation. Explicit driver bindings override identity
hints but still require an accepted endpoint scheme; absent bindings never fall
back. Schemes are opaque strings, not vendor-specific VISA parser extensions.

The descriptor's operation envelope is checked against the actual backend at open.
Unsupported required operations fail before opening when the descriptor makes
that possible; missing actual requirements or invalid accessors close the newly
opened backend. Optional reduced support is permitted. Runtime holds an immutable
support snapshot and rejects unsupported operations before calling the provider.

All eight typed class views are implemented: DMM, DC power supply, function
generator, oscilloscope, switch, counter, power meter and spectrum analyzer.
[`spec/driver-operations.json`](../spec/driver-operations.json) defines 83 closed
operations, parameter constraints and units, including common identity, reset and
shutdown. Generated Rust request enums and sealed C# request records feed the
protocol-neutral `CatalogExecutor` / `ICatalogExecutor`. Strongly typed sync/async
views convert and validate owned replies; callers need no SCPI session. Existing
basic DMM/PSU interfaces also remain as SDK convenience capabilities; the legacy
facade constructors/raw-session escape hatches are not compatibility requirements.

A backend advertises only operations actually implemented. The descriptor is an
upper bound, checked against accessor availability and the actual immutable
operation set at open. Catalog-only non-SCPI backends support typed operations and
checked shutdown without implementing the older basic capability interfaces.
The binary fixtures prove this boundary independently of SCPI.

`ScpiFactory` exposes Sync and WorkerAsync; adding a native connector exposes
NativeAsync under the same driver ID. A native-only factory is also available.
Generated factory helpers accept explicit passive identity rules from their input.
Neither SDK contains manufacturer/model defaults. Application registration is
static and explicit; mode selection never falls back.

Factories expose an immutable execution-mode set, captured at registration along
with their descriptors. Existing factories default to Sync. New APIs are explicit:
Rust `open_native` / `open_worker` and .NET `OpenNativeAsync` / `OpenWorkerAsync`.
They select once and never fall back to another mode or driver.

Rust contracts use boxed Send futures with no executor dependency. Runtime and
synthetic native async require the optional `async` feature and Tokio; the default
SDK closure has no Tokio dependency. Existing Send capability contracts are
preserved. Separate `Local*` worker traits allow genuinely non-Send SDK state;
the fixture uses `Rc<RefCell<_>>`, created and destroyed inside its worker thread.
.NET uses statically referenced ValueTask/Task interfaces and CancellationToken.
Neither runtime converts native async into a synchronous API.

## Session ownership

All typed views share one handle and lock for each complete backend operation,
including multi-message exchanges and state updates. There is no public raw-I/O
escape hatch or public multi-operation/nested transaction API in this milestone.
Backend callbacks must not reenter the handle; runtime rejects reentry rather
than deadlocking or disposing an active operation. Providers must not invoke
untrusted callbacks while executing under the operation lease.

Invalid known inputs and unsupported operations produce no provider I/O. Provider
communication/parse/contract failures quarantine the handle until successful
explicit provider recovery. A provider returning SessionClosed/SessionClosing
after dispatch also requires recovery; the handle's own preflight close rejection
performs no provider I/O and causes no quarantine. No generic SCPI flush, retry,
reconnect, identity, completion, reset or error-queue traffic is generated by
the runtime.

Checked shutdown first consults the immutable enabled support map; an exposed but
unadvertised shutdown accessor is never called, and no shutdown-related recovery
runs in that case. Resource cleanup still occurs and checked close reports failure.

Close publishes a closing flag before waiting on the operation lock, rejects
queued/new work, and preserves that flag when active work completes. It optionally
requests provider-defined shutdown and always releases resources, even if shutdown
fails. If recovery is required and fails, shutdown is not attempted through the
uncertain protocol state; the close reports failure and releases resources.
For every frontend, repeated checked close shares the same result and the
first close fixes shutdown policy; a later stronger request reports Shutdown
instead of claiming that shutdown occurred. Provider shutdown must attempt all
applicable outputs and fail on unknown/partial coverage. Borrowed host I/O must survive backend close.
Ordinary disposal/Drop releases resources without issuing implicit output/reset
commands; use explicit checked close for shutdown outcomes.
Sync C# `Dispose` does not replay a previously reported checked-close failure during unwinding; its first resource-release failure still reports an error. Rust `Drop` is best effort and has no returned error channel, so use explicit `close` to observe cleanup failures. Concurrent checked closers observe the first accepted shutdown policy and its retained outcome; resource cleanup runs once.

## Async cancellation and cleanup

Native operations hold one lease across all awaits, including complete protocol
transactions. Dropping a dispatched Rust future or canceling a dispatched .NET
operation quarantines the connection before unlocking. Queued cancellation skips
provider I/O. Providers must stop host-resource access before their native future
is dropped or canceled; a provider with uninterruptible blocking work must use the
worker mode. Merely discarding a .NET Task does not cancel work: the continuing
task retains the lease and resource until completion.

The worker owns opening, every SDK call, recovery, shutdown and destruction on
one dedicated OS thread. Requests carry owned values; a canceled running waiter
returns promptly, while the worker retains all resources and marks Draining.
After that call finishes, subsequent operations require explicit recovery. Close
rejects queued/new work and drains the active call before cleanup. An
uncooperative SDK call can remain blocked indefinitely; canceling a waiter cannot
authorize concurrent reuse or destruction. Explicit worker sync methods wait for
worker responses, rather than invoking a hidden native-async bridge.

Abandoned opens retain cleanup ownership, including partial acquisition and
completed-but-undelivered handles. Native and worker checked close have an owned
completion independent of individual waiters. Drop/finalization requests only
resource cleanup. For Rust native close, keep Tokio alive until checked close
finishes. Executor shutdown resolves close waiters with a terminal failure and
synchronously releases resources when the active lease ends; it cannot promise
that protocol shutdown ran. Native resource-only close/Dispose must be nonblocking
and must not depend on another async task. Worker resource destruction always
stays on its thread.

Reentry is rejected across awaits in the native task/context, and on the worker
thread before queuing. Providers must not create arbitrary cross-task callback
cycles around an active lease: task-local checks cannot detect every such cycle.

## Explicit-profile SCPI package

The interpreter contains opt-in IEEE 488.2 common-command literals, not subsystem
templates or bundled device profiles. Each external driver supplies an explicit
validated profile: operation plans, named placeholders, token maps, optional
numeric defaults, channels and complete typed output inventory. Legacy `generic_*`
traffic is evidence for conversion, not a universally supported standard. Missing
operations or templates fail; there is no generic fallback or automatic probing.

Message IO accepts a complete unterminated body and returns exactly one complete
response. The host adapter owns terminators, framing, transport timeouts and bounds
before allocation; the interpreter rechecks the 1 MiB response limit and rejects
NUL. Commands are printable ASCII, bounded to 64 KiB, with at most 32 plan steps.
Argument tokens use closed explicit mappings, never arbitrary wire fragments.
Numeric formatting/parsing is invariant, finite and strict. Scalar overload has an
explicit driver-reported status. The generic numeric parser does not infer device
overload from magnitude; interpretation of sentinel values belongs to the device
implementation. OpenTAP preserves reported overload as a failed verdict. Samples/traces are owned, finite and bounded to one million points.
The current scope waveform contract remains ASCII preamble plus data; binary scope
capture was not present in the old library and is not introduced here.

A whole multistep configuration/acquisition runs under the same handle lease.
DMM READ/FETCH requires a configured measurement unit; successful direct measure
and configure operations establish it. Reset and successful clean-boundary recovery
invalidate configured DMM/power state. Scope preamble must provide a positive
sample interval. Failed/partial operations quarantine the handle; recovery delegates
to an explicit host strategy that establishes a clean message boundary or fails.
Nothing silently flushes, retries, resets or reconnects.

Shutdown inventory distinguishes PSU channel outputs from generator outputs.
All disable requests are prepared before the first write; failure on one output
does not prevent attempts on the others. Unknown coverage cannot advertise checked
shutdown. Metadata channel count counts PSU outputs, not input channels. No-output
instruments may explicitly declare an empty inventory. Reset is a separate operation.

Owned IO is closed once. Borrowed IO is not closed/disposed by the interpreter;
its host resource must survive destruction of the adapter wrapper as well. Rust
borrowed adapters must not close the underlying resource from `Drop`; use host-owned
shared storage rather than a wrapper whose destruction owns the host connection.
Connectors retain cleanup responsibility for partial acquisition/open failure.
Native cancellation must stop all resource access before completing/canceling;
uninterruptible work belongs on the worker thread.

## External tooling and tests

The installable Python package `instrument-driver-tools==0.2.0a1` includes the
exact versioned profile schema and semantic operation catalog. Generate static
Rust/C# profiles and typed test scenarios from any directory, without a main-repo
checkout, runtime JSON parsing or reflection activation:

```sh
python -m pip install ./tools/driver-tools
instrument-driver generate --input profiles.json --output-dir generated --contract-major 1
instrument-driver generate-tests --input cases.json --output-dir generated
# CI runs the same commands with --check (read-only drift detection).
```

This initial installation command is for this checkout. External repositories
install the checksummed wheel attached to the SDK preview release; no PyPI release
is assumed. Both `generate` commands accept `--module` / `--namespace`. An optional
`--schema` must match the installed release exactly. Malformed versions, names,
placeholders, roles, inventories and injection tokens fail before file emission.
The CLI rejects duplicate JSON properties, non-JSON numeric constants, symlink
outputs and overwriting handwritten code. Generated contracts in the main SDK
come from the operation manifest using the `contracts` subcommand and rustfmt.

The TestKit packages supply strict scripted message IO, thread/lifetime observation
and typed catalog scenarios. Unexpected traffic fails even if the backend catches
an error; assert complete consumption. They are development dependencies only.
The 139 shared SCPI cases assert typed replies, units, errors and exact traffic for
all 83 operations across Sync, NativeAsync, WorkerAsync and worker blocking
frontends in both languages. Separate tests cover non-SCPI catalog backends,
multistep serialization, profile rejection, recovery, partial shutdown, borrowed
IO and native cancellation. The original 43 synchronous SDK cases and 14 async
cases remain, including worker cancellation/affinity, abandoned opening, reentry,
close ownership and Rust executor interruption. Synthetic tests establish no
physical-device compatibility claim.

```sh
cargo test -p instrument-runtime --features async
cargo test --manifest-path validation/driver-sdk/rust-tests/Cargo.toml --features async
cargo test --manifest-path validation/scpi/rust-tests/Cargo.toml --features async
dotnet test dotnet/tests/InstrumentComponents.Runtime.Tests -c Release
python -m unittest discover -s tools/driver-tools/tests
python tools/validate-driver-sdk.py --artifacts /tmp/sdk-net8 --framework net8.0 --isolate-runtime
python tools/validate-driver-sdk.py --artifacts /tmp/sdk-net10 --framework net10.0 --isolate-runtime
```

The package proof requires Python 3.11+, Rust stable, .NET 8+10 SDKs and Native AOT
prerequisites on the native x64 host. Windows uses Visual Studio C++ build tools;
Linux uses clang/zlib (or `--cpp-compiler gcc`). Linux isolation requires bubblewrap
namespaces, which restricted sandboxes may need permission to create. Hosted CI
passes `--isolation-sudo` to elevate only this isolation step under its namespace
policy; package builds and ordinary execution remain unprivileged. Artifacts
must be outside the checkout in an empty directory. No device I/O occurs.

The harness builds/installs a wheel into a fresh venv, generates consumers from
copied JSON, packs the four SDK crates/NuGets plus unpublished synthetic fixture,
and builds outside the checkout. Cargo uses checksummed package archives/offline
registry; NuGet permits SDK IDs from the local feed only and framework packages
from nuget.org. Consumers use package dependencies only, with fresh caches.
Rust default and optional async consumers assert binary and SCPI behavior,
including strongly typed views for all eight classes. A separate clean core build
removes TestKit/fixture packages from the available feed and checks the closure;
the default Rust closure also excludes Tokio. This proves the new SDK can build
without external implementations, not that the old facade has been cut over.

.NET package consumers disable reflection JSON and publish Native AOT with all
trimming/AOT warnings treated as errors. An intentional actual IL2026 failure
proves the warning gate is active. The native consumer executes its assertions.
Linux isolation hides source, SDK/shared runtimes and network. Windows verifies
native x64 PE with no CLR header and runs on the native host. Logs, exact commands,
exit codes, package/dependency inventories and binary hashes are recorded.
`--framework` selects net8.0 or net10.0; `--isolate-runtime` is Linux-only.
Source inspection or cross-publishing does not substitute for native execution.

`.github/workflows/driver-sdk.yml` runs the Linux/Windows × net8/net10 native
matrix and is called by PR CI and preview release CI. `driver-package.yml` is the
reusable external-repo gate: pin its main-repo commit, pass a checksummed released
wheel URL and caller-relative manifests, test projects and native consumer. It
checks out only the caller repo, requires SDK package dependencies and executes
both native platforms/TFMs. Driver consumers must assert values and fail nonzero.
Caller-owned production packages must not depend on TestKit. All runtime/vendor
SDK deployment prerequisites remain the external repository's responsibility.

## Migration status

Implemented in this checkout: independently versioned preview contracts/runtime,
eight typed classes, standards-only explicit-profile SCPI interpreter, generator,
schema, TestKit, all-mode synthetic tests, portable package/AOT proof and release
assets. The release manifest is `spec/driver-sdk-release.json`. The completed
`0.2.0-alpha.1` public release and consumer execution are recorded in
[`spec/public-release-evidence.json`](../spec/public-release-evidence.json).

Implemented across five external repositories: all source model/USB hints, source-derived operation profiles, endpoint-specific inventory bindings and independently specified wire cases. The main facade/discovery/OpenTAP consume registered neutral handles; vendor tables, device exceptions and legacy device fixtures have moved out of main production sources and artifacts. See `spec/device-extraction-index.json` for ownership and disposition counts.

Local packed native proofs and hosted Windows/Linux .NET8/10 SDK and independent driver proofs are recorded by source revision. All-five composition, public registry setup/publication, and physical bench evidence are distinct gates; consult the current evidence record rather than treating a workflow definition as a passing run. No physical hardware validation has been performed. See [delivery sequence and inputs](device-package-delivery.md).

### Explicit argument choices and physical inventories

A profile may supply up to sixteen nonrecursive `choices` for an operation with
optional numeric arguments. Each choice declares a `presence` predicate and its
own steps, tokens, defaults and optional physical result unit. Predicates must be
disjoint. Every selected branch must consume all required and present arguments;
an absent placeholder needs an explicit default. An uncovered combination returns
`UnsupportedOperation` before IO. These are supplied wire plans, with no generic
command fallback in the interpreter. The CLI and both language constructors
validate the same constraints. `validation/scpi/choice-profiles.json` and the
shared choice/invalid-case files exercise this contract through all four frontends.

Channel IDs describe supported request addresses. They do not establish the
complete physical output inventory. `InventoryBinding` reconstructs a validated,
immutable profile before a factory opens any IO. Unknown outputs (`null`/`None`)
disable metadata channel count and checked shutdown. An explicit empty output
set describes outputless classes; it contradicts PSU or generator output
capabilities. A complete output list enables channel count for supplies and
checked shutdown only when every declared output can be disabled. Supply
requests outside that declared list fail before IO, even if their channel occurs
in the broader supported-channel hints. Binding snapshots never mutate an
already-created profile or an open handle.

Shutdown first attempts every declared output disable, then every explicit
literal shutdown step, collecting failures. Driver-owned stop, open-all and
continuous-sweep-off actions can coexist with output disables. Core shutdown
never implies reset. An application requiring reset invokes it separately and
handles its result separately.

Temperature has a result dimension independent of its physical unit. Without an
explicit unit declaration a temperature measurement returns `Unknown`, and that
unit propagates to subsequent DMM Read/Fetch. Explicit Celsius or Kelvin bindings
are possible when the caller has evidence for those units. Reset, failed IO and
successful recovery after a fault invalidate configured unit state. Unknown
physical units remain distinct from an unconfigured function. No temperature
configuration command is inferred by the SDK.

### Development bootstrap before registry publication

`driver-sdk.yml` can produce release-form SDK archives and the tool wheel from an
explicit source repository and commit. The ordinary Cargo dependency archive is
separate from the SDK and driver packages. `instrument-driver prepare-feeds
--artifacts <downloaded-artifact> --output <empty-directory>` verifies every
required archive against `packages.json` before preparing fresh local Cargo and
NuGet feeds. The result records `pinned-sdk-artifact-bootstrap` and exact hashes.
The caller must obtain the artifact from its pinned trusted workflow execution;
checksums alone do not authenticate an untrusted producer. These checks establish
package composition and native execution, not public-registry availability.
Production device manifests still declare exact versioned registry dependencies;
no sibling paths, copied SDK binaries or main-repository checkout enter their
consumer builds. Registry publication remains a separate release gate.

## Current application boundary

The facade and OpenTAP now consume explicitly registered protocol-neutral handles. The main model registry is empty; device hints, templates and exceptions live in five external repositories. There are no compatibility constructors or raw session escape adapters. All class views of a multifunction device share one handle. See `spec/device-extraction-index.json` for the full source-to-case ownership ledger and `docs-site/docs/getting-started.md` for the new API.

SCPI factories now accept immutable endpoint-specific profiles under one canonical ID. They select the endpoint profile and reject missing required operations before connector IO. Supported channels and complete outputs remain distinct. External `ConnectionBinding` wrappers apply model-owned token and shutdown plans; the shared interpreter knows no model names.

Publisher provisioning automation is in `tools/publisher-setup`. Policy setup, first publication, public-registry consumer validation, and physical evidence remain separate gates. The helper does not publish packages.

Channel-bearing plans require a nonempty explicit supported-channel inventory; a known-empty set cannot advertise an operation with no valid request. Empty channel sets remain valid for non-channel DMM operations and metadata-only ChannelCount with a complete empty output inventory. This is checked consistently by the Python tool and Rust/C# constructors before connector IO.

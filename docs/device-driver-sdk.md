# Experimental device driver SDK

The additive SDK implements synchronous, native async and dedicated worker
device composition in Rust and .NET. It is independent of the existing `instrument-core` /
`InstrumentComponents` implementation and does not change existing applications.
The API is experimental; it is not a published stable contract.

| Role | Rust | .NET |
|---|---|---|
| Public contracts | `instrument-api` | `InstrumentComponents.Abstractions` |
| Selection and shared handles | `instrument-runtime` | `InstrumentComponents.Runtime` |
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

| Capability | Sync | Native async | Worker sync/async |
|---|---|---|---|
| DMM DC voltage, in volts with explicit status | Implemented | Implemented | Implemented |
| PSU voltage/read/output, positive one-based channels | Implemented | Implemented | Implemented |
| Optional explicit internal/external remote sense | Implemented contract | Implemented contract | Implemented contract |
| Optional reset, recovery and safe shutdown | Implemented contract | Implemented contract | Implemented contract |

The three binary fixtures implement DMM/PSU/reset/shutdown and deliberately omit
remote sense. The original fixture supports Sync; the native fixture supports
NativeAsync; the local SDK fixture supports WorkerAsync with explicit sync and
async frontends. Unsupported modes fail before factory opening or I/O. No SCPI
implementation package is supplied yet.

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

Typed DMM/PSU views share one handle and lock for each complete backend operation,
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
For native/worker handles, repeated close shares the same result and the
first close fixes shutdown policy; a later stronger request reports Shutdown
instead of claiming that shutdown occurred. Provider shutdown must attempt all
applicable outputs and fail on unknown/partial coverage. Borrowed host I/O must survive backend close.
Ordinary disposal/Drop releases resources without issuing implicit output/reset
commands; use explicit checked close for shutdown outcomes.

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

## Shared tests and package proof

Both suites preserve all 43 `spec/driver-contract-cases.json` scenarios
(schema version 1), asserting
selection, values, errors, unsupported modes, quarantine/recovery and lifecycle
counts. Language-specific adversarial tests cover capability validation, concurrent
views, queued operations versus close, simultaneous close callers and backend
reentry. The synthetic PSU models enabled output states: shutdown attempts both
known outputs after a failure, and reports unknown inventory as failure. The fixture uses a strict two-message binary
measurement; a generic SCPI fallback cannot satisfy it.

The additional `spec/driver-async-cases.json` runs 14 shared scenarios in each
native async, worker async and worker sync cell in both languages.
Language-specific controlled tests cover
queued/running cancellation, abandoned/undelivered open, close waiter cancellation,
first-close policy, reentry, worker affinity and native executor interruption.
Rust runtime unit tests pause the final unlock to verify interrupted-close
cleanup cannot lose ownership, and pause worker recovery before dispatch to
verify cancellation prevents provider I/O. The converse test proves dispatched
recovery stays owned until it returns. .NET tests cover queued/running recovery
cancellation, task abandonment and worker finalization, whose semantics differ from dropping a Rust future.

```sh
cargo test -p instrument-runtime --features async
cargo test --manifest-path validation/driver-sdk/rust-tests/Cargo.toml --features async
cargo test --manifest-path validation/driver-sdk/rust-tests/Cargo.toml
dotnet test dotnet/tests/InstrumentComponents.Runtime.Tests -c Release
python tools/validate-driver-sdk.py --artifacts /tmp/driver-sdk-proof --isolate-runtime
```

The proof requires Python 3.11+, Rust, .NET 8+, Linux x64 Native AOT prerequisites
and bubblewrap. The artifact directory must be empty and outside the checkout.
If necessary, pass `--cpp-compiler gcc`; the default uses the SDK's compiler choice.
Restricted sandboxes may need permission for bubblewrap namespaces. Omitting
`--isolate-runtime` explicitly records that isolation was not tested.

The tool packs the new SDK and synthetic driver, removes source path dependencies
from staged Rust package manifests, uses a checksummed Cargo directory registry,
vendors ordinary released dependencies, and builds consumers outside the workspace
with fresh Cargo/NuGet caches. Rust runs both default and optional async modes. The
.NET consumer uses only PackageReferences. It publishes and runs `net8.0` /
`linux-x64` Native AOT, with reflection-based JSON disabled and trimming/AOT warnings
as errors. Its assertions cover binary values/traffic, PSU behavior, unsupported
operations/modes, recovery, shutdown and resource lifetime. A separate Rust core-only
consumer checks that its package closure excludes the synthetic driver and legacy
packages. SDK package inventories and dependency checks accompany the logs.

The `--isolate-runtime` execution hides installed shared .NET runtimes, SDK trees,
source and network, keeping only published files and required OS libraries visible.
These checks establish this sync/native/worker synthetic Linux proof only, not hardware,
Windows/macOS, vendor SDK deployment or OpenTAP host AOT compatibility.

## Migration status and next work

- **Implemented:** standalone contracts/runtime, sync/native/worker operations, passive
  selection, cancellation/ownership and affine SDK cleanup, shared contracts,
  isolated package consumers and executable Native AOT validation tooling.
- **Pending:** full SCPI implementation package,
  reusable profile generators/schemas/TestKit/external workflows, additional
  synthetic SCPI fixtures, broader scenario coverage and targets.
- **Pending:** integrating new handles with the existing facade/discovery/OpenTAP
  and extracting vendor profiles, model hints, state and exceptions.

Existing legacy core/facade artifacts still bundle their current device support.
The additive SDK does not yet accomplish the final repository-wide separation.
No package versions are bumped or published. The existing hardware prerequisite
for the breaking release remains in force; see [roadmap](roadmap.md).

No external repository is needed for this milestone. Before extracting the first
real family (proposed pilot: N6705C), notify the user that a separate family-driver
repository is needed. Do not create or publish it implicitly. Extraction must move
behavior, profiles, identification hints and fixtures together and retain explicit
registration in application composition.

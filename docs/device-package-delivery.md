# Device package delivery

## Decisions and current boundary

The November 2026 implementation direction supersedes the earlier hardware gate
on preview releases. Support all 47 existing manufacturer/model hint rows and the
USB hint at initial device-package cutover, across all eight instrument classes.
Require native Linux x64 and Windows x64 with Rust and .NET 8/10 Native AOT.
Constructor/source compatibility is not required. Publish tested early previews
before a physical bench is available; record hardware evidence separately before
claiming a model is hardware validated. Neither old nor new API has that evidence.

The new API/runtime/SCPI packages are vendor-neutral and package independently at
`0.2.0-alpha.1`. Applications reference chosen device packages and register their
factories statically at build time. Core builds and artifacts never require a
driver package. Non-SCPI backends implement typed capabilities directly; SCPI
drivers supply explicit profiles to the separate standards interpreter. Device
exceptions belong to those drivers, never shared core conditionals.

The new typed API has 83 operations across DMM, PSU, function generator, scope,
switch, counter, power meter and spectrum analyzer, including identity/reset/
shutdown. `spec/device-migration-inventory.json` mechanically reconciles 458
Rust/C# sync/async public symbols, every existing model/profile, legacy generic
resolution, units, non-table behavior and OpenTAP steps. Removed constructors,
raw session access, raw dialect writers and structural helpers have explicit
dispositions. No model hint has been silently upgraded into a hardware claim.

This is a pre-extraction milestone. Existing facade/core/VISA/OpenTAP packages
still use their legacy device data and shared classes. They must not be described
as having completed the new separation. SDK package conformance does not validate
OpenTAP's host for Native AOT, a proprietary SDK, VISA deployment or hardware.

## Maintainer inputs needed

Create these repositories under the proposed owner `josh-hemphill`, or provide
replacement owner/URLs. Public visibility fits the current open source profiles;
future proprietary implementations require separate private repositories.

| Proposed repository | Existing model hints | Existing vendor profiles |
|---|---:|---|
| `instrument-components-keysight` | 27 (includes Agilent) | N6705C, sensor, X-Series |
| `instrument-components-keithley` | 6 | DMM6500 |
| `instrument-components-rigol` | 6 | DSA |
| `instrument-components-siglent` | 4 | Legacy explicit generic resolution |
| `instrument-components-tektronix` | 4 | Legacy explicit generic resolution |

These groups may contain independently versioned family packages. Separate repos
per model are unnecessary. All groups remain release scope; using N6705C and
DMM6500 as first conversions only orders the work.

Provide push/CI access, select the existing project's license for copied source,
and reserve Cargo/NuGet package IDs for each family before publication. Native
vendor runtimes or licensed SDK downloads, when required, need their own deployment
and licensing inputs. These are specific future inputs, not prerequisites for
the neutral SDK or local synthetic tests.

For the **main SDK preview**, configure trusted publishers for:

| Registry | Package IDs | Trusted publisher |
|---|---|---|
| crates.io | `instrument-api`, `instrument-runtime`, `instrument-scpi`, `instrument-testkit` | `josh-hemphill/instrument-components`, workflow `driver-sdk-preview.yml`, no environment |
| NuGet | `InstrumentComponents.Abstractions`, `.Runtime`, `.Scpi`, `.TestKit` | Same repository/workflow; NuGet profile `josh-hemphill`; permit new IDs and versions |

Existing `release.yml` publisher policies do not authorize a differently named
workflow automatically. Reserve these IDs and resolve any ownership collision
before tagging. The versioned Python wheel and SHA256SUMS attach to the GitHub
prerelease; PyPI setup is not required. Actual registry publication and preview
tag creation remain maintainer actions, not consequences of local validation.

## Concrete handoff

```sh
python -m pip install ./tools/driver-tools
instrument-driver inventory --repository . --output spec/device-migration-inventory.json --check
python tools/export-driver-handoff.py --output /tmp/device-package-handoff
```

The export refuses stale inventory and nonempty destinations. It contains one
scoped `handoff.json` and conversion checklist per repository, the full operation/
OpenTAP ledger, source-reference legacy code and transcripts, and a SHA256 manifest.
Source-reference material is an audit input, never an SDK runtime resource or
dependency. Legacy TOML/vendor JSON is **not** already valid new-profile input;
convert it to the released strict schema and verify behavior explicitly.

Important conversion obligations include:

- N6705 boolean remote sense remains unsupported. Advertise Internal/External
  only with explicit vendor commands and evidence; do not restore its invalid
  ON/OFF fallback.
- DMM6500 ranged measurement currently reaches a legacy generic fallback.
  Implement explicit correct ranged plans or reject that operation/argument
  before I/O; there is no fallback in the new interpreter.
- Preserve configured DMM/power units, reset/recovery invalidation, channel/output
  inventories and all-output shutdown. Keep reset distinct from safe shutdown.
- Convert legacy generic commands into **explicit driver-owned input**, with
  exact traffic/value tests. A generic profile match does not establish a real
  device's complete capability set. Preserve identity hints as hints.
- Preserve the existing ASCII scope capture contract. Binary scope capture and
  new hardware claims require separate work and evidence.

## Remaining delivery sequence and gates

1. **SDK preview readiness.** Complete the source, lint, shared-case and packed
   consumer gates. Execute native Linux/Windows × net8/net10 package-only AOT,
   strict warning/negative warning gates and Linux runtime isolation. Preserve
   exact logs, source/artifact hashes and any concrete external access blocker.
   A reviewed workflow is not Windows execution evidence.
2. **Publish SDK preview.** Configure the trusted publishers above, then create
   `driver-sdk-v0.2.0-alpha.1` from the reviewed commit. The preview workflow
   validates versions, reruns all four native matrix cells and only then publishes
   the four crates/NuGets in dependency order and attaches the tooling wheel.
   It excludes legacy facade/VISA/OpenTAP and all synthetic driver packages.
   Hardware evidence is not a preview publication gate. A failed required native
   platform check prevents publication.
3. **Extract every vendor group.** Create the repositories, import their handoff,
   implement explicit profile/native backends and exported factories, and port
   transcripts. Use SDK packages and released tooling without the main checkout.
   Require per-family operation/identity/output/recovery/error/cancellation tests,
   Rust default+async and C# parity. A capability omitted because legacy support
   was only a hint must have an explicit disposition, not disappear unnoticed.
4. **Independent package proof.** Pin reusable `driver-package.yml` to a reviewed
   SDK commit. Supply the exact wheel URL/hash and caller-local manifests/tests/
   package-only AOT consumer. Run fresh native Linux/Windows × net8/net10 gates.
   Publish independently versioned early family packages after publisher setup.
5. **Application cutover.** Refactor facade/discovery/OpenTAP composition to pass
   selected external factories. Discovery supplies passive identity evidence and
   explicit host adapters; typed session behavior remains protocol-neutral.
   Hardware identity/probing is opt-in driver/host policy. Replace legacy public
   constructors where needed. Keep OpenTAP step behavior/value tests, mapping all
   current steps through the operation ledger. Keep transport/VISA optional.
6. **Prove separation and release.** Remove every vendor profile/model row/
   special-case branch from main production packages after its external owner
   passes parity. Build/package the main SDK/core/facade with external driver
   packages unavailable; inspect package closure/resources. Separately compose
   apps with all chosen drivers, asserting all current models/class operations,
   static registration and Native AOT on both platforms. Publish early application
   and device previews with precise unverified-hardware status.
7. **Bench evidence.** On available equipment, record model/serial/firmware,
   package versions, platform, connection/runtime versions, commands/results and
   pass/fail artifacts. Add hardware evidence per feature/model. Reliable mock/
   transcript/AOT proofs come first; they cannot substitute for physical evidence.

The concrete blockers for steps 2–4 are package-ID ownership/trusted publishing
and the five external repository URLs/access. Steps 5–6 need real independent
device package versions to prove the final composition. Implementing neutral
contracts, profiles, generators and tests does not depend on those repositories.
Do not mark the full migration, a platform execution or publication complete from
preparatory source alone.

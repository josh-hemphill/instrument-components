# Shared dual-native contracts

These files are the executable contract between the Rust and C# implementations. Both languages load the same JSON in tests. Do not encode the same rule twice in language-specific fixtures.

| File | Role |
|---|---|
| `scpi-vectors.json` | Dialect resolution, vendor overrides, and generic command strings |
| `classifier-cases.json` | Address / `*IDN?` / merge / override → kinds |
| `driver-contract-cases.json` | Experimental SDK v1 passive matching, synchronous values/errors, unsupported modes and ownership/lifecycle cases |
| `driver-operations.json` | Closed typed eight-class SDK operation IDL: requests, units, constraints and deliberate breaking removals |
| `driver-scpi-cases.json` | 139 shared exact-traffic/value/error cases, emitted as static Rust/C# TestKit inputs |
| `driver-profile-token-cases.json` | Shared direct/generated semantic-domain and shutdown-profile acceptance/rejection cases |
| `device-repositories.json` | Proposed external repository ownership and explicit exception/state conversion obligations |
| `device-migration-inventory.json` | Generated all-device/source/profile/public-symbol/OpenTAP preservation ledger with hashes |
| `driver-sdk-release.json` | Independent preview versions, package IDs and both native platform/framework publication gates |
| `generic-scpi-map.json` | Maps `generic_*` dialect keys to `scpi_commands.toml` (codegen check) |
| `transcript.schema.json` | Schema for `fixtures/*.json` I/O transcripts |
| `vendors.schema.json` | Schema for `vendors/*.json` hardware dialect profiles |
| `vendors/*.json` | Real vendor SCPI dialects (merged before `generic_*` by `gen-dialects.ts`) |
| `opentap-operations.json` | Curated OpenTAP step catalog (stable `stepTypeName` values are TapPlan contract) |
| `opentap-operations.schema.json` | Schema for `opentap-operations.json` |

## Rules

1. Edit TOML under `crates/instrument-core/data/` or JSON under `spec/vendors/`, then regenerate. Never hand-edit generated files.
2. New typed-class methods that emit SCPI need a row in `scpi-vectors.json` (or a shared transcript that asserts the value).
3. Transcripts under `fixtures/` must drive a typed-class action and assert **values**, not only step counts.
4. Do not attach new vendor globs to models used as generic in mock catalog (`34461A`, `E36312A`).
5. `tools/gen-dialects.ts` rejects vendor JSON that does not match `vendors.schema.json` (id pattern, extra properties, empty commands). Optional `notes` are documentation only and are not generated into dialect tables.

```bash
node tools/gen-shared-tables.ts
node tools/gen-dialects.ts
node dotnet/tools/gen-registry.ts
node tools/validate-opentap-operations.ts
node tools/gen-opentap-steps.ts
```

`driver-async-cases.json` extends the experimental driver SDK with a binding
sync/native/worker matrix, 14 shared native/worker value/error/lifecycle cases,
and named language-specific cancellation/ownership scenarios. The original 43
`driver-contract-cases.json` cases remain authoritative for the sync API. See
[SDK guide](../docs/device-driver-sdk.md) for executor and cancellation differences.

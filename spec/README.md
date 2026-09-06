# Shared dual-native contracts

These files are the executable contract between the Rust and C# implementations. Both languages load the same JSON in tests. Do not encode the same rule twice in language-specific fixtures.

| File | Role |
|---|---|
| `scpi-vectors.json` | Dialect resolution, vendor overrides, and generic command strings |
| `classifier-cases.json` | Address / `*IDN?` / merge / override → kinds |
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
deno run --allow-read --allow-write --allow-run=rustfmt tools/gen-shared-tables.ts
deno run --allow-read --allow-write --allow-run=rustfmt tools/gen-dialects.ts
deno run --allow-read --allow-write dotnet/tools/gen-registry.ts
deno run --allow-read tools/validate-opentap-operations.ts
deno run --allow-read --allow-write tools/gen-opentap-steps.ts
```

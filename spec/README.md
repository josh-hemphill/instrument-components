# Shared specifications

`driver-operations.json` defines the protocol-neutral catalog and Rust/C# generation. `driver-*-cases.json` define neutral, async and explicit synthetic SCPI interpreter fixtures. `opentap-operations.json` maps74 steps to neutral operations. These contain no built-in real device implementations.

`device-extraction-index.json` preserves source hashes and ownership/dispositions for47 hints+USB,48 model-kind rows,13TOML+2vendorJSON sources,83 operations,458 symbols and74 OpenTAP steps. `device-repositories.json` records the actual external repositories. Original literal device templates, fixtures, legacy source and conversion tooling are preserved in external provenance, excluded from production package archives.

`driver-sdk-release.json` records SDK preview/release policy. `publishers.json` lists exact crates and NuGet scopes for the batch setup helper. None of these manifests alone proves publication, hardware behavior or a passing CI run.

`public-release-evidence.json` records the completed `0.2.0-alpha.1` publication,
public registry archive hashes and source commits, release run/job results,
public package consumers and all-five native composition. It is separate from
the historical `device-execution-evidence.json` artifact-bootstrap record.
Hardware validation remains unperformed. New Rust publisher policies are
configured and read back; their first OIDC publication awaits the next version.

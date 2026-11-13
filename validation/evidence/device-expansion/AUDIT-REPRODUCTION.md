# Reproduce the archive audit

Use Python3.11+ and check out the exact source revisions in github-run.json. Download each artifact ID in github-artifacts.json through the GitHub UI/CLI/connector, retaining the original ZIP bytes and names. Do not rezip extracted directories: verification uses GitHub's original ZIP digest.

For the neutral SDK producer or a vendor run's four SDK artifacts:

```sh
python audit-sdk-artifacts.py /path/to/zips --core-repository /path/to/instrument-components
```

For vendor generated/source and packed consumers, keep all four driver-proof and four matching driver-sdk ZIPs in the same directory:

```sh
python audit-native-artifacts.py /path/to/zips --cases 1859 --family keysight --driver-repository /path/to/instrument-components-keysight
# For Tektronix: --cases 452 --family tektronix --driver-repository /path/to/instrument-components-tektronix
```

SDK and driver source/dependencies, native PE/ELF x64/CLR absence, phase exit/log hashes and intentional IL2026+IL3050 error probes are inspected. The driver audit also compares all packed Rust src/generated-profile bytes with the selected source checkout and joins the twelve exact consumer SDK/feed hashes to the matching fourteen-entry producer inventory. The two excluded producer entries are its test-only synthetic providers. Public registries, new package publication and physical performance are outside these development-feed audits.

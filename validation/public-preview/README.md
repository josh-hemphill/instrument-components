# Public preview example

This is a hardware-free application targeting the coordinated `0.3.0-alpha.1` packages (check release availability before restoring). Download this directory, then run:

```sh
cargo run --manifest-path rust/Cargo.toml --release
cargo run --manifest-path rust/Cargo.toml --release --features async
dotnet run --project dotnet/Preview.csproj -c Release
```

The Rust projects need Rust 1.85 or newer. The C# project targets .NET 8; use `-p:TargetFramework=net10.0` with .NET 10. Native AOT additionally needs the platform C++ toolchain. The fixture requires no VISA runtime, hardware, registry credentials or sibling source repositories.

The programs explicitly register the published Keysight factory and use the development-only TestKit connector. They check passive selection, rejection of unsupported requirements before opening, a measurement of **1.25 Volt**, one connection/close, and sync/native-async/worker-async/worker-blocking execution. The values are scripted test inputs, not evidence from a physical instrument. TestKit does not belong in your production transport implementation.

The independent main-repository check copies these projects into an empty output directory, restores public Cargo/NuGet packages into new caches, runs Rust and Clippy, publishes and executes Native AOT, and records exact package/native/log hashes:

```sh
python tools/validate-public-preview.py --output /tmp/instrument-preview --framework net8.0
```

Run that command from the main checkout. Choose a new output directory for each run; on Windows use a path such as `C:\Temp\instrument-preview`. `.github/workflows/public-preview.yml` tests Linux/Windows x64 and .NET 8/10 separately from normal core builds. No external driver implementation is required by ordinary core builds or bundled in core artifacts.

The 0.3 example uses the explicit commissioned 34401A factory; see the getting-started upgrade instructions. `--feeds` is candidate development evidence; default validation uses fresh public packages after publication.

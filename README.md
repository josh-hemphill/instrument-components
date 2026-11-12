# Instrument Components

Rust and C# device contracts, serialized runtimes, standards tooling, and OpenTAP integration. Applications explicitly register independently packaged device implementations at build time. Core packages contain no device families and do not discover or activate plugins implicitly.

The **0.2.0-alpha.1 API is a breaking preview**. Source-derived external drivers have software transcript coverage; physical bench validation is pending. Earlier 0.1 examples and constructors are retired.

- [0.2 preview getting started](docs-site/docs/getting-started.md)
- [Public-package mock example](validation/public-preview/README.md)
- [Published documentation site](https://josh-hemphill.github.io/instrument-components/) (currently 0.1; use the preview guide above for this release)
- [Architecture and delivery](docs/device-driver-sdk.md)
- [Device package delivery status](docs/device-package-delivery.md)
- [Public alpha release evidence](spec/public-release-evidence.json)
- [Publisher setup automation](tools/publisher-setup/README.md)

Build without external device repositories:

```sh
cargo test --workspace --exclude instrument-visa --no-default-features
dotnet test dotnet/InstrumentComponents.sln -c Release
```

`instrument-visa` is an optional standards transport requiring a VISA installation when linked. No device packages, credentials, or hardware are needed for the software tests. Native package validation is `python tools/validate-driver-sdk.py`; it builds and executes fresh registry-form package consumers on the current x64 host. CI runs Windows/Linux with .NET 8 and 10. See the documentation for the distinction between artifact bootstrap, public registry validation, and hardware evidence.

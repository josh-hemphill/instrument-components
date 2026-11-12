# Discovery and registration

A host enumerator produces passive endpoint/identity evidence. Construct `DeviceCatalog` with a `DriverRegistry` and that evidence. Catalog construction and selection perform no IO. Raw VISA resource enumeration remains optional standards tooling and is separate from driver matching.

Each registered factory has a stable canonical ID, supported execution modes and explicit match rules. Exact manufacturer/model and USB vendor/product rules are supplied by external packages. Equal-ranked conflicting drivers fail with `AmbiguousDriver`; explicit driver selection is available. No unregistered default implementation is selected. A standard-SCPI fallback requires explicit selection permission.

Capabilities are operation-level. The factory descriptor can describe possible operations; the opened backend returns the immutable operations supported by that connection. Required operations are checked before connector open for SCPI profiles, then verified against the opened backend by the neutral runtime. Typed views share the same backend and cannot add capabilities.

## Inventories

Supported channel IDs and complete physical output inventory are distinct. A legacy one-channel hint is not proof of complete outputs. ChannelCount and checked Shutdown are available only when metadata and disable coverage guarantee them. Unknown output inventories do not advertise Shutdown. Closing without requesting Shutdown only closes the owned connection.

External package registration accepts model defaults and endpoint-specific connection bindings. Use `ConnectionBinding` with the canonical driver ID, endpoint and immutable `InventoryBinding`; C# passes `connections:` to `Drivers.Factories` / `NativeFactories`; Rust uses `sync_factories_for_connections`, `native_factories_for_connections` or `combined_factories_for_connections`. Endpoint scheme comparison is ASCII case-insensitive after trimming; addresses are exact. Duplicate normalized endpoints and unknown driver IDs are rejected before IO. One factory may serve two connections to the same model with different complete output sets.

Complete output inventories drive explicit device-owned disable plans. Shutdown attempts every known output and aggregates failures. Reset is a separate requested operation. There is no implicit reset, probe, reconnect or recovery retry. Do not enable outputs to discover their inventory.

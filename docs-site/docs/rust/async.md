# Async execution

Rust enables `async` on the SDK/facade and selected device packages. C# exposes the equivalent frontends without a feature switch.

| Frontend | Rust | C# |
|---|---|---|
| Synchronous | DeviceHandle | DeviceHandle |
| Native async transport | NativeDeviceHandle | NativeDeviceHandle |
| Dedicated worker, async caller | WorkerDeviceHandle | WorkerDeviceHandle |
| Dedicated worker, blocking caller | same worker handle | same worker handle |

Call `open_native` / `OpenNativeAsync` only when a factory supplies genuine async IO. `open_worker` / `OpenWorkerAsync` runs a sync transport on a dedicated worker with thread affinity; it does not turn VISA calls into native async. The combined SCPI factory can register both connectors under one canonical ID.

The runtime serializes one complete logical operation, including multi-command plans. Class views share configuration and unit state. A canceled operation that may have started IO leaves recovery responsibility explicit; queued cancellation must not issue commands. No hidden retry, reconnect or reset is performed. Use the SDK recovery contract at a confirmed clean boundary; recovery and reset invalidate cached configured units.

Close the handle explicitly and await async close. Owned connections are disposed once; borrowed connections stay with the application. Request checked Shutdown only when complete output inventory is known. A closed/closing handle rejects subsequent calls. An operation-level timeout is distinct from caller cancellation; connectors must translate their transport failures into the shared error kinds.

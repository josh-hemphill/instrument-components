# C# VISA async — status and APM spike

## Supported model today (go)

[`VisaAsyncTransport`](../dotnet/src/InstrumentComponents.Visa/VisaAsyncTransport.cs) wraps sync `VisaTransport` in `SyncAsAsyncTransport`. That means:

- `WriteAsync` / `ReadAsync` run blocking VISA I/O on the thread pool.
- `CancellationToken` cancels queued bridge work before it starts; it does **not** cancel an in-flight native VISA call the way true APM would.
- This is intentional until vendor APM proves reliable across Keysight and NI on Windows **and** Linux.

Document this limitation in consumer-facing docs; do not advertise “true async VISA I/O” for the C# package yet.

## APM spike (bounded research)

### Goal

Decide whether `IviFoundation.Visa` (8.x, `net6.0+`) exposes a portable Begin/End or Task-based I/O path we can use instead of the sync bridge.

### API surface checked (IviFoundation.Visa 8.0.2)

- Package targets **`net6.0`** (not Windows-only) — aligns with our Phase 4 `net8.0` TFM.
- Formatted I/O / message-based sessions historically expose **APM** (`BeginRead` / `EndRead`, `BeginWrite` / `EndWrite`) on `IMessageBasedSession` in VISA.NET Shared Components.
- Vendor implementations vary: some complete APM on I/O completion threads; others are thin wrappers over sync calls. Linux VISA.NET installs are newer and less field-tested than Windows.

### Spike criteria (must pass before production swap)

1. Cancel mid-read without hanging the process (Windows NI **or** Keysight).
2. Same on a second vendor **or** document single-vendor support explicitly.
3. Linux compile + at least one Linux vendor runtime smoke (if no hardware, criteria 3 stays “blocked — needs self-hosted runner”).
4. No increase in flaky timeouts vs sync bridge on mock-free hardware soak (≥100 queries).

### Recommendation (no-go for production swap now)

| Decision | Rationale |
|---|---|
| **Keep sync bridge as the shipped async path** | Meets `async`/`await` ergonomics for app code without betting on vendor APM variance. |
| **Do not swap production code in this roadmap** | Spike criteria 2–4 are not verified in CI (no VISA on hosted runners). |
| **Revisit when** | Self-hosted Windows+Linux VISA runners exist, or a vendor documents reliable Task-based I/O for .NET 8. |

### Optional follow-up prototype (out of scope for “done”)

If revisiting: add an internal `VisaApmTransport` behind an experimental flag, implement `BeginRead`/`EndRead` via `Task.Factory.FromAsync`, and compare cancel behavior against `SyncAsAsyncTransport` on one Windows install before enabling by default.

## Related

- Rust already has true async via `visa-rs` `InstrumentTokioAdapter`.
- Cross-platform C# VISA **build** is unblocked on `net8.0`; **runtime** still needs a vendor VISA install on the target OS.


Calls are serialized and offloaded to the thread pool. Cancellation stops queued
calls before they start. A running native call finishes under its VISA timeout
before its task completes, so read/write memory remains valid; cancellation does
not interrupt the native driver. Session queries serialize the write/read pair.
Dispose waits for any active call before releasing the native session.

Hardware validation uses the self-hosted INSTR smoke workflow. No passing hardware run is recorded by this review.
SOCKET address parsing is supported, but short LF-terminated SOCKET I/O remains
unverified on vendor stacks; use INSTR resources for the supported hardware path.

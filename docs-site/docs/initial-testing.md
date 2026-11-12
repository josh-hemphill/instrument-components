# Initial preview testing

Start with the [hardware-free public-package example](getting-started.md#run-without-hardware). It restores `0.2.0-alpha.1` packages and checks a scripted measurement of **1.25 Volt**, passive selection, capability rejection before opening, and connection lifetime in the sync and async frontends. No credentials, VISA installation or device connection are needed.

The published alpha passed the five vendor transcript corpora and all-five static application tests on Windows/Linux x64 with .NET 8/10 Native AOT. These tests establish software behavior against the preserved source expectations. None of the device/model hints have physical bench validation yet.

## Issues found after publication

Additional independent reviews found the following edge cases in `0.2.0-alpha.1`:

| Area | Published behavior | Source correction prepared for the next preview |
|---|---|---|
| SCPI byte-stream block headers | Signed/whitespace length fields can be accepted. Negative lengths can escape as an untyped .NET exception; malformed Rust headers can be reported as timeouts. | Require ASCII decimal length digits and preserve typed parse errors in sync/async reads. |
| C# connection ownership | An undefined integer cast to `ConnectionOwnership` can open a provider and unintentionally skip cleanup. | Reject undefined values before invoking providers in registry and SCPI factory entry points. |
| Extreme retry configuration | The largest retry count can overflow the initial-attempt calculation. | Saturate the count in both languages instead of wrapping or panicking. |
| Catalog endpoint identity | C# Unicode case folding can reject distinct opaque schemes or disagree between construction and lookup. | Use one trimmed ASCII scheme key for construction/lookup in both languages, consistent with registration; addresses remain exact. |
| Repeated sync checked close | A later shutdown request or repeated failed checked close can return success without performing shutdown. | Retain the first accepted shutdown intent and outcome, reject strengthening, and replay checked failures while cleaning resources once. |
| Timeout recovery drain | A continuous stream can keep cleanup running after a query timeout; C# async cleanup ignores caller cancellation. | Bound the total drain, restore I/O timeouts, retain cancellation and require successful cleanup before retrying. |
| Built-in message adapter response bound | The VISA and bench adapters wrap byte sessions without the response bound required by the SDK host contract. | Add configurable payload limits in both languages and apply the existing 1 MiB SDK policy in the built-in adapters; oversized headers fail before reading their bodies. |

Normal typed ownership (`Owned` or `Borrowed`) and default retry settings are unaffected by the ownership and retry edge cases. Use the mock example to assess the API; a corrected preview is recommended before broader initial distribution. Do not interpret a passing mock example as coverage of malformed hardware replies.

The fixes and their regression tests are source changes for the **next** preview. Published `0.2.0-alpha.1` archives and tags are immutable and still have these limitations. New package versions and public-consumer validation are required to deliver the fixes.

## Choosing a real connection

Supply identity evidence and a connector explicitly. Package installation and candidate selection never probe or open equipment. Request required operations when opening; inspect actual handle capabilities rather than inferring them from a model name. Use named ownership enum values, and reserve borrowed ownership for host-managed connections.

Channel hints are not a complete output inventory. Bind a reviewed connection-specific inventory before requesting checked all-output shutdown. Closing a resource and safely disabling every physical output are separate operations; dropping or disposing a handle does not imply shutdown or reset.

The optional SCPI message interface requires the host connector to bound responses and establish clean message boundaries. Published alpha.1 byte-stream helpers have no configurable response-byte ceiling. In the prepared source, `MaxResponseBytes` (C#) / `max_response_bytes` (Rust) limits response **payload bytes**, excluding block headers and terminators. The raw helper default is `null` / `None`; zero allows only empty payloads. Built-in VISA and bench message adapters explicitly use the existing 1 MiB SDK limit. Oversized ASCII, indefinite blocks and declared definite blocks produce typed parse failures before payload allocation; they are not truncated or automatically retried. Treat a framing or limit failure as an uncertain boundary and explicitly recover or reopen before more traffic.

Prepared byte-session cleanup has a **100 ms total drain budget** and needs EOF or a **full 50 ms quiet-read timeout** to report success. A timeout on a shortened remaining interval is a failure. Continuous data cannot renew the budget; query retries require successful cleanup. C# async cleanup observes the original cancellation token, and cleanup restores the configured I/O timeout. These bounds assume the host transport honors its timeout and cancellation contract; the session cannot preempt a blocking transport that ignores both.

Before bench work, document firmware, transport/runtime versions, selected package versions, wiring, expected units and acceptance ranges. The separate bench application requires an explicit reviewed configuration and hardware flag. Its self-test and configuration validation perform no hardware IO.

## Reporting results

Include package versions, OS/architecture, .NET or Rust version, selected driver ID, execution frontend, error kind and a minimal reproduction. For hardware results, add firmware and a redacted command/response transcript. Keep credentials and sensitive endpoint details out of reports. Use the corresponding repository's issue tracker; source evidence and physical observations should remain distinguishable.

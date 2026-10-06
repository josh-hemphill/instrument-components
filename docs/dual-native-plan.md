# Dual-native implementation plan

Rust and C# are **first-class native implementations**, not a port. They share
JSON contracts, SCPI fixtures, and CI gates. They do **not** share a runtime.

This is the working plan for remaining work after Streams A–G. Update this file
when a decision changes.

## Current state (implemented A–H)

Merged into `latest` as PRs #5–#8, #10, and #11. Stream G's DMM/PSU transcripts and Stream H's self-hosted hardware workflow are
implemented in this checkout. See [the review resolution](roadmap.md#october-2026-code-review-resolution)
for the current release fixes and outstanding hardware evidence.

| Stream | PR | What landed |
|--------|----|-------------|
| A | #5 | CI gates, crate `0.1`, this plan, release requires `ci.yml` + `dotnet.yml` |
| B | #6 | Session honesty: catalogs inherit `ConnectOptions`, `IoTimeout()`, reconnect diagnostics only on success, C# culture-invariant SCPI, ProbeSystErr, ResetOnConnect, SharedLock fail-closed, VISA dispose |
| C | #7 | `spec/` JSON contracts, dialect-resolved SCPI for PowerMeter + SpectrumAnalyzer only, vendor profiles before `generic_*` |
| D | #8 | C# examples, multi-session test, dual-native docs, Counter timeout + scope binary waveform **deferred** |
| E | #10 | Query retry+flush, honest OPC/ERR probes, Ok(0) fail-closed, framed reads do not reconnect before flush, Rust async Drop restore |
| F | #11 | Dialect emission for DMM/PSU/FGen/scope/switch/counter with fallback; leftover-placeholder + extra-optional-var hardening; CI `TestDialect*` fixtures |
| G | implemented | Golden DMM/PSU transcripts, Keithley DMM6500 + Keysight N6705C vendor JSON dialects |

Dialect emission: PowerMeter and SpectrumAnalyzer **require** dialect keys.
DMM, PSU, function generator, oscilloscope, switch, and counter use
`dialect_io::try_command` / `try_formatted` and C# `DialectCommand.Try`
(dialect first, `scpi_commands` / `ScpiCommands` fallback). Command ids live
in `crates/instrument-core/data/dialects/profiles.toml`,
`spec/vendors/*.json`, and fallback templates in `scpi_commands.toml` (not
`spec/commands.json`).

No hardware evidence. `crates/instrument/tests/hardware.rs` is `#[ignore]`.
GitHub-hosted CI uses MockTransport + transcripts. That is the correct CI
shape (see [Hardware emulators](#hardware-emulators-ni--keysight)).

## Working rules

- Shared fixtures over one-sided tests. If a behavior exists in both
  languages, the test data lives in `spec/` or `fixtures/` and both suites
  consume it.
- Do not expand the instrument registry. Adding classes, growing UniFFI, or
  IVI adapters is out of scope until remaining classes use dialect emission and
  we have hardware evidence for at least one class.
- Generated files are not source of truth. Edit `spec/`, `spec/vendors/`,
  and dialect TOML (and the generator); regenerate; do not hand-edit
  `generated.rs` / `Generated.cs`.
- Do not publish `0.2.0` before hardware evidence.
- Do not add vendor VISA (NI-VISA, Keysight IO Libraries) to GitHub-hosted CI.

## Hardware emulators (NI / Keysight)

**Neither NI nor Keysight offers a CI-loadable VISA/instrument emulator** we
can install on GitHub-hosted Linux runners.

| Offering | What it actually is | Use here? |
|----------|---------------------|-----------|
| NI MAX “simulated devices” | Windows DAQmx / PXI simulation in NI MAX, not SCPI/VISA message-based instruments in a container | No |
| Keysight IO Libraries / Connection Expert | Real VISA runtime (Windows; Linux IOLS exists as a full vendor install). Not an emulator | Self-hosted hardware only |
| Keysight Command Expert / BenchVue | Desktop instrument control, not a headless SCPI mock | No |
| Keysight MCP `mock` provider | Mock for Keysight’s MCP server, not `visa-rs` / `Ivi.Visa` | No |
| PyVISA-sim (`@sim`) | Python-only VISA simulation. Does not back this crate’s native backends | No |
| ivi-cli-mock (third-party Docker) | SCPI over TCP (`TCPIP::localhost::5025::SOCKET` / HiSLIP). Not vendor VISA | Only if we add a core TCP SOCKET transport later |

This library has **no raw TCP `SOCKET` transport**. `TCPIP::host::port::SOCKET`
is an address parse only; I/O goes through the VISA backend. CI therefore
stays on **MockTransport + golden transcripts**. Real NI/Keysight VISA stays
on **self-hosted runners with physical instruments**.

A later optional core TCP SOCKET transport could talk to a local SCPI mock
(ivi-cli-mock or a one-file responder). That tests sockets, **not** vendor
VISA. Do not confuse the two.

## Roadmap (remaining)

```text
latest  (includes F as #11)
  └─ G  DMM + PSU transcripts + vendor profiles
        └─ H  self-hosted hardware smoke (this branch; not GitHub-hosted)
```

G is the stack parent. H does not change GitHub-hosted MockTransport CI.

---

## Stream E — Session I/O honesty

**Goal:** Queries that time out flush stale data and retry. Probes only cache
support when the reply is a real OPC/error response. Zero-byte reads fail
closed. Rust async timeout restore survives cancel/drop. Document that VISA
reconnect is unsupported.

**Does not:** Add TCP SOCKET transport. Change `reconnect_on_failure` default.
Enable `CheckErrors` on typed measures. Implement VISA `Reconnect()`. Dialect
emission for remaining classes (Stream F).

### E.1 Query retry + flush

**Problem:** `query` / `Query` calls `write_with_retry` then a single
`read_response`. `retries` only covers the write. A timed-out read leaves the
instrument’s reply in the buffer. The next query can parse that stale value as
a new reading.

**Behavior:**

1. Attempt = write (with existing write retry) + read.
2. If read returns `Timeout`, call `flush` (drain whatever is there), then
   `try_reconnect` (no-op on VISA; keep the call for future transports).
3. Back off (`retry_backoff * attempt`) and retry the **write+read pair**.
4. After `retries` exhausted, return the last timeout.
5. Non-timeout read errors still fail immediately (same as writes: only
   timeout is retryable).

Apply in:

- Rust `query` / `query_with_timeout` (`crates/instrument-core/src/scpi/session.rs`)
- Rust async `query` / `query_with_timeout` (`async_session.rs`)
- C# `Query` / `QueryWithTimeout` (`ScpiSession.cs`)
- C# `QueryAsync` / `QueryWithTimeoutAsync` (`AsyncScpiSession.cs`)

`write` / `Write` retry behavior is unchanged.

**MockTransport:** add `fail_reads(n)` / `FailReads(n)` — the next `n` `read()`
calls return `Timeout` **without consuming a script step**. That lets a script
keep a stale `Read` for `flush` to drain, then a real `Read` for the retry.

Existing `fail_writes` tests must keep passing (write retry still consumes no
script step on write timeout).

### E.2 Honest OPC / SYST:ERR probes

**Problem:** `probe_opc` / `ProbeOpc` caches `true` on any successful query.
Same for `ProbeSystErr`. SCPI errors as query replies (`-113,"Undefined header"`)
look like success to the session.

**Canonical replies** (shared helpers, both languages):

| Probe | Supported iff trimmed reply is |
|-------|--------------------------------|
| `*OPC?` | `1` or `+1` |
| `SYST:ERR?` | matches `^[+-]?\d+\s*,` (SCPI error queue form `code,message`) |

Anything else (including `-113,"Undefined header"`, empty, `OK`) → not
supported. Cache that result. `WaitComplete` then uses `*WAI` + settle, not a
false OPC.

Put parsers in:

- Rust `crates/instrument-core/src/scpi/protocol.rs`
- C# `dotnet/src/InstrumentComponents/Scpi/ScpiProtocol.cs`

Call them from the probe methods. Unit-test the parsers with the table above
plus the `-113` case.

### E.3 `Ok(0)` fail-closed

**Problem:** `read_framed_response` (Rust) and `ReadResponse` (C#) treat a
successful 0-byte read as “not done yet” and sleep 1 ms. That can spin until
the I/O timeout. VISA transports already map 0 bytes to closed.

**Behavior:** A successful 0-byte `read` / `Read` is `ErrorKind::Timeout`
(instrument produced no data in this attempt). Do not loop on it.

Apply in sync + async framed readers in both languages.

### E.4 Rust async timeout restore on cancel

**Problem:** Async `read_response` / `flush` restore `opts.io_timeout()` after
`.await`. Dropping the future (cancel) skips restore. C# `finally` already
runs.

**Behavior:**

1. Add `AsyncTransport::apply_read_timeout(&mut self, timeout: Duration) -> Result<()>`
   with default `Ok(())`.
2. Implement on `SyncAsAsyncTransport` (delegate to inner `set_read_timeout`),
   `MockTransport` (store timeout like the sync path), and
   `VisaAsyncTransport` (`with_sync_instrument` + `AttrTmoValue`, same as
   `set_read_timeout` today).
3. In async `read_response` / `flush`, install a drop guard that calls
   `apply_read_timeout(opts.io_timeout())` if restore has not run.

Keep existing tests: `CancelledFlushStillRestoresIoTimeout`,
`ProbeOpcFailureRestoresIoTimeout`. Add: dropping a timed-out query future
restores `IoTimeout()` on the mock.

### E.5 Document VISA reconnect

`reconnect_on_failure` remains default **true**. `try_reconnect` still swallows
`Unsupported`. Document on `ConnectOptions.reconnect_on_failure` (Rust rustdoc
+ C# XML): VISA backends return unsupported; write/query retry still runs;
TCP reconnect needs a SOCKET transport (not this stream).

Do **not** flip the default to false in E. That would be a behavior change
without a reconnectable transport to justify it.

### E.6 Tests (both languages)

| Case | What it proves |
|------|----------------|
| Query times out, flush drains stale, retry returns fresh | E.1 happy path |
| Query retries exhausted still Timeout | E.1 fail |
| Write retry (`fail_writes`) unchanged | no regression |
| `*OPC?` → `-113,"Undefined header"` → ProbeOpc false, WaitComplete does not send `*OPC?` again as the wait | E.2 |
| `*OPC?` → `1` → ProbeOpc true | E.2 |
| `SYST:ERR?` → `OK` → ProbeSystErr false | E.2 |
| `SYST:ERR?` → `0,"No error"` → ProbeSystErr true | E.2 |
| `read` returns `Ok(0)` → Timeout, no spin | E.3 |
| Drop of async query future restores IoTimeout | E.4 (Rust) |

Use MockTransport scripts + `fail_reads`. Mirror C# tests in
`ReliabilityTests.cs` / async reliability tests.

### E.7 Docs

- This file: mark E done when merged.
- `docs/roadmap.md`: add E–H mermaid.
- `docs/parity-checklist.md`: rows for the new tests.
- `CHANGELOG.md` Unreleased: query retry+flush, probe honesty, Ok(0), async Drop.

### E.8 Out of scope (do not sneak in)

- `check_errors_after_io` on typed measures
- TCP SOCKET transport
- VISA `Reconnect()` implementation
- Dialect emission for DMM/PSU/etc. (F)
- Hardware job in `ci.yml` (H)

---

## Stream F — Dialect emission for remaining classes

**Goal:** Every catalog class emits dialect-resolved SCPI. PowerMeter and
SpectrumAnalyzer still **require** dialect keys. Remaining classes try the
session dialect first and fall back to `scpi_commands` / `ScpiCommands`.

**Classes:** DMM, PSU, function generator, oscilloscope, switch, counter.

**Approach:**

1. Reuse `crates/instrument/src/classes/dialect_io.rs` (`try_command`,
   `try_formatted`). Missing key → fallback. Leftover `{ident}` placeholders
   → fallback (including no-arg `try_command`, so Rust cannot emit braces
   that C# would reject). Extra supplied vars that the template does not
   mention are ignored when the template has placeholders (vendor `{range}`
   still runs if `{resolution}` is also passed). A constant template cannot
   represent any supplied var, so those calls fall back (ranged DMM measure
   keeps the range).
2. Command ids are dialect keys in `profiles.toml` plus fallback templates in
   `scpi_commands.toml` / generated `ScpiCommands`. There is no
   `spec/commands.json`.
3. C# lockstep: `DialectCommand.Try` in
   `dotnet/src/InstrumentComponents/Classes/DialectCommand.cs`.
4. Oscilloscope **binary waveform** (`#N` IEEE block) stays **deferred**. ASCII
   capture remains the supported path.

**Tests:** MockTransport scripts covering dialect-resolved commands plus
explicit fallback: DMM measure with range (`:MEAS:VOLT:DC? 10`), FGen
`read_frequency` (`:SOUR:FREQ?`), scope `read_timebase_scale`
(`:TIMebase:SCALe?`). CI fixture profiles `ci_dmm_dialect_wins` /
`ci_psu_dialect_wins` (IDN `TestDialect*`) prove a non-generic profile is
selected and emits different SCPI, leftover `{channel}` on `read` falls back,
and extra optional configure args do not drop `{range}`.

**Does not:** Add hardware vendor profiles (G). Expand the class registry. Scope binary.

---

## Stream G — DMM + PSU transcripts and vendor profiles

**Goal:** Golden SCPI transcripts for DMM and PSU, plus 1–2 real vendor dialect
profiles so dialect resolution is not only `generic_*` / `TestDialect*`.

**Fixtures today:** `fixtures/` has `smu2602`, `scope_ds1054z`, `switch_34970a`,
`counter_53230a`, `dmm_dmm6500`, `psu_n6705c`.

**Approach:**

1. Add `fixtures/dmm_dmm6500.json` and `fixtures/psu_n6705c.json` in the same
   transcript schema as existing fixtures. Both languages assert **values**.
2. Add vendor profiles under `spec/vendors/` for instruments already in
   `model_registry.toml` (do not expand the registry):
   - Keithley DMM6500 (`keithley_dmm6500`): model glob `*DMM6500*` so
     `*IDN?` model `MODEL DMM6500` matches. Un-ranged measure is
     `:SENS:FUNC "VOLT:DC";:READ?`. Configure/initiate/fetch/read stay on
     generic fallback. A ranged measure is a constant template plus `{range}`,
     so it falls back to `:MEAS:VOLT:DC? 10` (explicit fallback test, **not**
     complete vendor SCPI).
   - Keysight N6705C (`keysight_n6705c`, 4 channels): manufacturer glob `*`
     and model `N6705*` so Agilent or Keysight IDN matches. Channel-list SCPI
     (`:VOLT {volts}, (@{channel})`). `sense_enable` is omitted (N6705 uses
     INT/EXT, not ON/OFF) and stays on generic `:OUTP{channel}:SENS`.
3. `tools/gen-dialects.ts` loads `spec/vendors/*.json`, checks them against
   `vendors.schema.json` (id pattern, additionalProperties, required fields),
   and inserts those profiles after non-generic TOML rows (CI `TestDialect*`
   fixtures stay first) and before `generic_*`. Do not duplicate G vendors in
   `profiles.toml`. Existing PM/SA vendors stay in TOML.
4. Do not attach vendors to `34461A` or `E36312A` — those stay generic in
   mock catalog and `spec/scpi-vectors.json`.
5. Both Rust and C# transcript tests consume the same JSON (sync only).
   Transcripts prove typed-class SCPI for the injected identity; they are
   **not** proof a live `*IDN?` matches until H runs on hardware.

**Does not:** Hardware smoke (H). More classes’ transcripts (can follow after G
if needed). Registry expansion. A complete DMM6500 ranged-measure dialect.

---

## Stream H — Self-hosted hardware smoke

**Goal:** One ignored-by-default job that talks to a real DMM on a self-hosted
runner. Not GitHub-hosted. Not vendor emulators (they do not exist for this
stack).

**Approach:**

1. Keep `crates/instrument/tests/hardware.rs` `#[ignore]` and C#
   `HardwareTests` in `Category=Hardware` so GitHub-hosted CI never opens
   VISA. Existing discover / first-device IDN tests stay local-only.
2. Add `.github/workflows/hardware.yml`: `workflow_dispatch` only, `runs-on:
   [self-hosted, visa]`. Input `instrument_resource` becomes
   `INSTRUMENT_RESOURCE`. Jobs measure DC voltage in Rust and C# (or one
   suite). Concurrency group `hardware-smoke` so two dispatches do not share
   the instrument.
3. Smoke is **one class** (DMM) and **one resource**. Discovery uses
   `StaticEnumerator` / `StaticEnumerator.FromAddresses` so the rest of the
   bench is not probed. The instrument must classify as `Dmm`. Reading must
   be finite and `|V| < 1e6` (reject overload sentinels). The log prints the
   resolved dialect id. If the `*IDN?` model contains `DMM6500`, the dialect
   must be `keithley_dmm6500` (G vendor evidence). Other DMMs only prove
   VISA open + some SCPI.
4. C# `HardwareFact` **skips** when `INSTRUMENT_RESOURCE` is unset. Rust
   `#[ignore]` tests **assert** if you pass `--ignored` without the env.
   The workflow always sets the env.
5. A passing dispatch log is hardware evidence. Landing the workflow is not.
   Do not discuss `0.2.0` until that log exists. After G merges, retarget
   this PR to `latest` so GitHub-hosted CI compiles `hardware.rs` and runs
   `Visa.Tests` (`Category!=Hardware`).

**Does not:** NI-VISA / Keysight IO Libraries on `ubuntu-latest`. PyVISA-sim.
Changing MockTransport CI (`ci.yml` / `dotnet.yml` mock jobs). PSU smoke.
Oscilloscope binary.

---

## Explicitly later (not E–H)

- TCP SOCKET transport in core (enables ivi-cli-mock / local SCPI responder;
  still not vendor VISA)
- `check_errors_after_io` on typed measures
- Oscilloscope binary waveform typed API
- C# Counter `ReadFrequencyAsync` timeout (deferred in D)
- UniFFI, IVI adapters, new instrument classes
- Publishing `0.2.0`

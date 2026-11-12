# Errors and evidence

Rust `DriverError.kind` and C# `DriverException.Kind` report shared categories: invalid arguments, missing/ambiguous drivers, unsupported operations/modes, transport/timeout/parse/device failures, recovery required, closing/closed sessions, and shutdown failures.

Validate arguments, plan variants, channel domains, enum tokens and response roles before IO whenever possible. Unsupported operations must produce no commands. Preserve transport diagnostics without converting failures into fabricated measurement values. Recovery must be explicit after a disrupted operation; do not assume an IO exception means nothing reached the instrument.

Measurements carry value, physical unit and status. Unknown temperature units stay Unknown until a device-owned plan or explicit configuration establishes Celsius/Kelvin. Reset/recovery invalidate configured read/fetch units. Finite numeric magnitudes are not universally treated as overload; an implementation must report overload from its own documented device evidence. OpenTAP marks reported overload as a failed result.

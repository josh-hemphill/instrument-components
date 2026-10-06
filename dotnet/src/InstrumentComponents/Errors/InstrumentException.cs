using InstrumentComponents.Kind;

namespace InstrumentComponents.Errors;

public class InstrumentException : Exception
{
    public InstrumentException(string message) : base(message) { }
    public InstrumentException(string message, Exception inner) : base(message, inner) { }
}

public class TransportException : InstrumentException
{
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception inner) : base(message, inner) { }
}

public sealed class TransportClosedException : TransportException
{
    public TransportClosedException() : base("connection closed") { }
}

public sealed class InstrumentTimeoutException : InstrumentException
{
    public InstrumentTimeoutException() : base("operation timed out") { }
}

public sealed class InstrumentUnsupportedException : InstrumentException
{
    public InstrumentUnsupportedException(string detail) : base($"unsupported: {detail}") { }
}

public sealed class DeviceNotFoundException : InstrumentException
{
    public string Address { get; }
    public DeviceNotFoundException(string address) : base($"device not found: {address}") => Address = address;
}

public sealed class UnsupportedKindException : InstrumentException
{
    public string Address { get; }
    public InstrumentKind Kind { get; }
    public IReadOnlyList<InstrumentKind> Supported { get; }

    public UnsupportedKindException(string address, InstrumentKind kind, IReadOnlyList<InstrumentKind> supported)
        : base($"kind {kind} not supported at {address}; supported: [{string.Join(", ", supported)}]")
    {
        Address = address;
        Kind = kind;
        Supported = supported;
    }
}

public sealed class ParseException : InstrumentException
{
    public ParseException(string message) : base($"parse error: {message}") { }
}

public sealed class SessionLimitException : InstrumentException
{
    public string Address { get; }
    public SessionLimitException(string address) : base($"session limit reached for {address}") => Address = address;
    public SessionLimitException(string address, Exception inner) : base($"session limit reached for {address}", inner) => Address = address;
}

public sealed class InvalidAddressException : InstrumentException
{
    public InvalidAddressException(string message) : base($"invalid address: {message}") { }
}

public sealed class MockExhaustedException : InstrumentException
{
    public MockExhaustedException() : base("mock script exhausted: expected write") { }
}

public sealed class MockMismatchException : InstrumentException
{
    public string Expected { get; }
    public string Actual { get; }
    public MockMismatchException(string expected, string actual)
        : base($"mock script mismatch: expected {expected}, got {actual}")
    {
        Expected = expected;
        Actual = actual;
    }
}

public sealed class CommunicationException : InstrumentException
{
    public string Address { get; }
    public string? Command { get; }
    public uint Attempts { get; }

    public CommunicationException(string address, string? command, uint attempts, Exception source)
        : base($"communication failed at {address} after {attempts} attempt(s): {source.Message}", source)
    {
        Address = address;
        Command = command;
        Attempts = attempts;
    }
}

public sealed class ScpiCommandException : InstrumentException
{
    public string Command { get; }
    public ScpiCommandException(string command, string message) : base($"SCPI command '{command}' failed: {message}") => Command = command;
}

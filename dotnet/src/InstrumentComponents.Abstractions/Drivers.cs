using System.Collections.Frozen;

namespace InstrumentComponents.Drivers;

public static class DriverContract { public const uint Major = 1; public static IReadOnlySet<ExecutionMode> SyncModes { get; } = new[] { ExecutionMode.Sync }.ToFrozenSet(); }

public enum DriverErrorKind
{
    InvalidArgument, DuplicateDriver, IncompatibleContract, DriverNotFound,
    AmbiguousDriver, UnsupportedOperation, UnsupportedExecutionMode,
    DriverContractViolation, Transport, Timeout, Parse, Device,
    SessionNeedsRecovery, SessionClosing, SessionClosed, Shutdown,
}

public sealed class DriverException(DriverErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public DriverErrorKind Kind { get; } = kind;
}

/// <summary>Explicit execution paths; no implicit sync-over-native-async bridge.</summary>
public enum ExecutionMode { Sync, NativeAsync, WorkerAsync }
public enum ConnectionOwnership { Owned, Borrowed }
public sealed record Endpoint(string Scheme, string Address);
public sealed record DeviceIdentity(string? Manufacturer = null, string? Model = null,
    string? Serial = null, uint? VendorId = null, uint? ProductId = null);
/// <summary>Passive evidence; matching a candidate never opens or probes it.</summary>
public sealed record DeviceCandidate(Endpoint Endpoint, DeviceIdentity Identity);

public abstract record MatchRule(string Scheme);
public sealed record ExactIdentityRule(string Scheme, string Manufacturer, string Model) : MatchRule(Scheme);
public sealed record ExactProductRule(string Scheme, uint VendorId, uint ProductId) : MatchRule(Scheme);
/// <summary>Exact manufacturer plus a model pattern using only '*' wildcards.</summary>
public sealed record FamilyRule(string Scheme, string Manufacturer, string ModelPattern) : MatchRule(Scheme);
/// <summary>Eligible only through an application's explicit standard-SCPI policy.</summary>
public sealed record StandardScpiRule(string Scheme) : MatchRule(Scheme);

/// <summary>Immutable registration metadata; runtime snapshots it at construction.</summary>
public sealed class DriverDescriptor
{
    public string Id { get; }
    public uint ContractMajor { get; }
    public IReadOnlyList<MatchRule> Rules { get; }
    public IReadOnlySet<Operation> Operations { get; }

    public DriverDescriptor(string id, uint contractMajor, IEnumerable<MatchRule> rules, IEnumerable<Operation> operations)
    {
        Id = id;
        ContractMajor = contractMajor;
        Rules = Array.AsReadOnly(rules.ToArray());
        Operations = operations.ToFrozenSet();
    }
}

/// <summary>Factories create arbitrary connections, not necessarily SCPI/byte transports.</summary>
public sealed record DriverOpenContext(DeviceCandidate Candidate, ConnectionOwnership Ownership, IReadOnlySet<Operation>? RequiredOperations=null);
public interface IDriverFactory
{
    DriverDescriptor Descriptor { get; }
    IReadOnlySet<ExecutionMode> ExecutionModes => DriverContract.SyncModes;
    IDeviceBackend Open(DriverOpenContext context) => throw new DriverException(DriverErrorKind.UnsupportedExecutionMode, "Direct sync unavailable.");
    ValueTask<IAsyncDeviceBackend> OpenNativeAsync(DriverOpenContext context, CancellationToken cancellationToken = default) => ValueTask.FromException<IAsyncDeviceBackend>(new DriverException(DriverErrorKind.UnsupportedExecutionMode, "Native async unavailable."));
    IDeviceBackend OpenWorker(DriverOpenContext context) => Open(context);
}

public enum MeasurementStatus { Valid, Overload }
/// <summary>Voltage measurements are in SI volts; overload is explicit.</summary>
public readonly record struct Measurement(double Value, MeasurementStatus Status = MeasurementStatus.Valid);
public interface IDmmMeasurement { Measurement MeasureVoltageDc(double? range = null); }
public interface IPsuOutput
{
    void SetVoltage(uint channel, double volts);
    Measurement ReadVoltage(uint channel);
    void OutputEnable(uint channel, bool enabled);
}
public enum SenseMode { Internal, External }
public interface IRemoteSense { void SetMode(uint channel, SenseMode mode); }
public interface IDeviceReset { void Reset(); }
/// <summary>Attempt every applicable output; unknown/partial coverage must fail.</summary>
public interface ISafeShutdown { void Shutdown(); }

/// <summary>
/// Shared provider protocol/state behind serialized capability views. Dispose releases
/// resources once and preserves borrowed I/O. Capability accessors are stable,
/// side-effect-free and perform no I/O. No callbacks may reenter the device handle
/// while executing a backend operation. Failed Open must release acquired resources.
/// </summary>
public interface IDeviceBackend : IDisposable
{
    Catalog.ICatalogBackend? Catalog => null;
    IReadOnlySet<Operation> Operations { get; }
    IDmmMeasurement? Dmm => null;
    IPsuOutput? Psu => null;
    IRemoteSense? RemoteSense => null;
    IDeviceReset? Reset => null;
    ISafeShutdown? Shutdown => null;
    void Recover() => throw new DriverException(DriverErrorKind.SessionNeedsRecovery, "Provider has no recovery strategy.");
}

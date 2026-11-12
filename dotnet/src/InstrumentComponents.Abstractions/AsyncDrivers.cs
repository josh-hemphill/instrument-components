namespace InstrumentComponents.Drivers;

public interface IAsyncDmmMeasurement { ValueTask<Measurement> MeasureVoltageDcAsync(double? range = null, CancellationToken cancellationToken = default); }
public interface IAsyncPsuOutput
{
    ValueTask SetVoltageAsync(uint channel, double volts, CancellationToken cancellationToken = default);
    ValueTask<Measurement> ReadVoltageAsync(uint channel, CancellationToken cancellationToken = default);
    ValueTask OutputEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default);
}
public interface IAsyncRemoteSense { ValueTask SetModeAsync(uint channel, SenseMode mode, CancellationToken cancellationToken = default); }
public interface IAsyncReset { ValueTask ResetAsync(CancellationToken cancellationToken = default); }
public interface IAsyncSafeShutdown { ValueTask ShutdownAsync(CancellationToken cancellationToken = default); }

/// <summary>
/// Native operations must stop all host accesses before completing/canceling. A task
/// reference being discarded does not cancel work. Use a worker for blocking calls.
/// Dispose is synchronous, nonblocking, resource-only and preserves borrowed I/O.
/// Factories must release partial acquisitions when open fails or is canceled.
/// </summary>
public interface IAsyncDeviceBackend : IDisposable
{
    Catalog.IAsyncCatalogBackend? Catalog => null;
    IReadOnlySet<Operation> Operations { get; }
    IAsyncDmmMeasurement? Dmm => null;
    IAsyncPsuOutput? Psu => null;
    IAsyncRemoteSense? RemoteSense => null;
    IAsyncReset? Reset => null;
    IAsyncSafeShutdown? Shutdown => null;
    ValueTask RecoverAsync(CancellationToken cancellationToken = default) => ValueTask.FromException(new DriverException(DriverErrorKind.SessionNeedsRecovery, "No recovery strategy."));
}

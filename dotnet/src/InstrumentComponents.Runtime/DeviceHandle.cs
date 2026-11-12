using System.Collections.Frozen;
using System.Runtime.ExceptionServices;
using InstrumentComponents.Drivers;
using static InstrumentComponents.Runtime.DriverRegistry;

namespace InstrumentComponents.Runtime;

/// <summary>Shared synchronous connection and whole-operation serialization boundary.</summary>
public sealed class DeviceHandle : IDisposable, Drivers.Catalog.ICatalogExecutor
{
    private readonly object _gate = new();
    private IDeviceBackend? _backend;
    private bool _needsRecovery;
    private bool _closeRequiresShutdown;
    private ExceptionDispatchInfo? _closeFailure;
    private int _operationThread;
    private int _waiting;
    private int _lifecycle; // 0 open, 1 resource-close requested, 3 shutdown-close requested, 2 closed.
    public IReadOnlySet<Operation> Operations { get; }
    public int PendingOperations => Volatile.Read(ref _waiting);
    public bool ClosingRequested => Volatile.Read(ref _lifecycle) != 0;
    public MatchEvidence Evidence { get; }

    internal DeviceHandle(IDeviceBackend backend, FrozenSet<Operation> operations, MatchEvidence evidence)
    { _backend = backend; Operations = operations; Evidence = evidence; }

    private void CheckReentrancy()
    {
        if (Volatile.Read(ref _operationThread) == Environment.CurrentManagedThreadId)
            throw Error(DriverErrorKind.DriverContractViolation, "Backend must not reenter its device handle.");
    }
    private void CheckLifecycle()
    {
        CheckReentrancy();
        var state = Volatile.Read(ref _lifecycle);
        if (state == 2) throw Error(DriverErrorKind.SessionClosed, "Session closed.");
        if (state != 0) throw Error(DriverErrorKind.SessionClosing, "Close requested.");
    }

    private void EnterGate()
    {
        Interlocked.Increment(ref _waiting);
        try { Monitor.Enter(_gate); }
        finally { Interlocked.Decrement(ref _waiting); }
    }

    internal T Run<T>(Operation operation, Func<IDeviceBackend, T> action)
    {
        CheckLifecycle();
        EnterGate();
        try
        {
            CheckLifecycle();
            if (_needsRecovery) throw Error(DriverErrorKind.SessionNeedsRecovery, "Provider recovery required.");
            if (!Operations.Contains(operation)) throw Error(DriverErrorKind.UnsupportedOperation, operation.ToString());
            Volatile.Write(ref _operationThread, Environment.CurrentManagedThreadId);
            try { return action(_backend!); }
            catch (Exception ex)
            {
                if (ex is not DriverException { Kind: DriverErrorKind.InvalidArgument or DriverErrorKind.UnsupportedOperation }) _needsRecovery = true;
                throw;
            }
            finally { Volatile.Write(ref _operationThread, 0); }
        }
        finally { Monitor.Exit(_gate); }
    }

    public Drivers.Catalog.CatalogReply Invoke(Drivers.Catalog.CatalogRequest request) {
        request.Validate(); return Run(Drivers.Catalog.CatalogOperations.Runtime(request.Operation), b => {
            var reply = Capability(b.Catalog).Invoke(request); reply = reply.OwnedCopy(); reply.Validate(request); return reply;
        });
    }
    public DmmView Dmm => new(this);
    public PowerSupplyView Psu => new(this);
    internal static T Capability<T>(T? capability) where T : class => capability
        ?? throw Error(DriverErrorKind.DriverContractViolation, "Provider removed an advertised capability.");
    public void Reset() => Run(Operation.Reset, b => { Capability(b.Reset).Reset(); return true; });
    public void SetSenseMode(uint channel, SenseMode mode)
    {
        Channel(channel);
        if (!Enum.IsDefined(mode)) throw Error(DriverErrorKind.InvalidArgument, "Invalid sense mode.");
        Run(Operation.RemoteSense, b => { Capability(b.RemoteSense).SetMode(channel, mode); return true; });
    }
    public void Recover()
    {
        CheckLifecycle();
        EnterGate();
        try
        {
            CheckLifecycle();
            if (!_needsRecovery) return;
            Volatile.Write(ref _operationThread, Environment.CurrentManagedThreadId);
            try { _backend!.Recover(); _needsRecovery = false; }
            finally { Volatile.Write(ref _operationThread, 0); }
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>Reject queued work, wait for active work, optionally shut down, always release owned resources.</summary>
    public void Close(bool requireShutdown = false)
        => CloseCore(requireShutdown, replayOutcome: true);

    private void CloseCore(bool requireShutdown, bool replayOutcome)
    {
        CheckReentrancy();
        var firstRequest = Interlocked.CompareExchange(ref _lifecycle, requireShutdown ? 3 : 1, 0) == 0;
        var reportOutcome = replayOutcome || firstRequest;
        EnterGate();
        try
        {
            if (_backend is not { } backend)
            {
                if (reportOutcome)
                {
                    _closeFailure?.Throw();
                    if (requireShutdown && !_closeRequiresShutdown)
                        throw Error(DriverErrorKind.Shutdown, "Cannot strengthen the first close request.");
                }
                return;
            }
            _closeRequiresShutdown = Volatile.Read(ref _lifecycle) == 3;
            _backend = null;
            Volatile.Write(ref _operationThread, Environment.CurrentManagedThreadId);
            var failures = new List<Exception>();
            try
            {
                if (_closeRequiresShutdown && !(Operations.Contains(Operation.Shutdown) || Operations.Contains(Operation.CatalogShutdown)))
                    failures.Add(Error(DriverErrorKind.UnsupportedOperation, "Safe shutdown is not enabled for this session."));
                else if (_closeRequiresShutdown)
                {
                    var safeToShutdown = true;
                    if (_needsRecovery)
                    {
                        try { backend.Recover(); }
                        catch (Exception ex) { failures.Add(ex); safeToShutdown = false; }
                    }
                    if (safeToShutdown)
                    {
                        try { if (Operations.Contains(Operation.Shutdown)) (backend.Shutdown ?? throw Error(DriverErrorKind.UnsupportedOperation, "Safe shutdown unsupported.")).Shutdown(); else Capability(backend.Catalog).Invoke(new Drivers.Catalog.ShutdownRequest()).IntoUnit(); }
                        catch (Exception ex) { failures.Add(ex); }
                    }
                }
            }
            finally
            {
                try { backend.Dispose(); } catch (Exception ex) { failures.Add(ex); }
                if (failures.Count > 0)
                    _closeFailure = ExceptionDispatchInfo.Capture(new DriverException(DriverErrorKind.Shutdown, "Shutdown/resource release failed.", new AggregateException(failures)));
                Volatile.Write(ref _lifecycle, 2);
                Volatile.Write(ref _operationThread, 0);
            }
            if (reportOutcome)
            {
                _closeFailure?.Throw();
                if (requireShutdown && !_closeRequiresShutdown)
                    throw Error(DriverErrorKind.Shutdown, "Cannot strengthen the first close request.");
            }
        }
        finally { Monitor.Exit(_gate); }
    }
    /// <summary>Resource release only: no implicit reset or output commands.</summary>
    public void Dispose() => CloseCore(false, replayOutcome: false);
    internal static Measurement Validate(Measurement value)
    {
        if (!Enum.IsDefined(value.Status) || (value.Status == MeasurementStatus.Valid && !double.IsFinite(value.Value)))
            throw Error(DriverErrorKind.DriverContractViolation, "Valid measurement must be finite with a defined status.");
        return value;
    }
    internal static void Channel(uint channel)
    { if (channel == 0) throw Error(DriverErrorKind.InvalidArgument, "Channel must be positive."); }
}

public sealed class DmmView
{
    private readonly DeviceHandle _handle;
    internal DmmView(DeviceHandle handle) => _handle = handle;
    public Measurement MeasureVoltageDc(double? range = null)
    {
        if (range.HasValue && (!double.IsFinite(range.Value) || range.Value <= 0))
            throw Error(DriverErrorKind.InvalidArgument, "Range must be finite and positive.");
        return _handle.Run(Operation.MeasureVoltageDc, b => DeviceHandle.Validate(DeviceHandle.Capability(b.Dmm).MeasureVoltageDc(range)));
    }
}

public sealed class PowerSupplyView
{
    private readonly DeviceHandle _handle;
    internal PowerSupplyView(DeviceHandle handle) => _handle = handle;
    public void SetVoltage(uint channel, double volts)
    {
        DeviceHandle.Channel(channel);
        if (!double.IsFinite(volts)) throw Error(DriverErrorKind.InvalidArgument, "Voltage must be finite.");
        _handle.Run(Operation.SetVoltage, b => { DeviceHandle.Capability(b.Psu).SetVoltage(channel, volts); return true; });
    }
    public Measurement ReadVoltage(uint channel)
    {
        DeviceHandle.Channel(channel);
        return _handle.Run(Operation.ReadVoltage, b => DeviceHandle.Validate(DeviceHandle.Capability(b.Psu).ReadVoltage(channel)));
    }
    public void OutputEnable(uint channel, bool enabled)
    {
        DeviceHandle.Channel(channel);
        _handle.Run(Operation.OutputEnable, b => { DeviceHandle.Capability(b.Psu).OutputEnable(channel, enabled); return true; });
    }
}

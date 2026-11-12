using System.Collections.Concurrent;
using System.Collections.Frozen;
using InstrumentComponents.Drivers;
using static InstrumentComponents.Runtime.DriverRegistry;

namespace InstrumentComponents.Runtime;

public sealed partial class DriverRegistry
{
    public async Task<WorkerDeviceHandle> OpenWorkerAsync(DeviceCandidate candidate, DriverSelection? selection = null,
        IEnumerable<Operation>? required = null, ConnectionOwnership ownership = ConnectionOwnership.Owned, CancellationToken cancellationToken = default)
    {
        ValidateOwnership(ownership);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = Select(candidate, selection); var registration = _registrations[evidence.DriverId];
        if (!registration.Modes.Contains(ExecutionMode.WorkerAsync)) throw Error(DriverErrorKind.UnsupportedExecutionMode, "Worker mode unavailable.");
        var requested = (required ?? []).ToFrozenSet();
        if (!requested.IsSubsetOf(registration.Descriptor.Operations)) throw Error(DriverErrorKind.UnsupportedOperation, "Required operation outside envelope.");
        var state = new WorkerState(registration.Factory, new(candidate, ownership, requested), registration.Descriptor.Operations, requested);
        state.Start();
        try
        {
            var operations = await state.Opened.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(state, operations, evidence);
        }
        catch { state.RequestClose(false); throw; } // owner remains the worker even during a blocked open
    }
}

internal sealed class WorkerJob
{
    private enum Stage { Queued, Running, CanceledQueued, CanceledRunning, Done }
    private readonly object _gate = new();
    private Stage _stage;
    private readonly TaskCompletionSource<object?>? _completion;
    private readonly ManualResetEventSlim? _ready;
    private readonly WorkerState _owner;
    private readonly CancellationToken _token;
    private CancellationTokenRegistration _registration;
    private bool _dispatched;
    private object? _value;
    private Exception? _failure;
    internal readonly Operation? Operation;
    internal readonly Func<IDeviceBackend, object?> Action;
    internal WorkerJob(WorkerState owner, Operation? operation, Func<IDeviceBackend, object?> action, bool asynchronous, CancellationToken token)
    {
        _owner = owner; Operation = operation; Action = action; _token = token;
        if (asynchronous) _completion = new(TaskCreationOptions.RunContinuationsAsynchronously); else _ready = new();
        if (token.CanBeCanceled) _registration = token.Register(Cancel);
    }
    private void Cancel()
    {
        lock (_gate)
        {
            if (_stage == Stage.Done) return;
            if (_stage == Stage.Queued) _stage = Stage.CanceledQueued;
            else if (_stage == Stage.Running) { _stage = Stage.CanceledRunning; if (_dispatched) Volatile.Write(ref _owner.State.Draining, 1); }
            _completion?.TrySetCanceled(_token);
        }
    }
    internal bool TryStart()
    {
        lock (_gate)
        {
            if (_stage == Stage.CanceledQueued) { _stage = Stage.Done; return false; }
            if (_stage == Stage.Done) return false;
            _stage = Stage.Running; return true;
        }
    }
    internal bool Dispatch()
    {
        lock (_gate) { if (_stage == Stage.CanceledRunning) return false; _dispatched = true; return true; }
    }
    internal void Complete(object? value, Exception? failure)
    {
        lock (_gate)
        {
            if (_stage == Stage.Done) return;
            if (_dispatched && (_stage == Stage.CanceledRunning || (failure is not null && AsyncControl.Uncertain(failure)))) Volatile.Write(ref _owner.State.Recovery, 1);
            _stage = Stage.Done; _value = value; _failure = failure;
            Volatile.Write(ref _owner.State.Active, 0); Volatile.Write(ref _owner.State.Draining, 0);
            if (failure is null) _completion?.TrySetResult(value); else _completion?.TrySetException(failure);
            _ready?.Set();
        }
    }
    internal void DisposeUnqueued() { _registration.Dispose(); _ready?.Dispose(); }
    internal object? Wait()
    {
        try { _ready!.Wait(); if (_failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw(); return _value; }
        finally { _ready!.Dispose(); }
    }
    internal async Task<object?> WaitAsync()
    { try { return await _completion!.Task.ConfigureAwait(false); } finally { _registration.Dispose(); } }
}

internal readonly record struct WorkerCommand(WorkerJob? Job, bool Shutdown = false);
internal sealed class WorkerState(IDriverFactory factory, DriverOpenContext context, IReadOnlySet<Operation> envelope, FrozenSet<Operation> required)
{
    internal readonly AsyncControl State = new();
    private readonly BlockingCollection<WorkerCommand> _queue = new();
    private readonly TaskCompletionSource<FrozenSet<Operation>> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Managed thread IDs may be reused after a worker exits (including Native
    // AOT on Windows). Reentry is an executing scope, not a historical ID match.
    [ThreadStatic] private static WorkerState? _executing;
    internal Task<FrozenSet<Operation>> Opened => _opened.Task;
    internal void CheckReentry()
    { if (ReferenceEquals(_executing, this)) throw Error(DriverErrorKind.DriverContractViolation, "Worker backend reentry."); }
    internal void Start() => new Thread(Run) { IsBackground = true, Name = "Instrument driver" }.Start();
    internal void Enqueue(WorkerJob job)
    {
        CheckReentry(); State.Check();
        Interlocked.Increment(ref State.Waiting);
        try { _queue.Add(new(job)); }
        catch { Interlocked.Decrement(ref State.Waiting); job.Complete(null, Error(DriverErrorKind.SessionClosed, "Worker disconnected.")); }
    }
    internal void RequestClose(bool shutdown)
    {
        CheckReentry();
        if (!State.BeginClose(shutdown)) return;
        Interlocked.Increment(ref State.Waiting);
        try { _queue.Add(new(null, shutdown)); }
        catch { Interlocked.Decrement(ref State.Waiting); State.FinishClose(Error(DriverErrorKind.SessionClosed, "Worker disconnected.")); }
    }
    private void Run()
    {
        _executing = this;
        IDeviceBackend? backend = null; Exception? failure = null;
        try
        {
            if (Volatile.Read(ref State.Lifecycle) != 0) { _opened.TrySetCanceled(); return; }
            backend = factory.OpenWorker(context);
            var operations = backend.Operations.ToFrozenSet();
            var invalid = !operations.IsSubsetOf(envelope)
                || (operations.Any(o => o > Operation.Shutdown) && backend.Catalog is null)
                || (operations.Contains(Operation.MeasureVoltageDc) && backend.Dmm is null)
                || (operations.Overlaps([Operation.SetVoltage, Operation.ReadVoltage, Operation.OutputEnable]) && backend.Psu is null)
                || (operations.Contains(Operation.RemoteSense) && backend.RemoteSense is null)
                || (operations.Contains(Operation.Reset) && backend.Reset is null)
                || (operations.Contains(Operation.Shutdown) && backend.Shutdown is null);
            if (invalid) throw Error(DriverErrorKind.DriverContractViolation, "Invalid worker capabilities.");
            if (!required.IsSubsetOf(operations)) throw Error(DriverErrorKind.UnsupportedOperation, "Missing required capabilities.");
            if (Volatile.Read(ref State.Lifecycle) != 0) { _opened.TrySetCanceled(); return; }
            _opened.TrySetResult(operations);
            foreach (var command in _queue.GetConsumingEnumerable())
            {
                Interlocked.Decrement(ref State.Waiting);
                if (command.Job is not { } job)
                {
                    var errors = new List<Exception>();
                    if (command.Shutdown && !(operations.Contains(Operation.Shutdown) || operations.Contains(Operation.CatalogShutdown))) errors.Add(Error(DriverErrorKind.UnsupportedOperation, "Shutdown disabled."));
                    else if (command.Shutdown)
                    {
                        var recovered = true;
                        if (Volatile.Read(ref State.Recovery) != 0) try { backend.Recover(); } catch (Exception e) { errors.Add(e); recovered = false; }
                        if (recovered) try { if (operations.Contains(Operation.Shutdown)) DeviceHandle.Capability(backend.Shutdown).Shutdown(); else DeviceHandle.Capability(backend.Catalog).Invoke(new Drivers.Catalog.ShutdownRequest()).IntoUnit(); } catch (Exception e) { errors.Add(e); }
                    }
                    if (errors.Count > 0) failure = new DriverException(DriverErrorKind.Shutdown, "Worker shutdown failed.", new AggregateException(errors));
                    break;
                }
                if (!job.TryStart()) continue;
                Volatile.Write(ref State.Active, 1);
                object? result = null; Exception? error = null;
                try
                {
                    State.Check();
                    if (job.Operation is null)
                    { if (Volatile.Read(ref State.Recovery) != 0 && job.Dispatch()) { backend.Recover(); Volatile.Write(ref State.Recovery, 0); } }
                    else
                    {
                        if (Volatile.Read(ref State.Recovery) != 0) throw Error(DriverErrorKind.SessionNeedsRecovery, "Recovery required.");
                        if (!operations.Contains(job.Operation.Value)) throw Error(DriverErrorKind.UnsupportedOperation, "Operation unavailable.");
                        if (job.Dispatch()) result = job.Action(backend);
                    }
                }
                catch (Exception e) { error = e; }
                job.Complete(result, error);
            }
        }
        catch (Exception e) { failure = e; }
        finally
        {
            try { backend?.Dispose(); }
            catch (Exception cleanup) { failure = new DriverException(!_opened.Task.IsCompleted && failure is DriverException original ? original.Kind : DriverErrorKind.Shutdown, "Worker cleanup failed.", failure is null ? cleanup : new AggregateException(failure, cleanup)); }
            if (!_opened.Task.IsCompleted) _opened.TrySetException(failure ?? Error(DriverErrorKind.SessionClosed, "Open abandoned."));
            _queue.CompleteAdding();
            while (_queue.TryTake(out var pending)) { Interlocked.Decrement(ref State.Waiting); pending.Job?.Complete(null, Error(DriverErrorKind.SessionClosed, "Worker terminated.")); }
            Volatile.Write(ref State.Active, 0); Volatile.Write(ref State.Draining, 0);
            _executing = null;
            State.FinishClose(failure);
        }
    }
}

/// <summary>Owned requests run on one dedicated thread. Cancellation abandons waiting, not SDK work.</summary>
public sealed class WorkerDeviceHandle : IAsyncDisposable, IDisposable, Drivers.Catalog.ICatalogExecutor, Drivers.Catalog.IAsyncCatalogExecutor
{
    private readonly WorkerState _owner;
    public IReadOnlySet<Operation> Operations { get; }
    public MatchEvidence Evidence { get; }
    public bool RecoveryRequired => Volatile.Read(ref _owner.State.Recovery) != 0;
    public bool Draining => Volatile.Read(ref _owner.State.Draining) != 0;
    public bool ClosingRequested => Volatile.Read(ref _owner.State.Lifecycle) != 0;
    public int PendingOperations => Volatile.Read(ref _owner.State.Waiting);
    internal WorkerDeviceHandle(WorkerState owner, FrozenSet<Operation> operations, MatchEvidence evidence) { _owner = owner; Operations = operations; Evidence = evidence; }
    ~WorkerDeviceHandle() { try { _owner.RequestClose(false); } catch { /* actor owns cleanup, never dispose SDK here */ } }
    private object? Sync(Operation? operation, Func<IDeviceBackend, object?> action)
    {
        _owner.CheckReentry(); _owner.State.Check();
        var job = new WorkerJob(_owner, operation, action, false, CancellationToken.None); try { _owner.Enqueue(job); } catch { job.DisposeUnqueued(); throw; } return job.Wait();
    }
    private Task<object?> Async(Operation? operation, Func<IDeviceBackend, object?> action, CancellationToken ct)
    {
        _owner.CheckReentry(); _owner.State.Check(); ct.ThrowIfCancellationRequested();
        var job = new WorkerJob(_owner, operation, action, true, ct); try { _owner.Enqueue(job); } catch { job.DisposeUnqueued(); throw; } return job.WaitAsync();
    }
    public Drivers.Catalog.CatalogReply Invoke(Drivers.Catalog.CatalogRequest request) {
        request.Validate(); return (Drivers.Catalog.CatalogReply)Sync(Drivers.Catalog.CatalogOperations.Runtime(request.Operation), b => {
            var reply = DeviceHandle.Capability(b.Catalog).Invoke(request); reply = reply.OwnedCopy(); reply.Validate(request); return reply;
        })!;
    }
    public async Task<Drivers.Catalog.CatalogReply> InvokeAsync(Drivers.Catalog.CatalogRequest request, CancellationToken cancellationToken = default) {
        request.Validate(); return (Drivers.Catalog.CatalogReply)(await Async(Drivers.Catalog.CatalogOperations.Runtime(request.Operation), b => {
            var reply = DeviceHandle.Capability(b.Catalog).Invoke(request); reply = reply.OwnedCopy(); reply.Validate(request); return reply;
        }, cancellationToken).ConfigureAwait(false))!;
    }
    public Measurement MeasureVoltageDc(double? range = null)
    { AsyncControl.ValidateRange(range); return (Measurement)Sync(Operation.MeasureVoltageDc, b => DeviceHandle.Validate(DeviceHandle.Capability(b.Dmm).MeasureVoltageDc(range)))!; }
    public async Task<Measurement> MeasureVoltageDcAsync(double? range = null, CancellationToken cancellationToken = default)
    { AsyncControl.ValidateRange(range); return (Measurement)(await Async(Operation.MeasureVoltageDc, b => DeviceHandle.Validate(DeviceHandle.Capability(b.Dmm).MeasureVoltageDc(range)), cancellationToken).ConfigureAwait(false))!; }
    public void SetVoltage(uint channel, double volts)
    { AsyncControl.ValidateVoltage(channel, volts); Sync(Operation.SetVoltage, b => { DeviceHandle.Capability(b.Psu).SetVoltage(channel, volts); return null; }); }
    public async Task SetVoltageAsync(uint channel, double volts, CancellationToken cancellationToken = default)
    { AsyncControl.ValidateVoltage(channel, volts); await Async(Operation.SetVoltage, b => { DeviceHandle.Capability(b.Psu).SetVoltage(channel, volts); return null; }, cancellationToken).ConfigureAwait(false); }
    public Measurement ReadVoltage(uint channel)
    { DeviceHandle.Channel(channel); return (Measurement)Sync(Operation.ReadVoltage, b => DeviceHandle.Validate(DeviceHandle.Capability(b.Psu).ReadVoltage(channel)))!; }
    public async Task<Measurement> ReadVoltageAsync(uint channel, CancellationToken cancellationToken = default)
    { DeviceHandle.Channel(channel); return (Measurement)(await Async(Operation.ReadVoltage, b => DeviceHandle.Validate(DeviceHandle.Capability(b.Psu).ReadVoltage(channel)), cancellationToken).ConfigureAwait(false))!; }
    public void OutputEnable(uint channel, bool enabled)
    { DeviceHandle.Channel(channel); Sync(Operation.OutputEnable, b => { DeviceHandle.Capability(b.Psu).OutputEnable(channel, enabled); return null; }); }
    public async Task OutputEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default)
    { DeviceHandle.Channel(channel); await Async(Operation.OutputEnable, b => { DeviceHandle.Capability(b.Psu).OutputEnable(channel, enabled); return null; }, cancellationToken).ConfigureAwait(false); }
    public void Reset() => Sync(Operation.Reset, b => { DeviceHandle.Capability(b.Reset).Reset(); return null; });
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    { await Async(Operation.Reset, b => { DeviceHandle.Capability(b.Reset).Reset(); return null; }, cancellationToken).ConfigureAwait(false); }
    public async Task SetSenseModeAsync(uint channel, SenseMode mode, CancellationToken cancellationToken = default)
    { DeviceHandle.Channel(channel); if (!Enum.IsDefined(mode)) throw Error(DriverErrorKind.InvalidArgument, "Invalid sense mode."); await Async(Operation.RemoteSense, b => { DeviceHandle.Capability(b.RemoteSense).SetMode(channel, mode); return null; }, cancellationToken).ConfigureAwait(false); }
    public void Recover() => Sync(null, _ => null);
    public void SetSenseMode(uint channel, SenseMode mode)
    { DeviceHandle.Channel(channel); if (!Enum.IsDefined(mode)) throw Error(DriverErrorKind.InvalidArgument, "Invalid sense mode."); Sync(Operation.RemoteSense, b => { DeviceHandle.Capability(b.RemoteSense).SetMode(channel, mode); return null; }); }
    public void Close(bool requireShutdown = false) { _owner.CheckReentry(); _owner.RequestClose(requireShutdown); _owner.State.WaitCloseSync(requireShutdown); }
    public async Task RecoverAsync(CancellationToken cancellationToken = default) { await Async(null, _ => null, cancellationToken).ConfigureAwait(false); }
    public Task CloseAsync(bool requireShutdown = false, CancellationToken cancellationToken = default)
    { _owner.CheckReentry(); cancellationToken.ThrowIfCancellationRequested(); _owner.RequestClose(requireShutdown); return _owner.State.WaitClose(requireShutdown, cancellationToken); }
    public void Dispose() { Close(); GC.SuppressFinalize(this); }
    public ValueTask DisposeAsync() => new(CloseAsync());
}

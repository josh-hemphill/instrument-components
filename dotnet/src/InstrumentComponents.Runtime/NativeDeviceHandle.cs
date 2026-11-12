using System.Collections.Frozen;
using InstrumentComponents.Drivers;
using static InstrumentComponents.Runtime.DriverRegistry;

namespace InstrumentComponents.Runtime;

public sealed partial class DriverRegistry
{
    public async Task<NativeDeviceHandle> OpenNativeAsync(DeviceCandidate candidate, DriverSelection? selection = null,
        IEnumerable<Operation>? required = null, ConnectionOwnership ownership = ConnectionOwnership.Owned, CancellationToken cancellationToken = default)
    {
        ValidateOwnership(ownership);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = Select(candidate, selection); var registration = _registrations[evidence.DriverId];
        if (!registration.Modes.Contains(ExecutionMode.NativeAsync)) throw Error(DriverErrorKind.UnsupportedExecutionMode, "Native async unavailable.");
        var requested = (required ?? []).ToFrozenSet();
        if (!requested.IsSubsetOf(registration.Descriptor.Operations)) throw Error(DriverErrorKind.UnsupportedOperation, "Required operation outside envelope.");
        var backend = await registration.Factory.OpenNativeAsync(new(candidate, ownership, requested), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ops = backend.Operations.ToFrozenSet();
            var invalid = !ops.IsSubsetOf(registration.Descriptor.Operations)
                || (ops.Any(o => o > Operation.Shutdown) && backend.Catalog is null)
                || (ops.Contains(Operation.MeasureVoltageDc) && backend.Dmm is null)
                || (ops.Overlaps([Operation.SetVoltage, Operation.ReadVoltage, Operation.OutputEnable]) && backend.Psu is null)
                || (ops.Contains(Operation.RemoteSense) && backend.RemoteSense is null)
                || (ops.Contains(Operation.Reset) && backend.Reset is null)
                || (ops.Contains(Operation.Shutdown) && backend.Shutdown is null);
            if (invalid) throw Error(DriverErrorKind.DriverContractViolation, "Invalid async capabilities.");
            if (!requested.IsSubsetOf(ops)) throw Error(DriverErrorKind.UnsupportedOperation, "Missing required capabilities.");
            return new(backend, ops, evidence);
        }
        catch (Exception failure)
        {
            try { backend.Dispose(); }
            catch (Exception cleanup) { throw new DriverException(failure is DriverException d ? d.Kind : DriverErrorKind.DriverContractViolation, "Open validation/cleanup failed.", new AggregateException(failure, cleanup)); }
            throw;
        }
    }
}

/// <summary>Async-only native connection. Close owns its task independently of its waiters.</summary>
public sealed class NativeDeviceHandle : IAsyncDisposable, Drivers.Catalog.IAsyncCatalogExecutor
{
    private sealed class Owner(NativeDeviceHandle handle, Owner? previous)
    { internal readonly NativeDeviceHandle Handle = handle; internal readonly Owner? Previous = previous; internal bool Active = true; }
    private static readonly AsyncLocal<Owner?> Context = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncControl _state = new();
    private IAsyncDeviceBackend? _backend;
    public IReadOnlySet<Operation> Operations { get; }
    public MatchEvidence Evidence { get; }
    public bool RecoveryRequired => Volatile.Read(ref _state.Recovery) != 0;
    public bool ClosingRequested => Volatile.Read(ref _state.Lifecycle) != 0;
    public int PendingOperations => Volatile.Read(ref _state.Waiting);
    internal NativeDeviceHandle(IAsyncDeviceBackend backend, FrozenSet<Operation> operations, MatchEvidence evidence)
    { _backend = backend; Operations = operations; Evidence = evidence; }
    ~NativeDeviceHandle() { try { Interlocked.Exchange(ref _backend, null)?.Dispose(); } catch { /* resource-only fallback; no protocol shutdown claim */ } }
    private void Reentry()
    {
        for (var owner = Context.Value; owner is not null; owner = owner.Previous)
            if (Volatile.Read(ref owner.Active) && ReferenceEquals(owner.Handle, this)) throw Error(DriverErrorKind.DriverContractViolation, "Async backend reentry.");
    }
    private async Task Enter(CancellationToken ct)
    {
        Interlocked.Increment(ref _state.Waiting);
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _state.Waiting); }
    }
    private async ValueTask<T> Run<T>(Operation operation, Func<IAsyncDeviceBackend, CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        Reentry(); _state.Check(); ct.ThrowIfCancellationRequested();
        await Enter(ct).ConfigureAwait(false);
        Owner? owner = null;
        try
        {
            _state.Check(); ct.ThrowIfCancellationRequested();
            if (RecoveryRequired) throw Error(DriverErrorKind.SessionNeedsRecovery, "Recovery required.");
            if (!Operations.Contains(operation)) throw Error(DriverErrorKind.UnsupportedOperation, "Operation unavailable.");
            owner = new(this, Context.Value); Context.Value = owner; Volatile.Write(ref _state.Active, 1);
            try { return await action(_backend!, ct).ConfigureAwait(false); }
            catch (Exception e) { if (AsyncControl.Uncertain(e)) Volatile.Write(ref _state.Recovery, 1); throw; }
        }
        finally { if (owner is not null) { Volatile.Write(ref owner.Active, false); Context.Value = owner.Previous; } Volatile.Write(ref _state.Active, 0); _gate.Release(); }
    }
    public async Task<Drivers.Catalog.CatalogReply> InvokeAsync(Drivers.Catalog.CatalogRequest request, CancellationToken cancellationToken = default) {
        request.Validate(); return await Run(Drivers.Catalog.CatalogOperations.Runtime(request.Operation), async (b, ct) => {
            var reply = await DeviceHandle.Capability(b.Catalog).InvokeAsync(request, ct).ConfigureAwait(false); reply = reply.OwnedCopy(); reply.Validate(request); return reply;
        }, cancellationToken).ConfigureAwait(false);
    }
    public ValueTask<Measurement> MeasureVoltageDcAsync(double? range = null, CancellationToken cancellationToken = default)
    {
        AsyncControl.ValidateRange(range);
        return Run(Operation.MeasureVoltageDc, async (b, ct) => DeviceHandle.Validate(await DeviceHandle.Capability(b.Dmm).MeasureVoltageDcAsync(range, ct).ConfigureAwait(false)), cancellationToken);
    }
    public async ValueTask SetVoltageAsync(uint channel, double volts, CancellationToken cancellationToken = default)
    { AsyncControl.ValidateVoltage(channel, volts); await Run(Operation.SetVoltage, async (b, ct) => { await DeviceHandle.Capability(b.Psu).SetVoltageAsync(channel, volts, ct).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false); }
    public ValueTask<Measurement> ReadVoltageAsync(uint channel, CancellationToken cancellationToken = default)
    { DeviceHandle.Channel(channel); return Run(Operation.ReadVoltage, async (b, ct) => DeviceHandle.Validate(await DeviceHandle.Capability(b.Psu).ReadVoltageAsync(channel, ct).ConfigureAwait(false)), cancellationToken); }
    public async ValueTask OutputEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default)
    { DeviceHandle.Channel(channel); await Run(Operation.OutputEnable, async (b, ct) => { await DeviceHandle.Capability(b.Psu).OutputEnableAsync(channel, enabled, ct).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false); }
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    { await Run(Operation.Reset, async (b, ct) => { await DeviceHandle.Capability(b.Reset).ResetAsync(ct).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false); }
    public async ValueTask SetSenseModeAsync(uint channel, SenseMode mode, CancellationToken cancellationToken = default)
    {
        DeviceHandle.Channel(channel); if (!Enum.IsDefined(mode)) throw Error(DriverErrorKind.InvalidArgument, "Invalid sense mode.");
        await Run(Operation.RemoteSense, async (b, ct) => { await DeviceHandle.Capability(b.RemoteSense).SetModeAsync(channel, mode, ct).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
    }
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        Reentry(); _state.Check(); cancellationToken.ThrowIfCancellationRequested(); await Enter(cancellationToken).ConfigureAwait(false);
        Owner? owner = null;
        try
        {
            _state.Check(); cancellationToken.ThrowIfCancellationRequested(); if (!RecoveryRequired) return;
            owner = new(this, Context.Value); Context.Value = owner;
            await _backend!.RecoverAsync(cancellationToken).ConfigureAwait(false); Volatile.Write(ref _state.Recovery, 0);
        }
        finally { if (owner is not null) { Volatile.Write(ref owner.Active, false); Context.Value = owner.Previous; } _gate.Release(); }
    }
    public Task CloseAsync(bool requireShutdown = false, CancellationToken cancellationToken = default)
    {
        Reentry(); cancellationToken.ThrowIfCancellationRequested();
        if (_state.BeginClose(requireShutdown)) _ = CloseCore(requireShutdown);
        return _state.WaitClose(requireShutdown, cancellationToken);
    }
    private async Task CloseCore(bool shutdown)
    {
        Exception? failure = null;
        try
        {
            await Enter(CancellationToken.None).ConfigureAwait(false);
            var owner = new Owner(this, Context.Value); Context.Value = owner;
            try
            {
                var backend = Interlocked.Exchange(ref _backend, null);
                var errors = new List<Exception>();
                if (backend is not null)
                {
                    try
                    {
                        if (shutdown && !(Operations.Contains(Operation.Shutdown) || Operations.Contains(Operation.CatalogShutdown))) errors.Add(Error(DriverErrorKind.UnsupportedOperation, "Shutdown disabled."));
                        else if (shutdown)
                        {
                            var recovered = true;
                            if (RecoveryRequired) try { await backend.RecoverAsync().ConfigureAwait(false); } catch (Exception e) { errors.Add(e); recovered = false; }
                            if (recovered) try { if (Operations.Contains(Operation.Shutdown)) await DeviceHandle.Capability(backend.Shutdown).ShutdownAsync().ConfigureAwait(false); else (await DeviceHandle.Capability(backend.Catalog).InvokeAsync(new Drivers.Catalog.ShutdownRequest()).ConfigureAwait(false)).IntoUnit(); } catch (Exception e) { errors.Add(e); }
                        }
                    }
                    finally { try { backend.Dispose(); } catch (Exception e) { errors.Add(e); } }
                }
                if (errors.Count > 0) failure = new DriverException(DriverErrorKind.Shutdown, "Shutdown/resource release failed.", new AggregateException(errors));
            }
            finally { owner.Active = false; Context.Value = owner.Previous; _gate.Release(); }
        }
        catch (Exception e) { failure = e; }
        _state.FinishClose(failure); GC.SuppressFinalize(this);
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}

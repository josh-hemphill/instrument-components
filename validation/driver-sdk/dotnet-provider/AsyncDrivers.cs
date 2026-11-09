using System.Collections.Concurrent;
using System.Collections.Frozen;
using InstrumentComponents.Drivers;

namespace InstrumentComponents.SyntheticDriver;

public sealed class AsyncTrace
{
    public Trace Base { get; } = new();
    public bool BlockMeasure, BlockOpen, BlockRecover, BlockShutdown, FailOpen;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource OpenRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RecoverStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RecoverRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ShutdownStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ShutdownRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<Task>? Hook;
}
public sealed class NativeFactory(AsyncTrace trace) : IDriverFactory
{
    public static DeviceCandidate Candidate => new(new("fake", "device-1"), new("Acme", "N100"));
    public DriverDescriptor Descriptor { get; } = new("synthetic.native", DriverContract.Major, [new ExactIdentityRule("fake", "Acme", "N100")], BinaryFactory.Supported);
    public IReadOnlySet<ExecutionMode> ExecutionModes { get; } = new[] { ExecutionMode.NativeAsync }.ToFrozenSet();
    public async ValueTask<IAsyncDeviceBackend> OpenNativeAsync(DriverOpenContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref trace.Base.Opens);
        var device = new NativeDevice(trace, context.Ownership);
        try
        {
            trace.OpenStarted.TrySetResult();
            if (trace.BlockOpen) await trace.OpenRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (trace.FailOpen) throw new DriverException(DriverErrorKind.Device, "Open failed after acquisition.");
            return device;
        }
        catch (Exception failure)
        {
            try { device.Dispose(); } catch (Exception cleanup) { throw new DriverException(failure is DriverException d ? d.Kind : DriverErrorKind.Shutdown, "Open cleanup failed.", new AggregateException(failure, cleanup)); }
            throw;
        }
    }
}
internal sealed class NativeDevice(AsyncTrace trace, ConnectionOwnership ownership)
    : IAsyncDeviceBackend, IAsyncDmmMeasurement, IAsyncPsuOutput, IAsyncReset, IAsyncSafeShutdown
{
    private readonly BinaryDevice _inner = new(trace.Base, ownership);
    private int _closed;
    public IReadOnlySet<Operation> Operations => _inner.Operations;
    public IAsyncDmmMeasurement? Dmm => trace.Base.MissingDmm ? null : this;
    public IAsyncPsuOutput Psu => this;
    public IAsyncReset Reset => this;
    public IAsyncSafeShutdown Shutdown => this;
    public async ValueTask<Measurement> MeasureVoltageDcAsync(double? range = null, CancellationToken cancellationToken = default)
    {
        var hook = trace.Hook; trace.Hook = null; if (hook is not null) await hook().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); _inner.Exchange([0xa1]); trace.Started.TrySetResult();
        if (trace.Base.FailMeasure) { trace.Base.FailMeasure = false; throw new DriverException(trace.Base.MeasureError, "Injected provider failure after first write."); }
        await Task.Yield();
        if (trace.BlockMeasure) await trace.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadBinary();
    }
    private Measurement ReadBinary() => new(System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(_inner.Exchange([0xb2])));
    public ValueTask SetVoltageAsync(uint channel, double volts, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _inner.SetVoltage(channel, volts); return ValueTask.CompletedTask; }
    public ValueTask<Measurement> ReadVoltageAsync(uint channel, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_inner.ReadVoltage(channel)); }
    public ValueTask OutputEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _inner.OutputEnable(channel, enabled); return ValueTask.CompletedTask; }
    public ValueTask ResetAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); ((IDeviceReset)_inner).Reset(); return ValueTask.CompletedTask; }
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    { trace.ShutdownStarted.TrySetResult(); if (trace.BlockShutdown) await trace.ShutdownRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false); ((ISafeShutdown)_inner).Shutdown(); }
    public async ValueTask RecoverAsync(CancellationToken cancellationToken = default)
    { trace.RecoverStarted.TrySetResult(); if (trace.BlockRecover) await trace.RecoverRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false); _inner.Recover(); }
    public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) _inner.Dispose(); }
}

public sealed class WorkerTrace
{
    public Trace Base { get; } = new();
    public ConcurrentQueue<(string Event, int Thread)> Affinity { get; } = new();
    public bool BlockOpen, FailOpen;
    public ManualResetEventSlim OpenStarted { get; } = new();
    public ManualResetEventSlim OpenRelease { get; } = new();
    internal void Record(string name) => Affinity.Enqueue((name, Environment.CurrentManagedThreadId));
}
public sealed class WorkerFactory(WorkerTrace trace) : IDriverFactory
{
    public static DeviceCandidate Candidate => new(new("fake", "device-1"), new("Acme", "W100"));
    public DriverDescriptor Descriptor { get; } = new("synthetic.worker", DriverContract.Major, [new ExactIdentityRule("fake", "Acme", "W100")], BinaryFactory.Supported);
    public IReadOnlySet<ExecutionMode> ExecutionModes { get; } = new[] { ExecutionMode.WorkerAsync }.ToFrozenSet();
    public IDeviceBackend OpenWorker(DriverOpenContext context)
    {
        trace.Record("open"); Interlocked.Increment(ref trace.Base.Opens);
        var device = new ThreadDevice(trace, context.Ownership);
        try
        {
            trace.OpenStarted.Set();
            if (trace.BlockOpen && !trace.OpenRelease.Wait(TimeSpan.FromSeconds(15))) throw new DriverException(DriverErrorKind.Timeout, "Fixture open release timeout.");
            if (trace.FailOpen) throw new DriverException(DriverErrorKind.Device, "Worker open failed after acquisition.");
            return device;
        }
        catch (Exception failure)
        {
            try { device.Dispose(); } catch (Exception cleanup) { throw new DriverException(failure is DriverException d ? d.Kind : DriverErrorKind.DriverContractViolation, "Worker open cleanup failed.", new AggregateException(failure, cleanup)); }
            throw;
        }
    }
}
internal sealed class ThreadDevice(WorkerTrace trace, ConnectionOwnership ownership)
    : IDeviceBackend, IDmmMeasurement, IPsuOutput, IDeviceReset, ISafeShutdown
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly BinaryDevice _inner = new(trace.Base, ownership);
    private bool _closed;
    private void Check(string name) { if (_thread != Environment.CurrentManagedThreadId) throw new DriverException(DriverErrorKind.DriverContractViolation, "Wrong SDK thread."); trace.Record(name); }
    public IReadOnlySet<Operation> Operations { get { Check("metadata"); return _inner.Operations; } }
    public IDmmMeasurement? Dmm { get { Check("accessor"); return trace.Base.MissingDmm ? null : this; } }
    public IPsuOutput Psu { get { Check("accessor"); return this; } }
    public IDeviceReset Reset { get { Check("accessor"); return this; } }
    public ISafeShutdown Shutdown { get { Check("accessor"); return this; } }
    public Measurement MeasureVoltageDc(double? range = null) { Check("measure"); return _inner.MeasureVoltageDc(range); }
    public void SetVoltage(uint channel, double volts) { Check("set"); _inner.SetVoltage(channel, volts); }
    public Measurement ReadVoltage(uint channel) { Check("read"); return _inner.ReadVoltage(channel); }
    public void OutputEnable(uint channel, bool enabled) { Check("output"); _inner.OutputEnable(channel, enabled); }
    void IDeviceReset.Reset() { Check("reset"); ((IDeviceReset)_inner).Reset(); }
    void ISafeShutdown.Shutdown() { Check("shutdown"); ((ISafeShutdown)_inner).Shutdown(); }
    public void Recover() { Check("recover"); _inner.Recover(); }
    public void Dispose() { Check("close"); if (_closed) return; _closed = true; try { _inner.Dispose(); } finally { trace.Record("drop"); } }
}

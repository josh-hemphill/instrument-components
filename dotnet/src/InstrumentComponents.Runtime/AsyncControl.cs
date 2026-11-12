using InstrumentComponents.Drivers;
using static InstrumentComponents.Runtime.DriverRegistry;

namespace InstrumentComponents.Runtime;

internal sealed class AsyncControl
{
    private readonly object _closeGate = new();
    private TaskCompletionSource? _closed;
    private bool _shutdown, _completed;
    private Exception? _failure;
    internal int Lifecycle, Recovery, Active, Draining, Waiting;
    internal void Check()
    {
        var state = Volatile.Read(ref Lifecycle);
        if (state == 1) throw Error(DriverErrorKind.SessionClosing, "Close requested.");
        if (state == 2) throw Error(DriverErrorKind.SessionClosed, "Session closed.");
    }
    internal bool BeginClose(bool shutdown)
    {
        lock (_closeGate)
        {
            if (_closed is not null) return false;
            _closed = new(TaskCreationOptions.RunContinuationsAsynchronously); _shutdown = shutdown;
            Volatile.Write(ref Lifecycle, 1); return true;
        }
    }
    internal void FinishClose(Exception? failure)
    {
        lock (_closeGate)
        {
            if (_completed) return;
            _completed = true; _failure = failure;
            _closed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref Lifecycle, 2);
            if (failure is null) _closed.TrySetResult(); else _closed.TrySetException(failure);
            Monitor.PulseAll(_closeGate);
        }
    }
    internal void WaitCloseSync(bool shutdown)
    {
        lock (_closeGate)
        {
            while (!_completed) Monitor.Wait(_closeGate);
            if (_failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
            if (shutdown && !_shutdown) throw Error(DriverErrorKind.Shutdown, "First close did not request shutdown.");
        }
    }
    internal async Task WaitClose(bool shutdown, CancellationToken ct)
    {
        Task wait; bool requested;
        lock (_closeGate) { wait = _closed!.Task; requested = _shutdown; }
        await wait.WaitAsync(ct).ConfigureAwait(false);
        if (shutdown && !requested) throw Error(DriverErrorKind.Shutdown, "First close did not request shutdown.");
    }
    internal static bool Uncertain(Exception e) => e is not DriverException { Kind: DriverErrorKind.InvalidArgument or DriverErrorKind.UnsupportedOperation };
    internal static void ValidateRange(double? range)
    { if (range.HasValue && (!double.IsFinite(range.Value) || range.Value <= 0)) throw Error(DriverErrorKind.InvalidArgument, "Invalid range."); }
    internal static void ValidateVoltage(uint channel, double volts)
    { DeviceHandle.Channel(channel); if (!double.IsFinite(volts)) throw Error(DriverErrorKind.InvalidArgument, "Invalid voltage."); }
}

using System.Text.Json;
using System.Runtime.CompilerServices;
using InstrumentComponents.Drivers;
using InstrumentComponents.SyntheticDriver;

namespace InstrumentComponents.Runtime.Tests;

public sealed class AsyncDriverTests
{
    private static JsonDocument Cases() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "driver-async-cases.json")));
    private static bool Flag(JsonElement row, string key) => row.TryGetProperty(key, out var p) && p.GetBoolean();
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); } }
    private static void Configure(Trace trace, JsonElement row)
    {
        if (row.TryGetProperty("measureError", out var kind)) trace.MeasureError = Enum.Parse<DriverErrorKind>(kind.GetString()!);
        trace.FailMeasure = Flag(row, "failMeasure"); trace.FailShutdown = Flag(row, "failShutdown"); trace.FailClose = Flag(row, "failClose"); trace.MissingDmm = Flag(row, "missingDmm"); trace.UnknownInventory = Flag(row, "unknownInventory");
        if (Flag(row, "disableShutdown")) trace.DisabledOperations.Add(Operation.Shutdown);
    }
    [Fact]
    public async Task SharedAsyncCasesRunNativeAndWorker()
    {
        using var data = Cases(); Assert.Equal(1, data.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var mode in new[] { 0, 1, 2 })
        foreach (var row in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            var worker = mode != 0; var syncWorker = mode == 2;
            var nativeTrace = new AsyncTrace { FailOpen = Flag(row, "failOpen") }; var workerTrace = new WorkerTrace { FailOpen = Flag(row, "failOpen") };
            var trace = worker ? workerTrace.Base : nativeTrace.Base; Configure(trace, row);
            var registry = new DriverRegistry([worker ? new WorkerFactory(workerTrace) : new NativeFactory(nativeTrace)]);
            NativeDeviceHandle? native = null; WorkerDeviceHandle? local = null;
            var ownership = Flag(row, "borrowed") ? ConnectionOwnership.Borrowed : ConnectionOwnership.Owned;
            var open = await Record.ExceptionAsync(async () =>
            { if (worker) local = await registry.OpenWorkerAsync(WorkerFactory.Candidate, ownership: ownership); else native = await registry.OpenNativeAsync(NativeFactory.Candidate, ownership: ownership); });
            if (row.TryGetProperty("openError", out var expectedOpen)) Assert.Equal(expectedOpen.GetString(), Assert.IsType<DriverException>(open).Kind.ToString());
            else
            {
                Assert.Null(open);
                foreach (var step in row.GetProperty("steps").EnumerateArray())
                {
                    double? value = null;
                    var failure = await Record.ExceptionAsync(async () =>
                    {
                        if (syncWorker)
                        {
                            switch (step.GetProperty("action").GetString())
                            {
                                case "measure": value = local!.MeasureVoltageDc().Value; break;
                                case "bad-range": value = local!.MeasureVoltageDc(0).Value; break;
                                case "set": local!.SetVoltage(1, step.GetProperty("value").GetDouble()); break;
                                case "read": value = local!.ReadVoltage(1).Value; break;
                                case "output": local!.OutputEnable(1, true); break;
                                case "reset": local!.Reset(); break;
                                case "sense": local!.SetSenseMode(1, SenseMode.External); break;
                                case "recover": local!.Recover(); break;
                                case "close": local!.Close(Flag(step, "shutdown")); break;
                                default: throw new InvalidOperationException();
                            }
                            return;
                        }
                        switch (step.GetProperty("action").GetString())
                        {
                            case "measure": value = worker ? (await local!.MeasureVoltageDcAsync()).Value : (await native!.MeasureVoltageDcAsync()).Value; break;
                            case "bad-range": value = worker ? (await local!.MeasureVoltageDcAsync(0)).Value : (await native!.MeasureVoltageDcAsync(0)).Value; break;
                            case "set": if (worker) await local!.SetVoltageAsync(1, step.GetProperty("value").GetDouble()); else await native!.SetVoltageAsync(1, step.GetProperty("value").GetDouble()); break;
                            case "read": value = worker ? (await local!.ReadVoltageAsync(1)).Value : (await native!.ReadVoltageAsync(1)).Value; break;
                            case "output": if (worker) await local!.OutputEnableAsync(1, true); else await native!.OutputEnableAsync(1, true); break;
                            case "reset": if (worker) await local!.ResetAsync(); else await native!.ResetAsync(); break;
                            case "sense": if (worker) await local!.SetSenseModeAsync(1, SenseMode.External); else await native!.SetSenseModeAsync(1, SenseMode.External); break;
                            case "recover": if (worker) await local!.RecoverAsync(); else await native!.RecoverAsync(); break;
                            case "close": if (worker) await local!.CloseAsync(Flag(step, "shutdown")); else await native!.CloseAsync(Flag(step, "shutdown")); break;
                            default: throw new InvalidOperationException();
                        }
                    });
                    if (step.TryGetProperty("error", out var expected)) Assert.Equal(expected.GetString(), Assert.IsType<DriverException>(failure).Kind.ToString());
                    else { Assert.Null(failure); if (step.GetProperty("action").GetString() is "measure" or "read") Assert.Equal(step.GetProperty("value").GetDouble(), value); }
                }
            }
            await Until(() => trace.Closes == row.GetProperty("closes").GetInt32());
            Assert.Equal(row.GetProperty("shutdowns").GetInt32(), trace.Shutdowns); Assert.Equal(row.GetProperty("recoveries").GetInt32(), trace.Recoveries);
            Assert.Equal(row.TryGetProperty("disposes", out var disposes) ? disposes.GetInt32() : 1, trace.Disposes);
            if (Flag(row, "noIo")) Assert.Empty(trace.Events);
            if (worker) Affinity(workerTrace);
        }
    }
    private static void Affinity(WorkerTrace trace)
    { var events = trace.Affinity.ToArray(); Assert.NotEmpty(events); Assert.All(events, e => Assert.Equal(events[0].Thread, e.Thread)); Assert.Contains(events, e => e.Event == "close"); Assert.Contains(events, e => e.Event == "drop"); }

    [Fact]
    public async Task NativeQueuedAndRunningCancellationRespectOwnership()
    {
        var trace = new AsyncTrace { BlockMeasure = true }; var h = await new DriverRegistry([new NativeFactory(trace)]).OpenNativeAsync(NativeFactory.Candidate);
        using var runningToken = new CancellationTokenSource(); using var queuedToken = new CancellationTokenSource();
        var active = h.MeasureVoltageDcAsync(cancellationToken: runningToken.Token).AsTask(); await trace.Started.Task;
        var queued = h.SetVoltageAsync(1, 9, queuedToken.Token).AsTask(); await Until(() => h.PendingOperations == 1);
        queuedToken.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); Assert.Equal(0, h.PendingOperations);
        runningToken.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.True(h.RecoveryRequired); Assert.Equal(new[] { "A1" }, trace.Base.Events);
        await h.RecoverAsync(); trace.BlockMeasure = false; Assert.Equal(3.3, (await h.MeasureVoltageDcAsync()).Value); await h.CloseAsync(true);
    }
    [Fact]
    public async Task NativeCloseWaiterCancellationDoesNotCancelShutdownAndReentrySpansAwait()
    {
        var trace = new AsyncTrace(); var h = await new DriverRegistry([new NativeFactory(trace)]).OpenNativeAsync(NativeFactory.Candidate);
        trace.Hook = async () => { await Task.Yield(); await h.ResetAsync(); };
        Assert.Equal(DriverErrorKind.DriverContractViolation, (await Assert.ThrowsAsync<DriverException>(() => h.MeasureVoltageDcAsync().AsTask())).Kind);
        await h.RecoverAsync(); trace.BlockShutdown = true; using var token = new CancellationTokenSource();
        var close = h.CloseAsync(true, token.Token); await trace.ShutdownStarted.Task; token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => close);
        Assert.Equal(0, trace.Base.Closes); trace.ShutdownRelease.TrySetResult(); await h.CloseAsync(true); Assert.Equal(1, trace.Base.Closes);
    }
    [Fact]
    public async Task NotAwaitingNativeTaskDoesNotCancelOrReleaseItsLease()
    {
        var trace = new AsyncTrace { BlockMeasure = true }; var h = await new DriverRegistry([new NativeFactory(trace)]).OpenNativeAsync(NativeFactory.Candidate);
        var operation = h.MeasureVoltageDcAsync().AsTask(); await trace.Started.Task;
        var close = h.CloseAsync(); await Until(() => h.PendingOperations == 1); Assert.False(close.IsCompleted); Assert.Equal(0, trace.Base.Closes);
        trace.Release.TrySetResult(); await close; Assert.Equal(3.3, (await operation).Value); Assert.False(h.RecoveryRequired);
    }
    [Fact]
    public async Task NativeCanceledOpenReleasesPartialAcquisition()
    {
        var trace = new AsyncTrace { BlockOpen = true }; using var token = new CancellationTokenSource();
        var open = new DriverRegistry([new NativeFactory(trace)]).OpenNativeAsync(NativeFactory.Candidate, cancellationToken: token.Token);
        await trace.OpenStarted.Task; token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open); Assert.Equal(1, trace.Base.Closes);
    }
    [Fact]
    public async Task WorkerRunningCancellationDrainsOnSameThreadBeforeClose()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var trace = new WorkerTrace(); trace.Base.Started = started; trace.Base.Release = release;
        var h = await new DriverRegistry([new WorkerFactory(trace)]).OpenWorkerAsync(WorkerFactory.Candidate);
        using var token = new CancellationTokenSource(); var active = h.MeasureVoltageDcAsync(cancellationToken: token.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5))); token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.True(h.Draining); Assert.Equal(0, trace.Base.Closes);
        var recovery = h.RecoverAsync(); await Until(() => h.PendingOperations == 1);
        using var closeToken = new CancellationTokenSource(); var close = h.CloseAsync(true, closeToken.Token); closeToken.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => close);
        Assert.Equal(0, trace.Base.Closes); Assert.Equal(0, trace.Base.Recoveries); release.Set(); await h.CloseAsync(true);
        Assert.Contains((await Assert.ThrowsAsync<DriverException>(() => recovery)).Kind, new[] { DriverErrorKind.SessionClosing, DriverErrorKind.SessionClosed });
        Assert.Equal(1, trace.Base.Recoveries); Affinity(trace);
    }
    [Fact]
    public async Task WorkerQueuedCancellationSyncFrontendAndReentry()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var trace = new WorkerTrace(); trace.Base.Started = started; trace.Base.Release = release;
        var h = await new DriverRegistry([new WorkerFactory(trace)]).OpenWorkerAsync(WorkerFactory.Candidate);
        var active = h.MeasureVoltageDcAsync(); Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        using var token = new CancellationTokenSource(); var queued = h.SetVoltageAsync(1, 8, token.Token); await Until(() => h.PendingOperations == 1);
        token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); release.Set(); await active; await Until(() => h.PendingOperations == 0);
        Assert.False(h.RecoveryRequired); Assert.DoesNotContain("set:1", trace.Base.Events);
        h.SetVoltage(1, 7); Assert.Equal(7, (await h.ReadVoltageAsync(1)).Value);
        trace.Base.OnMeasure = h.Reset;
        Assert.Equal(DriverErrorKind.DriverContractViolation, (await Assert.ThrowsAsync<DriverException>(() => h.MeasureVoltageDcAsync())).Kind);
        await h.RecoverAsync(); await h.CloseAsync(true); Affinity(trace);
    }
    [Fact]
    public async Task ClosedWorkerCanBeDisposedAgainFromOtherWorkerExecutionScopes()
    {
        var oldTrace=new WorkerTrace();var closed=await new DriverRegistry([new WorkerFactory(oldTrace)]).OpenWorkerAsync(WorkerFactory.Candidate);
        await closed.CloseAsync();
        for(var i=0;i<16;i++) {
            var trace=new WorkerTrace();trace.Base.OnMeasure=closed.Dispose;
            await using var next=await new DriverRegistry([new WorkerFactory(trace)]).OpenWorkerAsync(WorkerFactory.Candidate);
            Assert.Equal(3.3,(await next.MeasureVoltageDcAsync()).Value);await next.CloseAsync();
        }
        await closed.DisposeAsync();Assert.Equal(1,oldTrace.Base.Closes);
    }
    [Fact]
    public async Task WorkerAbandonedOpenRetainsResourcesUntilOpenReturns()
    {
        var trace = new WorkerTrace { BlockOpen = true }; using var token = new CancellationTokenSource();
        var open = new DriverRegistry([new WorkerFactory(trace)]).OpenWorkerAsync(WorkerFactory.Candidate, cancellationToken: token.Token);
        Assert.True(trace.OpenStarted.Wait(TimeSpan.FromSeconds(5))); token.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Equal(0, trace.Base.Closes); trace.OpenRelease.Set(); await Until(() => trace.Base.Closes == 1); await Until(() => trace.Affinity.Any(e => e.Event == "drop")); Affinity(trace);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompletedOpenWithoutDelivery(WorkerTrace trace)
    {
        var task = new DriverRegistry([new WorkerFactory(trace)]).OpenWorkerAsync(WorkerFactory.Candidate);
        Assert.True(task.Wait(TimeSpan.FromSeconds(5))); return new WeakReference(task);
    }
    [Fact]
    public async Task CompletedUndeliveredWorkerOpenIsFinalizedByRequestingOwnerCleanup()
    {
        var trace = new WorkerTrace(); var task = CompletedOpenWithoutDelivery(trace);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(task.IsAlive); await Until(() => trace.Base.Closes == 1); await Until(() => trace.Affinity.Any(e => e.Event == "drop")); Affinity(trace);
    }
    [Fact]
    public async Task PreCanceledRequestsDoNotOpenOrDispatchAndWorkerFrontendsShareLifecycle()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var nt = new AsyncTrace(); var wt = new WorkerTrace();
        var nr = new DriverRegistry([new NativeFactory(nt)]); var wr = new DriverRegistry([new WorkerFactory(wt)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nr.OpenNativeAsync(NativeFactory.Candidate, cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wr.OpenWorkerAsync(WorkerFactory.Candidate, cancellationToken: canceled.Token));
        Assert.Equal(0, nt.Base.Opens); Assert.Equal(0, wt.Base.Opens);
        var native = await nr.OpenNativeAsync(NativeFactory.Candidate);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.MeasureVoltageDcAsync(cancellationToken: canceled.Token).AsTask());
        Assert.Empty(nt.Base.Events); Assert.False(native.RecoveryRequired); await native.CloseAsync();
        var worker = await wr.OpenWorkerAsync(WorkerFactory.Candidate);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.MeasureVoltageDcAsync(cancellationToken: canceled.Token));
        Assert.Empty(wt.Base.Events); Assert.False(worker.RecoveryRequired);
        var values = await Task.WhenAll(Task.Run(() => worker.MeasureVoltageDc()), worker.MeasureVoltageDcAsync());
        Assert.All(values, value => Assert.Equal(3.3, value.Value));
        Assert.Equal(new[] { "A1", "B2", "A1", "B2" }, wt.Base.Events);
        wt.Base.FailMeasure = true;
        Assert.Equal(DriverErrorKind.Timeout, Assert.Throws<DriverException>(() => worker.MeasureVoltageDc()).Kind);
        Assert.True(worker.RecoveryRequired); worker.Recover(); Assert.False(worker.RecoveryRequired);
        worker.Close(); await worker.CloseAsync();
        Assert.Equal(DriverErrorKind.Shutdown, Assert.Throws<DriverException>(() => worker.Close(true)).Kind);
        Assert.Equal(1, wt.Base.Closes); Assert.Equal(0, wt.Base.Shutdowns); Affinity(wt);
    }
    private sealed class RecoveryFactory(WorkerTrace trace, ManualResetEventSlim started, ManualResetEventSlim release) : IDriverFactory
    {
        private readonly WorkerFactory _factory = new(trace);
        public DriverDescriptor Descriptor => _factory.Descriptor;
        public IReadOnlySet<ExecutionMode> ExecutionModes => _factory.ExecutionModes;
        public IDeviceBackend OpenWorker(DriverOpenContext context) => new RecoveryBackend(_factory.OpenWorker(context), started, release);
    }
    private sealed class RecoveryBackend(IDeviceBackend inner, ManualResetEventSlim started, ManualResetEventSlim release) : IDeviceBackend
    {
        public IReadOnlySet<Operation> Operations => inner.Operations;
        public IDmmMeasurement? Dmm => inner.Dmm;
        public IPsuOutput? Psu => inner.Psu;
        public IDeviceReset? Reset => inner.Reset;
        public ISafeShutdown? Shutdown => inner.Shutdown;
        public void Recover() { started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); inner.Recover(); }
        public void Dispose() => inner.Dispose();
    }
    [Fact]
    public async Task WorkerRecoveryCancellationBeforeAndAfterDispatchRetainsOwnership()
    {
        using var measured = new ManualResetEventSlim(); using var measureRelease = new ManualResetEventSlim();
        using var recovering = new ManualResetEventSlim(); using var recoverRelease = new ManualResetEventSlim();
        var trace = new WorkerTrace(); trace.Base.Started = measured; trace.Base.Release = measureRelease;
        var handle = await new DriverRegistry([new RecoveryFactory(trace, recovering, recoverRelease)]).OpenWorkerAsync(WorkerFactory.Candidate);
        using var activeToken = new CancellationTokenSource();
        var active = handle.MeasureVoltageDcAsync(cancellationToken: activeToken.Token);
        Assert.True(measured.Wait(TimeSpan.FromSeconds(5))); activeToken.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        using var queuedToken = new CancellationTokenSource(); var queuedRecovery = handle.RecoverAsync(queuedToken.Token);
        await Until(() => handle.PendingOperations == 1); queuedToken.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedRecovery);
        measureRelease.Set(); await Until(() => handle.PendingOperations == 0 && !handle.Draining);
        Assert.True(handle.RecoveryRequired); Assert.Equal(0, trace.Base.Recoveries); Assert.False(recovering.IsSet);
        using var dispatchedToken = new CancellationTokenSource(); var dispatched = handle.RecoverAsync(dispatchedToken.Token);
        Assert.True(recovering.Wait(TimeSpan.FromSeconds(5))); dispatchedToken.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatched);
        Assert.True(handle.Draining); Assert.Equal(0, trace.Base.Closes);
        var close = handle.CloseAsync(true); Assert.False(close.IsCompleted); recoverRelease.Set(); await close;
        Assert.Equal(2, trace.Base.Recoveries); Assert.Equal(1, trace.Base.Closes); Affinity(trace);
    }
    [Fact]
    public async Task UnsupportedModeCellsFailBeforeOpening()
    {
        var native = new AsyncTrace(); var worker = new WorkerTrace();
        var nr = new DriverRegistry([new NativeFactory(native)]); var wr = new DriverRegistry([new WorkerFactory(worker)]);
        Assert.Equal(DriverErrorKind.UnsupportedExecutionMode, Assert.Throws<DriverException>(() => nr.Open(NativeFactory.Candidate)).Kind);
        Assert.Equal(DriverErrorKind.UnsupportedExecutionMode, (await Assert.ThrowsAsync<DriverException>(() => wr.OpenNativeAsync(WorkerFactory.Candidate))).Kind);
        Assert.Equal(DriverErrorKind.UnsupportedExecutionMode, (await Assert.ThrowsAsync<DriverException>(() => nr.OpenWorkerAsync(NativeFactory.Candidate))).Kind);
        Assert.Equal(0, native.Base.Opens); Assert.Equal(0, worker.Base.Opens);
    }
}

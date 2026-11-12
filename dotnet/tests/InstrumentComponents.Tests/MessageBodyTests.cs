using InstrumentComponents.Connect;
using InstrumentComponents.Mock;
using InstrumentComponents.Scpi;
using InstrumentComponents.Errors;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Tests;
public class MessageBodyTests
{
    [Fact] public async Task SyncAndNativeAsyncPreserveCompletePayloadBody()
    {
        var options = new ConnectOptions { Retries = 0, Terminator = "\r\n" };
        var sync = new ScpiSession(new MockTransport([new WriteStep { Data = "READ?\r\n" }, new ReadStep { Data = " \t1.25 \r\n" }]), options);
        Assert.Equal(" \t1.25 ", sync.QueryBody("READ?"));
        var native = await AsyncScpiSession.CreateAsync(new MockTransport([new WriteStep { Data = "READ?\r\n" }, new ReadStep { Data = " \t1.25 \r\n" }]), options);
        Assert.Equal(" \t1.25 ", await native.QueryBodyAsync("READ?"));
    }
    [Fact] public async Task RetryPolicyRejectsBeforeSyncOrAsyncIo()
    {
        var options = new ConnectOptions { Retries = 1 };
        var sync = new ScpiSession(new MockTransport([]), options);
        Assert.Throws<InvalidOperationException>(() => sync.QueryBody("READ?"));
        var native = await AsyncScpiSession.CreateAsync(new MockTransport([]), options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.QueryBodyAsync("READ?"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task IndependentDeadlinesExpireStalledOrIncompleteBodiesWithoutRetryOrDrain(bool partial)
    {
        var options = new ConnectOptions { Retries = 0, Terminator = "\r\n", PerOpTimeout = TimeSpan.FromMilliseconds(20) };
        var wire = new StalledWire { Partial = partial }; var sync = new ScpiSession(wire, options);
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<InstrumentTimeoutException>(() => sync.QueryBody("READ?"));
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(15), TimeSpan.FromSeconds(5));
        Assert.Equal(partial ? 3 : 2, wire.Calls);
        var asyncWire = new StalledAsyncWire { Partial = partial }; var native = await AsyncScpiSession.CreateAsync(asyncWire, options);
        started.Restart(); await Assert.ThrowsAsync<InstrumentTimeoutException>(() => native.QueryBodyAsync("READ?"));
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(15), TimeSpan.FromSeconds(5));
        Assert.Equal(partial ? 3 : 2, asyncWire.Calls); Assert.Equal(0, asyncWire.Active);
    }
    private sealed class StalledWire : TransportBase
    {
        internal bool Partial; internal int Calls; private TimeSpan _timeout;
        public override void Write(ReadOnlySpan<byte> data) => Calls++;
        public override int Read(Span<byte> buffer)
        {
            Calls++; if (Partial) { Partial = false; buffer[0] = (byte)'+'; return 1; }
            using var pending = new ManualResetEventSlim(false);
            if (!pending.Wait(_timeout)) throw new InstrumentTimeoutException();
            throw new InvalidOperationException("Pending response unexpectedly completed");
        }
        public override void SetReadTimeout(TimeSpan timeout) => _timeout = timeout;
        public override void Clear() => throw new InvalidOperationException("No automatic clear");
    }
    private sealed class StalledAsyncWire : AsyncTransportBase
    {
        internal bool Partial; internal int Calls, Active; private TimeSpan _timeout;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) { Calls++; return ValueTask.CompletedTask; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Calls++; if (Partial) { Partial = false; buffer.Span[0] = (byte)'+'; return 1; }
            Active++; using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(_timeout);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token); throw new InvalidOperationException("Pending response unexpectedly completed"); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InstrumentTimeoutException(); }
            finally { Active--; }
        }
        public override ValueTask SetReadTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken = default) { _timeout = timeout; return ValueTask.CompletedTask; }
        public override ValueTask ClearAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No automatic clear");
    }
}

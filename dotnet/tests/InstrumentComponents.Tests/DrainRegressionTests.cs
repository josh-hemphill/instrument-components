using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Scpi;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Tests;

public class DrainRegressionTests
{
    private static ConnectOptions Options(uint retries = 2) => new()
    {
        ReadTimeout = TimeSpan.FromSeconds(3), WriteTimeout = TimeSpan.FromSeconds(3),
        Retries = retries, RetryBackoff = TimeSpan.Zero, ReconnectOnFailure = false
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuousDrainStopsAndRestoresTimeout(bool asynchronous)
    {
        var transport = new StreamingTransport();
        if (asynchronous)
        {
            using var session = await AsyncScpiSession.CreateAsync(transport, Options());
            await Assert.ThrowsAsync<InstrumentTimeoutException>(() => session.FlushAsync());
        }
        else
        {
            using var session = new ScpiSession(transport, Options());
            Assert.Throws<InstrumentTimeoutException>(session.Flush);
        }
        Assert.InRange(transport.Reads, 0, 99);
        Assert.Equal(TimeSpan.FromSeconds(3), transport.Timeout);
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 2u)]
    [InlineData(true, 0u)]
    [InlineData(true, 2u)]
    public async Task FailedDrainDoesNotRetryTheQuery(bool asynchronous, uint retries)
    {
        var transport = new StreamingTransport { TimeoutFirstRead = true };
        if (asynchronous)
        {
            using var session = await AsyncScpiSession.CreateAsync(transport, Options(retries));
            await Assert.ThrowsAsync<InstrumentTimeoutException>(() => session.QueryAsync("READ?"));
        }
        else
        {
            using var session = new ScpiSession(transport, Options(retries));
            Assert.Throws<InstrumentTimeoutException>(() => session.Query("READ?"));
        }
        Assert.Equal(1, transport.Writes);
        Assert.InRange(transport.Reads, 1, 99);
        Assert.Equal(TimeSpan.FromSeconds(3), transport.Timeout);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public async Task QueryRecoveryPreservesCancellation(uint retries)
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new StreamingTransport { TimeoutFirstRead = true, CancelOnDrain = cancellation };
        using var session = await AsyncScpiSession.CreateAsync(transport, Options(retries));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync("READ?", cancellation.Token));
        Assert.Equal(1, transport.Writes);
        Assert.True(transport.DrainReceivedCancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(3), transport.Timeout);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task QuietSignalsBudgetAndTimeoutConfigurationRemainDistinct(bool asynchronous, int scenario)
    {
        var transport = new StreamingTransport
        {
            QuietAfter = 2, TimeoutAtQuiet = scenario != 0,
            DelayMs = scenario == 2 ? 45 : 2, FailShortTimeout = scenario == 3
        };
        if (asynchronous)
        {
            using var session = await AsyncScpiSession.CreateAsync(transport, Options());
            if (scenario < 2) await session.FlushAsync();
            else await Assert.ThrowsAsync<InstrumentTimeoutException>(() => session.FlushAsync());
        }
        else
        {
            using var session = new ScpiSession(transport, Options());
            if (scenario < 2) session.Flush();
            else Assert.Throws<InstrumentTimeoutException>(session.Flush);
        }
        if (scenario < 2) Assert.Equal(3, transport.Reads);
        if (scenario == 3) Assert.Equal(0, transport.Reads);
        Assert.Equal(TimeSpan.FromSeconds(3), transport.Timeout);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedTimeoutRestorationIsNotSuccessfulCleanup(bool asynchronous, bool query)
    {
        var transport = new StreamingTransport { TimeoutFirstRead = query, QuietAfter = query ? 2 : 1, TimeoutAtQuiet = true, FailRestore = true };
        if (asynchronous)
        {
            using var session = await AsyncScpiSession.CreateAsync(transport, Options());
            await Assert.ThrowsAsync<TransportException>(() => query ? session.QueryAsync("READ?") : session.FlushAsync());
        }
        else
        {
            using var session = new ScpiSession(transport, Options());
            Assert.Throws<TransportException>(() => { if (query) session.Query("READ?"); else session.Flush(); });
        }
        Assert.Equal(query ? 1 : 0, transport.Writes);
    }

    // The finite guard makes the old unbounded loop fail instead of hanging.
    private sealed class StreamingTransport : TransportBase, IAsyncTransport
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public bool TimeoutFirstRead { get; init; }
        public int? QuietAfter { get; init; }
        public bool TimeoutAtQuiet { get; init; }
        public int DelayMs { get; init; } = 2;
        public bool FailShortTimeout { get; init; }
        public bool FailRestore { get; init; }
        private bool _shortApplied;
        public CancellationTokenSource? CancelOnDrain { get; init; }
        public bool DrainReceivedCancellationToken { get; private set; }
        public override void Write(ReadOnlySpan<byte> data) => Writes++;
        public override int Read(Span<byte> buffer)
        {
            Reads++;
            if (TimeoutFirstRead && Reads == 1) throw new InstrumentTimeoutException();
            if (Reads >= 100) throw new InvalidOperationException("finite fake-stream guard reached");
            if (QuietAfter is int quiet && Reads > quiet)
            {
                if (TimeoutAtQuiet) throw new InstrumentTimeoutException();
                return 0;
            }
            Thread.Sleep(DelayMs);
            buffer[0] = (byte)'x';
            return 1;
        }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout)
        {
            if (FailRestore && _shortApplied && timeout >= TimeSpan.FromSeconds(1)) throw new TransportException("restore failed");
            Timeout = timeout;
            if (timeout <= TimeSpan.FromMilliseconds(50)) _shortApplied = true;
            if (FailShortTimeout && timeout < TimeSpan.FromSeconds(3)) throw new InstrumentTimeoutException();
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Write(data.Span); return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Read(buffer.Span);
            if (CancelOnDrain is not null)
            {
                DrainReceivedCancellationToken = cancellationToken.CanBeCanceled;
                CancelOnDrain.Cancel();
            }
            return ValueTask.FromResult(count);
        }
        public ValueTask SetReadTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); SetReadTimeout(timeout); return ValueTask.CompletedTask;
        }
        public ValueTask ConfigureAsync(ConnectOptions opts, CancellationToken cancellationToken = default) => SetReadTimeoutAsync(opts.IoTimeout(), cancellationToken);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ReconnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("unexpected reconnect");
    }
}

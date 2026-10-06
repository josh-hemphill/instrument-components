using InstrumentComponents.Connect;
using InstrumentComponents.Diagnostics;
using InstrumentComponents.Errors;
using InstrumentComponents.Mock;
using InstrumentComponents.Scpi;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Tests;

public class AsyncReliabilityTests
{
    [Fact]
    public async Task QueryRetriesAfterTimeoutThenSucceeds()
    {
        var transport = new MockTransport([
            new WriteStep { Data = ":MEAS:VOLT:DC?\n" },
            new ReadStep { Data = "1.0\n" },
            new WriteStep { Data = ":MEAS:VOLT:DC?\n" },
            new ReadStep { Data = "1.0\n" },
        ]).FailWrites(1);

        var opts = new ConnectOptions { Retries = 1, RetryBackoff = TimeSpan.FromMilliseconds(1) };
        var session = await AsyncScpiSession.CreateAsync(transport, opts);
        var volts = await session.QueryAsync(":MEAS:VOLT:DC?");
        Assert.Equal("1.0", volts.Trim());
    }

    [Fact]
    public async Task QueryRetriesReadTimeoutFlushesStaleThenSucceeds()
    {
        var transport = new MockTransport([
            new WriteStep { Data = ":MEAS:VOLT:DC?\n" },
            new ReadStep { Data = "1.0\n" },
            new WriteStep { Data = ":MEAS:VOLT:DC?\n" },
            new ReadStep { Data = "3.3\n" },
        ]).FailReads(1);

        var opts = new ConnectOptions { Retries = 1, RetryBackoff = TimeSpan.FromMilliseconds(1), ReconnectOnFailure = false };
        var session = await AsyncScpiSession.CreateAsync(transport, opts);
        var volts = await session.QueryAsync(":MEAS:VOLT:DC?");
        Assert.Equal("3.3", volts.Trim());
    }

    [Fact]
    public async Task QueryReadRetriesExhaustedIsTimeout()
    {
        var transport = new MockTransport([
            new WriteStep { Data = ":MEAS:VOLT:DC?\n" },
            new WriteStep { Data = ":MEAS:VOLT:DC?\n" },
        ]).FailReads(4);

        var opts = new ConnectOptions { Retries = 1, RetryBackoff = TimeSpan.FromMilliseconds(1), ReconnectOnFailure = false };
        var session = await AsyncScpiSession.CreateAsync(transport, opts);
        await Assert.ThrowsAsync<InstrumentTimeoutException>(() => session.QueryAsync(":MEAS:VOLT:DC?"));
    }

    [Fact]
    public async Task ProbeOpcUndefinedHeaderIsUnsupported()
    {
        var transport = new MockTransport([
            new WriteStep { Data = "*OPC?\n" },
            new ReadStep { Data = "-113,\"Undefined header\"\n" },
        ]);
        var session = await AsyncScpiSession.CreateAsync(transport, new ConnectOptions { Retries = 0, ReconnectOnFailure = false });
        Assert.False(await session.ProbeOpcAsync());
        await Assert.ThrowsAsync<InstrumentUnsupportedException>(() => new InstrumentComponents.Ieee4882.AsyncIeee4882(session).WaitCompleteAsync());
    }

    [Fact]
    public async Task ZeroByteReadIsTimeoutWithoutSpin()
    {
        var session = await AsyncScpiSession.CreateAsync(new ZeroByteAsyncTransport(), new ConnectOptions
        {
            Retries = 0,
            ReconnectOnFailure = false,
            ReadTimeout = TimeSpan.FromSeconds(10),
        });
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<InstrumentTimeoutException>(() => session.QueryAsync("*IDN?"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "zero-byte read spun instead of failing closed");
    }

    [Fact]
    public async Task ZeroByteReadDoesNotReconnect()
    {
        var transport = new ReconnectProbeAsyncTransport { ZeroByte = true };
        var session = await AsyncScpiSession.CreateAsync(transport, new ConnectOptions
        {
            Retries = 0,
            ReconnectOnFailure = true,
        });
        await Assert.ThrowsAsync<InstrumentTimeoutException>(() => session.QueryAsync("*IDN?"));
        Assert.Equal(0, transport.Reconnects);
    }

    [Fact]
    public async Task QueryReadTimeoutReconnectsOnceThenSucceeds()
    {
        var transport = new ReconnectProbeAsyncTransport
        {
            RemainingTimeouts = 2,
            Payload = "3.3\n"u8.ToArray(),
        };
        var session = await AsyncScpiSession.CreateAsync(transport, new ConnectOptions
        {
            Retries = 1,
            RetryBackoff = TimeSpan.FromMilliseconds(1),
            ReconnectOnFailure = true,
        });
        var volts = await session.QueryAsync(":MEAS:VOLT:DC?");
        Assert.Equal("3.3", volts.Trim());
        Assert.Equal(1, transport.Reconnects);
    }
}

sealed class ReconnectProbeAsyncTransport : AsyncTransportBase
{
    public int Reconnects { get; private set; }
    public uint RemainingTimeouts { get; set; }
    public bool ZeroByte { get; set; }
    public byte[]? Payload { get; set; }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (ZeroByte)
            return ValueTask.FromResult(0);
        if (RemainingTimeouts > 0)
        {
            RemainingTimeouts--;
            return ValueTask.FromException<int>(new InstrumentTimeoutException());
        }
        if (Payload is { } data)
        {
            Payload = null;
            data.CopyTo(buffer.Span);
            return ValueTask.FromResult(data.Length);
        }
        return ValueTask.FromException<int>(new TransportClosedException());
    }

    public override ValueTask ReconnectAsync(CancellationToken cancellationToken = default)
    {
        Reconnects++;
        return ValueTask.CompletedTask;
    }
}

sealed class ZeroByteAsyncTransport : AsyncTransportBase
{
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0);
}

public class DiagnosticsTests
{
    [Fact]
    public void DiagnosticsRecordsFailuresAndObserverEvents()
    {
        var health = new DeviceHealth();
        var healthLock = new object();
        var events = new List<CommsEvent>();
        var observer = new RecordingObserver(events);

        var diag = new CommsDiagnostics("mock://dmm-1")
            .WithHealth(health, healthLock)
            .WithObserver(observer);

        var transport = new MockTransport([
            new WriteStep { Data = "*IDN?\n" },
            new ReadStep { Data = "Acme,123,SN,1.0\n" },
        ]).FailWrites(5);

        var opts = new ConnectOptions { Retries = 0 };
        var session = new ScpiSession(transport, opts).WithDiagnostics(diag);

        Assert.ThrowsAny<InstrumentException>(() => session.Query("*IDN?"));

        lock (healthLock)
        {
            Assert.Equal(1u, health.ConsecutiveFailures);
            Assert.Equal(1u, health.TotalFailures);
            Assert.NotNull(health.LastError);
        }

        Assert.Contains(events, e => e.Kind == CommsEventKind.Timeout);
        Assert.DoesNotContain(events, e => e.Kind == CommsEventKind.Reconnect);
    }
}

public class AsyncDiagnosticsTests
{
    [Fact]
    public async Task AsyncDiagnosticsRecordsFailuresAndObserverEvents()
    {
        var health = new DeviceHealth();
        var healthLock = new object();
        var events = new List<CommsEvent>();
        var observer = new RecordingObserver(events);

        var diag = new CommsDiagnostics("mock://dmm-1")
            .WithHealth(health, healthLock)
            .WithObserver(observer);

        var transport = new MockTransport([
            new WriteStep { Data = "*IDN?\n" },
            new ReadStep { Data = "Acme,123,SN,1.0\n" },
        ]).FailWrites(5);

        var opts = new ConnectOptions { Retries = 0 };
        var session = (await AsyncScpiSession.CreateAsync(transport, opts)).WithDiagnostics(diag);

        await Assert.ThrowsAnyAsync<InstrumentException>(() => session.QueryAsync("*IDN?"));

        lock (healthLock)
        {
            Assert.Equal(1u, health.ConsecutiveFailures);
            Assert.Equal(1u, health.TotalFailures);
            Assert.NotNull(health.LastError);
        }

        Assert.Contains(events, e => e.Kind == CommsEventKind.Timeout);
        Assert.DoesNotContain(events, e => e.Kind == CommsEventKind.Reconnect);
    }
}

sealed class RecordingObserver(List<CommsEvent> events) : ICommsObserver
{
    public void OnEvent(CommsEvent evt) => events.Add(evt);
}

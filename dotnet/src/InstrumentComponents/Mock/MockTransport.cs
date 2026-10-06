using System.Text;
using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Mock;

/// <summary>Scripted request/response transport for deterministic CI.</summary>
public sealed class MockTransport : TransportBase, IAsyncTransport
{
    private readonly List<ScriptStep> _script;
    private readonly List<ScriptStep> _steps;
    private int _stepIndex;
    private byte[]? _pendingRead;
    private int _readOffset;
    private TransportIdentity _identity = new();
    private uint _failWritesRemaining;
    private uint _failReadsRemaining;

    public MockTransport(IReadOnlyList<ScriptStep> steps)
    {
        _script = steps.ToList();
        _steps = steps.ToList();
    }

    public MockTransport Reopen()
    {
        var t = new MockTransport(_script)
        {
            _identity = _identity,
            _failWritesRemaining = _failWritesRemaining,
            _failReadsRemaining = _failReadsRemaining,
        };
        return t;
    }

    public IReadOnlyList<ScriptStep> Script => _script;

    public MockTransport WithIdentity(TransportIdentity identity)
    {
        _identity = identity;
        return this;
    }

    public MockTransport FailWrites(uint count)
    {
        _failWritesRemaining = count;
        return this;
    }

    /// <summary>Fails the next N reads with timeout without consuming a script step.</summary>
    public MockTransport FailReads(uint count)
    {
        _failReadsRemaining = count;
        return this;
    }

    private ScriptStep PeekStep()
    {
        if (_stepIndex >= _steps.Count)
            throw new MockExhaustedException();
        return _steps[_stepIndex];
    }

    private ScriptStep NextStep()
    {
        if (_stepIndex >= _steps.Count)
            throw new MockExhaustedException();
        return _steps[_stepIndex++];
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (_failWritesRemaining > 0)
        {
            _failWritesRemaining--;
            throw new InstrumentTimeoutException();
        }

        var step = NextStep();
        if (step is WriteStep ws)
        {
            var actual = Encoding.UTF8.GetString(data);
            if (NormalizeCmd(actual) != NormalizeCmd(ws.Data))
                throw new MockMismatchException(ws.Data, actual);
            return;
        }
        throw new MockMismatchException($"write, got {step.GetType().Name}", Encoding.UTF8.GetString(data));
    }

    public override int Read(Span<byte> buffer)
    {
        if (_failReadsRemaining > 0)
        {
            _failReadsRemaining--;
            throw new InstrumentTimeoutException();
        }

        if (buffer.IsEmpty) return 0;
        if (_pendingRead is null)
        {
            if (PeekStep() is not ReadStep)
                throw new InstrumentTimeoutException();
            _pendingRead = Encoding.UTF8.GetBytes(((ReadStep)NextStep()).Data);
            _readOffset = 0;
        }
        var n = Math.Min(buffer.Length, _pendingRead.Length - _readOffset);
        _pendingRead.AsSpan(_readOffset, n).CopyTo(buffer);
        _readOffset += n;
        if (_readOffset == _pendingRead.Length) _pendingRead = null;
        return n;
    }

    public override void Clear()
    {
        if (NextStep() is ClearStep) return;
        throw new MockMismatchException("clear", "unexpected step");
    }

    public override void SetReadTimeout(TimeSpan timeout) { }

    public override TransportIdentity Identity => _identity;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask SetReadTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask ReconnectAsync(CancellationToken cancellationToken = default)
    {
        Reconnect();
        return ValueTask.CompletedTask;
    }

    public ValueTask ConfigureAsync(ConnectOptions opts, CancellationToken cancellationToken = default) =>
        SetReadTimeoutAsync(opts.IoTimeout(), cancellationToken);

    private static string NormalizeCmd(string s) => s.Trim().ToUpperInvariant();
}

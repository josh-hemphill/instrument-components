using System.Buffers;
using System.Text;
using InstrumentComponents.Connect;
using InstrumentComponents.Diagnostics;
using InstrumentComponents.Errors;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Scpi;

/// <summary>Async SCPI session over a transport.</summary>
public sealed class AsyncScpiSession : IDisposable
{
    private readonly IAsyncTransport _transport;
    private readonly ConnectOptions _opts;
    private readonly List<byte> _readBuffer = new(4096);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;
    private string? _pendingErrorReply;
    private bool _skipBlockTerminator;
    private bool? _systErrSupported;
    private bool? _opcSupported;
    private CommsDiagnostics? _diagnostics;
    private string? _pendingCommand;

    public static async Task<AsyncScpiSession> CreateAsync(
        IAsyncTransport transport,
        ConnectOptions opts,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await transport.ConfigureAsync(opts, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            try { (transport as IDisposable)?.Dispose(); } catch { }
            throw;
        }
        var session = new AsyncScpiSession(transport, opts);
        if (opts.ResetOnConnect)
        {
            try { await new global::InstrumentComponents.Ieee4882.AsyncIeee4882(session).ClearStatusAsync(cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { try { session.Dispose(); } catch { } throw; } catch { }
            try { await new global::InstrumentComponents.Ieee4882.AsyncIeee4882(session).ResetAsync(cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { try { session.Dispose(); } catch { } throw; } catch { }
            await session.RestoreIoTimeoutAsync().ConfigureAwait(false);
        }
        return session;
    }

    private AsyncScpiSession(IAsyncTransport transport, ConnectOptions opts)
    {
        _transport = transport;
        _opts = opts;
    }

    public AsyncScpiSession WithDiagnostics(CommsDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        return this;
    }

    public IAsyncTransport Transport => _transport;
    public ConnectOptions Options => _opts;

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await FlushAsyncCore(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task FlushAsyncCore(CancellationToken cancellationToken = default)
    {
        await _transport.SetReadTimeoutAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        var chunk = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            while (true)
            {
                try
                {
                    var n = await _transport.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (n == 0) break;
                }
                catch (InstrumentTimeoutException)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            await RestoreIoTimeoutAsync().ConfigureAwait(false);
        }
        _readBuffer.Clear();
    }

    public async Task WriteAsync(string command, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); await WriteWithRetryAsync(command, false, cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    public Task<string> QueryAsync(string command, CancellationToken cancellationToken = default) =>
        QueryWithTimeoutAsync(command, EffectiveReadTimeout(), cancellationToken);

    public async Task<string> QueryWithTimeoutAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await QueryWithTimeoutAsyncCore(command, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<string> QueryWithTimeoutAsyncCore(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var maxAttempts = ScpiProtocol.MaxWriteAttempts(true, _opts.Retries);
        uint attempts = 0;
        while (true)
        {
            attempts++;
            await WriteWithRetryAsync(command, idempotent: true, cancellationToken).ConfigureAwait(false);
            try
            {
                var bytes = await ReadResponseAsync(timeout, cancellationToken).ConfigureAwait(false);
                return Encoding.UTF8.GetString(bytes).Trim();
            }
            catch (InstrumentTimeoutException) when (attempts < maxAttempts)
            {
                try { await FlushAsyncCore(CancellationToken.None).ConfigureAwait(false); } catch { }
                if (_opts.ReconnectOnFailure)
                    await TryReconnectAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_opts.RetryBackoff * (int)attempts, cancellationToken).ConfigureAwait(false);
            }
            catch (InstrumentTimeoutException)
            {
                try { await FlushAsyncCore(CancellationToken.None).ConfigureAwait(false); } catch { }
                throw;
            }
        }
    }

    private async Task WriteWithRetryAsync(string command, bool idempotent, CancellationToken cancellationToken)
    {
        var payload = ScpiProtocol.NormalizeCommand(command, _opts.Terminator);
        var data = Encoding.UTF8.GetBytes(payload);
        var attempts = 0u;
        var maxAttempts = ScpiProtocol.MaxWriteAttempts(idempotent, _opts.Retries);

        while (true)
        {
            attempts++;
            var started = DateTime.UtcNow;
            _pendingCommand = command;
            try
            {
                await _transport.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                RecordSuccess(CommsEventKind.WriteOk, command, attempts, started);
                return;
            }
            catch (InstrumentTimeoutException) when (attempts < maxAttempts)
            {
                RecordFailure(CommsEventKind.Timeout, command, attempts, started, "write timeout");
                if (_opts.ReconnectOnFailure)
                    await TryReconnectAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_opts.RetryBackoff, cancellationToken).ConfigureAwait(false);
            }
            catch (InstrumentTimeoutException)
            {
                RecordFailure(CommsEventKind.Timeout, command, attempts, started, "write timeout");
                throw;
            }
            catch (Exception ex)
            {
                RecordFailure(CommsEventKind.WriteFailed, command, attempts, started, ex.Message);
                throw;
            }
        }
    }

    private async Task<byte[]> ReadResponseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _readBuffer.Clear();
        var command = _pendingCommand;
        var chunk = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            while (true)
            {
                var started = DateTime.UtcNow;
                int n;
                try
                {
                    var remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(readStarted);
                    if (remaining <= TimeSpan.Zero) throw new InstrumentTimeoutException();
                    await _transport.SetReadTimeoutAsync(remaining, cancellationToken).ConfigureAwait(false);
                    n = await _transport.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
                catch (InstrumentTimeoutException)
                {
                    if (TryCompleteBufferedFrame(command, started, out var timedOutPayload))
                        return timedOutPayload;
                    RecordFailure(CommsEventKind.Timeout, command, 1, started, "read timeout");
                    throw;
                }
                catch (Exception ex)
                {
                    RecordFailure(CommsEventKind.ReadFailed, command, 1, started, ex.Message);
                    throw;
                }

                if (n == 0)
                {
                    if (TryCompleteBufferedFrame(command, started, out var zeroPayload))
                        return zeroPayload;
                    RecordFailure(CommsEventKind.Timeout, command, 1, started, "zero-byte read");
                    throw new InstrumentTimeoutException();
                }

                for (var i = 0; i < n; i++)
                {
                    if (_skipBlockTerminator && (chunk[i] == '\r' || chunk[i] == '\n')) continue;
                    _skipBlockTerminator = false;
                    _readBuffer.Add(chunk[i]);
                }
                if (TryCompleteBufferedFrame(command, started, out var payload))
                    return payload;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            await RestoreIoTimeoutAsync().ConfigureAwait(false);
        }
    }

    private bool TryCompleteBufferedFrame(string? command, DateTime started, out byte[] payload)
    {
        payload = [];
        if (_readBuffer.Count == 0)
            return false;
        try
        {
            (payload, _) = ScpiFraming.ExtractResponse(_readBuffer.ToArray(), _opts.Terminator);
            _skipBlockTerminator = _readBuffer[0] == (byte)'#' && _readBuffer.Count > 1 && _readBuffer[1] != (byte)'0';
            RecordSuccess(CommsEventKind.ReadOk, command, 1, started);
            return true;
        }
        catch (InstrumentTimeoutException)
        {
            return false;
        }
    }

    private void RecordSuccess(CommsEventKind kind, string? command, uint attempt, DateTime started) =>
        _diagnostics?.RecordSuccess(kind, command, attempt, DateTime.UtcNow - started);

    private void RecordFailure(CommsEventKind kind, string? command, uint attempt, DateTime started, string detail) =>
        _diagnostics?.RecordFailure(kind, command, attempt, DateTime.UtcNow - started, detail);

    private void RecordReconnect() =>
        _diagnostics?.RecordSuccess(CommsEventKind.Reconnect, null, 1, TimeSpan.Zero);

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _transport.ReconnectAsync(cancellationToken).ConfigureAwait(false);
            RecordReconnect();
        }
        catch
        {
            // Unsupported or failed reconnect must not look like success.
        }
    }

    private TimeSpan EffectiveReadTimeout() => _opts.PerOpTimeout ?? _opts.ReadTimeout;

    /// Restores the configured I/O timeout without the caller token; swallows restore errors.
    private async ValueTask RestoreIoTimeoutAsync()
    {
        try
        {
            await _transport.SetReadTimeoutAsync(_opts.IoTimeout(), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: do not hide the original I/O result or fail session create.
        }
    }

    public async Task<bool> ProbeSystErrAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ProbeSystErrAsyncCore(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<bool> ProbeSystErrAsyncCore(CancellationToken cancellationToken = default)
    {
        if (_systErrSupported is { } v) return v;
        try
        {
            var resp = await QueryWithTimeoutAsyncCore("SYST:ERR?", TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            _systErrSupported = ScpiProtocol.IsSystErrSupportedReply(resp);
            // A zero reply describes the queue at probe time, not at the next check.
            if (_systErrSupported.Value && !ScpiProtocol.IsNoErrorReply(resp)) _pendingErrorReply = resp;
        }
        catch (InstrumentException) { return false; }
        return _systErrSupported.Value;
    }

    public async Task<bool> ProbeOpcAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ProbeOpcAsyncCore(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<bool> ProbeOpcAsyncCore(CancellationToken cancellationToken = default)
    {
        if (_opcSupported is { } v) return v;
        try
        {
            var resp = await QueryWithTimeoutAsyncCore("*OPC?", TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            _opcSupported = ScpiProtocol.IsOpcSupportedReply(resp);
        }
        catch (InstrumentException) { return false; }
        return _opcSupported.Value;
    }

    public async Task<IReadOnlyList<string>> CheckErrorsAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await CheckErrorsAsyncCore(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<IReadOnlyList<string>> CheckErrorsAsyncCore(CancellationToken cancellationToken = default)
    {
        if (_systErrSupported == false) return Array.Empty<string>();
        var errors = new List<string>();
        while (true)
        {
            var resp = _pendingErrorReply ?? await QueryWithTimeoutAsyncCore("SYST:ERR?", EffectiveReadTimeout(), cancellationToken).ConfigureAwait(false);
            _pendingErrorReply = null;
            if (!ScpiProtocol.IsSystErrSupportedReply(resp))
            {
                if (_systErrSupported is null) { _systErrSupported = false; return errors; }
                throw new ParseException($"invalid error queue reply '{resp}'");
            }
            _systErrSupported = true;
            if (ScpiProtocol.IsNoErrorReply(resp))
                break;
            errors.Add(resp);
            if (errors.Count >= 50) break;
        }
        return errors;
    }

    internal async Task<string> QueryCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureOpcAvailable();
            await WriteWithRetryAsync("*OPC?", false, cancellationToken).ConfigureAwait(false);
            try { return Encoding.UTF8.GetString(await ReadResponseAsync(timeout, cancellationToken).ConfigureAwait(false)).Trim(); }
            catch (InstrumentTimeoutException) { try { await FlushAsyncCore(CancellationToken.None).ConfigureAwait(false); } catch { } throw; }
        }
        finally { _operationGate.Release(); }
    }

    internal void EnsureOpcAvailable()
    {
        if (_opcSupported == false) throw new InstrumentUnsupportedException("operation completion requires *OPC? support");
    }

    public void Dispose()
    {
        _operationGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_transport is IDisposable disposable) disposable.Dispose();
            GC.SuppressFinalize(this);
        }
        finally { _operationGate.Release(); }
    }
}

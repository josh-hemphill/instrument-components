using InstrumentComponents.Connect;

namespace InstrumentComponents.Transport;

/// <summary>Serializes and offloads synchronous transport calls to the thread pool.
/// Cancellation prevents queued work; native calls already started finish before the task returns.
/// </summary>
public sealed class SyncAsAsyncTransport<T> : IAsyncTransport, IDisposable where T : ITransport
{
    private readonly T _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SyncAsAsyncTransport(T inner) => _inner = inner;
    public T Inner => _inner;
    public TransportIdentity Identity => _inner.Identity;

    private async Task<TResult> Run<TResult>(Func<TResult> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Do not cancel the await of a running call: it still owns the supplied buffer.
            return await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return operation(); }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private ValueTask Run(Action operation, CancellationToken ct) =>
        new(Run(() => { operation(); return true; }, ct));

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        Run(() => _inner.Write(data.Span), cancellationToken);
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(Run(() => _inner.Read(buffer.Span), cancellationToken));
    public ValueTask ClearAsync(CancellationToken cancellationToken = default) => Run(_inner.Clear, cancellationToken);
    public ValueTask SetReadTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Run(() => _inner.SetReadTimeout(timeout), cancellationToken);
    public ValueTask ReconnectAsync(CancellationToken cancellationToken = default) => Run(_inner.Reconnect, cancellationToken);
    public ValueTask ConfigureAsync(ConnectOptions opts, CancellationToken cancellationToken = default) =>
        Run(() => _inner.Configure(opts), cancellationToken);

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_inner is IDisposable disposable) disposable.Dispose();
        }
        finally { _gate.Release(); }
    }
}

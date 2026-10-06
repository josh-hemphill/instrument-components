using System.Text;
using InstrumentComponents.Connect;
using InstrumentComponents.Mock;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Recording;

/// <summary>Records I/O on a wrapped transport for replay as a MockTransport script.</summary>
public sealed class RecordingTransport<T> : TransportBase, IDisposable where T : ITransport
{
    private readonly T _inner;
    private bool _disposed;
    private bool _ownsInner = true;
    public List<ScriptStep> Steps { get; } = new();

    public RecordingTransport(T inner) => _inner = inner;

    private T ActiveInner
    {
        get { ObjectDisposedException.ThrowIf(_disposed || !_ownsInner, this); return _inner; }
    }

    public IReadOnlyList<ScriptStep> IntoScript() => Steps;

    public T IntoInner()
    {
        ObjectDisposedException.ThrowIf(_disposed || !_ownsInner, this);
        _ownsInner = false;
        return _inner;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsInner && _inner is IDisposable disposable) disposable.Dispose();
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        Steps.Add(new WriteStep { Data = Encoding.UTF8.GetString(data) });
        ActiveInner.Write(data);
    }

    public override int Read(Span<byte> buffer)
    {
        var n = ActiveInner.Read(buffer);
        Steps.Add(new ReadStep { Data = Encoding.UTF8.GetString(buffer[..n]) });
        return n;
    }

    public override void Clear()
    {
        Steps.Add(new ClearStep());
        ActiveInner.Clear();
    }

    public override void SetReadTimeout(TimeSpan timeout) => ActiveInner.SetReadTimeout(timeout);

    public override void Reconnect() => ActiveInner.Reconnect();

    public override TransportIdentity Identity => ActiveInner.Identity;

    public override void Configure(ConnectOptions opts) => ActiveInner.Configure(opts);
}

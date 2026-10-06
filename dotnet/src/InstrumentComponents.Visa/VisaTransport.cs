using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Transport;
using Ivi.Visa;

namespace InstrumentComponents.Visa;

/// <summary>VISA instrument session transport (sync).</summary>
public sealed class VisaTransport : TransportBase, IDisposable
{
    private IMessageBasedSession? _session;
    private readonly TransportIdentity _identity;

    public VisaTransport(IMessageBasedSession session, TransportIdentity? identity = null)
    {
        _session = session;
        _identity = identity ?? new TransportIdentity();
    }

    public IMessageBasedSession Session =>
        _session ?? throw new TransportClosedException();

    public override void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            Session.RawIO.Write(data.ToArray());
        }
        catch (IOTimeoutException)
        {
            throw new InstrumentTimeoutException();
        }
        catch (Exception ex)
        {
            throw new TransportException(ex.Message, ex);
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            var data = Session.RawIO.Read(buffer.Length);
            if (data.Length == 0)
                throw new TransportClosedException();
            data.CopyTo(buffer);
            return data.Length;
        }
        catch (IOTimeoutException)
        {
            throw new InstrumentTimeoutException();
        }
        catch (TransportClosedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TransportException(ex.Message, ex);
        }
    }

    public override void Clear()
    {
        try
        {
            Session.Clear();
        }
        catch (Exception ex)
        {
            throw new TransportException(ex.Message, ex);
        }
    }

    public override void SetReadTimeout(TimeSpan timeout)
    {
        try
        {
            Session.TimeoutMilliseconds = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
        }
        catch (Exception ex)
        {
            throw new TransportException(ex.Message, ex);
        }
    }

    public override void Reconnect() =>
        throw new InstrumentUnsupportedException("reconnect");

    public override TransportIdentity Identity => _identity;

    public override void Configure(ConnectOptions opts) => SetReadTimeout(opts.IoTimeout());

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}

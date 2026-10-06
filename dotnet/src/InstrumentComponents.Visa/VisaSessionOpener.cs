using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Session;
using InstrumentComponents.Transport;
using Ivi.Visa;

namespace InstrumentComponents.Visa;

/// <summary>Opens VISA sessions for InstrumentComponents.</summary>
public sealed class VisaSessionOpener : ISessionOpener, IAsyncSessionOpener
{
    private readonly Func<string, AccessModes, int, IVisaSession> _open;
    public VisaSessionOpener() : this((address, access, timeout) => GlobalResourceManager.Open(address, access, timeout)) { }
    internal VisaSessionOpener(Func<string, AccessModes, int, IVisaSession> open) => _open = open;

    public ITransport Open(ResourceAddress address, ConnectOptions opts)
    {
        var accessMode = MapAccessMode(opts.AccessMode);
        try
        {
            var session = _open(address.Raw, accessMode, (int)opts.OpenTimeout.TotalMilliseconds);
            if (session is IMessageBasedSession messageSession) return new VisaTransport(messageSession);
            try { session.Dispose(); } catch { }
            throw new InstrumentUnsupportedException($"VISA resource '{address.Raw}' is not a message session");
        }
        catch (InstrumentException) { throw; }
        catch (NativeVisaException ex) when (ex.ErrorCode == -1073807300) { throw new SessionLimitException(address.Raw, ex); }
        catch (NativeVisaException ex) when (ex.ErrorCode == -1073807339) { throw new InstrumentTimeoutException(); }
        catch (IOTimeoutException) { throw new InstrumentTimeoutException(); }
        catch (Exception ex) { throw new TransportException(ex.Message, ex); }
    }

    public async ValueTask<IAsyncTransport> OpenAsync(ResourceAddress address, ConnectOptions opts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transport = await Task.Run(() => Open(address, opts), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            (transport as IDisposable)?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new VisaAsyncTransport((VisaTransport)transport);
    }

    internal static AccessModes MapAccessMode(AccessMode mode)
    {
        if (mode.SharedLock)
            throw new InstrumentUnsupportedException(
                "Ivi.Visa AccessModes does not support shared lock");
        return mode.ExclusiveLock ? AccessModes.ExclusiveLock : AccessModes.None;
    }
}

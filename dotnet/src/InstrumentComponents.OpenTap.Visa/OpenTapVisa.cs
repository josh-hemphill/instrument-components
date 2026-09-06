using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;
using InstrumentComponents.Transport;
using InstrumentComponents.Visa;

namespace InstrumentComponents.OpenTap.Visa;

/// <summary>Registers a VISA-backed <see cref="IOpenTapScpiIoProvider"/> without the main pack importing IVI.</summary>
public static class OpenTapVisa
{
    /// <summary>Assigns the VISA provider only when none is registered.</summary>
    public static void Register() =>
        OpenTapScpiIo.Provider ??= new OpenTapVisaScpiIoProvider();

    /// <summary>Replaces the process-wide provider with the VISA implementation.</summary>
    public static void RegisterOverride() =>
        OpenTapScpiIo.Provider = new OpenTapVisaScpiIoProvider();
}

/// <summary>Opens NI/Keysight VISA message sessions for OpenTAP <c>VisaAddress</c>.</summary>
public sealed class OpenTapVisaScpiIoProvider : IOpenTapScpiIoProvider
{
    private readonly ISessionOpener _opener;

    public OpenTapVisaScpiIoProvider() : this(new VisaSessionOpener())
    {
    }

    public OpenTapVisaScpiIoProvider(ISessionOpener opener)
    {
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
    }

    public IScpiIo Open(string visaAddress, TimeSpan ioTimeout)
    {
        if (string.IsNullOrWhiteSpace(visaAddress))
            throw new InvalidAddressException("empty address");

        var address = ResourceAddress.Parse(visaAddress);
        if (address.Interface == InterfaceKind.Unknown)
            throw new InvalidAddressException(visaAddress);

        var timeout = ioTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : ioTimeout;
        var opts = new ConnectOptions
        {
            OpenTimeout = timeout,
            ReadTimeout = timeout,
            WriteTimeout = timeout,
            PerOpTimeout = timeout,
            ResetOnConnect = false,
        };

        ITransport? transport = null;
        try
        {
            transport = _opener.Open(address, opts);
            return new ScpiSession(transport, opts);
        }
        catch
        {
            (transport as IDisposable)?.Dispose();
            throw;
        }
    }
}

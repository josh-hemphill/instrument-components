using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Scpi;
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
        var transport = new VisaSessionOpener().Open(address, opts);
        return new ScpiSession(transport, opts);
    }
}


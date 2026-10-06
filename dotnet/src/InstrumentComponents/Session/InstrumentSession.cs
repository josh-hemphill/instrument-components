using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Diagnostics;
using InstrumentComponents.Dialects;
using InstrumentComponents.Errors;
using InstrumentComponents.Identity;
using InstrumentComponents.Ieee4882;
using InstrumentComponents.Kind;
using InstrumentComponents.Scpi;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Session;

/// <summary>Active instrument session with SCPI and cached identity.</summary>
public sealed class InstrumentSession : IDisposable
{
    public ResourceAddress Address { get; }
    public ScpiSession Scpi { get; }
    private readonly DeviceIdentity _identity;
    internal string DmmMeasurementUnit { get; set; } = "";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, byte> _powerSupplyChannels = new();
    private uint? _powerSupplyChannelCount;
    /// <summary>Explicit physical channel count for devices whose dialect has no channel metadata.</summary>
    public uint? PowerSupplyChannelCount
    {
        get => _powerSupplyChannelCount;
        set { if (value == 0) throw new ArgumentOutOfRangeException(nameof(value)); _powerSupplyChannelCount = value; }
    }
    internal void TrackPowerSupplyChannel(uint channel)
    {
        if (channel == 0) throw new ArgumentOutOfRangeException(nameof(channel));
        _powerSupplyChannels.TryAdd(channel, 0);
    }
    internal IReadOnlyList<uint> PowerSupplyChannels(uint dialectCount) =>
        Enumerable.Range(1, checked((int)Math.Max(1, PowerSupplyChannelCount ?? dialectCount)))
            .Select(x => (uint)x).Concat(_powerSupplyChannels.Keys).Distinct().Order().ToArray();

    public InstrumentSession(
        ResourceAddress address,
        ITransport transport,
        ConnectOptions opts,
        DeviceIdentity identity,
        CommsDiagnostics? diagnostics = null)
    {
        Address = address;
        _identity = identity;
        var scpi = new ScpiSession(transport, opts);
        if (diagnostics is not null)
            scpi = scpi.WithDiagnostics(diagnostics!);
        Scpi = scpi;
    }

    /// <summary>
    /// Builds a session over host-owned message I/O (no VISA open, no extra framing).
    /// Pass <paramref name="ownsIo"/> false when the host retains I/O lifetime (OpenTAP pack).
    /// </summary>
    public static InstrumentSession FromIo(
        ResourceAddress address,
        IScpiIo io,
        DeviceIdentity identity,
        CommsDiagnostics? diagnostics = null,
        bool ownsIo = true)
    {
        ArgumentNullException.ThrowIfNull(io);
        var scpi = new ScpiSession(io, ownsIo);
        if (diagnostics is not null)
            scpi = scpi.WithDiagnostics(diagnostics);
        return new InstrumentSession(address, scpi, identity);
    }

    private InstrumentSession(ResourceAddress address, ScpiSession scpi, DeviceIdentity identity)
    {
        Address = address;
        Scpi = scpi;
        _identity = identity;
    }

    public string AddressStr => Address.Raw;
    public DeviceIdentity Identity => _identity;

    /// <summary>Resolves the dialect profile for <paramref name="kind"/> using this session's identity.</summary>
    public DialectProfile DialectFor(InstrumentKind kind) =>
        DialectRegistry.Resolve(kind, _identity.Manufacturer, _identity.Model);

    public Idn Idn() => ScpiComm("idn", scpi => new global::InstrumentComponents.Ieee4882.Ieee4882(scpi).Idn());

    public void Reset() => ScpiComm("*RST", scpi => scpi.WithTransaction(() => { DmmMeasurementUnit = ""; new global::InstrumentComponents.Ieee4882.Ieee4882(scpi).Reset(); return true; }));

    public void ClearStatus() => ScpiComm("*CLS", scpi => { new global::InstrumentComponents.Ieee4882.Ieee4882(scpi).ClearStatus(); return true; });

    public void WaitComplete() => ScpiComm("*OPC?", scpi => { new global::InstrumentComponents.Ieee4882.Ieee4882(scpi).WaitComplete(); return true; });

    public IReadOnlyList<string> CheckErrors() => ScpiComm("SYST:ERR?", scpi => scpi.CheckErrors());

    private T ScpiComm<T>(string command, Func<ScpiSession, T> action)
    {
        try
        {
            return action(Scpi);
        }
        catch (InstrumentException ex) when (ex is not CommunicationException)
        {
            throw new CommunicationException(Address.Raw, command, 1, ex);
        }
    }

    public void Dispose() => Scpi.Dispose();
}

/// <summary>Reuses a single underlying session across typed views.</summary>
public sealed class SessionPool : IDisposable
{
    private readonly InstrumentSession _session;
    private readonly object _lock = new();

    public SessionPool(InstrumentSession session) => _session = session;

    [Obsolete("Use WithSession for a multi-command transaction. Individual SCPI queries are serialized by the session.")]
    public InstrumentSession Lock() => _session;

    public void Dispose() { lock (_lock) _session.Dispose(); }

    public T WithSession<T>(Func<InstrumentSession, T> operation)
    {
        lock (_lock) return _session.Scpi.WithTransaction(() => operation(_session));
    }
}

public static class SessionHelpers
{
    public static void EnsureKindSupported(ResourceAddress address, InstrumentKind kind, IReadOnlyList<InstrumentKind> supported)
    {
        if (supported.Contains(kind)) return;
        throw new UnsupportedKindException(address.Raw, kind, supported);
    }
}

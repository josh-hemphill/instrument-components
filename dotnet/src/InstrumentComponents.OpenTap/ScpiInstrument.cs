using InstrumentComponents.Address;
using InstrumentComponents.Classes;
using InstrumentComponents.Classifier;
using InstrumentComponents.Errors;
using InstrumentComponents.Identity;
using InstrumentComponents.Kind;
using InstrumentComponents.Registry;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;
using OpenTap;

namespace InstrumentComponents.OpenTap;

/// <summary>Shared SCPI resource: VisaAddress, injected session, identity, and extra class views.</summary>
public abstract class ScpiInstrument : Instrument, IInstrumentIdentity, IInstrumentShutdown
{
    public const int DefaultIoTimeoutMilliseconds = 5000;
    public const int MinIoTimeoutMilliseconds = 100;
    public const int MaxIoTimeoutMilliseconds = 120_000;

    private IScpiIo? _attached;
    private bool _ownsAttached;
    private InstrumentSession? _session;
    private readonly DeviceIdentity _identity = new();
    private IReadOnlyList<InstrumentKind> _supportedKinds = [];

    protected ScpiInstrument()
    {
    }

    protected ScpiInstrument(IScpiIo io)
    {
        _attached = io ?? throw new ArgumentNullException(nameof(io));
    }

    [VisaAddress]
    [Display("Visa Address", Group: "Communication", Order: 1)]
    public string VisaAddress { get; set; } = string.Empty;

    [Display("IO Timeout (ms)", Group: "Communication", Order: 2)]
    public int IoTimeoutMilliseconds { get; set; } = DefaultIoTimeoutMilliseconds;

    [EmbedProperties]
    [Display("Identity", Order: 10)]
    public ScpiIdentityFields IdentityFields { get; set; } = new();

    /// <summary>Host injects an already-open message session after TestPlan.Load.</summary>
    public void AttachSession(IScpiIo io)
    {
        DisposeOwnedAttached();
        _attached = io ?? throw new ArgumentNullException(nameof(io));
        _ownsAttached = false;
        var reconnect = IsConnected;
        DropSession();
        if (reconnect)
            ConnectAttached();
    }

    public override void Open()
    {
        EnsureAttached();
        if (_attached is null)
        {
            throw new InvalidOperationException(
                "No SCPI session attached. The host must call AttachSession, pass the IScpiIo constructor, or register OpenTapScpiIo.Provider before Open. This pack does not open a vendor VISA resource manager.");
        }

        if (IsConnected && _session is not null)
            return;

        try
        {
            ConnectAttached();
            if (!IsConnected)
                base.Open();
        }
        catch
        {
            AbandonFailedOpen();
            throw;
        }
    }

    public override void Close()
    {
        DropSession();
        DisposeOwnedAttached();
        if (IsConnected)
            base.Close();
    }

    public Idn QueryIdn() => RequireSession().Idn();

    public void Reset() => RequireSession().Reset();

    public abstract void OutputOff();

    /// <summary>Kind of this OpenTAP resource type; extra views of other kinds are classified.</summary>
    protected abstract InstrumentKind PrimaryKind { get; }

    public Dmm AsDmm() => View(InstrumentKind.Dmm, session => new Dmm(session));

    public DcPowerSupply AsDcPowerSupply() =>
        View(InstrumentKind.DcPowerSupply, session => new DcPowerSupply(session));

    public FunctionGenerator AsFunctionGenerator() =>
        View(InstrumentKind.FunctionGenerator, session => new FunctionGenerator(session));

    public Oscilloscope AsOscilloscope() =>
        View(InstrumentKind.Oscilloscope, session => new Oscilloscope(session));

    public Switch AsSwitch() => View(InstrumentKind.Switch, session => new Switch(session));

    public Counter AsCounter() => View(InstrumentKind.Counter, session => new Counter(session));

    public PowerMeter AsPowerMeter() =>
        View(InstrumentKind.PowerMeter, session => new PowerMeter(session));

    public SpectrumAnalyzer AsSpectrumAnalyzer() =>
        View(InstrumentKind.SpectrumAnalyzer, session => new SpectrumAnalyzer(session));

    private void EnsureAttached()
    {
        if (_attached is not null)
            return;

        var provider = OpenTapScpiIo.Provider;
        if (provider is null)
            return;

        if (string.IsNullOrWhiteSpace(VisaAddress))
        {
            throw new InvalidOperationException(
                $"Set {nameof(VisaAddress)} before Open when using {nameof(OpenTapScpiIo)}.{nameof(OpenTapScpiIo.Provider)}.");
        }

        _attached = provider.Open(VisaAddress, TimeSpan.FromMilliseconds(ClampTimeout()));
        _ownsAttached = true;
    }

    private void ConnectAttached()
    {
        if (_attached is null)
        {
            throw new InvalidOperationException(
                "No SCPI session attached. The host must call AttachSession, pass the IScpiIo constructor, or register OpenTapScpiIo.Provider before Open. This pack does not open a vendor VISA resource manager.");
        }

        DropSession();
        _attached.IoTimeout = TimeSpan.FromMilliseconds(ClampTimeout());
        _session = InstrumentSession.FromIo(
            ParseOrFallback(VisaAddress),
            _attached,
            _identity,
            ownsIo: false);
        try
        {
            var idn = _session.Idn();
            _identity.Manufacturer = idn.Manufacturer;
            _identity.Model = idn.Model;
            _identity.Serial = idn.Serial;
            _identity.Firmware = idn.Firmware;
            IdentityFields.CopyFrom(idn);
            RefreshSupportedKinds(idn);
        }
        catch
        {
            AbandonFailedOpen();
            throw;
        }
    }

    private void AbandonFailedOpen()
    {
        DropSession();
        DisposeOwnedAttached();
        ClearIdentity();
        if (IsConnected)
            base.Close();
    }

    private void DropSession()
    {
        var session = _session;
        _session = null;
        _supportedKinds = [];
        session?.Dispose();
    }

    private void DisposeOwnedAttached()
    {
        if (!_ownsAttached)
            return;

        _ownsAttached = false;
        var attached = _attached;
        _attached = null;
        attached?.Dispose();
    }

    private T View<T>(InstrumentKind kind, Func<InstrumentSession, T> factory)
    {
        var session = RequireSession();
        if (kind != PrimaryKind)
        {
            var known = _supportedKinds.Where(k => k != InstrumentKind.Unknown).Distinct().ToList();
            if (known.Count > 0)
                SessionHelpers.EnsureKindSupported(session.Address, kind, known);
        }

        return factory(session);
    }

    private void RefreshSupportedKinds(Idn idn)
    {
        var (_, classified) = Classifier.Classifier.ClassifyFromIdentity(idn, ModelRegistry.Embedded());
        _supportedKinds = classified.Select(k => k.Kind).Distinct().ToList();
    }

    private void ClearIdentity()
    {
        _identity.Manufacturer = null;
        _identity.Model = null;
        _identity.Serial = null;
        _identity.Firmware = null;
        IdentityFields.Manufacturer = string.Empty;
        IdentityFields.Model = string.Empty;
        IdentityFields.Serial = string.Empty;
        IdentityFields.Firmware = string.Empty;
    }

    protected InstrumentSession RequireSession() =>
        _session ?? throw new InvalidOperationException($"{GetType().Name} is not open.");

    protected int ClampTimeout() => Math.Clamp(
        IoTimeoutMilliseconds,
        MinIoTimeoutMilliseconds,
        MaxIoTimeoutMilliseconds);

    internal static ResourceAddress ParseOrFallback(string visaAddress)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(visaAddress))
                return ResourceAddress.Parse(visaAddress);
        }
        catch (InvalidAddressException)
        {
            // Host may leave an alias that this parser does not model.
        }

        return ResourceAddress.Parse("mock://unspecified");
    }
}

public sealed class ScpiIdentityFields
{
    [Display("Manufacturer", Order: 1)]
    public string Manufacturer { get; set; } = string.Empty;

    [Display("Model", Order: 2)]
    public string Model { get; set; } = string.Empty;

    [Display("Serial", Order: 3)]
    public string Serial { get; set; } = string.Empty;

    [Display("Firmware", Order: 4)]
    public string Firmware { get; set; } = string.Empty;

    public void CopyFrom(Idn idn)
    {
        Manufacturer = idn.Manufacturer;
        Model = idn.Model;
        Serial = idn.Serial;
        Firmware = idn.Firmware;
    }
}

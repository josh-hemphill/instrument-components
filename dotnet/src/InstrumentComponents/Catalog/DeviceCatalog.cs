using InstrumentComponents.Classifier;
using InstrumentComponents.Classes;
using InstrumentComponents.Connect;
using InstrumentComponents.Device;
using InstrumentComponents.Diagnostics;
using InstrumentComponents.Errors;
using InstrumentComponents.Identity;
using InstrumentComponents.Kind;
using InstrumentComponents.Mock;
using InstrumentComponents.Registry;
using InstrumentComponents.Session;

namespace InstrumentComponents.Catalog;

/// <summary>Catalog of discovered or injected devices.</summary>
public sealed class DeviceCatalog
{
    private readonly ISessionOpener _opener;
    private readonly IAsyncSessionOpener? _asyncOpener;
    private readonly List<DiscoveredDevice> _devices;
    private readonly Dictionary<string, int> _byAddress = new();
    private readonly Dictionary<string, int> _byDeviceId = new();
    private readonly Dictionary<string, DeviceHealth> _healthRegistry = new();
    private readonly Dictionary<string, object> _healthLocks = new();
    private ConnectOptions _connectOptions = new();
    private readonly ICommsObserver? _observer;

    private DeviceCatalog(
        ISessionOpener opener,
        List<DiscoveredDevice> devices,
        ICommsObserver? observer,
        IAsyncSessionOpener? asyncOpener = null)
    {
        _opener = opener;
        _asyncOpener = asyncOpener;
        _devices = devices;
        _observer = observer;
        for (var idx = 0; idx < devices.Count; idx++)
        {
            var dev = devices[idx];
            _byAddress[dev.Address.Raw] = idx;
            _byDeviceId[dev.GetDeviceId().Value] = idx;
            _healthRegistry[dev.Address.Raw] = new DeviceHealth();
            _healthLocks[dev.Address.Raw] = new object();
        }
    }

    public DeviceCatalog WithConnectOptions(ConnectOptions opts)
    {
        _connectOptions = opts;
        return this;
    }

    public ConnectOptions ConnectOptions => _connectOptions;

    public static DeviceCatalog FromDevices(ISessionOpener opener, IReadOnlyList<DiscoveredDevice> devices) =>
        new(opener, devices.ToList(), null);

    public static DeviceCatalog FromDevicesWithObserver(
        ISessionOpener opener,
        IReadOnlyList<DiscoveredDevice> devices,
        ICommsObserver? observer,
        IAsyncSessionOpener? asyncOpener = null) =>
        new(opener, devices.ToList(), observer, asyncOpener);

    public static DeviceCatalog FromFixture(string address, ScriptedFixture fixture)
    {
        var addr = address.StartsWith("mock://", StringComparison.Ordinal)
            ? Address.ResourceAddress.Parse(address)
            : MockAddress.Parse(address);

        var idn = fixture.Idn;
        var kinds = fixture.Kinds.ToList();
        var transport = fixture.IntoTransport();
        var opener = new MockSessionOpener();
        opener.Register(addr.Raw, transport);

        var (identity, layer1) = Classifier.Classifier.ClassifyFromAddress(addr, ModelRegistry.Embedded());
        identity.Manufacturer = idn.Manufacturer;
        identity.Model = idn.Model;
        identity.Serial = idn.Serial;
        identity.Firmware = idn.Firmware;

        var (supported, classification) = Classifier.Classifier.MergeClassifications(
            [layer1],
            kinds.Count == 0 ? null : kinds);

        var device = new DiscoveredDevice
        {
            Address = addr,
            Identity = identity,
            SupportedKinds = supported,
            Classification = classification,
            Reachable = true,
        };

        return FromDevices(opener, [device]);
    }

    public IReadOnlyList<DiscoveredDevice> Devices => _devices;

    public IReadOnlyList<DiscoveredDevice> DevicesByKind(InstrumentKind kind) =>
        _devices.Where(d => d.SupportedKinds.Contains(kind)).ToList();

    public DeviceRef Device(string address)
    {
        if (!_byAddress.TryGetValue(address, out var idx))
            throw new DeviceNotFoundException(address);
        return DeviceAt(idx);
    }

    public DeviceRef DeviceById(DeviceId id)
    {
        if (!_byDeviceId.TryGetValue(id.Value, out var idx))
            throw new DeviceNotFoundException(id.Value);
        return DeviceAt(idx);
    }

    public DeviceRef ReconnectByIdentity(DeviceId id) => DeviceById(id);

    public DeviceHealth Health(string address)
    {
        if (!_healthRegistry.TryGetValue(address, out var health))
            throw new DeviceNotFoundException(address);
        lock (_healthLocks[address])
            return new DeviceHealth
            {
                ConsecutiveFailures = health.ConsecutiveFailures,
                TotalOperations = health.TotalOperations,
                TotalFailures = health.TotalFailures,
                LastError = health.LastError,
                LastSuccessUnixMs = health.LastSuccessUnixMs,
                LastFailureUnixMs = health.LastFailureUnixMs,
            };
    }

    private DeviceRef DeviceAt(int idx)
    {
        var dev = _devices[idx];
        return new DeviceRef(dev, _opener, _connectOptions, _healthRegistry[dev.Address.Raw], _healthLocks[dev.Address.Raw], _observer, _asyncOpener);
    }

    public Dmm OpenDmm(string address) => Device(address).OpenDmm();
    public DcPowerSupply OpenDcPowerSupply(string address) => Device(address).OpenDcPowerSupply();
    public FunctionGenerator OpenFunctionGenerator(string address) => Device(address).OpenFunctionGenerator();
    public Oscilloscope OpenOscilloscope(string address) => Device(address).OpenOscilloscope();
    public Switch OpenSwitch(string address) => Device(address).OpenSwitch();
    public Counter OpenCounter(string address) => Device(address).OpenCounter();
    public PowerMeter OpenPowerMeter(string address) => Device(address).OpenPowerMeter();
    public SpectrumAnalyzer OpenSpectrumAnalyzer(string address) => Device(address).OpenSpectrumAnalyzer();

    public void PrintSummary()
    {
        foreach (var dev in _devices)
        {
            Console.WriteLine(
                $"{dev.Identity.Model ?? "?"} ({dev.GetDeviceId()}) @ {dev.Address.Raw} — kinds: [{string.Join(", ", dev.SupportedKinds)}] reachable: {dev.Reachable}");
        }
    }
}

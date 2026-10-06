using InstrumentComponents.Address;
using InstrumentComponents.Classifier;
using InstrumentComponents.Classes;
using InstrumentComponents.Connect;
using InstrumentComponents.Diagnostics;
using InstrumentComponents.Identity;
using InstrumentComponents.Kind;
using InstrumentComponents.Session;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Device;

/// <summary>Handle to a catalog device — cheap to clone, opens sessions on demand.</summary>
public sealed class DeviceRef
{
    private readonly DiscoveredDevice _device;
    private readonly ISessionOpener _opener;
    private readonly IAsyncSessionOpener? _asyncOpener;
    private ConnectOptions _connectOptions;
    private readonly DeviceHealth _health;
    private readonly object _healthLock;
    private readonly ICommsObserver? _observer;

    internal DeviceRef(
        DiscoveredDevice device,
        ISessionOpener opener,
        ConnectOptions connectOptions,
        DeviceHealth health,
        object healthLock,
        ICommsObserver? observer,
        IAsyncSessionOpener? asyncOpener = null)
    {
        _device = device;
        _opener = opener;
        _asyncOpener = asyncOpener;
        _connectOptions = connectOptions;
        _health = health;
        _healthLock = healthLock;
        _observer = observer;
    }

    public DiscoveredDevice Discovered => _device;
    public ResourceAddress Address => _device.Address;
    public IReadOnlyList<InstrumentKind> SupportedKinds => _device.SupportedKinds;
    public ConnectOptions ConnectOptions => _connectOptions;

    public DeviceRef WithConnectOptions(ConnectOptions opts)
    {
        _connectOptions = opts;
        return this;
    }

    public DeviceHealth Health()
    {
        lock (_healthLock)
            return new DeviceHealth
            {
                ConsecutiveFailures = _health.ConsecutiveFailures,
                TotalOperations = _health.TotalOperations,
                TotalFailures = _health.TotalFailures,
                LastError = _health.LastError,
                LastSuccessUnixMs = _health.LastSuccessUnixMs,
                LastFailureUnixMs = _health.LastFailureUnixMs,
            };
    }

    private CommsDiagnostics CreateDiagnostics()
    {
        var diag = new CommsDiagnostics(_device.Address.Raw).WithHealth(_health, _healthLock);
        if (_observer is not null)
            diag = diag.WithObserver(_observer);
        return diag;
    }

    public InstrumentSession OpenSession()
    {
        var transport = _opener.Open(_device.Address, _connectOptions);
        return new InstrumentSession(_device.Address, transport, _connectOptions, _device.Identity, CreateDiagnostics());
    }

    public SessionPool SessionPool() => new(OpenSession());

    public Dmm OpenDmm()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Dmm, _device.SupportedKinds);
        return new Dmm(OpenSession());
    }

    public DcPowerSupply OpenDcPowerSupply()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.DcPowerSupply, _device.SupportedKinds);
        return new DcPowerSupply(OpenSession());
    }

    public FunctionGenerator OpenFunctionGenerator()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.FunctionGenerator, _device.SupportedKinds);
        return new FunctionGenerator(OpenSession());
    }

    public Oscilloscope OpenOscilloscope()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Oscilloscope, _device.SupportedKinds);
        return new Oscilloscope(OpenSession());
    }

    public Switch OpenSwitch()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Switch, _device.SupportedKinds);
        return new Switch(OpenSession());
    }

    public Counter OpenCounter()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Counter, _device.SupportedKinds);
        return new Counter(OpenSession());
    }

    public PowerMeter OpenPowerMeter()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.PowerMeter, _device.SupportedKinds);
        return new PowerMeter(OpenSession());
    }

    public SpectrumAnalyzer OpenSpectrumAnalyzer()
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.SpectrumAnalyzer, _device.SupportedKinds);
        return new SpectrumAnalyzer(OpenSession());
    }

    public async Task<AsyncInstrumentSession> OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_asyncOpener is null)
        {
            var opened = await Task.Run(() => _opener.Open(_device.Address, _connectOptions), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                (opened as IDisposable)?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return await AsyncInstrumentSession.CreateAsync(_device.Address, new SyncAsAsyncTransport<ITransport>(opened),
                _connectOptions, _device.Identity, CreateDiagnostics(), cancellationToken).ConfigureAwait(false);
        }

        var transport = await _asyncOpener.OpenAsync(_device.Address, _connectOptions, cancellationToken).ConfigureAwait(false);
        return await AsyncInstrumentSession.CreateAsync(
            _device.Address, transport, _connectOptions, _device.Identity, CreateDiagnostics(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AsyncDmm> OpenDmmAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Dmm, _device.SupportedKinds);
        return new AsyncDmm(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<AsyncDcPowerSupply> OpenDcPowerSupplyAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.DcPowerSupply, _device.SupportedKinds);
        return new AsyncDcPowerSupply(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<AsyncFunctionGenerator> OpenFunctionGeneratorAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.FunctionGenerator, _device.SupportedKinds);
        return new AsyncFunctionGenerator(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<AsyncOscilloscope> OpenOscilloscopeAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Oscilloscope, _device.SupportedKinds);
        return new AsyncOscilloscope(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<AsyncSwitch> OpenSwitchAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Switch, _device.SupportedKinds);
        return new AsyncSwitch(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<AsyncCounter> OpenCounterAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.Counter, _device.SupportedKinds);
        return new AsyncCounter(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<AsyncPowerMeter> OpenPowerMeterAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.PowerMeter, _device.SupportedKinds);
        return new AsyncPowerMeter(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<AsyncSpectrumAnalyzer> OpenSpectrumAnalyzerAsync(CancellationToken cancellationToken = default)
    {
        SessionHelpers.EnsureKindSupported(_device.Address, InstrumentKind.SpectrumAnalyzer, _device.SupportedKinds);
        return new AsyncSpectrumAnalyzer(await OpenSessionAsync(cancellationToken).ConfigureAwait(false));
    }
}

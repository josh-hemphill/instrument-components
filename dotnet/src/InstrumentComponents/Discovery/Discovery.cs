using InstrumentComponents.Catalog;
using InstrumentComponents.Classifier;
using InstrumentComponents.Connect;
using InstrumentComponents.Diagnostics;
using InstrumentComponents.Enumerator;
using InstrumentComponents.Identity;
using InstrumentComponents.Ieee4882;
using InstrumentComponents.Kind;
using InstrumentComponents.Probe;
using InstrumentComponents.Registry;
using InstrumentComponents.Session;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Discovery;

/// <summary>Discovery builder for scanning and classifying instruments.</summary>
public sealed class Discovery
{
    private static readonly string[] DefaultPatterns = ["?*INSTR", "USB?*::INSTR", "GPIB?*::INSTR", "ASRL?*INSTR"];

    private readonly IResourceEnumerator _enumerator;
    private readonly ISessionOpener _opener;
    private readonly IAsyncSessionOpener? _asyncOpener;
    private ModelRegistry _registry;
    private readonly List<string> _manualAddresses = new();
    private readonly Dictionary<string, List<InstrumentKind>> _kindOverrides = new();
    private ConnectOptions _connectOptions = new();
    private ProbePolicy _probePolicy = ProbePolicy.ReadOnly;
    private int _probeConcurrency = 4;
    private ICommsObserver? _observer;

    public Discovery(IResourceEnumerator enumerator, ISessionOpener opener, ModelRegistry registry, IAsyncSessionOpener? asyncOpener = null)
    {
        _enumerator = enumerator;
        _opener = opener;
        _asyncOpener = asyncOpener;
        _registry = registry;
    }

    public Discovery ManualAddress(string address)
    {
        _manualAddresses.Add(address);
        return this;
    }

    public Discovery OverrideKinds(string address, IReadOnlyList<InstrumentKind> kinds)
    {
        _kindOverrides[address] = kinds.ToList();
        return this;
    }

    public Discovery WithRegistry(ModelRegistry registry)
    {
        _registry = registry;
        return this;
    }

    public Discovery WithProbePolicy(ProbePolicy policy)
    {
        _probePolicy = policy;
        return this;
    }

    public Discovery ConnectOptions(ConnectOptions opts)
    {
        _connectOptions = opts;
        return this;
    }

    public Discovery Observer(ICommsObserver observer)
    {
        _observer = observer;
        return this;
    }

    public DeviceCatalog Scan() => ScanAsync().GetAwaiter().GetResult();

    public async Task<DeviceCatalog> ScanAsync(CancellationToken cancellationToken = default)
    {
        var rawMap = new Dictionary<ulong, RawResource>();
        foreach (var pattern in DefaultPatterns)
        {
            foreach (var res in _enumerator.List(pattern))
                rawMap[res.Address.DedupKey] = res;
        }

        foreach (var manual in _manualAddresses)
        {
            var address = Address.ResourceAddress.Parse(manual);
            if (!rawMap.ContainsKey(address.DedupKey))
            rawMap[address.DedupKey] = new RawResource
            {
                Address = address,
                IdentityHint = new TransportIdentity(),
            };
        }

        var candidates = rawMap.Values.ToList();
        var devices = await ProbeDevicesParallelAsync(candidates, cancellationToken).ConfigureAwait(false);
        return DeviceCatalog.FromDevicesWithObserver(_opener, devices, _observer, _asyncOpener)
            .WithConnectOptions(_connectOptions);
    }

    private async Task<List<DiscoveredDevice>> ProbeDevicesParallelAsync(List<RawResource> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return new List<DiscoveredDevice>();

        using var semaphore = new SemaphoreSlim(_probeConcurrency);
        var tasks = candidates.Select(async raw =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var device = await Task.Run(() => ProbeOne(raw), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return device;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                return PanicFallbackDevice(raw);
            }
            finally
            {
                semaphore.Release();
            }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    private DiscoveredDevice ProbeOne(RawResource raw)
    {
        _kindOverrides.TryGetValue(raw.Address.Raw, out var overrideKinds);
        var (identity, layer1) = Classifier.Classifier.ClassifyFromAddress(raw.Address, _registry);
        var (hintIdentity, layer2) = Classifier.Classifier.ClassifyFromTransportHint(raw.IdentityHint, _registry);
        identity.Merge(hintIdentity);
        var layers = new List<List<ClassifiedKind>> { layer1, layer2 };

        InstrumentSession? session = null;
        try
        {
            var transport = _opener.Open(raw.Address, _connectOptions);
            session = new InstrumentSession(raw.Address, transport, _connectOptions, identity);
            session.ClearStatus();
            try { session.Scpi.Flush(); } catch { /* ignore drain errors */ }

            try
            {
                var idn = new global::InstrumentComponents.Ieee4882.Ieee4882(session.Scpi).Idn();
                var (idnIdentity, layer4) = Classifier.Classifier.ClassifyFromIdentity(idn, _registry);
                identity.Merge(idnIdentity);
                layers.Add(layer4);

                try
                {
                    identity.Options = new global::InstrumentComponents.Ieee4882.Ieee4882(session.Scpi).Options();
                }
                catch { /* optional */ }
            }
            catch { /* IDN optional for unreachable classification */ }

            if (_probePolicy != ProbePolicy.None)
            {
                var probeKinds = Classifier.Classifier.ClassifyWithPolicy(session.Scpi, _probePolicy);
                if (probeKinds.Count > 0)
                    layers.Add(probeKinds);
            }

            var (supported, classification) = Classifier.Classifier.MergeClassifications(layers, overrideKinds);
            return new DiscoveredDevice
            {
                Address = raw.Address,
                Identity = identity,
                SupportedKinds = supported,
                Classification = classification,
                Reachable = true,
            };
        }
        catch (Exception ex)
        {
            return UnreachableDevice(raw, identity, layers, overrideKinds, ex.Message);
        }
        finally
        {
            try { session?.Dispose(); } catch { /* preserve probe outcome */ }
        }
    }

    private DiscoveredDevice PanicFallbackDevice(RawResource raw)
    {
        _kindOverrides.TryGetValue(raw.Address.Raw, out var overrideKinds);
        var (identity, layer1) = Classifier.Classifier.ClassifyFromAddress(raw.Address, _registry);
        var (hintIdentity, layer2) = Classifier.Classifier.ClassifyFromTransportHint(raw.IdentityHint, _registry);
        identity.Merge(hintIdentity);
        return UnreachableDevice(raw, identity, [layer1, layer2], overrideKinds, "probe panicked");
    }

    private DiscoveredDevice UnreachableDevice(
        RawResource raw, DeviceIdentity identity, List<List<ClassifiedKind>> layers,
        List<InstrumentKind>? overrideKinds, string error)
    {
        var (supported, classification) = Classifier.Classifier.MergeClassifications(layers, overrideKinds);
        return new DiscoveredDevice
        {
            Address = raw.Address,
            Identity = identity,
            SupportedKinds = supported,
            Classification = classification,
            Reachable = false,
            Error = error,
        };
    }
}

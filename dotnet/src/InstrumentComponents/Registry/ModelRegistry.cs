using System.Text.Json;
using System.Text.Json.Serialization;
using InstrumentComponents.Kind;

namespace InstrumentComponents.Registry;

public sealed class UsbHint
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<InstrumentKind> Kinds { get; init; } = Array.Empty<InstrumentKind>();
}

/// <summary>IVI-registry-inspired model → kinds lookup (hints only).</summary>
public sealed partial class ModelRegistry
{
    private readonly Dictionary<string, List<InstrumentKind>> _byModel = new();
    private readonly Dictionary<(string Vid, string Pid), UsbHint> _byUsb = new();
    private readonly Dictionary<string, List<InstrumentKind>> _runtime = new();

    /// <summary>Core has no embedded device implementations or identity table.</summary>
    public static ModelRegistry Embedded() => new();

    public static ModelRegistry FromJson(string json)
    {
        var file = JsonSerializer.Deserialize(json, RegistryJsonContext.Default.RegistryFile)
            ?? throw new InvalidOperationException("registry parse failed");
        var registry = new ModelRegistry();
        foreach (var entry in file.Entries)
        {
            var key = NormalizeKey(entry.Manufacturer, entry.Model);
            registry._byModel[key] = ParseKinds(entry.Kinds);
        }
        foreach (var entry in file.UsbEntries)
        {
            var key = (entry.Vid.ToUpperInvariant(), entry.Pid.ToUpperInvariant());
            registry._byUsb[key] = new UsbHint
            {
                Manufacturer = entry.Manufacturer,
                Model = entry.Model,
                Kinds = ParseKinds(entry.Kinds),
            };
        }
        return registry;
    }

    public void Merge(ModelRegistry other)
    {
        foreach (var (k, v) in other._byModel)
            if (!_byModel.ContainsKey(k)) _byModel[k] = v;
        foreach (var (k, v) in other._byUsb)
            if (!_byUsb.ContainsKey(k)) _byUsb[k] = v;
        foreach (var (k, v) in other._runtime)
            _runtime[k] = v;
    }

    public void AddRuntime(string manufacturer, string model, IReadOnlyList<InstrumentKind> kinds) =>
        _runtime[NormalizeKey(manufacturer, model)] = kinds.ToList();

    public IReadOnlyList<InstrumentKind>? LookupModel(string manufacturer, string model)
    {
        var key = NormalizeKey(manufacturer, model);
        if (_runtime.TryGetValue(key, out var rt)) return rt;
        return _byModel.TryGetValue(key, out var kinds) ? kinds : null;
    }

    public UsbHint? LookupUsb(string vid, string pid)
    {
        var key = (vid.ToUpperInvariant(), pid.ToUpperInvariant());
        return _byUsb.TryGetValue(key, out var hint) ? hint : null;
    }

    private static string NormalizeKey(string manufacturer, string model) =>
        $"{manufacturer.Trim().ToLowerInvariant()}|{model.Trim().ToLowerInvariant()}";

    private static List<InstrumentKind> ParseKinds(IReadOnlyList<string> labels) =>
        labels.Select(l => InstrumentKindExtensions.FromLabel(l))
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .ToList();

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive=true)]
    [JsonSerializable(typeof(RegistryFile))]
    private partial class RegistryJsonContext:JsonSerializerContext { }

    private sealed class RegistryFile
    {
        public List<ModelEntry> Entries { get; set; } = new();
        public List<UsbEntry> UsbEntries { get; set; } = new();
    }

    private sealed class ModelEntry
    {
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public List<string> Kinds { get; set; } = new();
    }

    private sealed class UsbEntry
    {
        public string Vid { get; set; } = "";
        public string Pid { get; set; } = "";
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public List<string> Kinds { get; set; } = new();
    }
}

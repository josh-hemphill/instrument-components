using System.Reflection;
using System.Text.Json;

namespace InstrumentComponents.OpenTap.Tests;

public class ManifestValidationTests
{
    [Fact]
    public void ManifestCoversEveryShippedStepExactlyOnce()
    {
        var operations = LoadOperations();
        var shipped = OpenTapCatalog.StepTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var declared = operations.Select(operation => operation.StepTypeName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(shipped, declared);
        Assert.Equal(operations.Count, operations.Select(operation => operation.Id).Distinct().Count());
    }

    [Fact]
    public void ManifestDisplayAndParametersMatchStepMetadata()
    {
        var byName = OpenTapCatalog.StepTypes().ToDictionary(type => type.Name, StringComparer.Ordinal);
        foreach (var operation in LoadOperations())
        {
            Assert.True(byName.TryGetValue(operation.StepTypeName, out var type), operation.StepTypeName);
            var display = OpenTapCatalog.RequireDisplay(type);
            Assert.Equal(operation.DisplayName, display.Name);
            Assert.Equal(operation.DisplayGroups, OpenTapCatalog.GroupsOf(display));
            Assert.Equal(operation.Description, display.Description);
            Assert.Equal($"InstrumentComponents.OpenTap.{operation.InstrumentType}", InstrumentType(type).FullName);

            var settings = OpenTapCatalog.Settings(type).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var parameter in operation.Parameters)
            {
                Assert.Contains(parameter.Name, settings);
                var property = type.GetProperty(parameter.Name);
                Assert.NotNull(property);
                Assert.Equal(parameter.DisplayName, OpenTapCatalog.RequireDisplay(property!).Name);
            }
        }
    }

    [Fact]
    public void ManifestMethodsExistOnTypedInstrumentViews()
    {
        foreach (var operation in LoadOperations())
        {
            var view = ViewType(operation);
            foreach (var invoke in operation.Invokes())
            {
                var methods = view.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Where(method => method.Name == invoke.Method)
                    .ToList();
                Assert.True(methods.Count > 0, $"{operation.Id} missing {view.Name}.{invoke.Method}");
            }
        }
    }

    private static Type InstrumentType(Type stepType)
    {
        var property = stepType.GetProperty("Instrument")
            ?? throw new InvalidOperationException($"{stepType.Name} has no Instrument property.");
        return property.PropertyType;
    }

    private static Type ViewType(OperationRecord operation)
    {
        if (operation.Kind == "utility")
            return typeof(ScpiInstrument);

        var instrument = typeof(ScpiInstrument).Assembly.GetType($"InstrumentComponents.OpenTap.{operation.InstrumentType}")
            ?? throw new InvalidOperationException($"unknown instrument {operation.InstrumentType}");
        var accessor = instrument.GetProperty(operation.Accessor)
            ?? throw new InvalidOperationException($"{operation.InstrumentType} missing accessor {operation.Accessor}");
        return accessor.PropertyType;
    }

    private static List<OperationRecord> LoadOperations()
    {
        var path = Path.Combine(RepoPaths.Root(), "spec", "opentap-operations.json");
        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<ManifestFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("opentap-operations.json deserialized to null");
        Assert.Equal(1, file.Version);
        Assert.NotEmpty(file.Operations);
        return file.Operations;
    }

    private sealed class ManifestFile
    {
        public int Version { get; set; }
        public List<OperationRecord> Operations { get; set; } = [];
    }

    private sealed class OperationRecord
    {
        public string Id { get; set; } = "";
        public string StepTypeName { get; set; } = "";
        public string Kind { get; set; } = "";
        public string InstrumentType { get; set; } = "";
        public string Accessor { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string[] DisplayGroups { get; set; } = [];
        public string Description { get; set; } = "";
        public List<ParameterRecord> Parameters { get; set; } = [];
        public InvokeRecord? Invoke { get; set; }
        public List<InvokeRecord> Composite { get; set; } = [];

        public IEnumerable<InvokeRecord> Invokes()
        {
            if (Invoke is not null)
                yield return Invoke;
            foreach (var invoke in Composite)
                yield return invoke;
        }
    }

    private sealed class ParameterRecord
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    private sealed class InvokeRecord
    {
        public string Method { get; set; } = "";
    }
}

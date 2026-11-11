using System.Text.Json;
using InstrumentComponents.Address;
using InstrumentComponents.Classifier;
using InstrumentComponents.Identity;
using InstrumentComponents.Kind;
using InstrumentComponents.Registry;

namespace InstrumentComponents.Tests;

internal static class RepoFiles
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "spec", "driver-operations.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find repo root (spec/scpi-vectors.json)");
    }

    public static string Spec(string name) => Path.Combine(Root(), "spec", name);
    public static string Fixture(string name) => Path.Combine(Root(), "fixtures", name);
}

internal static class SpecJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static T Load<T>(string name) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(RepoFiles.Spec(name)), Options)
        ?? throw new InvalidOperationException($"empty spec {name}");
}

public class SharedContractTests
{
    private sealed class ClassifierFileDto
    {
        public List<ClassifierCaseDto> Cases { get; set; } = [];
    }

    private sealed class ClassifierCaseDto
    {
        public string Id { get; set; } = "";
        public string Layer { get; set; } = "";
        public string? Address { get; set; }
        public IdnDto? Idn { get; set; }
        public IdentityDto? ExpectedIdentity { get; set; }
        public List<KindDto>? ExpectedKinds { get; set; }
        public List<List<KindDto>>? BaseLayers { get; set; }
        public List<string>? OverrideKinds { get; set; }
        public List<string>? ExpectedSupported { get; set; }
    }

    private sealed class IdnDto
    {
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string Serial { get; set; } = "";
        public string Firmware { get; set; } = "";
    }

    private sealed class IdentityDto
    {
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
    }

    private sealed class KindDto
    {
        public string Kind { get; set; } = "";
        public byte Confidence { get; set; }
        public string Source { get; set; } = "";
    }

    private static InstrumentKind ParseKind(string label) =>
        InstrumentKindExtensions.FromLabel(label) ?? throw new ArgumentException($"unknown kind {label}");

    private static ClassifySource ParseSource(string label) =>
        Enum.Parse<ClassifySource>(label);



    [Fact]
    public void ClassifierCasesMatchVectors()
    {
        var file = SpecJson.Load<ClassifierFileDto>("classifier-cases.json");
        var registry = ModelRegistry.FromJson(File.ReadAllText(Path.Combine(RepoFiles.Root(),"validation","standards","registry.json")));
        foreach (var caseRow in file.Cases)
        {
            switch (caseRow.Layer)
            {
                case "address":
                {
                    var addr = ResourceAddress.Parse(caseRow.Address!);
                    var (identity, kinds) = Classifier.Classifier.ClassifyFromAddress(addr, registry);
                    AssertIdentity(caseRow, identity);
                    AssertKinds(caseRow, kinds);
                    break;
                }
                case "idn":
                {
                    var idn = new Idn(caseRow.Idn!.Manufacturer, caseRow.Idn.Model, caseRow.Idn.Serial, caseRow.Idn.Firmware);
                    var (identity, kinds) = Classifier.Classifier.ClassifyFromIdentity(idn, registry);
                    AssertIdentity(caseRow, identity);
                    AssertKinds(caseRow, kinds);
                    break;
                }
                case "override":
                case "merge":
                {
                    var layers = caseRow.BaseLayers!
                        .Select(layer => layer.Select(ToClassified).ToList())
                        .ToList();
                    IReadOnlyList<InstrumentKind>? overrides = caseRow.OverrideKinds?
                        .Select(ParseKind)
                        .ToList();
                    var (supported, _) = Classifier.Classifier.MergeClassifications(layers, overrides);
                    var expected = caseRow.ExpectedSupported!.Select(ParseKind).ToList();
                    Assert.Equal(expected, supported);
                    break;
                }
                default:
                    throw new InvalidOperationException($"unknown classifier layer {caseRow.Layer}");
            }
        }
    }

    private static void AssertIdentity(ClassifierCaseDto caseRow, DeviceIdentity identity)
    {
        if (caseRow.ExpectedIdentity is null) return;
        if (caseRow.ExpectedIdentity.Manufacturer is { } mfr)
            Assert.Equal(mfr, identity.Manufacturer);
        if (caseRow.ExpectedIdentity.Model is { } model)
            Assert.Equal(model, identity.Model);
    }

    private static void AssertKinds(ClassifierCaseDto caseRow, List<ClassifiedKind> kinds)
    {
        var expected = caseRow.ExpectedKinds!
            .Select(k => (ParseKind(k.Kind), k.Confidence, ParseSource(k.Source)))
            .OrderBy(k => k.ToString())
            .ToList();
        var actual = kinds
            .Select(k => (k.Kind, k.Confidence, k.Source))
            .OrderBy(k => k.ToString())
            .ToList();
        Assert.Equal(expected, actual);
    }

    private static ClassifiedKind ToClassified(KindDto dto) =>
        new(ParseKind(dto.Kind), dto.Confidence, ParseSource(dto.Source));
}

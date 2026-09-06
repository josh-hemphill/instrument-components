using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

internal static class PublicContractCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static JsonObject Instruments()
    {
        var root = new JsonObject();
        foreach (var type in OpenTapCatalog.InstrumentTypes())
            root[type.Name] = Snapshot(type, OpenTapCatalog.Create(type));
        return root;
    }

    public static JsonObject Steps()
    {
        var root = new JsonObject();
        foreach (var type in OpenTapCatalog.StepTypes())
            root[type.Name] = Snapshot(type, OpenTapCatalog.Create(type));
        return root;
    }

    public static JsonObject ResultTables() => new()
    {
        ["sample"] = Table(PhaseIResults.SampleTable, PhaseIResults.SampleColumns),
        ["scalar"] = Table(PhaseIResults.ScalarTable, PhaseIResults.ScalarColumns),
        ["identity"] = Table(PhaseIResults.IdentityTable, PhaseIResults.IdentityColumns),
    };

    public static string Serialize(JsonNode node) =>
        node.ToJsonString(JsonOptions) + Environment.NewLine;

    public static void AssertMatchesGolden(JsonNode actual, string goldenFileName)
    {
        var path = RepoPaths.Golden(goldenFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var actualJson = Serialize(actual);
        if (Environment.GetEnvironmentVariable("UPDATE_OPENTAP_GOLDEN") == "1")
            File.WriteAllText(path, actualJson);

        Assert.True(File.Exists(path), $"missing golden contract {path}");
        var expected = JsonNode.Parse(File.ReadAllText(path));
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"{goldenFileName} drifted from the public OpenTAP contract.{Environment.NewLine}Actual:{Environment.NewLine}{actualJson}");
    }

    private static JsonObject Snapshot(Type type, object instance)
    {
        var display = OpenTapCatalog.RequireDisplay(type);
        var settings = new JsonArray();
        foreach (var property in OpenTapCatalog.Settings(type))
            settings.Add(Setting(property, instance));

        return new JsonObject
        {
            ["fullName"] = type.FullName,
            ["displayName"] = display.Name,
            ["groups"] = ToArray(OpenTapCatalog.GroupsOf(display)),
            ["description"] = display.Description,
            ["settings"] = settings,
        };
    }

    private static JsonObject Setting(System.Reflection.PropertyInfo property, object instance)
    {
        var display = OpenTapCatalog.RequireDisplay(property);
        return new JsonObject
        {
            ["name"] = property.Name,
            ["type"] = OpenTapCatalog.TypeName(property.PropertyType),
            ["displayName"] = display.Name,
            ["order"] = display.Order,
            ["group"] = ToArray(OpenTapCatalog.GroupsOf(display)),
            ["defaultValue"] = ToJson(property.GetValue(instance)),
        };
    }

    private static JsonObject Table(string name, IReadOnlyList<string> columns)
    {
        var columnArray = new JsonArray();
        foreach (var column in columns)
            columnArray.Add(column);
        return new JsonObject
        {
            ["table"] = name,
            ["columns"] = columnArray,
        };
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        Enum enumerated => JsonValue.Create(enumerated.ToString()),
        IFormattable formattable => JsonValue.Create(formattable.ToString(null, CultureInfo.InvariantCulture)),
        ScpiIdentityFields identity => new JsonObject
        {
            ["manufacturer"] = identity.Manufacturer,
            ["model"] = identity.Model,
            ["serial"] = identity.Serial,
            ["firmware"] = identity.Firmware,
        },
        _ => JsonValue.Create(value.ToString()),
    };
}

using System.Reflection;
using OpenTap;
using DisplayAttribute = OpenTap.DisplayAttribute;

namespace InstrumentComponents.OpenTap.Tests;

internal static class OpenTapCatalog
{
    public static readonly string Namespace = "InstrumentComponents.OpenTap";

    public static IReadOnlyList<Type> InstrumentTypes() =>
        ConcretePublicTypes()
            .Where(type => typeof(Instrument).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<Type> StepTypes() =>
        ConcretePublicTypes()
            .Where(type => typeof(TestStep).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    public static object Create(Type type) =>
        Activator.CreateInstance(type)
        ?? throw new InvalidOperationException($"could not construct {type.FullName}");

    public static IReadOnlyList<PropertyInfo> Settings(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => property.GetCustomAttribute<DisplayAttribute>() is not null)
            .OrderBy(property => DisplayOrder(property))
            .ThenBy(property => property.Name, StringComparer.Ordinal)
            .ToList();

    public static DisplayAttribute RequireDisplay(MemberInfo member) =>
        member.GetCustomAttribute<DisplayAttribute>()
        ?? throw new InvalidOperationException($"{member.DeclaringType?.FullName}.{member.Name} is missing [Display].");

    public static string[] GroupsOf(DisplayAttribute display)
    {
        var groupsProperty = typeof(DisplayAttribute).GetProperty("Groups");
        if (groupsProperty?.GetValue(display) is string[] namedGroups)
            return namedGroups;

        var groupProperty = typeof(DisplayAttribute).GetProperty("Group");
        return groupProperty?.GetValue(display) switch
        {
            string[] groups => groups,
            string group when !string.IsNullOrEmpty(group) => [group],
            _ => [],
        };
    }

    public static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
            return $"{inner.FullName}?";
        return type.FullName ?? type.Name;
    }

    private static IEnumerable<Type> ConcretePublicTypes() =>
        typeof(ScpiInstrument).Assembly
            .GetTypes()
            .Where(type => type.IsPublic && type.IsClass && !type.IsAbstract && type.Namespace == Namespace);

    private static double DisplayOrder(PropertyInfo property) =>
        property.GetCustomAttribute<DisplayAttribute>()?.Order ?? double.MaxValue;
}

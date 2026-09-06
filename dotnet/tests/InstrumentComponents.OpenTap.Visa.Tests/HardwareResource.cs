using InstrumentComponents.Address;
using InstrumentComponents.OpenTap;

namespace InstrumentComponents.OpenTap.Visa.Tests;

/// <summary>Parses the self-hosted DMM smoke resource string.</summary>
internal static class HardwareResource
{
    public const string VariableName = "INSTRUMENT_RESOURCE";

    public static bool TryFromEnv(out string resource, out string? error) =>
        TryParse(Environment.GetEnvironmentVariable(VariableName), out resource, out error);

    public static bool TryParse(string? raw, out string resource, out string? error)
    {
        resource = "";
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{VariableName} must be a VISA resource string";
            return false;
        }

        try
        {
            resource = ResourceAddress.Parse(raw.Trim()).Raw;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{VariableName} is not a valid VISA address: {ex.Message}";
            return false;
        }
    }
}

/// <summary>Runs only when INSTRUMENT_RESOURCE is set (self-hosted smoke). Unset env skips; it does not fail.</summary>
internal sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!HardwareResource.TryFromEnv(out _, out var error))
            Skip = error;
    }
}

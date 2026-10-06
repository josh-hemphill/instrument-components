using InstrumentComponents.Errors;
using System.Globalization;

namespace InstrumentComponents.Scpi;

internal static class ScpiProtocol
{
    public static string NormalizeCommand(string command, string terminator)
    {
        if (!command.EndsWith(terminator, StringComparison.Ordinal))
            return command + terminator;
        return command;
    }

    public static uint MaxWriteAttempts(bool idempotent, uint retries) => idempotent ? retries + 1 : 1;

    public static bool IsOpcSupportedReply(string response)
    {
        var trimmed = response.Trim();
        return trimmed == "1" || trimmed == "+1";
    }

    public static bool IsSystErrSupportedReply(string response)
    {
        var s = response.Trim();
        var i = 0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            i++;
        var digitsStart = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
            i++;
        if (i == digitsStart)
            return false;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
        return i < s.Length && s[i] == ',';
    }

    public static bool IsNoErrorReply(string response) =>
        int.TryParse(response.Split(',')[0].Trim(), out var code) && code == 0;

    public static double ParseF64(string response)
    {
        if (!double.TryParse(response.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ParseException($"expected number, got '{response}'");
        return value;
    }

    public static IReadOnlyList<double> ParseF64Csv(string response)
    {
        var values = new List<double>();
        foreach (var part in response.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new ParseException($"expected number, got '{trimmed}'");
            values.Add(value);
        }
        return values;
    }
}


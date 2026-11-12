using InstrumentComponents.Errors;

namespace InstrumentComponents.Scpi;

/// <summary>IEEE 488.2 SCPI response framing.</summary>
public static class ScpiFraming
{
    /// <summary>Reads a complete SCPI response from accumulated bytes.</summary>
    public static (byte[] Payload, int Consumed) ExtractResponse(ReadOnlySpan<byte> buffer, string terminator)
    {
        if (buffer.IsEmpty)
            throw new InstrumentTimeoutException();

        if (buffer[0] == (byte)'#')
            return ReadBlockResponse(buffer);

        return ReadTerminatorResponse(buffer, terminator);
    }

    private static (byte[], int) ReadBlockResponse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2)
            throw new InstrumentTimeoutException();

        var digitCount = buffer[1] - '0';
        if (digitCount < 0 || digitCount > 9)
            throw new ParseException("invalid block digit count");

        if (digitCount == 0)
            return ReadIndefiniteBlock(buffer);

        var lenStart = 2;
        var lenEnd = lenStart + digitCount;
        if (buffer.Length < lenEnd)
            throw new InstrumentTimeoutException();

        var dataLen = 0;
        foreach (var digit in buffer[lenStart..lenEnd])
        {
            if (digit < (byte)'0' || digit > (byte)'9')
                throw new ParseException("invalid block length: ASCII digits required");
            dataLen = checked(dataLen * 10 + digit - (byte)'0');
        }

        var dataStart = lenEnd;
        var dataEnd = dataStart + dataLen;
        if (buffer.Length < dataEnd)
            throw new InstrumentTimeoutException();

        return (buffer.Slice(dataStart, dataLen).ToArray(), dataEnd);
    }

    private static (byte[], int) ReadIndefiniteBlock(ReadOnlySpan<byte> buffer)
    {
        const int start = 2;
        var slice = buffer[start..];
        var pos = slice.IndexOf((byte)'\n');
        if (pos < 0)
            throw new InstrumentTimeoutException();
        return (slice[..pos].ToArray(), start + pos + 1);
    }

    private static (byte[], int) ReadTerminatorResponse(ReadOnlySpan<byte> buffer, string terminator)
    {
        var termBytes = System.Text.Encoding.UTF8.GetBytes(terminator);
        if (termBytes.Length == 0)
            return (buffer.ToArray(), buffer.Length);

        var pos = FindSubslice(buffer, termBytes);
        if (pos >= 0)
            return (buffer[..pos].ToArray(), pos + termBytes.Length);

        var nl = buffer.IndexOf((byte)'\n');
        if (nl >= 0)
            return (buffer[..nl].ToArray(), nl + 1);

        throw new InstrumentTimeoutException();
    }

    private static int FindSubslice(ReadOnlySpan<byte> haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }
}

using InstrumentComponents.Errors;

namespace InstrumentComponents.Scpi;

/// <summary>IEEE 488.2 SCPI response framing.</summary>
public static class ScpiFraming
{
    /// <summary>Reads a complete SCPI response from accumulated bytes.</summary>
    public static (byte[] Payload, int Consumed) ExtractResponse(ReadOnlySpan<byte> buffer, string terminator) => ExtractResponse(buffer, terminator, null);

    /// <summary>Reads a response while bounding payload bytes before copying; framing does not count against the limit.</summary>
    public static (byte[] Payload, int Consumed) ExtractResponse(ReadOnlySpan<byte> buffer, string terminator, int? maxResponseBytes)
    {
        if (maxResponseBytes < 0) throw new ParseException("negative response payload limit");
        if (buffer.IsEmpty)
            throw new InstrumentTimeoutException();

        if (buffer[0] == (byte)'#')
            return ReadBlockResponse(buffer, terminator, maxResponseBytes);

        return ReadTerminatorResponse(buffer, terminator, maxResponseBytes);
    }

    private static (byte[], int) ReadBlockResponse(ReadOnlySpan<byte> buffer, string terminator, int? limit)
    {
        if (buffer.Length < 2)
            throw new InstrumentTimeoutException();

        var digitCount = buffer[1] - '0';
        if (digitCount < 0 || digitCount > 9)
            throw new ParseException("invalid block digit count");

        if (digitCount == 0)
            return ReadIndefiniteBlock(buffer, terminator, limit);

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

        CheckPayloadLimit(dataLen, limit);
        var dataStart = lenEnd;
        var dataEnd = dataStart + dataLen;
        if (buffer.Length < dataEnd)
            throw new InstrumentTimeoutException();

        return (buffer.Slice(dataStart, dataLen).ToArray(), dataEnd);
    }

    private static (byte[], int) ReadIndefiniteBlock(ReadOnlySpan<byte> buffer, string terminator, int? limit)
    {
        const int start = 2;
        var slice = buffer[start..];
        var pos = slice.IndexOf((byte)'\n');
        if (pos < 0)
        {
            var partialCrlf = terminator == "\r\n" && !slice.IsEmpty && slice[^1] == (byte)'\r' ? 1 : 0;
            CheckPayloadLimit(slice.Length - partialCrlf, limit);
            throw new InstrumentTimeoutException();
        }
        var payloadLength = terminator == "\r\n" && pos > 0 && slice[pos - 1] == (byte)'\r' ? pos - 1 : pos;
        CheckPayloadLimit(payloadLength, limit);
        return (slice[..payloadLength].ToArray(), start + pos + 1);
    }

    private static (byte[], int) ReadTerminatorResponse(ReadOnlySpan<byte> buffer, string terminator, int? limit)
    {
        var termBytes = System.Text.Encoding.UTF8.GetBytes(terminator);
        if (termBytes.Length == 0)
        {
            CheckPayloadLimit(buffer.Length, limit);
            return (buffer.ToArray(), buffer.Length);
        }

        var pos = FindSubslice(buffer, termBytes);
        if (pos >= 0)
        {
            CheckPayloadLimit(pos, limit);
            return (buffer[..pos].ToArray(), pos + termBytes.Length);
        }

        var nl = buffer.IndexOf((byte)'\n');
        if (nl >= 0)
        {
            CheckPayloadLimit(nl, limit);
            return (buffer[..nl].ToArray(), nl + 1);
        }
        var partial = Math.Min(buffer.Length, termBytes.Length - 1);
        while (partial > 0 && !buffer.EndsWith(termBytes.AsSpan(0, partial))) partial--;
        CheckPayloadLimit(buffer.Length - partial, limit);
        throw new InstrumentTimeoutException();
    }

    internal static void AppendResponseByte(List<byte> buffer, byte value, string terminator, int? limit)
    {
        if (limit is int maximum)
        {
            var wireMaximum = Math.Min(int.MaxValue, (long)maximum + 11 + Math.Max(2, System.Text.Encoding.UTF8.GetByteCount(terminator)));
            if (maximum < 0 || buffer.Count >= wireMaximum)
                throw new ParseException("response exceeds configured payload limit");
            if (buffer.Count == buffer.Capacity)
                buffer.Capacity = (int)Math.Min(wireMaximum, Math.Max(4L, (long)buffer.Count * 2));
        }
        buffer.Add(value);
    }

    private static void CheckPayloadLimit(int length, int? limit)
    {
        if (limit is int maximum && length > maximum)
            throw new ParseException("response exceeds configured payload limit");
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

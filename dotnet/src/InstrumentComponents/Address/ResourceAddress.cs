using InstrumentComponents.Errors;

namespace InstrumentComponents.Address;

/// <summary>Typed VISA resource address with canonical dedup key.</summary>
public sealed class ResourceAddress
{
    public InterfaceKind Interface { get; }
    public string Raw { get; }
    public AddressParts Components { get; }
    public ulong DedupKey { get; }

    private ResourceAddress(InterfaceKind iface, string raw, AddressParts components)
    {
        Interface = iface;
        Raw = raw;
        Components = components;
        DedupKey = ComputeDedupKey(iface, raw);
    }

    /// <summary>Parses a VISA resource string into a typed address.</summary>
    public static ResourceAddress Parse(string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidAddressException("empty address");

        var upper = trimmed.ToUpperInvariant();
        InterfaceKind iface;
        AddressParts parts;

        if (upper.StartsWith("USB", StringComparison.Ordinal))
            (iface, parts) = ParseUsb(trimmed);
        else if (upper.StartsWith("GPIB", StringComparison.Ordinal))
            (iface, parts) = ParseGpib(trimmed);
        else if (upper.StartsWith("TCPIP", StringComparison.Ordinal) || upper.StartsWith("TCP", StringComparison.Ordinal))
            (iface, parts) = ParseTcpip(trimmed);
        else if (upper.StartsWith("ASRL", StringComparison.Ordinal))
            (iface, parts) = ParseSerial(trimmed);
        else if (upper.StartsWith("VXI", StringComparison.Ordinal))
            (iface, parts) = (InterfaceKind.Vxi, new AddressParts());
        else if (upper.StartsWith("PXI", StringComparison.Ordinal))
            (iface, parts) = (InterfaceKind.Pxi, new AddressParts());
        else if (upper.StartsWith("MOCK://", StringComparison.Ordinal))
            (iface, parts) = (InterfaceKind.Unknown, new AddressParts());
        else
            (iface, parts) = (InterfaceKind.Unknown, new AddressParts());

        return new ResourceAddress(iface, trimmed, parts);
    }

    private static ulong ComputeDedupKey(InterfaceKind iface, string raw)
    {
        return unchecked((ulong)(uint)HashCode.Combine(iface, raw.ToUpperInvariant()));
    }

    private static (InterfaceKind, AddressParts) ParseUsb(string raw)
    {
        var parts = raw.Split("::");
        return (InterfaceKind.Usb, new AddressParts
        {
            Vid = parts.Length > 1 ? NormalizeHexId(parts[1]) : null,
            Pid = parts.Length > 2 ? NormalizeHexId(parts[2]) : null,
            Serial = parts.Length > 3 ? parts[3] : null,
        });
    }

    private static (InterfaceKind, AddressParts) ParseGpib(string raw)
    {
        var parts = raw.Split("::");
        uint? board = null;
        if (parts.Length > 0 && parts[0].StartsWith("GPIB", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(parts[0][4..], out var b))
            board = b;
        return (InterfaceKind.Gpib, new AddressParts
        {
            Board = board,
            PrimaryAddress = parts.Length > 1 && uint.TryParse(parts[1], out var p) ? p : null,
            SecondaryAddress = parts.Length > 2 && uint.TryParse(parts[2], out var s) ? s : null,
        });
    }

    private static (InterfaceKind, AddressParts) ParseTcpip(string raw)
    {
        var parts = raw.Split("::");
        ushort? port = parts.Length == 4 && parts[3].Equals("SOCKET", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(parts[2], out var p) ? p : null;
        return (InterfaceKind.Tcpip, new AddressParts
        {
            Host = parts.Length > 1 ? parts[1] : null,
            Port = port,
        });
    }

    private static (InterfaceKind, AddressParts) ParseSerial(string raw)
    {
        var first = raw.Split("::").FirstOrDefault() ?? raw;
        uint? board = first.StartsWith("ASRL", StringComparison.OrdinalIgnoreCase) &&
                      uint.TryParse(first[4..], out var b) ? b : null;
        return (InterfaceKind.Serial, new AddressParts { Board = board });
    }

    private static string NormalizeHexId(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t[2..];
        return t.ToUpperInvariant();
    }
}

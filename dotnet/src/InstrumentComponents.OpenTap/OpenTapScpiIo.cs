using InstrumentComponents.Scpi;

namespace InstrumentComponents.OpenTap;

/// <summary>Optional host seam so Open() can obtain SCPI I/O without AttachSession.</summary>
public interface IOpenTapScpiIoProvider
{
    IScpiIo Open(string visaAddress, TimeSpan ioTimeout);
}

/// <summary>Process-wide SCPI I/O provider used when no session is attached.</summary>
public static class OpenTapScpiIo
{
    public static IOpenTapScpiIoProvider? Provider { get; set; }
}

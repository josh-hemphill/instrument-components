using InstrumentComponents.Identity;
using InstrumentComponents.Scpi;

namespace InstrumentComponents.Ieee4882;

/// <summary>IEEE 488.2 common commands (sync).</summary>
public sealed class Ieee4882
{
    private readonly ScpiSession _session;

    public Ieee4882(ScpiSession session) => _session = session;

    public Idn Idn()
    {
        var resp = _session.Query("*IDN?");
        return Identity.Idn.Parse(resp);
    }

    public void Reset()
    {
        _session.Write("*RST");
        WaitComplete();
    }

    public void ClearStatus() => _session.Write("*CLS");

    public bool OpcQuery()
    {
        return ScpiProtocol.IsOpcSupportedReply(_session.Query("*OPC?"));
    }

    public void WaitComplete()
    {
        _session.EnsureOpcAvailable();
        var reply = _session.QueryCompletion(TimeSpan.FromSeconds(30));
        if (!ScpiProtocol.IsOpcSupportedReply(reply))
            throw new global::InstrumentComponents.Errors.InstrumentUnsupportedException($"operation completion reply '{reply}'");
    }

    public string Options() => _session.Query("*OPT?");
}

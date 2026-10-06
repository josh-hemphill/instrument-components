using InstrumentComponents.Dialects;
using InstrumentComponents.Identity;
using InstrumentComponents.Kind;
using InstrumentComponents.Session;
using InstrumentComponents.Scpi;

namespace InstrumentComponents.Classes;

/// <summary>DC power supply session view.</summary>
public sealed class DcPowerSupply : IInstrumentIdentity, IInstrumentShutdown
{
    private readonly InstrumentSession _session;

    public DcPowerSupply(InstrumentSession session) => _session = session;

    public InstrumentSession Session => _session;

    public Idn QueryIdn() => _session.Idn();

    public void Reset() => _session.Reset();

    public void OutputOff()
    {
        var failures = new List<Exception>();
        foreach (var channel in _session.PowerSupplyChannels(Dialect.Channels))
            try { OutputEnable(channel, false); } catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException("Failed to disable all PSU outputs.", failures);
        if (Dialect.Id == "generic_dcpwr" && _session.PowerSupplyChannelCount is null)
            throw new global::InstrumentComponents.Errors.InstrumentUnsupportedException("PSU physical channel count is unknown. Set Session.PowerSupplyChannelCount before shutdown; known outputs were disabled.");
    }

    private DialectProfile Dialect => _session.DialectFor(InstrumentKind.DcPowerSupply);

    private string Cmd(string key, string fallback, params (string Name, string Value)[] vars) =>
        DialectCommand.Try(Dialect, key, fallback, vars);

    public uint ChannelCount => _session.PowerSupplyChannels(Dialect.Channels).Max();

    public void SetVoltage(uint channel, double volts) =>
        _session.Scpi.Write(Cmd("set_voltage", ScpiCommands.PsuSetVoltage(channel, volts),
            ("channel", channel.ToString()), ("volts", ScpiFormat.Double(volts))));

    public void SetCurrentLimit(uint channel, double amps) =>
        _session.Scpi.Write(Cmd("set_current_limit", ScpiCommands.PsuSetCurrentLimit(channel, amps),
            ("channel", channel.ToString()), ("amps", ScpiFormat.Double(amps))));

    public void OutputEnable(uint channel, bool enabled)
    {
        _session.TrackPowerSupplyChannel(channel);
        var state = enabled ? "ON" : "OFF";
        _session.Scpi.Write(Cmd("output_enable", ScpiCommands.PsuOutputEnable(channel, enabled),
            ("channel", channel.ToString()), ("state", state)));
    }

    public bool OutputStateQuery(uint channel) =>
        ParseOnOff(_session.Scpi.Query(Cmd("output_state_query", ScpiCommands.PsuOutputStateQuery(channel),
            ("channel", channel.ToString()))));

    public void OvpLevel(uint channel, double volts) =>
        _session.Scpi.Write(Cmd("ovp_level", ScpiCommands.PsuOvpLevel(channel, volts),
            ("channel", channel.ToString()), ("volts", ScpiFormat.Double(volts))));

    public void OvpEnable(uint channel, bool enabled)
    {
        var state = enabled ? "ON" : "OFF";
        _session.Scpi.Write(Cmd("ovp_enable", ScpiCommands.PsuOvpEnable(channel, state),
            ("channel", channel.ToString()), ("state", state)));
    }

    public bool OvpQuery(uint channel) =>
        ParseOnOff(_session.Scpi.Query(Cmd("ovp_query", ScpiCommands.PsuOvpQuery(channel),
            ("channel", channel.ToString()))));

    public void SenseEnable(uint channel, bool enabled)
    {
        if (Dialect.Id == "keysight_n6705c")
            throw new global::InstrumentComponents.Errors.InstrumentUnsupportedException("N6705 remote sense requires INT/EXT; ON/OFF is not supported");
        var state = enabled ? "ON" : "OFF";
        _session.Scpi.Write(Cmd("sense_enable", ScpiCommands.PsuSenseEnable(channel, state),
            ("channel", channel.ToString()), ("state", state)));
    }

    public double ReadVoltage(uint channel) =>
        ScpiSession.ParseF64(_session.Scpi.Query(Cmd("read_voltage", ScpiCommands.PsuReadVoltage(channel),
            ("channel", channel.ToString()))));

    public double ReadCurrent(uint channel) =>
        ScpiSession.ParseF64(_session.Scpi.Query(Cmd("read_current", ScpiCommands.PsuReadCurrent(channel),
            ("channel", channel.ToString()))));

    internal static bool ParseOnOff(string response)
    {
        var trimmed = response.Trim().ToUpperInvariant();
        return trimmed switch
        {
            "1" or "ON" => true,
            "0" or "OFF" => false,
            _ => throw new global::InstrumentComponents.Errors.ParseException($"expected ON/OFF state, got '{response}'"),
        };
    }
}

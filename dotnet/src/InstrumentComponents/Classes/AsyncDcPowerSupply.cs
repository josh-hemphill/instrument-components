using InstrumentComponents.Dialects;
using InstrumentComponents.Identity;
using InstrumentComponents.Kind;
using InstrumentComponents.Session;
using InstrumentComponents.Scpi;

namespace InstrumentComponents.Classes;

public sealed class AsyncDcPowerSupply
{
    private readonly AsyncInstrumentSession _session;

    public AsyncDcPowerSupply(AsyncInstrumentSession session) => _session = session;

    public AsyncInstrumentSession Session => _session;

    public Task<Idn> QueryIdnAsync(CancellationToken cancellationToken = default) =>
        _session.IdnAsync(cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        _session.ResetAsync(cancellationToken);

    public async Task OutputOffAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        foreach (var channel in _session.PowerSupplyChannels(Dialect.Channels))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await OutputEnableAsync(channel, false, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Failed to disable all PSU outputs.", failures);
        if (Dialect.Id == "generic_dcpwr" && _session.PowerSupplyChannelCount is null)
            throw new global::InstrumentComponents.Errors.InstrumentUnsupportedException("PSU physical channel count is unknown. Set Session.PowerSupplyChannelCount before shutdown; known outputs were disabled.");
    }

    private DialectProfile Dialect => _session.DialectFor(InstrumentKind.DcPowerSupply);

    private string Cmd(string key, string fallback, params (string Name, string Value)[] vars) =>
        DialectCommand.Try(Dialect, key, fallback, vars);

    public uint ChannelCount => _session.PowerSupplyChannels(Dialect.Channels).Max();

    public Task SetVoltageAsync(uint channel, double volts, CancellationToken cancellationToken = default) =>
        _session.Scpi.WriteAsync(Cmd("set_voltage", ScpiCommands.PsuSetVoltage(channel, volts),
            ("channel", channel.ToString()), ("volts", ScpiFormat.Double(volts))), cancellationToken);

    public Task SetCurrentLimitAsync(uint channel, double amps, CancellationToken cancellationToken = default) =>
        _session.Scpi.WriteAsync(Cmd("set_current_limit", ScpiCommands.PsuSetCurrentLimit(channel, amps),
            ("channel", channel.ToString()), ("amps", ScpiFormat.Double(amps))), cancellationToken);

    public Task OutputEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default)
    {
        _session.TrackPowerSupplyChannel(channel);
        var state = enabled ? "ON" : "OFF";
        return _session.Scpi.WriteAsync(Cmd("output_enable", ScpiCommands.PsuOutputEnable(channel, enabled),
            ("channel", channel.ToString()), ("state", state)), cancellationToken);
    }

    public async Task<bool> OutputStateQueryAsync(uint channel, CancellationToken cancellationToken = default)
    {
        var resp = await _session.Scpi.QueryAsync(Cmd("output_state_query", ScpiCommands.PsuOutputStateQuery(channel),
            ("channel", channel.ToString())), cancellationToken).ConfigureAwait(false);
        return DcPowerSupply.ParseOnOff(resp);
    }

    public Task OvpLevelAsync(uint channel, double volts, CancellationToken cancellationToken = default) =>
        _session.Scpi.WriteAsync(Cmd("ovp_level", ScpiCommands.PsuOvpLevel(channel, volts),
            ("channel", channel.ToString()), ("volts", ScpiFormat.Double(volts))), cancellationToken);

    public Task OvpEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default)
    {
        var state = enabled ? "ON" : "OFF";
        return _session.Scpi.WriteAsync(Cmd("ovp_enable", ScpiCommands.PsuOvpEnable(channel, state),
            ("channel", channel.ToString()), ("state", state)), cancellationToken);
    }

    public async Task<bool> OvpQueryAsync(uint channel, CancellationToken cancellationToken = default)
    {
        var resp = await _session.Scpi.QueryAsync(Cmd("ovp_query", ScpiCommands.PsuOvpQuery(channel),
            ("channel", channel.ToString())), cancellationToken).ConfigureAwait(false);
        return DcPowerSupply.ParseOnOff(resp);
    }

    public Task SenseEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default)
    {
        if (Dialect.Id == "keysight_n6705c")
            throw new global::InstrumentComponents.Errors.InstrumentUnsupportedException("N6705 remote sense requires INT/EXT; ON/OFF is not supported");
        var state = enabled ? "ON" : "OFF";
        return _session.Scpi.WriteAsync(Cmd("sense_enable", ScpiCommands.PsuSenseEnable(channel, state),
            ("channel", channel.ToString()), ("state", state)), cancellationToken);
    }

    public async Task<double> ReadVoltageAsync(uint channel, CancellationToken cancellationToken = default)
    {
        var resp = await _session.Scpi.QueryAsync(Cmd("read_voltage", ScpiCommands.PsuReadVoltage(channel),
            ("channel", channel.ToString())), cancellationToken).ConfigureAwait(false);
        return ScpiSession.ParseF64(resp);
    }

    public async Task<double> ReadCurrentAsync(uint channel, CancellationToken cancellationToken = default)
    {
        var resp = await _session.Scpi.QueryAsync(Cmd("read_current", ScpiCommands.PsuReadCurrent(channel),
            ("channel", channel.ToString())), cancellationToken).ConfigureAwait(false);
        return ScpiSession.ParseF64(resp);
    }
}

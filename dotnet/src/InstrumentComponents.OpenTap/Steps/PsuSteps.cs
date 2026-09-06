using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("PSU Configure Output", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.PowerSupply], Description: "Set voltage, current limit, and output enable.")]
public sealed class PsuConfigureOutputStep : InstrumentBoundStep<DcPowerSupplyInstrument>
{
    [Display("Channel", Order: 2, Description: "1-based output channel.")]
    public uint Channel { get; set; } = 1;

    [Display("Voltage", Order: 3, Description: "Output voltage setpoint.")]
    [Unit("V")]
    public double Voltage { get; set; }

    [Display("Current limit", Order: 4, Description: "Output current limit.")]
    [Unit("A")]
    public double CurrentLimit { get; set; }

    [Display("Output enabled", Order: 5, Description: "Enable the selected output.")]
    public bool OutputEnabled { get; set; } = true;

    public PsuConfigureOutputStep()
    {
        Rules.Add(() => Channel >= 1, "Channel must be at least 1.", nameof(Channel));
        Rules.Add(() => double.IsFinite(Voltage), "Voltage must be a finite number.", nameof(Voltage));
        Rules.Add(() => double.IsFinite(CurrentLimit), "Current limit must be a finite number.", nameof(CurrentLimit));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var psu = instrument.Supply;
        psu.SetVoltage(Channel, Voltage);
        psu.SetCurrentLimit(Channel, CurrentLimit);
        psu.OutputEnable(Channel, OutputEnabled);
        PhaseIResults.PublishScalar(Results, "Voltage", Voltage, "V");
        PhaseIResults.PublishScalar(Results, "CurrentLimit", CurrentLimit, "A");
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("PSU Readback", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.PowerSupply], Description: "Read voltage and current.")]
public sealed class PsuReadbackStep : InstrumentBoundStep<DcPowerSupplyInstrument>
{
    [Display("Channel", Order: 2, Description: "1-based output channel.")]
    public uint Channel { get; set; } = 1;

    public PsuReadbackStep()
    {
        Rules.Add(() => Channel >= 1, "Channel must be at least 1.", nameof(Channel));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var psu = instrument.Supply;
        var volts = psu.ReadVoltage(Channel);
        var amps = psu.ReadCurrent(Channel);
        PhaseIResults.PublishSample(Results, $"CH{Channel}.V", 0, volts);
        PhaseIResults.PublishSample(Results, $"CH{Channel}.I", 0, amps);
        PhaseIResults.PublishScalar(Results, $"CH{Channel}.V", volts, "V");
        PhaseIResults.PublishScalar(Results, $"CH{Channel}.I", amps, "A");
        UpgradeVerdict(Verdict.Pass);
    }
}

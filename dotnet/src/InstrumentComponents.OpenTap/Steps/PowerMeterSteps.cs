using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Power Meter Read", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.PowerMeter], Description: "Read power.")]
public sealed class PowerMeterReadStep : InstrumentBoundStep<PowerMeterInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var value = instrument.Meter.Read();
        PhaseIResults.PublishScalar(Results, "Power", value, "");
        UpgradeVerdict(Verdict.Pass);
    }
}

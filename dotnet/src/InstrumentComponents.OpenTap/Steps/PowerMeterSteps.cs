using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Power Meter Read", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.PowerMeter], Description: "Read power.")]
public sealed class PowerMeterReadStep : OptionalLimitStep<PowerMeterInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        PublishScalar("Power", instrument.Meter.Read(), "");
    }
}

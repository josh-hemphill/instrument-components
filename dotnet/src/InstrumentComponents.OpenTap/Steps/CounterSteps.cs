using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Counter Measure Frequency", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Counter], Description: "Measure frequency.")]
public sealed class CounterMeasureFrequencyStep : OptionalLimitStep<CounterInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        PublishScalar("Frequency", instrument.Counter.MeasureFrequency(), "Hz");
    }
}

[Display("Counter Measure Period", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Counter], Description: "Measure period.")]
public sealed class CounterMeasurePeriodStep : OptionalLimitStep<CounterInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        PublishScalar("Period", instrument.Counter.MeasurePeriod(), "s");
    }
}

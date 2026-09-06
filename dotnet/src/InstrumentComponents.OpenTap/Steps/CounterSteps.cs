using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Counter Measure Frequency", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Counter], Description: "Measure frequency.")]
public sealed class CounterMeasureFrequencyStep : InstrumentBoundStep<CounterInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var value = instrument.Counter.MeasureFrequency();
        PhaseIResults.PublishScalar(Results, "Frequency", value, "Hz");
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Counter Measure Period", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Counter], Description: "Measure period.")]
public sealed class CounterMeasurePeriodStep : InstrumentBoundStep<CounterInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var value = instrument.Counter.MeasurePeriod();
        PhaseIResults.PublishScalar(Results, "Period", value, "s");
        UpgradeVerdict(Verdict.Pass);
    }
}

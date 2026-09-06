using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("DMM Measure Voltage DC", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Dmm], Description: "Acquire VDC samples.")]
public sealed class DmmMeasureVoltageDcStep : InstrumentBoundStep<DmmInstrument>
{
    [Display("Channel", Order: 2, Description: "Result channel label.")]
    public string Channel { get; set; } = "VDC";

    [Display("Sample Count", Order: 3, Description: "Number of sequential readings.")]
    public int SampleCount { get; set; } = 1;

    [Display("Interval Ms", Order: 4, Description: "Delay between samples.")]
    [Unit("ms")]
    public int IntervalMs { get; set; }

    public DmmMeasureVoltageDcStep()
    {
        Rules.Add(() => SampleCount >= 1, "Sample count must be at least 1.", nameof(SampleCount));
        Rules.Add(() => IntervalMs >= 0, "Interval must be zero or positive.", nameof(IntervalMs));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var count = Math.Max(1, SampleCount);
        for (var i = 0; i < count; i++)
        {
            TapThread.ThrowIfAborted();
            var value = instrument.Dmm.MeasureVoltageDc();
            PhaseIResults.PublishSample(Results, Channel, i, value);
            if (IntervalMs > 0 && i < count - 1)
                TapThread.Sleep(IntervalMs);
        }

        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("DMM Measure Scalar", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Dmm], Description: "One VDC reading with optional limits.")]
public sealed class DmmMeasureScalarStep : InstrumentBoundStep<DmmInstrument>
{
    [Display("Name", Order: 2, Description: "Scalar result name.")]
    public string MetricName { get; set; } = "VDC";

    [Display("Unit", Order: 3, Description: "Scalar result unit.")]
    public string Unit { get; set; } = "V";

    [Display("Limit low", Order: 4, Description: "Optional inclusive lower limit.")]
    [Unit("V")]
    public double? LimitLow { get; set; }

    [Display("Limit high", Order: 5, Description: "Optional inclusive upper limit.")]
    [Unit("V")]
    public double? LimitHigh { get; set; }

    public DmmMeasureScalarStep()
    {
        Rules.Add(
            () => LimitLow is null || LimitHigh is null || LimitLow <= LimitHigh,
            "Limit low must not exceed limit high.",
            nameof(LimitLow),
            nameof(LimitHigh));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var value = instrument.Dmm.MeasureVoltageDc();
        PhaseIResults.PublishScalar(Results, MetricName, value, Unit, LimitLow, LimitHigh);
        if (PhaseIResults.IsOutOfBand(value, LimitLow, LimitHigh))
        {
            Log.Error("{0}={1} outside limits", MetricName, value);
            UpgradeVerdict(Verdict.Fail);
            return;
        }

        UpgradeVerdict(Verdict.Pass);
    }
}

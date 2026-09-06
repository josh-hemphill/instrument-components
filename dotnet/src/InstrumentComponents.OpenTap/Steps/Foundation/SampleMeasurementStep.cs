using OpenTap;

namespace InstrumentComponents.OpenTap;

/// <summary>Acquires abort-safe sample rows into the Phase I Sample table.</summary>
public abstract class SampleMeasurementStep<TInstrument> : InstrumentBoundStep<TInstrument>
    where TInstrument : ScpiInstrument
{
    [Display("Channel", Order: 2, Description: "Result channel label.")]
    public string Channel { get; set; } = "CH";

    [Display("Sample Count", Order: 3, Description: "Number of sequential readings.")]
    public int SampleCount { get; set; } = 1;

    [Display("Interval Ms", Order: 4, Description: "Delay between samples.")]
    [Unit("ms")]
    public int IntervalMs { get; set; }

    protected SampleMeasurementStep()
    {
        Rules.Add(() => SampleCount >= 1, "Sample count must be at least 1.", nameof(SampleCount));
        Rules.Add(() => IntervalMs >= 0, "Interval must be zero or positive.", nameof(IntervalMs));
    }

    protected void AcquireSamples(Func<double> measure)
    {
        var count = Math.Max(1, SampleCount);
        for (var i = 0; i < count; i++)
        {
            TapThread.ThrowIfAborted();
            PhaseIResults.PublishSample(Results, Channel, i, measure());
            if (IntervalMs > 0 && i < count - 1)
                TapThread.Sleep(IntervalMs);
        }

        UpgradeVerdict(Verdict.Pass);
    }
}

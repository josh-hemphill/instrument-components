using OpenTap;

namespace InstrumentComponents.OpenTap;

/// <summary>Publishes a Phase I scalar and applies optional inclusive limits.</summary>
public abstract class OptionalLimitStep<TInstrument> : InstrumentBoundStep<TInstrument>
    where TInstrument : ScpiInstrument
{
    [Display("Limit low", Order: 90, Description: "Optional inclusive lower limit.")]
    public double? LimitLow { get; set; }

    [Display("Limit high", Order: 91, Description: "Optional inclusive upper limit.")]
    public double? LimitHigh { get; set; }

    protected OptionalLimitStep()
    {
        Rules.Add(
            () => LimitLow is null || LimitHigh is null || LimitLow <= LimitHigh,
            "Limit low must not exceed limit high.",
            nameof(LimitLow),
            nameof(LimitHigh));
    }

    protected void PublishScalar(string name, double value, string unit)
    {
        PhaseIResults.PublishScalar(Results, name, value, unit, LimitLow, LimitHigh);
        if (PhaseIResults.IsOutOfBand(value, LimitLow, LimitHigh))
        {
            Log.Error("{0}={1} outside limits", name, value);
            UpgradeVerdict(Verdict.Fail);
            return;
        }

        UpgradeVerdict(Verdict.Pass);
    }
}

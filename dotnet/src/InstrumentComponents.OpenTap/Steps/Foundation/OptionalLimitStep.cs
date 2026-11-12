using OpenTap;

namespace InstrumentComponents.OpenTap;

/// <summary>Publishes a Phase I scalar and applies optional inclusive limits.</summary>
public abstract class OptionalLimitStep<TInstrument> : InstrumentBoundStep<TInstrument>
    where TInstrument : DeviceInstrument
{
    [Display("Limit low", Order: 90, Description: "Optional inclusive lower limit.")]
    public double? LimitLow { get; set; }

    [Display("Limit high", Order: 91, Description: "Optional inclusive upper limit.")]
    public double? LimitHigh { get; set; }

    protected OptionalLimitStep()
    {
        Rules.Add(() => LimitLow is null || double.IsFinite(LimitLow.Value), "Limit low must be finite.", nameof(LimitLow));
        Rules.Add(() => LimitHigh is null || double.IsFinite(LimitHigh.Value), "Limit high must be finite.", nameof(LimitHigh));
        Rules.Add(
            () => LimitLow is null || LimitHigh is null || LimitLow <= LimitHigh,
            "Limit low must not exceed limit high.",
            nameof(LimitLow),
            nameof(LimitHigh));
    }

    protected void PublishScalar(string name,InstrumentComponents.Drivers.Catalog.Quantity value,string unit)
    {
        // Physical units come from the provider, including Unknown. Display labels
        // cannot turn an unknown temperature into a Celsius claim.
        var actual=PhaseIResults.UnitSymbol(value.Unit);
        if(unit.Length>0 && actual!="Unknown" && unit!=actual)throw new InvalidOperationException("Expected unit differs from the provider result; a display label cannot convert units.");
        PublishScalar(name,value.Value,actual);
        if(value.Status!=InstrumentComponents.Drivers.MeasurementStatus.Valid){Log.Error("{0}: driver reported {1}",name,value.Status);UpgradeVerdict(Verdict.Fail);}
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

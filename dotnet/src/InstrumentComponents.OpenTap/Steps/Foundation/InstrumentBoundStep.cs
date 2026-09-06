using System.Diagnostics.CodeAnalysis;
using OpenTap;

namespace InstrumentComponents.OpenTap;

/// <summary>Binds a typed OpenTAP instrument resource with shared validation and naming.</summary>
public abstract class InstrumentBoundStep<TInstrument> : TestStep, IFormatName
    where TInstrument : ScpiInstrument
{
    [Display("Instrument", Order: 1, Description: "Instrument resource that executes this step.")]
    public TInstrument Instrument { get; set; } = null!;

    protected InstrumentBoundStep()
    {
        Rules.Add(() => Instrument is not null, "Instrument must be assigned.", nameof(Instrument));
    }

    public virtual string GetFormattedName()
    {
        if (Instrument is not null && !string.IsNullOrWhiteSpace(Instrument.Name))
            return $"{Name} @ {Instrument.Name}";
        return Name;
    }

    protected bool TryGetInstrument([NotNullWhen(true)] out TInstrument instrument)
    {
        if (Instrument is not null)
        {
            instrument = Instrument;
            return true;
        }

        Log.Error("No instrument assigned.");
        UpgradeVerdict(Verdict.Error);
        instrument = null!;
        return false;
    }
}

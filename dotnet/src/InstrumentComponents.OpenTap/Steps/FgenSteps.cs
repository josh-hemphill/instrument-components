using InstrumentComponents.Classes;
using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("FGen Configure Output", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.FunctionGenerator], Description: "Set waveform and output.")]
public sealed class FgenConfigureOutputStep : InstrumentBoundStep<FunctionGeneratorInstrument>
{
    [Display("Waveform", Order: 2, Description: "Standard waveform.")]
    public Waveform Waveform { get; set; } = Waveform.Sine;

    [Display("Frequency (Hz)", Order: 3, Description: "Output frequency.")]
    [Unit("Hz")]
    public double FrequencyHz { get; set; } = 1000;

    [Display("Amplitude (Vpp)", Order: 4, Description: "Peak-to-peak amplitude.")]
    [Unit("V")]
    public double AmplitudeVpp { get; set; } = 1;

    [Display("Offset (V)", Order: 5, Description: "DC offset.")]
    [Unit("V")]
    public double OffsetVolts { get; set; }

    [Display("Output enabled", Order: 6, Description: "Enable the generator output.")]
    public bool OutputEnabled { get; set; } = true;

    public FgenConfigureOutputStep()
    {
        Rules.Add(() => double.IsFinite(FrequencyHz) && FrequencyHz > 0, "Frequency must be a finite value greater than 0.", nameof(FrequencyHz));
        Rules.Add(() => double.IsFinite(AmplitudeVpp) && AmplitudeVpp >= 0, "Amplitude must be a finite non-negative number.", nameof(AmplitudeVpp));
        Rules.Add(() => double.IsFinite(OffsetVolts), "Offset must be a finite number.", nameof(OffsetVolts));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var fgen = instrument.Generator;
        fgen.SetWaveform(Waveform);
        fgen.SetFrequency(FrequencyHz);
        fgen.SetAmplitude(AmplitudeVpp);
        fgen.SetOffset(OffsetVolts);
        fgen.OutputEnable(OutputEnabled);
        PhaseIResults.PublishScalar(Results, "Frequency", FrequencyHz, "Hz");
        PhaseIResults.PublishScalar(Results, "Amplitude", AmplitudeVpp, "V");
        UpgradeVerdict(Verdict.Pass);
    }
}

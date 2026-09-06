using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Spectrum Analyzer Marker Peak", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.SpectrumAnalyzer], Description: "Peak marker X/Y.")]
public sealed class SpectrumAnalyzerMarkerPeakStep : InstrumentBoundStep<SpectrumAnalyzerInstrument>
{
    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var analyzer = instrument.Analyzer;
        analyzer.MarkerPeak();
        var hz = analyzer.MarkerX();
        var dbm = analyzer.MarkerY();
        PhaseIResults.PublishScalar(Results, "MarkerX", hz, "Hz");
        PhaseIResults.PublishScalar(Results, "MarkerY", dbm, "dBm");
        UpgradeVerdict(Verdict.Pass);
    }
}

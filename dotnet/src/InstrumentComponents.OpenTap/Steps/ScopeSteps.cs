using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Scope Measure Vpp", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Oscilloscope], Description: "Peak-to-peak voltage.")]
public sealed class ScopeMeasureVppStep : InstrumentBoundStep<OscilloscopeInstrument>
{
    [Display("Channel", Order: 2, Description: "1-based scope channel.")]
    public uint Channel { get; set; } = 1;

    public ScopeMeasureVppStep()
    {
        Rules.Add(() => Channel >= 1, "Channel must be at least 1.", nameof(Channel));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var value = instrument.Scope.MeasureVpp(Channel);
        PhaseIResults.PublishScalar(Results, $"CH{Channel}.Vpp", value, "V");
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Scope Measure Frequency", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Oscilloscope], Description: "Frequency measurement.")]
public sealed class ScopeMeasureFrequencyStep : InstrumentBoundStep<OscilloscopeInstrument>
{
    [Display("Channel", Order: 2, Description: "1-based scope channel.")]
    public uint Channel { get; set; } = 1;

    public ScopeMeasureFrequencyStep()
    {
        Rules.Add(() => Channel >= 1, "Channel must be at least 1.", nameof(Channel));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var value = instrument.Scope.MeasureFrequency(Channel);
        PhaseIResults.PublishScalar(Results, $"CH{Channel}.Freq", value, "Hz");
        UpgradeVerdict(Verdict.Pass);
    }
}

[Display("Scope Capture Trace", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Oscilloscope], Description: "ASCII voltage trace as Sample rows.")]
public sealed class ScopeCaptureTraceStep : InstrumentBoundStep<OscilloscopeInstrument>
{
    [Display("Channel", Order: 2, Description: "1-based scope channel.")]
    public uint Channel { get; set; } = 1;

    public ScopeCaptureTraceStep()
    {
        Rules.Add(() => Channel >= 1, "Channel must be at least 1.", nameof(Channel));
    }

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        var trace = instrument.Scope.CaptureVoltageTrace(Channel);
        for (var i = 0; i < trace.Samples.Count; i++)
            PhaseIResults.PublishSample(Results, $"CH{Channel}", i, trace.Samples[i]);
        UpgradeVerdict(Verdict.Pass);
    }
}

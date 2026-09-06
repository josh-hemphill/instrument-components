using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("Scope Measure Vpp", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Oscilloscope], Description: "Peak-to-peak voltage.")]
public sealed class ScopeMeasureVppStep : OptionalLimitStep<OscilloscopeInstrument>
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

        PublishScalar($"CH{Channel}.Vpp", instrument.Scope.MeasureVpp(Channel), "V");
    }
}

[Display("Scope Measure Frequency", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Oscilloscope], Description: "Frequency measurement.")]
public sealed class ScopeMeasureFrequencyStep : OptionalLimitStep<OscilloscopeInstrument>
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

        PublishScalar($"CH{Channel}.Freq", instrument.Scope.MeasureFrequency(Channel), "Hz");
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

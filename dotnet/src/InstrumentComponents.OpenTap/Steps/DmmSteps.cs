using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("DMM Measure Voltage DC", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Dmm], Description: "Acquire VDC samples.")]
public sealed class DmmMeasureVoltageDcStep : SampleMeasurementStep<DmmInstrument>
{
    public DmmMeasureVoltageDcStep() => Channel = "VDC";

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        AcquireSamples(() => instrument.Dmm.MeasureVoltageDc());
    }
}

[Display("DMM Measure Scalar", Groups: [OpenTapDisplayGroups.Root, OpenTapDisplayGroups.Dmm], Description: "One VDC reading with optional limits.")]
public sealed class DmmMeasureScalarStep : OptionalLimitStep<DmmInstrument>
{
    [Display("Name", Order: 2, Description: "Scalar result name.")]
    public string MetricName { get; set; } = "VDC";

    [Display("Unit", Order: 3, Description: "Scalar result unit.")]
    public string Unit { get; set; } = "V";

    public override void Run()
    {
        if (!TryGetInstrument(out var instrument))
            return;

        PublishScalar(MetricName, instrument.Dmm.MeasureVoltageDc(), Unit);
    }
}

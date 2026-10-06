using InstrumentComponents.Classes;
using InstrumentComponents.Kind;
using InstrumentComponents.Scpi;
using OpenTap;

namespace InstrumentComponents.OpenTap;

[Display("DMM", Groups: ["Instrument Components"], Description: "SCPI digital multimeter.")]
public sealed class DmmInstrument : ScpiInstrument
{
    public DmmInstrument() => Name = "DMM";

    public DmmInstrument(IScpiIo io) : base(io) => Name = "DMM";

    protected override InstrumentKind PrimaryKind => InstrumentKind.Dmm;

    public Dmm Dmm => AsDmm();

    public override void OutputOff() => AsDmm().OutputOff();
}

[Display("DC Power Supply", Groups: ["Instrument Components"], Description: "SCPI DC power supply.")]
public sealed class DcPowerSupplyInstrument : ScpiInstrument
{
    public DcPowerSupplyInstrument() => Name = "DC Power Supply";

    public DcPowerSupplyInstrument(IScpiIo io) : base(io) => Name = "DC Power Supply";

    protected override InstrumentKind PrimaryKind => InstrumentKind.DcPowerSupply;

    [Display("Output Channels", Group: "Communication", Order: 3, Description: "Physical PSU channel count. Required for generic devices before safe shutdown; vendor dialects supply their known count.")]
    public uint? OutputChannelCount { get; set; }

    public DcPowerSupply Supply
    {
        get
        {
            var supply = AsDcPowerSupply();
            supply.Session.PowerSupplyChannelCount = OutputChannelCount;
            return supply;
        }
    }

    public override void OutputOff() => Supply.OutputOff();
}

[Display("Function Generator", Groups: ["Instrument Components"], Description: "SCPI function generator.")]
public sealed class FunctionGeneratorInstrument : ScpiInstrument
{
    public FunctionGeneratorInstrument() => Name = "Function Generator";

    public FunctionGeneratorInstrument(IScpiIo io) : base(io) => Name = "Function Generator";

    protected override InstrumentKind PrimaryKind => InstrumentKind.FunctionGenerator;

    public FunctionGenerator Generator => AsFunctionGenerator();

    public override void OutputOff() => AsFunctionGenerator().OutputOff();
}

[Display("Oscilloscope", Groups: ["Instrument Components"], Description: "SCPI oscilloscope.")]
public sealed class OscilloscopeInstrument : ScpiInstrument
{
    public OscilloscopeInstrument() => Name = "Oscilloscope";

    public OscilloscopeInstrument(IScpiIo io) : base(io) => Name = "Oscilloscope";

    protected override InstrumentKind PrimaryKind => InstrumentKind.Oscilloscope;

    public Oscilloscope Scope => AsOscilloscope();

    public override void OutputOff() => AsOscilloscope().OutputOff();
}

[Display("Switch", Groups: ["Instrument Components"], Description: "SCPI switch / matrix.")]
public sealed class SwitchInstrument : ScpiInstrument
{
    public SwitchInstrument() => Name = "Switch";

    public SwitchInstrument(IScpiIo io) : base(io) => Name = "Switch";

    protected override InstrumentKind PrimaryKind => InstrumentKind.Switch;

    public Classes.Switch Matrix => AsSwitch();

    public override void OutputOff() => AsSwitch().OutputOff();
}

[Display("Counter", Groups: ["Instrument Components"], Description: "SCPI frequency counter.")]
public sealed class CounterInstrument : ScpiInstrument
{
    public CounterInstrument() => Name = "Counter";

    public CounterInstrument(IScpiIo io) : base(io) => Name = "Counter";

    protected override InstrumentKind PrimaryKind => InstrumentKind.Counter;

    public Counter Counter => AsCounter();

    public override void OutputOff() => AsCounter().OutputOff();
}

[Display("Power Meter", Groups: ["Instrument Components"], Description: "SCPI RF power meter.")]
public sealed class PowerMeterInstrument : ScpiInstrument
{
    public PowerMeterInstrument() => Name = "Power Meter";

    public PowerMeterInstrument(IScpiIo io) : base(io) => Name = "Power Meter";

    protected override InstrumentKind PrimaryKind => InstrumentKind.PowerMeter;

    public PowerMeter Meter => AsPowerMeter();

    public override void OutputOff() => AsPowerMeter().OutputOff();
}

[Display("Spectrum Analyzer", Groups: ["Instrument Components"], Description: "SCPI spectrum analyzer.")]
public sealed class SpectrumAnalyzerInstrument : ScpiInstrument
{
    public SpectrumAnalyzerInstrument() => Name = "Spectrum Analyzer";

    public SpectrumAnalyzerInstrument(IScpiIo io) : base(io) => Name = "Spectrum Analyzer";

    protected override InstrumentKind PrimaryKind => InstrumentKind.SpectrumAnalyzer;

    public SpectrumAnalyzer Analyzer => AsSpectrumAnalyzer();

    public override void OutputOff() => AsSpectrumAnalyzer().OutputOff();
}

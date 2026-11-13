using InstrumentComponents.Drivers.Catalog;
using OpenTap;
namespace InstrumentComponents.OpenTap;
[Display("DMM",Groups:["Instrument Components"],Description:"Registered dmm device capabilities.")]
public sealed class DmmInstrument : DeviceInstrument
{
    public DmmInstrument()=>Name="DMM";
    protected override string PrimaryKind=>"Dmm";
    public Dmm Dmm=>AsDmm();
}
[Display("DC Power Supply",Groups:["Instrument Components"],Description:"Registered dc power supply device capabilities.")]
public sealed class DcPowerSupplyInstrument : DeviceInstrument
{
    public DcPowerSupplyInstrument()=>Name="DC Power Supply";
    protected override string PrimaryKind=>"DcPowerSupply";
    public DcPowerSupply Supply=>AsDcPowerSupply();
}
[Display("Function Generator",Groups:["Instrument Components"],Description:"Registered function generator device capabilities.")]
public sealed class FunctionGeneratorInstrument : DeviceInstrument
{
    public FunctionGeneratorInstrument()=>Name="Function Generator";
    protected override string PrimaryKind=>"FunctionGenerator";
    public FunctionGenerator Generator=>AsFunctionGenerator();
}
[Display("Oscilloscope",Groups:["Instrument Components"],Description:"Registered oscilloscope device capabilities.")]
public sealed class OscilloscopeInstrument : DeviceInstrument
{
    public OscilloscopeInstrument()=>Name="Oscilloscope";
    protected override string PrimaryKind=>"Oscilloscope";
    public Oscilloscope Scope=>AsOscilloscope();
}
[Display("Switch",Groups:["Instrument Components"],Description:"Registered switch device capabilities.")]
public sealed class SwitchInstrument : DeviceInstrument
{
    public SwitchInstrument()=>Name="Switch";
    protected override string PrimaryKind=>"Switch";
    public InstrumentComponents.Drivers.Catalog.Switch Matrix=>AsSwitch();
}
[Display("Counter",Groups:["Instrument Components"],Description:"Registered counter device capabilities.")]
public sealed class CounterInstrument : DeviceInstrument
{
    public CounterInstrument()=>Name="Counter";
    protected override string PrimaryKind=>"Counter";
    public Counter Counter=>AsCounter();
}
[Display("Power Meter",Groups:["Instrument Components"],Description:"Registered power meter device capabilities.")]
public sealed class PowerMeterInstrument : DeviceInstrument
{
    public PowerMeterInstrument()=>Name="Power Meter";
    protected override string PrimaryKind=>"PowerMeter";
    public PowerMeter Meter=>AsPowerMeter();
}
[Display("Spectrum Analyzer",Groups:["Instrument Components"],Description:"Registered spectrum analyzer device capabilities.")]
public sealed class SpectrumAnalyzerInstrument : DeviceInstrument
{
    public SpectrumAnalyzerInstrument()=>Name="Spectrum Analyzer";
    protected override string PrimaryKind=>"SpectrumAnalyzer";
    public SpectrumAnalyzer Analyzer=>AsSpectrumAnalyzer();
}

[Display("Data Acquisition", Groups: ["Instrument Components"], Description: "Registered channel-aware acquisition capabilities.")]
public sealed class DataAcquisitionInstrument : DeviceInstrument
{
    public DataAcquisitionInstrument() => Name = "Data Acquisition";
    protected override string PrimaryKind => "DataAcquisition";
    public DataAcquisition Acquisition => AsDataAcquisition();
}

[Display("RF Signal Generator", Groups: ["Instrument Components"], Description: "Registered RF source capabilities; output enable is explicit.")]
public sealed class RfSignalGeneratorInstrument : DeviceInstrument
{
    public RfSignalGeneratorInstrument() => Name = "RF Signal Generator";
    protected override string PrimaryKind => "RfSignalGenerator";
    public RfSignalGenerator Generator => AsRfSignalGenerator();
}

[Display("RF Receiver", Groups: ["Instrument Components"], Description: "Registered absolute RF measurement capabilities.")]
public sealed class RfReceiverInstrument : DeviceInstrument
{
    public RfReceiverInstrument() => Name = "RF Receiver";
    protected override string PrimaryKind => "RfReceiver";
    public RfReceiver Receiver => AsRfReceiver();
}

[Display("Digital IO", Groups: ["Instrument Components"], Description: "Registered byte input and output capabilities.")]
public sealed class DigitalIoInstrument : DeviceInstrument
{
    public DigitalIoInstrument() => Name = "Digital IO";
    protected override string PrimaryKind => "DigitalIo";
    public DigitalIo Io => AsDigitalIo();
}

using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
using OpenTap;
using CatalogIdentity=InstrumentComponents.Drivers.Catalog.DeviceIdentity;
namespace InstrumentComponents.OpenTap;

/// <summary>Protocol-neutral OpenTAP resource. The host composes factories and frozen bindings.</summary>
public abstract class DeviceInstrument : Instrument
{
    public const int DefaultIoTimeoutMilliseconds=5000,MinIoTimeoutMilliseconds=100,MaxIoTimeoutMilliseconds=120000;
    private DeviceHandle? _handle; private bool _ownsHandle;
    protected abstract string PrimaryKind { get; }
    [VisaAddress]
    [Display("Resource Address",Group:"Communication",Order:1)]
    public string VisaAddress { get; set; }=string.Empty;
    [Display("IO Timeout (ms)",Group:"Communication",Order:2,Description:"Applied by the host provider before opening.")]
    public int IoTimeoutMilliseconds { get; set; }=DefaultIoTimeoutMilliseconds;
    [Display("Driver ID",Group:"Communication",Order:3,Description:"Optional explicit driver selection; the host supplies the registered packages.")]
    public string DriverId { get; set; }=string.Empty;
    [EmbedProperties][Display("Identity",Order:10)]
    public DeviceIdentityFields IdentityFields { get; set; }=new();

    /// <summary>Attach before Open. A borrowed handle remains owned by the application.</summary>
    public void AttachDevice(DeviceHandle handle,bool ownsHandle=false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if(IsConnected)throw new InvalidOperationException("Close the resource before replacing its device handle.");
        Release();_handle=handle;_ownsHandle=ownsHandle;
    }
    public override void Open()
    {
        if(IsConnected)return;
        try {
            if(_handle is null) {
                var provider=OpenTapDevices.Provider ?? throw new InvalidOperationException("Attach a device or register OpenTapDevices.Provider before Open.");
                if(string.IsNullOrWhiteSpace(VisaAddress))throw new InvalidOperationException("Set Resource Address before using the host provider.");
                _handle=provider.Open(VisaAddress,TimeSpan.FromMilliseconds(Math.Clamp(IoTimeoutMilliseconds,MinIoTimeoutMilliseconds,MaxIoTimeoutMilliseconds)),new DriverSelection(DriverId:string.IsNullOrWhiteSpace(DriverId)?null:DriverId));
                _ownsHandle=true;
            }
            RequireKind(PrimaryKind,opening:true);base.Open();
        }
        catch(Exception failure) {
            try{Release();}catch(Exception cleanup){throw new AggregateException("Device open failed and cleanup also failed",failure,cleanup);}
            finally{ClearIdentity();if(IsConnected)base.Close();}
            throw;
        }
    }
    public override void Close()
    {
        try{Release();}finally{ClearIdentity();if(IsConnected)base.Close();}
    }
    private void Release(){var handle=_handle;var owned=_ownsHandle;_handle=null;_ownsHandle=false;if(owned)handle?.Close();}
    private void ClearIdentity()=>IdentityFields=new();
    protected DeviceHandle RequireHandle()=>IsConnected && _handle is not null ? _handle : throw new InvalidOperationException("Instrument resource is not open.");
    private DeviceHandle RequireKind(string kind,bool opening=false)
    {
        var handle=opening ? _handle ?? throw new InvalidOperationException("No device attached") : RequireHandle();
        if(handle.ClosingRequested)throw new DriverException(DriverErrorKind.SessionClosed,"Attached handle is closing or closed");
        if(!Enum.GetValues<CatalogOperation>().Any(o=>o.Describe().Kind==kind && handle.Operations.Contains(o.Runtime())))throw new DriverException(DriverErrorKind.UnsupportedOperation,"Device has no "+kind+" capabilities");
        return handle;
    }
    public Dmm AsDmm()=>new(RequireKind("Dmm"));
    public DcPowerSupply AsDcPowerSupply()=>new(RequireKind("DcPowerSupply"));
    public FunctionGenerator AsFunctionGenerator()=>new(RequireKind("FunctionGenerator"));
    public Oscilloscope AsOscilloscope()=>new(RequireKind("Oscilloscope"));
    public Drivers.Catalog.Switch AsSwitch()=>new(RequireKind("Switch"));
    public Counter AsCounter()=>new(RequireKind("Counter"));
    public PowerMeter AsPowerMeter()=>new(RequireKind("PowerMeter"));
    public SpectrumAnalyzer AsSpectrumAnalyzer()=>new(RequireKind("SpectrumAnalyzer"));
    public DataAcquisition AsDataAcquisition()=>new(RequireKind("DataAcquisition"));
    public RfSignalGenerator AsRfSignalGenerator()=>new(RequireKind("RfSignalGenerator"));
    public RfReceiver AsRfReceiver()=>new(RequireKind("RfReceiver"));
    public DigitalIo AsDigitalIo()=>new(RequireKind("DigitalIo"));
    public CatalogIdentity QueryIdentity(){var identity=RequireHandle().Invoke(new QueryIdentityRequest()).IntoIdentity();IdentityFields.CopyFrom(identity);return identity;}
    public void Reset()=>RequireHandle().Invoke(new ResetRequest()).IntoUnit();
    public void Shutdown()=>RequireHandle().Invoke(new ShutdownRequest()).IntoUnit();
}
public sealed class DeviceIdentityFields
{
    [Display("Manufacturer",Order:1)]public string Manufacturer{get;set;}=string.Empty;
    [Display("Model",Order:2)]public string Model{get;set;}=string.Empty;
    [Display("Serial",Order:3)]public string Serial{get;set;}=string.Empty;
    [Display("Firmware",Order:4)]public string Firmware{get;set;}=string.Empty;
    public void CopyFrom(CatalogIdentity identity){Manufacturer=identity.Manufacturer;Model=identity.Model;Serial=identity.Serial;Firmware=identity.Revision;}
}

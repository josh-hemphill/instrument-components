using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
namespace InstrumentComponents.Validation;

// Deliberately implements no SCPI/message transport interface. These tests verify
// the OpenTAP host's protocol-neutral boundary independently of driver wire tests.
internal sealed class OpaqueDevice : IDriverFactory
{
    public List<CatalogRequest> Calls { get; }=[];
    public int Opens,Closes;
    public bool FailShutdown{get;set;}
    public bool FailClose{get;set;}
    public MeasurementStatus Status{get;set;}=MeasurementStatus.Valid;
    public Unit DmmUnit=Unit.Volt,PowerUnit=Unit.Watt;
    public HashSet<Operation> Supported {get;}=Enum.GetValues<CatalogOperation>().Select(o=>o.Runtime()).ToHashSet();
    public IReadOnlySet<ExecutionMode> ExecutionModes=>new HashSet<ExecutionMode>{ExecutionMode.Sync,ExecutionMode.NativeAsync,ExecutionMode.WorkerAsync};
    public ValueTask<IAsyncDeviceBackend> OpenNativeAsync(DriverOpenContext context,CancellationToken ct=default){Opens++;return ValueTask.FromResult<IAsyncDeviceBackend>(new Backend(this));}
    public DriverDescriptor Descriptor=>new("synthetic.opaque",1,[new ExactIdentityRule("opaque","Example","Device")],Supported);
    public DeviceHandle OpenHandle()=>new DriverRegistry([this]).Open(new(new("opaque","fixture"),new("Example","Device")));
    public IDeviceBackend Open(DriverOpenContext context){Opens++;return new Backend(this);}
    private sealed class Backend(OpaqueDevice owner):IDeviceBackend,ICatalogBackend,IAsyncDeviceBackend,IAsyncCatalogBackend
    {
        public IReadOnlySet<Operation> Operations=>owner.Supported;
        public ICatalogBackend Catalog=>this;
        IAsyncCatalogBackend IAsyncDeviceBackend.Catalog=>this;
        public ValueTask<CatalogReply> InvokeAsync(CatalogRequest request,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return ValueTask.FromResult(Invoke(request));}
        public CatalogReply Invoke(CatalogRequest request)
        {
            owner.Calls.Add(request);
            if(request is ShutdownRequest && owner.FailShutdown)throw new DriverException(DriverErrorKind.Shutdown,"injected action failure");
            if(request is ResetRequest){owner.DmmUnit=Unit.Unknown;owner.PowerUnit=Unit.Unknown;}
            var metadata=request.Operation.Describe();
            var dimension=request.Operation.ToString();
            if(dimension.StartsWith("Dmm") && !dimension.EndsWith("Read") && !dimension.EndsWith("Fetch") && metadata.Unit!=Unit.Unknown)owner.DmmUnit=metadata.Unit==Unit.Temperature?Unit.Unknown:metadata.Unit;
            if(request is PowerMeterConfigureMeasurementRequest config)owner.PowerUnit=config.Unit==Drivers.Catalog.PowerUnit.Watt?Unit.Watt:Unit.Dbm;
            var unit=metadata.Unit switch{Unit.ConfiguredDmm=>owner.DmmUnit,Unit.ConfiguredPower=>owner.PowerUnit,Unit.Temperature=>Unit.Unknown,_=>metadata.Unit};
            return metadata.Result switch {
                ReplyKind.Quantity=>new CatalogReply.Numeric(new(1.25,unit,owner.Status)),
                ReplyKind.Boolean=>new CatalogReply.State(true),
                ReplyKind.Count=>new CatalogReply.Count(2),
                ReplyKind.Samples or ReplyKind.Trace=>new CatalogReply.Trace(new([1,2,3],unit,metadata.Result==ReplyKind.Trace?0.5:null)),
                ReplyKind.Identity=>new CatalogReply.Identity(new("Example","Device","SN","1")),
                _=>new CatalogReply.Empty()};
        }
        public void Dispose(){owner.Closes++;if(owner.FailClose)throw new DriverException(DriverErrorKind.Transport,"injected close failure");}
    }
}

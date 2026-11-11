using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
using InstrumentComponents.Scpi;
using InstrumentComponents.TestKit;
internal static class EndpointProof
{
    internal static async Task Run()
    {
        for(var variant=0;variant<5;variant++){
            var mode=variant==4?1:variant;
            var source=GeneratedDrivers.GeneratedProfiles.Create().Single();
            var unknown=source.BindInventory(new([1],null),[]);
            var known=source.BindInventory(new([1,3],[new(OutputKind.DcPowerSupply,1),new(OutputKind.DcPowerSupply,3),new(OutputKind.FunctionGenerator)]),[]);
            var a=new Script([ScriptStep.Write(":TEST:DcPowerSupplyOutputEnable 1,OFF"),ScriptStep.Write(":TEST:DcPowerSupplyOutputEnable 3,OFF"),ScriptStep.Write(":TEST:FunctionGeneratorOutputEnable OFF"),ScriptStep.Close()]);
            var b=new Script([ScriptStep.Query(":TEST:DcPowerSupplyReadVoltage? 1","2.2"),ScriptStep.Close()]);var connector=new Connections(a,b);
            var endpoints=new Dictionary<Endpoint,ScpiProfile>{{new(" ScPi ","known"),known}};
            IDriverFactory factory=variant==1?new AsyncScpiFactory(unknown,"scpi",connector,endpointProfiles:endpoints):new ScpiFactory(unknown,"scpi",connector,nativeConnector:connector,endpointProfiles:endpoints);
            endpoints.Clear(); // Caller mutation must not change a registered route.
            var registry=new DriverRegistry([factory]);var selection=new DriverSelection(AllowStandardScpi:true);var required=new HashSet<Operation>{Operation.CatalogShutdown,Operation.CatalogDcPowerSupplyChannelCount};
            var ca=new DeviceCandidate(new("scpi","known"),new());var cb=new DeviceCandidate(new("scpi","unknown"),new());
            try{if(mode==0)registry.Open(cb,selection,required);else if(mode==1)await registry.OpenNativeAsync(cb,selection,required);else await registry.OpenWorkerAsync(cb,selection,required);throw new InvalidOperationException("Unknown endpoint accepted required shutdown");}
            catch(DriverException error)when(error.Kind==DriverErrorKind.UnsupportedOperation){}
            Require(b.Opens==0,"Required capability rejection opened IO");
            DeviceHandle? syncA=mode==0?registry.Open(ca,selection,required):null,syncB=mode==0?registry.Open(cb,selection):null;
            NativeDeviceHandle? nativeA=mode==1?await registry.OpenNativeAsync(ca,selection,required):null,nativeB=mode==1?await registry.OpenNativeAsync(cb,selection):null;
            WorkerDeviceHandle? workerA=mode>=2?await registry.OpenWorkerAsync(ca,selection,required):null,workerB=mode>=2?await registry.OpenWorkerAsync(cb,selection):null;
            CatalogReply count=mode switch{0=>syncA!.Invoke(new DcPowerSupplyChannelCountRequest()),1=>await nativeA!.InvokeAsync(new DcPowerSupplyChannelCountRequest()),2=>await workerA!.InvokeAsync(new DcPowerSupplyChannelCountRequest()),_=>workerA!.Invoke(new DcPowerSupplyChannelCountRequest())};
            Require(count.IntoCount()==2,"Route inventory changed");
            try{if(mode==0)syncB!.Invoke(new DcPowerSupplyChannelCountRequest());else if(mode==1)await nativeB!.InvokeAsync(new DcPowerSupplyChannelCountRequest());else if(mode==2)await workerB!.InvokeAsync(new DcPowerSupplyChannelCountRequest());else workerB!.Invoke(new DcPowerSupplyChannelCountRequest());throw new InvalidOperationException("Unknown endpoint inherited complete inventory");}
            catch(DriverException error)when(error.Kind==DriverErrorKind.UnsupportedOperation){}
            var value=mode switch{0=>new DcPowerSupply(syncB!).ReadVoltage(1),1=>await new AsyncDcPowerSupply(nativeB!).ReadVoltageAsync(1),2=>await new AsyncDcPowerSupply(workerB!).ReadVoltageAsync(1),_=>new DcPowerSupply(workerB!).ReadVoltage(1)};
            Require(value.Value==2.2,"Wrong route value");
            if(mode==0){Require(syncA!.Evidence.DriverId==syncB!.Evidence.DriverId,"Canonical identity split");syncA.Close(true);syncB.Close();}
            else if(mode==1){await nativeA!.CloseAsync(true);await nativeB!.CloseAsync();}
            else{await workerA!.CloseAsync(true);await workerB!.CloseAsync();a.AssertThreadAffinity();b.AssertThreadAffinity();}
            a.AssertComplete();b.AssertComplete();Require(a.Opens==1&&b.Opens==1,"Unexpected connection count");
            var invalid=new[]{new KeyValuePair<Endpoint,ScpiProfile>(new("scpi","known"),known),new(new(" SCPI ","known"),known)};
            ExpectInvalid(()=>new ScpiFactory(unknown,"scpi",connector,endpointProfiles:invalid));
            ExpectInvalid(()=>new AsyncScpiFactory(unknown,"scpi",connector,endpointProfiles:[new(new("scpi","known"),new("synthetic.other",null,[],[new(CatalogOperation.Reset,[new("*RST")])]))]));
        }
        Console.WriteLine("PASS immutable endpoint profiles: one canonical ID, connection-specific capabilities, pre-open requirements, four frontends");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void ExpectInvalid(Action call){try{call();throw new InvalidOperationException("Invalid endpoint profiles accepted");}catch(DriverException error)when(error.Kind==DriverErrorKind.DriverContractViolation){}}
    private sealed class Connections(Script known,Script unknown):IScpiConnector,IAsyncScpiConnector{
        private Script Select(DriverOpenContext context)=>context.Candidate.Endpoint.Address=="known"?known:unknown;
        public IMessageIo Open(DriverOpenContext context)=>Select(context).Open(context);
        public ValueTask<IAsyncMessageIo> OpenAsync(DriverOpenContext context,CancellationToken ct=default)=>Select(context).OpenAsync(context,ct);
    }
}

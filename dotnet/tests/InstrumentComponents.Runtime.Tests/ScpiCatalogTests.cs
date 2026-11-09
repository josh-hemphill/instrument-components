using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Scpi;
using InstrumentComponents.TestKit;
using GeneratedDrivers;
namespace InstrumentComponents.Runtime.Tests;
public sealed class ScpiCatalogTests
{
    private static DeviceCandidate Candidate => new(new("scpi", "fixture"), new());
    [Fact]
    public async Task AllTypedOperationsAgreeAcrossFourFrontends()
    {
        var profile = GeneratedProfiles.Create().Single(); var cases = GeneratedCases.Create(); Assert.Equal(139, cases.Count);
        for (var mode = 0; mode < 4; mode++)
        foreach (var example in cases)
        {
            Assert.NotEmpty(example.Calls);
            var script = new Script(example.Script);
            IDriverFactory factory = mode == 1 ? new AsyncScpiFactory(profile,"scpi",script) : new ScpiFactory(profile,"scpi",script);
            var registry = new DriverRegistry([factory]); var selection = new DriverSelection(AllowStandardScpi: true);
            DeviceHandle? sync = mode == 0 ? registry.Open(Candidate, selection) : null;
            NativeDeviceHandle? native = mode == 1 ? await registry.OpenNativeAsync(Candidate, selection) : null;
            WorkerDeviceHandle? worker = mode >= 2 ? await registry.OpenWorkerAsync(Candidate, selection) : null;
            for (var i = 0; i < example.Calls.Count; i++)
            {
                CatalogReply? actual = null;
                var error = await Record.ExceptionAsync(async () => { actual = mode switch { 0 => sync!.Invoke(example.Calls[i]), 1 => await native!.InvokeAsync(example.Calls[i]), 2 => await worker!.InvokeAsync(example.Calls[i]), _ => worker!.Invoke(example.Calls[i]) }; });
                if (i == example.Calls.Count-1 && example.ExpectedError is { } expected) Assert.Equal(expected, Assert.IsType<DriverException>(error).Kind);
                else { Assert.Null(error); if (i == example.Calls.Count-1) EqualReply(example.Expected!,actual!); }
            }
            if (sync is not null) sync.Close(); else if (native is not null) await native.CloseAsync(); else { await worker!.CloseAsync(); script.AssertThreadAffinity(); }
            script.AssertComplete(); Assert.Equal(1,script.Closes);
        }
    }
    private static void EqualReply(CatalogReply expected, CatalogReply actual)
    {
        if (expected is CatalogReply.Trace e) { var a = Assert.IsType<CatalogReply.Trace>(actual); Assert.Equal(e.Value.Values,a.Value.Values); Assert.Equal(e.Value.Unit,a.Value.Unit); Assert.Equal(e.Value.SampleIntervalS,a.Value.SampleIntervalS); }
        else Assert.Equal(expected,actual);
    }
    [Fact]
    public void ManualProfilesRejectInvalidTemplatesInventoriesAndResponseRoles()
    {
        foreach(var command in new[]{":READ? {unknown}",":READ? {range:bad}",":READ? {range",":READ? }",":READ?\n*RST"})
            Assert.Throws<DriverException>(()=>new ScpiProfile("test.invalid",[1],[],[new(CatalogOperation.DmmMeasureVoltageDc,[new(command,ResponseRole.Value)])]));
        Assert.Throws<DriverException>(()=>new ScpiProfile("test.invalid",[1,1],[],[]));
        Assert.Throws<DriverException>(()=>new ScpiProfile("test.invalid",[1],[new(OutputKind.DcPowerSupply,2)],[]));
        Assert.Throws<DriverException>(()=>new ScpiProfile("test.invalid",[1],null,[new(CatalogOperation.Shutdown,[])]));
        Assert.Throws<DriverException>(()=>new ScpiProfile("test.invalid",[1],[new(OutputKind.DcPowerSupply,1)],[new(CatalogOperation.Shutdown,[])]));
        Assert.Throws<DriverException>(()=>new ScpiProfile("test.invalid",[1],[],[new(CatalogOperation.Reset,[new("*OPC?")])]));
        Assert.Throws<DriverException>(()=>new ScpiProfile("test.invalid",[1],[],[new(CatalogOperation.OscilloscopeCaptureVoltageTrace,[new(":DATA? {channel}",ResponseRole.Value),new(":PRE?",ResponseRole.Preamble)])]));
    }
    private static DeviceHandle Open(ScpiProfile profile,Script script,ConnectionOwnership ownership=ConnectionOwnership.Owned)=>new DriverRegistry([new ScpiFactory(profile,"scpi",script)]).Open(Candidate,new(AllowStandardScpi:true),ownership:ownership);
    [Fact]
    public void SharedSemanticTokenDomainsAndShutdownProfilesValidateBeforeOpen()
    {
        using var data=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"driver-profile-cases.json")));
        foreach(var row in data.RootElement.GetProperty("cases").EnumerateArray()) {
            var operation=Enum.Parse<CatalogOperation>(row.GetProperty("operation").GetString()!);var parameters=operation.Describe().Parameters;
            var tokens=parameters.Where(p=>p.Kind.SemanticValues() is not null).ToDictionary(p=>p.Name,p=>(IReadOnlyDictionary<string,string>)p.Kind.SemanticValues()!.ToDictionary(v=>v,v=>v));
            if(row.TryGetProperty("parameter",out var parameter))tokens[parameter.GetString()!]=row.GetProperty("tokens").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!);
            var steps=row.TryGetProperty("steps",out var supplied)?supplied.EnumerateArray().Select(s=>new Step(s.GetProperty("command").GetString()!,Enum.Parse<ResponseRole>(s.GetProperty("response").GetString()!))).ToArray():[new Step(":TEST "+string.Join(",",parameters.Select(p=>"{"+p.Name+"}")))];
            var plans=new List<Plan>{new(operation,steps,tokens)};
            var shutdown=row.GetProperty("shutdown").GetBoolean();if(shutdown)plans.Add(new(CatalogOperation.Shutdown,[]));
            var failure=Record.Exception(()=>new ScpiProfile("synthetic.tokens",[1],shutdown?[new(OutputKind.FunctionGenerator)]:[],plans));
            if(row.GetProperty("accept").GetBoolean())Assert.Null(failure);else Assert.IsType<DriverException>(failure);
        }
    }
    [Fact]
    public async Task BurstCountDoesNotRequireOrUsePhysicalChannelInventory()
    {
        var profile=new ScpiProfile("synthetic.counts",null,[],[new(CatalogOperation.FunctionGeneratorSetBurstCount,[new(":BURS {count}")])]);
        for(var mode=0;mode<4;mode++) {
            var script=new Script([ScriptStep.Write(":BURS 100"),ScriptStep.Write(":BURS 4294967295"),ScriptStep.Close()]);
            var registry=new DriverRegistry([new ScpiFactory(profile,"scpi",script,nativeConnector:script)]);var selection=new DriverSelection(AllowStandardScpi:true);
            var sync=mode==0?registry.Open(Candidate,selection):null;var native=mode==1?await registry.OpenNativeAsync(Candidate,selection):null;var worker=mode>=2?await registry.OpenWorkerAsync(Candidate,selection):null;
            async Task Call(uint count) {
                if(sync is not null)new InstrumentComponents.Drivers.Catalog.FunctionGenerator(sync).SetBurstCount(count);
                else if(native is not null)await new AsyncFunctionGenerator(native).SetBurstCountAsync(count);
                else if(mode==2)await new AsyncFunctionGenerator(worker!).SetBurstCountAsync(count);
                else new InstrumentComponents.Drivers.Catalog.FunctionGenerator(worker!).SetBurstCount(count);
            }
            await Call(100);await Call(uint.MaxValue);Assert.Equal(DriverErrorKind.InvalidArgument,(await Assert.ThrowsAsync<DriverException>(()=>Call(0))).Kind);
            if(sync is not null)sync.Close();else if(native is not null)await native.CloseAsync();else await worker!.CloseAsync();script.AssertComplete();
        }
    }
    [Fact]
    public void RecoveryEstablishesBoundaryResetInvalidatesUnitsAndBorrowedIoSurvives()
    {
        var profile=GeneratedProfiles.Create().Single();var script=new Script([
            ScriptStep.Query(":TEST:DmmMeasureVoltageDc? 2.5","bad"),ScriptStep.Recover(),
            ScriptStep.Query(":TEST:DmmMeasureVoltageDc? 2.5","3.3"),ScriptStep.Query(":TEST:DmmFetch?","4.2"),ScriptStep.Write(":TEST:Reset"),ScriptStep.Close()]);
        using var handle=Open(profile,script);var dmm=new InstrumentComponents.Drivers.Catalog.Dmm(handle);
        Assert.Equal(DriverErrorKind.Parse,Assert.Throws<DriverException>(()=>dmm.MeasureVoltageDc(2.5)).Kind);
        Assert.Equal(DriverErrorKind.SessionNeedsRecovery,Assert.Throws<DriverException>(()=>dmm.MeasureVoltageDc(2.5)).Kind);
        handle.Recover();Assert.Equal(3.3,dmm.MeasureVoltageDc(2.5).Value);Assert.Equal(Unit.Volt,dmm.Fetch().Unit);dmm.Reset();
        Assert.Equal(DriverErrorKind.InvalidArgument,Assert.Throws<DriverException>(()=>dmm.Read()).Kind);handle.Close();script.AssertComplete();
        var constant=new ScpiProfile("synthetic.constant",[1],[],[new(CatalogOperation.DmmMeasureVoltageDc,[new(":READ?",ResponseRole.Value)])]);var borrowed=new Script([ScriptStep.Query(":READ?","3.3")]);
        using var h=Open(constant,borrowed,ConnectionOwnership.Borrowed);
        Assert.Equal(DriverErrorKind.UnsupportedOperation,Assert.Throws<DriverException>(()=>h.Invoke(new DmmMeasureVoltageDcRequest(2.5))).Kind);
        Assert.Equal(3.3,h.Invoke(new DmmMeasureVoltageDcRequest(null)).IntoQuantity().Value);h.Close();Assert.Equal(0,borrowed.Closes);borrowed.AssertComplete();
    }
    [Fact]
    public void CheckedShutdownAttemptsEveryKnownOutputAndStillCloses()
    {
        var script=new Script([new("write",":TEST:DcPowerSupplyOutputEnable 1,OFF",Failure:DriverErrorKind.Transport),ScriptStep.Write(":TEST:DcPowerSupplyOutputEnable 2,OFF"),ScriptStep.Write(":TEST:FunctionGeneratorOutputEnable OFF"),ScriptStep.Close()]);
        using var h=Open(GeneratedProfiles.Create().Single(),script);Assert.Equal(DriverErrorKind.Shutdown,Assert.Throws<DriverException>(()=>h.Close(true)).Kind);script.AssertComplete();Assert.Equal(1,script.Closes);
    }
    [Fact]
    public async Task NativeMultistepOperationsHoldOneLeaseAndReturnOwnedSamples()
    {
        var examples=GeneratedCases.Create();var config=examples.Single(c=>c.Id=="SCPI-PowerMeterConfigureMeasurement");var waveform=examples.Single(c=>c.Id=="SCPI-OscilloscopeCaptureVoltageTrace");
        var script=new Script(config.Script.Concat(waveform.Script).Where(s=>s.Kind!="close").Append(ScriptStep.Close()));
        var h=await new DriverRegistry([new AsyncScpiFactory(GeneratedProfiles.Create().Single(),"scpi",script)]).OpenNativeAsync(Candidate,new(AllowStandardScpi:true));
        var a=h.InvokeAsync(config.Calls[0]);var b=h.InvokeAsync(waveform.Calls[0]);await Task.WhenAll(a,b);await h.CloseAsync();script.AssertComplete();Assert.Equal(3,(await b).IntoTrace().Values.Count);
    }
    private sealed class PausingConnector(Script script,TaskCompletionSource started,TaskCompletionSource release):IAsyncScpiConnector
    {
        public async ValueTask<IAsyncMessageIo> OpenAsync(DriverOpenContext context,CancellationToken ct=default)=>new Pause(await script.OpenAsync(context,ct),started,release);
        private sealed class Pause(IAsyncMessageIo inner,TaskCompletionSource started,TaskCompletionSource release):IAsyncMessageIo
        {
            public ValueTask WriteAsync(string message,CancellationToken ct=default)=>inner.WriteAsync(message,ct);
            public async ValueTask<string> QueryAsync(string message,CancellationToken ct=default){var response=await inner.QueryAsync(message,ct);started.TrySetResult();await release.Task.WaitAsync(ct);return response;}
            public ValueTask RecoverAsync(CancellationToken ct=default)=>inner.RecoverAsync(ct);
            public void Dispose()=>inner.Dispose();
        }
    }
    [Fact]
    public async Task NativeCatalogCancellationRequiresExplicitRecoveryBeforeMoreIo()
    {
        var script=new Script([ScriptStep.Query(":TEST:DmmMeasureVoltageDc? 2.5","3.3"),ScriptStep.Recover(),ScriptStep.Query(":TEST:DmmMeasureVoltageDc? 2.5","4.2"),ScriptStep.Close()]);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h=await new DriverRegistry([new AsyncScpiFactory(GeneratedProfiles.Create().Single(),"scpi",new PausingConnector(script,started,release))]).OpenNativeAsync(Candidate,new(AllowStandardScpi:true));
        using var ct=new CancellationTokenSource();var active=h.InvokeAsync(new DmmMeasureVoltageDcRequest(2.5),ct.Token);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>active);Assert.True(h.RecoveryRequired);
        Assert.Equal(DriverErrorKind.SessionNeedsRecovery,(await Assert.ThrowsAsync<DriverException>(()=>h.InvokeAsync(new DmmMeasureVoltageDcRequest(2.5)))).Kind);
        await h.RecoverAsync();release.TrySetResult();Assert.Equal(4.2,(await h.InvokeAsync(new DmmMeasureVoltageDcRequest(2.5))).IntoQuantity().Value);await h.CloseAsync();script.AssertComplete();
    }
    private sealed class CatalogOnlyFactory:IDriverFactory
    {
        public DriverDescriptor Descriptor=>new("opaque.catalog",DriverContract.Major,[new ExactIdentityRule("scpi","Opaque","SDK")],[Operation.CatalogDmmMeasureVoltageDc,Operation.CatalogShutdown]);
        public IReadOnlySet<ExecutionMode> ExecutionModes=>new HashSet<ExecutionMode>{ExecutionMode.Sync,ExecutionMode.NativeAsync,ExecutionMode.WorkerAsync};
        public int Shutdowns,Closes;
        public IDeviceBackend Open(DriverOpenContext context)=>new Backend(this);
        public ValueTask<IAsyncDeviceBackend> OpenNativeAsync(DriverOpenContext context,CancellationToken ct=default)=>ValueTask.FromResult<IAsyncDeviceBackend>(new Backend(this));
        private sealed class Backend(CatalogOnlyFactory owner):IDeviceBackend,IAsyncDeviceBackend,ICatalogBackend,IAsyncCatalogBackend
        {
            public IReadOnlySet<Operation> Operations=>owner.Descriptor.Operations;
            ICatalogBackend IDeviceBackend.Catalog=>this;IAsyncCatalogBackend IAsyncDeviceBackend.Catalog=>this;
            public CatalogReply Invoke(CatalogRequest request){if(request.Operation==CatalogOperation.Shutdown){Interlocked.Increment(ref owner.Shutdowns);return new CatalogReply.Empty();}return new CatalogReply.Numeric(new(3.3,Unit.Volt));}
            public ValueTask<CatalogReply> InvokeAsync(CatalogRequest request,CancellationToken ct=default)=>ValueTask.FromResult(Invoke(request));
            public void Dispose()=>Interlocked.Increment(ref owner.Closes);
        }
    }
    [Fact]
    public async Task CatalogOnlyProvidersNeedNoScpiOrOldCapabilitiesForCheckedClose()
    {
        for(var mode=0;mode<4;mode++)
        {
            var factory=new CatalogOnlyFactory();var registry=new DriverRegistry([factory]);var candidate=new DeviceCandidate(new("scpi","opaque"),new("Opaque","SDK"));
            if(mode==0){using var h=registry.Open(candidate);Assert.Equal(3.3,h.Invoke(new DmmMeasureVoltageDcRequest(null)).IntoQuantity().Value);h.Close(true);}
            else if(mode==1){await using var h=await registry.OpenNativeAsync(candidate);Assert.Equal(3.3,(await h.InvokeAsync(new DmmMeasureVoltageDcRequest(null))).IntoQuantity().Value);await h.CloseAsync(true);}
            else{await using var h=await registry.OpenWorkerAsync(candidate);if(mode==2)Assert.Equal(3.3,(await h.InvokeAsync(new DmmMeasureVoltageDcRequest(null))).IntoQuantity().Value);else Assert.Equal(3.3,h.Invoke(new DmmMeasureVoltageDcRequest(null)).IntoQuantity().Value);await h.CloseAsync(true);}
            Assert.Equal(1,factory.Shutdowns);Assert.Equal(1,factory.Closes);
        }
    }

}

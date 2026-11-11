using GeneratedDrivers;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
using InstrumentComponents.Scpi;
using InstrumentComponents.TestKit;
using DeviceIdentity = InstrumentComponents.Drivers.DeviceIdentity;
internal static class ScpiProof
{
    internal static async Task Run()
    {
        GeneratedProfiles.Create(); var cases = GeneratedCases.Create();
        var groups=new (IReadOnlyList<CatalogCase> Cases,Func<Script,IDriverFactory> Factory)[] {
            (cases,s=>GeneratedProfiles.CombinedFactories(s,s).Single()),
            (GeneratedChoice.GeneratedCases.Create(),s=>GeneratedChoice.GeneratedProfiles.CombinedFactories(s,s).Single())
        };
        foreach(var group in groups)
        for (var mode=0;mode<4;mode++) foreach(var test in group.Cases)
        {
            if(test.Calls.Count==0)throw new InvalidOperationException("Scenario must establish an outcome");
            var script = new Script(test.Script); IDriverFactory factory = group.Factory(script);
            var registry = new DriverRegistry([factory]); var candidate = new DeviceCandidate(new("scpi","fixture"),new DeviceIdentity("Synthetic","All")); var selection=new DriverSelection(AllowStandardScpi:true);
            var sync = mode==0 ? registry.Open(candidate,selection) : null; var native = mode==1 ? await registry.OpenNativeAsync(candidate,selection) : null; var worker=mode>=2 ? await registry.OpenWorkerAsync(candidate,selection):null;
            for(var i=0;i<test.Calls.Count;i++)
            {
                CatalogReply? reply=null; DriverException? failure=null;
                try { reply=mode switch { 0=>sync!.Invoke(test.Calls[i]),1=>await native!.InvokeAsync(test.Calls[i]),2=>await worker!.InvokeAsync(test.Calls[i]),_=>worker!.Invoke(test.Calls[i]) }; } catch(DriverException error){failure=error;}
                var last=i==test.Calls.Count-1;
                if(last && test.ExpectedError is { } expected){if(failure?.Kind!=expected)throw new InvalidOperationException(test.Id+": incorrect error");}
                else {if(failure is not null)throw failure; if(last && !Equal(reply!,test.Expected!))throw new InvalidOperationException(test.Id+": incorrect value/unit/status");}
            }
            if(sync is not null)sync.Close();else if(native is not null)await native.CloseAsync();else{await worker!.CloseAsync();script.AssertThreadAffinity();}
            script.AssertComplete();if(script.Closes!=1)throw new InvalidOperationException("Resource lifetime mismatch");
        }
        var selected=new[]{"SCPI-DmmMeasureVoltageDc","SCPI-DcPowerSupplyReadVoltage","SCPI-FunctionGeneratorReadFrequency","SCPI-OscilloscopeCaptureVoltageTrace","SCPI-SwitchIsClosed","SCPI-CounterMeasureFrequency","SCPI-PowerMeterRead","SCPI-SpectrumAnalyzerMarkerY","SCPI-QueryIdentity"};
        for(var mode=0;mode<4;mode++) {
            var script=new Script(selected.SelectMany(id=>cases.Single(c=>c.Id==id).Script).Where(s=>s.Kind!="close").Append(ScriptStep.Close()));
            var registry=new DriverRegistry(GeneratedProfiles.CombinedFactories(script,script));
            var candidate=new DeviceCandidate(new("scpi","fixture"),new DeviceIdentity("Synthetic","All"));
            if(mode==0){using var h=registry.Open(candidate);Typed(h);h.Close();}
            else if(mode==1){await using var h=await registry.OpenNativeAsync(candidate);await TypedAsync(h);await h.CloseAsync();}
            else{await using var h=await registry.OpenWorkerAsync(candidate);if(mode==2)await TypedAsync(h);else Typed(h);await h.CloseAsync();script.AssertThreadAffinity();}
            script.AssertComplete();
        }
        Console.WriteLine($"PASS SCPI packages: {groups.Sum(g=>g.Cases.Count)} shared cases x 4 frontends, all eight typed classes, exact traffic, units, errors, cleanup");
    }
    private static void Require(bool condition){if(!condition)throw new InvalidOperationException("Typed view value/unit/shape mismatch");}
    private static void Typed(ICatalogExecutor e) {
        Require(new InstrumentComponents.Drivers.Catalog.Dmm(e).MeasureVoltageDc(2.5).Unit==Unit.Volt);
        Require(new DcPowerSupply(e).ReadVoltage(1).Value==3.3);
        Require(new FunctionGenerator(e).ReadFrequency().Unit==Unit.Hertz);
        var waveform=new Oscilloscope(e).CaptureVoltageTrace(1);Require(waveform.Values.Count==3 && waveform.SampleIntervalS==0.5 && waveform.Unit==Unit.Volt);
        Require(new InstrumentComponents.Drivers.Catalog.Switch(e).IsClosed(1,1));Require(new Counter(e).MeasureFrequency().Unit==Unit.Hertz);
        var power=new PowerMeter(e);power.ConfigureMeasurement(PowerUnit.Watt,true,true,2.5,2.5);Require(power.Read().Unit==Unit.Watt);
        Require(new SpectrumAnalyzer(e).MarkerY().Unit==Unit.Dbm);Require(new InstrumentComponents.Drivers.Catalog.Dmm(e).QueryIdentity().Model=="All");
    }
    private static async Task TypedAsync(IAsyncCatalogExecutor e) {
        Require((await new AsyncDmm(e).MeasureVoltageDcAsync(2.5)).Unit==Unit.Volt);
        Require((await new AsyncDcPowerSupply(e).ReadVoltageAsync(1)).Value==3.3);
        Require((await new AsyncFunctionGenerator(e).ReadFrequencyAsync()).Unit==Unit.Hertz);
        var waveform=await new AsyncOscilloscope(e).CaptureVoltageTraceAsync(1);Require(waveform.Values.Count==3 && waveform.SampleIntervalS==0.5 && waveform.Unit==Unit.Volt);
        Require(await new AsyncSwitch(e).IsClosedAsync(1,1));Require((await new AsyncCounter(e).MeasureFrequencyAsync()).Unit==Unit.Hertz);
        var power=new AsyncPowerMeter(e);await power.ConfigureMeasurementAsync(PowerUnit.Watt,true,true,2.5,2.5);Require((await power.ReadAsync()).Unit==Unit.Watt);
        Require((await new AsyncSpectrumAnalyzer(e).MarkerYAsync()).Unit==Unit.Dbm);Require((await new AsyncDmm(e).QueryIdentityAsync()).Model=="All");
    }
    private static bool Equal(CatalogReply actual,CatalogReply expected) => (actual,expected) switch {
        (CatalogReply.Trace a,CatalogReply.Trace b)=>a.Value.Unit==b.Value.Unit && a.Value.SampleIntervalS==b.Value.SampleIntervalS && a.Value.Values.SequenceEqual(b.Value.Values),_=>actual==expected};
}

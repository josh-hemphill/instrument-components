using System.Text.Json;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using OpenTap;
namespace InstrumentComponents.OpenTap.Tests;
public class OperationExecutionTests
{
    [Fact]public void EveryShippedStepExecutesThroughOpaqueCapabilitiesAndPublishesItsShape()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root(),"spec","opentap-operations.json")));
        var rows=doc.RootElement.GetProperty("operations").EnumerateArray().ToArray();Assert.Equal(74,rows.Length);
        foreach(var row in rows){
            var name=row.GetProperty("stepTypeName").GetString()!;var type=typeof(DeviceInstrument).Assembly.GetType("InstrumentComponents.OpenTap."+name)!;
            var step=(TestStep)Activator.CreateInstance(type)!;var property=type.GetProperty("Instrument")!;
            var resource=(DeviceInstrument)Activator.CreateInstance(property.PropertyType==typeof(DeviceInstrument)?typeof(DmmInstrument):property.PropertyType)!;
            var backend=new OpaqueDevice();resource.AttachDevice(backend.OpenHandle(),true);property.SetValue(step,resource);
            // Defaults in every declared step must form a valid executable plan.
            var expected=row.GetProperty("kind").GetString()=="utility" ? (name=="IdentityQueryStep"?new[]{CatalogOperation.QueryIdentity}:new[]{CatalogOperation.Shutdown,CatalogOperation.Reset}) :
                (row.TryGetProperty("invoke",out var invoke)?new[]{invoke}:row.GetProperty("composite").EnumerateArray().ToArray()).Select(i=>Enum.Parse<CatalogOperation>(i.GetProperty("operationId").GetString()!)).ToArray();
            var listener=new Values();var plan=new TestPlan();plan.ChildTestSteps.Add(step);
            var run=plan.Execute([listener]);Assert.True(run.Verdict==Verdict.Pass,name+": "+run.Verdict+" "+step.Error);
            Assert.Equal(expected,backend.Calls.Select(c=>c.Operation));Assert.Equal(1,backend.Opens);Assert.Equal(1,backend.Closes);
            var mode=row.GetProperty("publish").GetProperty("mode").GetString();
            if(mode is "scalar" or "multiScalar" or "sampleAndScalar")Assert.NotEmpty(listener.Scalars);
            if(mode is "sample" or "sampleAndScalar")Assert.NotEmpty(listener.Samples);
            if(mode=="identity")Assert.Equal(new[]{"Example,Device,SN,1"},listener.Identities);
            Assert.All(listener.Scalars,value=>Assert.True(value.Value is 1.25 or 1.0 || value.Value>=0));
            Assert.All(listener.Samples,value=>Assert.True(double.IsFinite(value)));
        }
    }
    [Theory][InlineData(1.25,1.25,Verdict.Pass)][InlineData(2.0,3.0,Verdict.Fail)]
    public void ScalarLimitsUseProviderValueAndInclusiveBounds(double low,double high,Verdict expected)
    {
        var device=new OpaqueDevice();var resource=new DmmInstrument();resource.AttachDevice(device.OpenHandle(),true);
        var step=new DmmMeasureScalarStep{Instrument=resource,LimitLow=low,LimitHigh=high};var listener=new Values();var plan=new TestPlan();plan.ChildTestSteps.Add(step);
        Assert.Equal(expected,plan.Execute([listener]).Verdict);Assert.Equal((1.25,"V"),Assert.Single(listener.Scalars));
    }
    [Fact]public void TemperatureAndConfiguredReadsKeepUnknownPhysicalUnits()
    {
        var device=new OpaqueDevice();var resource=new DmmInstrument();resource.AttachDevice(device.OpenHandle(),true);
        var listener=new Values();var plan=new TestPlan();plan.ChildTestSteps.Add(new DmmMeasureTemperatureStep{Instrument=resource});plan.ChildTestSteps.Add(new DmmReadStep{Instrument=resource});plan.ChildTestSteps.Add(new DmmFetchStep{Instrument=resource});
        Assert.Equal(Verdict.Pass,plan.Execute([listener]).Verdict);Assert.Equal(new[]{"Unknown","Unknown","Unknown"},listener.Scalars.Select(s=>s.Unit));
    }
    [Theory][InlineData(false,"A")][InlineData(true,"Ohm")]
    public void ConfiguredFunctionUnitIsTakenFromRepliesAcrossViews(bool resistance,string expectedUnit)
    {
        var device=new OpaqueDevice();var resource=new DmmInstrument();resource.AttachDevice(device.OpenHandle(),true);var listener=new Values();var plan=new TestPlan();
        plan.ChildTestSteps.Add(resistance?new DmmConfigureResistanceStep{Instrument=resource}:new DmmConfigureCurrentDcStep{Instrument=resource});plan.ChildTestSteps.Add(new DmmFetchStep{Instrument=resource});plan.ChildTestSteps.Add(new DmmReadStep{Instrument=resource});
        Assert.Equal(Verdict.Pass,plan.Execute([listener]).Verdict);Assert.Equal(new[]{expectedUnit,expectedUnit},listener.Scalars.Select(s=>s.Unit));
    }
    [Fact]public void ScopeTraceAndSampleLoopPublishActualOwnedValues()
    {
        var device=new OpaqueDevice();var resource=new OscilloscopeInstrument();resource.AttachDevice(device.OpenHandle(),true);var listener=new Values();var plan=new TestPlan();plan.ChildTestSteps.Add(new ScopeCaptureTraceStep{Instrument=resource});
        Assert.Equal(Verdict.Pass,plan.Execute([listener]).Verdict);Assert.Equal(new[]{1.0,2.0,3.0},listener.Samples);
        var dmm=new DmmInstrument();dmm.AttachDevice(new OpaqueDevice().OpenHandle(),true);var samples=new TestPlan();samples.ChildTestSteps.Add(new DmmMeasureVoltageDcStep{Instrument=dmm,SampleCount=3});var sampleListener=new Values();
        Assert.Equal(Verdict.Pass,samples.Execute([sampleListener]).Verdict);Assert.Equal(new[]{1.25,1.25,1.25},sampleListener.Samples);
    }
    [Fact]public void DriverReportedOverloadFailsScalarAndSampleVerdictsWithoutMagnitudeHeuristics()
    {
        foreach(var generated in new[]{false,true}){
            var device=new OpaqueDevice{Status=MeasurementStatus.Overload};var resource=new DmmInstrument();resource.AttachDevice(device.OpenHandle(),true);var plan=new TestPlan();
            plan.ChildTestSteps.Add(generated?new DmmMeasureVoltageAcStep{Instrument=resource}:new DmmMeasureVoltageDcStep{Instrument=resource});
            Assert.Equal(Verdict.Fail,plan.Execute().Verdict);
        }
    }
    private sealed class Values:ResultListener{
        public List<(double Value,string Unit)> Scalars=[];public List<double> Samples=[];public List<string> Identities=[];
        public override void OnResultPublished(Guid stepRunId,ResultTable result){var columns=result.Columns.ToDictionary(c=>c.Name);
            if(result.Name==PhaseIResults.ScalarTable)Scalars.Add((Convert.ToDouble(columns["Value"].Data.GetValue(0)),(string)columns["Unit"].Data.GetValue(0)!));
            if(result.Name==PhaseIResults.SampleTable)Samples.Add(Convert.ToDouble(columns["Value"].Data.GetValue(0)));
            if(result.Name==PhaseIResults.IdentityTable)Identities.Add((string)columns["Idn"].Data.GetValue(0)!);
        }
    }
}

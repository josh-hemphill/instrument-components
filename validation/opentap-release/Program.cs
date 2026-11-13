using InstrumentComponents.OpenTap;
using InstrumentComponents.Validation;
using OpenTap;
// This managed fixture references DLLs installed from the TapPackage, never projects.
var resource=new DmmInstrument{Name="release-mock",VisaAddress="opaque://fixture"};
InstrumentSettings.Current.Add(resource);
try
{
    var plan=new TestPlan();plan.ChildTestSteps.Add(new DmmMeasureScalarStep{Instrument=resource,LimitLow=1.25,LimitHigh=1.25});
    plan.Save("Release.TapPlan");
    var settings=new TapSerializer().SerializeToString(InstrumentSettings.Current);
    InstrumentSettings.Current.Clear();
    using(var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(settings)))ComponentSettings.SetCurrent(stream);
    var loaded=TestPlan.Load("Release.TapPlan");
    var step=loaded.ChildTestSteps.Single() as DmmMeasureScalarStep ?? throw new InvalidOperationException("Installed plugin discovery failed");
    var backend=new OpaqueDevice();step.Instrument.AttachDevice(backend.OpenHandle(),true);
    var values=new Values();var run=loaded.Execute([values]);
    Console.WriteLine($"Managed mock: verdict={run.Verdict}, opens={backend.Opens}, closes={backend.Closes}, connected={step.Instrument.IsConnected}, scalar={values.Scalar}, error={step.Error}");
    if(run.Verdict!=Verdict.Pass||backend.Opens!=1||backend.Closes!=1||step.Instrument.IsConnected||values.Scalar!=(1.25,"V"))
        throw new InvalidOperationException("Installed managed mock plan/lifecycle failed");
    Console.WriteLine("PASS installed managed OpenTAP plugin discovery, saved/loaded mock plan, 1.25 V and one owned open/close; no hardware or AOT claim");
}
finally{InstrumentSettings.Current.Clear();}
internal sealed class Values:ResultListener
{
    internal (double,string)? Scalar;
    public override void OnResultPublished(Guid id,ResultTable table)
    {
        if(table.Name!=PhaseIResults.ScalarTable)return;
        var columns=table.Columns.ToDictionary(c=>c.Name);
        Scalar=(Convert.ToDouble(columns["Value"].Data.GetValue(0)),(string)columns["Unit"].Data.GetValue(0)!);
    }
}

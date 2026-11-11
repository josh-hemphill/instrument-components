using InstrumentComponents.OpenTap;
using InstrumentComponents.Validation;
using OpenTap;
var backend=new OpaqueDevice();var instrument=new DmmInstrument{Name="Opaque fixture"};instrument.AttachDevice(backend.OpenHandle(),true);
var plan=new TestPlan();plan.ChildTestSteps.Add(new IdentityQueryStep{Instrument=instrument});plan.ChildTestSteps.Add(new DmmMeasureScalarStep{Instrument=instrument,LimitLow=1.0,LimitHigh=2.0});plan.ChildTestSteps.Add(new SafeShutdownStep{Instrument=instrument});
var run=plan.Execute();if(run.Verdict!=Verdict.Pass)throw new InvalidOperationException("Fixture plan failed: "+run.Verdict);
Console.WriteLine("PASS OpenTAP neutral fixture plan; no hardware accessed");

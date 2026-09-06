```csharp
using InstrumentComponents.OpenTap;
using InstrumentComponents.Scpi;
using OpenTap;

var io = new DelegateScpiIo(
    write: _ => { },
    query: command => command.Trim().Equals("*IDN?", StringComparison.OrdinalIgnoreCase)
        ? "Acme,DMM1,SN,1.0"
        : "1.25");

var dmm = new DmmInstrument(io) { Name = "Bench DMM", VisaAddress = "mock://dmm" };
var plan = new TestPlan();
plan.ChildTestSteps.Add(new IdentityQueryStep { Instrument = dmm });
plan.ChildTestSteps.Add(new DmmMeasureVoltageAcStep { Instrument = dmm, LimitLow = 1.0, LimitHigh = 2.0 });
plan.Execute();
```

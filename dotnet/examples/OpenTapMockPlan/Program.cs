using InstrumentComponents.OpenTap;
using InstrumentComponents.Scpi;
using OpenTap;

// Plan-author CI pattern: inject message-level SCPI and execute typed steps without VISA.
var io = new DelegateScpiIo(
    write: _ => { },
    query: command =>
    {
        var trimmed = command.Trim();
        if (trimmed.Equals("*IDN?", StringComparison.OrdinalIgnoreCase))
            return "Acme,DMM1,SN,1.0";
        if (trimmed.StartsWith(":MEAS:VOLT:DC", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith(":MEAS:VOLT:AC", StringComparison.OrdinalIgnoreCase))
            return "1.25";
        return "0";
    });

var dmm = new DmmInstrument(io)
{
    Name = "Bench DMM",
    VisaAddress = "mock://dmm",
};

var listener = new PrintingListener();
var plan = new TestPlan();
plan.ChildTestSteps.Add(new IdentityQueryStep { Instrument = dmm });
plan.ChildTestSteps.Add(new DmmMeasureVoltageAcStep { Instrument = dmm, LimitLow = 1.0, LimitHigh = 2.0 });
plan.ChildTestSteps.Add(new SafeShutdownStep { Instrument = dmm });

var run = plan.Execute([listener]);
Console.WriteLine($"Verdict={run.Verdict}");
foreach (var line in listener.Lines)
    Console.WriteLine(line);

if (run.Verdict != Verdict.Pass)
    Environment.Exit(1);

internal sealed class PrintingListener : ResultListener
{
    public List<string> Lines { get; } = [];

    public override void OnResultPublished(Guid stepRunId, ResultTable result)
    {
        var columns = string.Join(",", result.Columns.Select(column => column.Name));
        Lines.Add($"{result.Name}[{columns}]");
        base.OnResultPublished(stepRunId, result);
    }
}

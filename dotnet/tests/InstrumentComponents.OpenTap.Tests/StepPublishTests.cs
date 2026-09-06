using InstrumentComponents.Scpi;
using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class StepPublishTests
{
    [Fact]
    public void IdentityAndDmmStepsPublishPhaseITables()
    {
        var io = new ScriptedIo(
            ("*IDN?", "Acme,DMM1,SN,1.0"),
            (":MEAS:VOLT:DC?", "1.25"));
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        var identity = new IdentityQueryStep { Instrument = dmm };
        var measure = new DmmMeasureVoltageDcStep { Instrument = dmm, SampleCount = 2, Channel = "VDC" };
        var scalar = new DmmMeasureScalarStep
        {
            Instrument = dmm,
            MetricName = "VDC",
            Unit = "V",
            LimitLow = 1.0,
            LimitHigh = 2.0,
        };
        var shutdown = new SafeShutdownStep { Instrument = dmm };

        var listener = new CollectingListener();
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(identity);
        plan.ChildTestSteps.Add(measure);
        plan.ChildTestSteps.Add(scalar);
        plan.ChildTestSteps.Add(shutdown);
        var run = plan.Execute([listener]);

        Assert.Equal(Verdict.Pass, run.Verdict);
        Assert.Contains(listener.Tables, t => t.Name == PhaseIResults.IdentityTable &&
            t.ColumnNames.SequenceEqual(PhaseIResults.IdentityColumns));
        Assert.Contains(listener.Tables, t => t.Name == PhaseIResults.SampleTable &&
            t.ColumnNames.SequenceEqual(PhaseIResults.SampleColumns));
        Assert.Contains(listener.Tables, t => t.Name == PhaseIResults.ScalarTable &&
            t.ColumnNames.SequenceEqual(PhaseIResults.ScalarColumns));
    }

    [Fact]
    public void CounterScalarFailsWhenOutOfBand()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,CNT1,SN,1.0"), (":MEASure:FREQuency?", "10"));
        var counter = new CounterInstrument(io) { VisaAddress = "mock://counter" };
        var step = new CounterMeasureFrequencyStep { Instrument = counter, LimitLow = 100 };
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(step);
        var run = plan.Execute();
        Assert.Equal(Verdict.Fail, run.Verdict);
    }

    [Fact]
    public void ScalarPassesWhenEqualToInclusiveLimits()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,SN,1.0"), (":MEAS:VOLT:DC?", "1.0"));
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        var step = new DmmMeasureScalarStep { Instrument = dmm, LimitLow = 1.0, LimitHigh = 1.0 };
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(step);
        Assert.Equal(Verdict.Pass, plan.Execute().Verdict);
    }

    [Fact]
    public void DmmScalarFailsWhenOutOfBand()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,SN,1.0"), (":MEAS:VOLT:DC?", "0.1"));
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        var step = new DmmMeasureScalarStep { Instrument = dmm, LimitLow = 1.0 };
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(step);
        var run = plan.Execute();
        Assert.Equal(Verdict.Fail, run.Verdict);
    }

    [Fact]
    public void SafeShutdownWithoutInstrumentIsError()
    {
        var step = new SafeShutdownStep { Instrument = null! };
        step.Run();
        Assert.Equal(Verdict.Error, step.Verdict);
    }

    private sealed class CollectingListener : ResultListener
    {
        public List<(string Name, IReadOnlyList<string> ColumnNames)> Tables { get; } = [];

        public override void OnResultPublished(Guid stepRunId, ResultTable result)
        {
            var names = result.Columns.Select(c => c.Name).ToList();
            Tables.Add((result.Name, names));
            base.OnResultPublished(stepRunId, result);
        }
    }

    private sealed class ScriptedIo : IScpiIo
    {
        private readonly Dictionary<string, string> _queries;

        public ScriptedIo(params (string Command, string Response)[] queries)
        {
            _queries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (command, response) in queries)
                _queries[command] = response;
        }

        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command)
        {
        }

        public string Query(string command) =>
            _queries.TryGetValue(command.Trim(), out var response) ? response : "";

        public void Dispose()
        {
        }
    }
}

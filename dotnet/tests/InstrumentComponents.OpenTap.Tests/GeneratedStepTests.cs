using InstrumentComponents.Scpi;
using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class GeneratedStepTests
{
    [Fact]
    public void GeneratedDmmStepsArePublicAndBound()
    {
        Assert.Equal(9, GeneratedOperationCatalog.StepTypes.Length);
        foreach (var type in GeneratedOperationCatalog.StepTypes)
        {
            Assert.Equal("InstrumentComponents.OpenTap", type.Namespace);
            Assert.EndsWith("Step", type.Name);
            Assert.Contains(type, OpenTapCatalog.StepTypes());
        }
    }

    [Fact]
    public void DmmMeasureVoltageAcPublishesScalar()
    {
        var io = new ScriptedAcIo();
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        var step = new DmmMeasureVoltageAcStep { Instrument = dmm, LimitLow = 1.0, LimitHigh = 2.0 };
        var listener = new CollectingListener();
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(step);
        var run = plan.Execute([listener]);
        Assert.Equal(Verdict.Pass, run.Verdict);
        Assert.Contains(listener.Tables, table => table.Name == PhaseIResults.ScalarTable);
    }

    [Fact]
    public void DmmConfigureVoltageDcWritesWithoutLimits()
    {
        var io = new ScriptedAcIo();
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        var step = new DmmConfigureVoltageDcStep { Instrument = dmm, Range = 10 };
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(step);
        Assert.Equal(Verdict.Pass, plan.Execute().Verdict);
        Assert.Contains(io.Writes, command => command.Contains("CONF:VOLT:DC", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CollectingListener : ResultListener
    {
        public List<(string Name, IReadOnlyList<string> ColumnNames)> Tables { get; } = [];

        public override void OnResultPublished(Guid stepRunId, ResultTable result)
        {
            Tables.Add((result.Name, result.Columns.Select(column => column.Name).ToList()));
            base.OnResultPublished(stepRunId, result);
        }
    }

    private sealed class ScriptedAcIo : IScpiIo
    {
        public List<string> Writes { get; } = [];
        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command) => Writes.Add(command);

        public string Query(string command)
        {
            var trimmed = command.Trim();
            if (trimmed.Equals("*IDN?", StringComparison.OrdinalIgnoreCase))
                return "Acme,DMM1,SN,1.0";
            if (trimmed.StartsWith(":MEAS:VOLT:AC", StringComparison.OrdinalIgnoreCase))
                return "1.25";
            if (trimmed.Equals("FETC?", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("READ?", StringComparison.OrdinalIgnoreCase))
                return "1.25";
            return "";
        }

        public void Dispose()
        {
        }
    }
}

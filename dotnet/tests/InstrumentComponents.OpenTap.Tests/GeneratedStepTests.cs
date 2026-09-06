using InstrumentComponents.Classes;
using InstrumentComponents.Scpi;
using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class GeneratedStepTests
{
    [Fact]
    public void GeneratedStepsArePublicAndBound()
    {
        Assert.NotEmpty(GeneratedOperationCatalog.StepTypes);
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
        var io = new ScriptedIo();
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
        var io = new ScriptedIo();
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        var step = new DmmConfigureVoltageDcStep { Instrument = dmm, Range = 10 };
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(step);
        Assert.Equal(Verdict.Pass, plan.Execute().Verdict);
        Assert.Contains(io.Writes, command => command.Contains("CONF:VOLT:DC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PsuReadVoltageAndSwitchIsClosedPublishScalars()
    {
        var psuIo = new ScriptedIo();
        var switchIo = new ScriptedIo();
        var psu = new DcPowerSupplyInstrument(psuIo) { VisaAddress = "mock://psu" };
        var matrix = new SwitchInstrument(switchIo) { VisaAddress = "mock://switch" };
        var listener = new CollectingListener();
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(new PsuReadVoltageStep { Instrument = psu, Channel = 1, LimitLow = 4.9, LimitHigh = 5.1 });
        plan.ChildTestSteps.Add(new SwitchIsClosedStep { Instrument = matrix, Channel1 = 1, Channel2 = 2 });
        Assert.Equal(Verdict.Pass, plan.Execute([listener]).Verdict);
        Assert.Equal(2, listener.Tables.Count(table => table.Name == PhaseIResults.ScalarTable));
    }

    [Fact]
    public void ScopeRunAndFgenDutyCycleWriteCommands()
    {
        var scopeIo = new ScriptedIo();
        var fgenIo = new ScriptedIo();
        var scope = new OscilloscopeInstrument(scopeIo) { VisaAddress = "mock://scope" };
        var fgen = new FunctionGeneratorInstrument(fgenIo) { VisaAddress = "mock://fgen" };
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(new ScopeRunStep { Instrument = scope });
        plan.ChildTestSteps.Add(new FgenSetDutyCycleStep { Instrument = fgen, Percent = 25 });
        Assert.Equal(Verdict.Pass, plan.Execute().Verdict);
        Assert.Contains(scopeIo.Writes, command => command.Contains("RUN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fgenIo.Writes, command => command.Contains("DCYC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PowerMeterConfigureAndSpecAnTraceCoverNewShapes()
    {
        var meterIo = new ScriptedIo();
        var analyzerIo = new ScriptedIo();
        var meter = new PowerMeterInstrument(meterIo) { VisaAddress = "mock://pm" };
        var analyzer = new SpectrumAnalyzerInstrument(analyzerIo) { VisaAddress = "mock://sa" };
        var listener = new CollectingListener();
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(new PowerMeterConfigureMeasurementStep
        {
            Instrument = meter,
            Unit = PowerUnit.Dbm,
            AutoRange = true,
            AutoAverage = true,
            CorrectionFreqHz = 1e9,
        });
        plan.ChildTestSteps.Add(new SpectrumAnalyzerFetchTraceStep { Instrument = analyzer });
        Assert.Equal(Verdict.Pass, plan.Execute([listener]).Verdict);
        Assert.Contains(meterIo.Writes, command => command.Contains("UNIT:POW", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(listener.Tables, table => table.Name == PhaseIResults.SampleTable);
    }

    [Fact]
    public void GeneratedChannelRulesMatchHandwrittenRoutes()
    {
        var voltage = new PsuSetVoltageStep { Channel = 0 };
        Assert.Contains("Channel must be at least 1.", voltage.Error, StringComparison.Ordinal);

        var route = new SwitchIsClosedStep { Channel1 = 3, Channel2 = 3 };
        Assert.Contains("Route endpoints must differ.", route.Error, StringComparison.Ordinal);
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

    private sealed class ScriptedIo : IScpiIo
    {
        public List<string> Writes { get; } = [];
        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command) => Writes.Add(command);

        public string Query(string command)
        {
            var trimmed = command.Trim();
            if (trimmed.Equals("*IDN?", StringComparison.OrdinalIgnoreCase))
                return "Acme,BOX1,SN,1.0";
            if (trimmed.Equals("*OPC?", StringComparison.OrdinalIgnoreCase))
                return "1";
            if (trimmed.StartsWith(":MEAS:VOLT:AC", StringComparison.OrdinalIgnoreCase))
                return "1.25";
            if (trimmed.StartsWith(":MEAS:VOLT?", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(":MEAS:CURR?", StringComparison.OrdinalIgnoreCase))
                return "5.0";
            if (trimmed.StartsWith(":OUTP", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('?'))
                return "1";
            if (trimmed.StartsWith(":ROUTe:CLOS?", StringComparison.OrdinalIgnoreCase))
                return "1";
            if (trimmed.Equals("FETC?", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("READ?", StringComparison.OrdinalIgnoreCase))
                return "1.25";
            if (trimmed.StartsWith(":TRAC:DATA?", StringComparison.OrdinalIgnoreCase))
                return "-10.0,-12.0,-11.0";
            if (trimmed.EndsWith('?'))
                return "0";
            return "";
        }

        public void Dispose()
        {
        }
    }
}

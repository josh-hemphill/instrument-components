using InstrumentComponents.Classes;
using InstrumentComponents.Scpi;
using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class PlanCoverageTests
{
    [Fact]
    public void GeneratedSettingsRoundTripThroughTapPlan()
    {
        InstrumentSettingsScope.Run(() =>
        {
            var dmm = new DmmInstrument { Name = "Dmm", VisaAddress = "TCPIP0::dmm::INSTR" };
            var psu = new DcPowerSupplyInstrument { Name = "Psu", VisaAddress = "TCPIP0::psu::INSTR" };
            var meter = new PowerMeterInstrument { Name = "Meter", VisaAddress = "TCPIP0::pm::INSTR" };
            InstrumentSettings.Current.Add(dmm);
            InstrumentSettings.Current.Add(psu);
            InstrumentSettings.Current.Add(meter);

            var plan = new TestPlan();
            plan.ChildTestSteps.Add(new DmmConfigureVoltageAcStep { Instrument = dmm, Range = 10, Resolution = 0.001 });
            plan.ChildTestSteps.Add(new PsuSetVoltageStep { Instrument = psu, Channel = 2, Voltage = 5.5 });
            plan.ChildTestSteps.Add(new PowerMeterConfigureMeasurementStep
            {
                Instrument = meter,
                Unit = PowerUnit.Watt,
                AutoRange = false,
                CorrectionFreqHz = 1e9,
            });

            var directory = Path.Combine(Path.GetTempPath(), $"ic-coverage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var planPath = Path.Combine(directory, "GeneratedSettings.TapPlan");
                var settingsPath = Path.Combine(directory, "Instruments.xml");
                plan.Save(planPath);
                File.WriteAllText(settingsPath, new TapSerializer().SerializeToString(InstrumentSettings.Current));

                var xml = File.ReadAllText(planPath);
                Assert.Contains("DmmConfigureVoltageAcStep", xml, StringComparison.Ordinal);
                Assert.Contains("PsuSetVoltageStep", xml, StringComparison.Ordinal);
                Assert.Contains("PowerMeterConfigureMeasurementStep", xml, StringComparison.Ordinal);

                InstrumentSettings.Current.Clear();
                using (var stream = File.OpenRead(settingsPath))
                    ComponentSettings.SetCurrent(stream);

                var loaded = TestPlan.Load(planPath);
                var configure = Assert.IsType<DmmConfigureVoltageAcStep>(loaded.ChildTestSteps[0]);
                Assert.Equal(10, configure.Range);
                Assert.Equal(0.001, configure.Resolution);
                Assert.Equal("TCPIP0::dmm::INSTR", configure.Instrument.VisaAddress);

                var voltage = Assert.IsType<PsuSetVoltageStep>(loaded.ChildTestSteps[1]);
                Assert.Equal(2u, voltage.Channel);
                Assert.Equal(5.5, voltage.Voltage);

                var power = Assert.IsType<PowerMeterConfigureMeasurementStep>(loaded.ChildTestSteps[2]);
                Assert.Equal(PowerUnit.Watt, power.Unit);
                Assert.False(power.AutoRange);
                Assert.Equal(1e9, power.CorrectionFreqHz);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void MixedPlanPublishesPhaseIValuesForEveryClass()
    {
        var io = new CoverageIo();
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        var psu = new DcPowerSupplyInstrument(io) { VisaAddress = "mock://psu" };
        var fgen = new FunctionGeneratorInstrument(io) { VisaAddress = "mock://fgen" };
        var scope = new OscilloscopeInstrument(io) { VisaAddress = "mock://scope" };
        var matrix = new SwitchInstrument(io) { VisaAddress = "mock://sw" };
        var counter = new CounterInstrument(io) { VisaAddress = "mock://cnt" };
        var meter = new PowerMeterInstrument(io) { VisaAddress = "mock://pm" };
        var analyzer = new SpectrumAnalyzerInstrument(io) { VisaAddress = "mock://sa" };

        var listener = new ValueListener();
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(new IdentityQueryStep { Instrument = dmm });
        plan.ChildTestSteps.Add(new DmmMeasureResistance4WireStep { Instrument = dmm, LimitLow = 90, LimitHigh = 110 });
        plan.ChildTestSteps.Add(new PsuReadCurrentStep { Instrument = psu, Channel = 1 });
        plan.ChildTestSteps.Add(new FgenSetLoadStep { Instrument = fgen, Ohms = 50 });
        plan.ChildTestSteps.Add(new ScopeMeasureVppStep { Instrument = scope, Channel = 1 });
        plan.ChildTestSteps.Add(new SwitchIsClosedStep { Instrument = matrix });
        plan.ChildTestSteps.Add(new CounterReadTotalizeStep { Instrument = counter });
        plan.ChildTestSteps.Add(new PowerMeterFetchStep { Instrument = meter });
        plan.ChildTestSteps.Add(new SpectrumAnalyzerFetchTraceStep { Instrument = analyzer });
        plan.ChildTestSteps.Add(new SafeShutdownStep { Instrument = psu });

        var run = plan.Execute([listener]);
        Assert.Equal(Verdict.Pass, run.Verdict);
        Assert.Contains(listener.Scalars, row => row.Name == "Resistance 4W" && (double)row.Value! == 100);
        Assert.Contains(listener.Scalars, row => row.Name == "Current" && (double)row.Value! == 0.5);
        Assert.Contains(listener.Scalars, row => row.Name == "CH1.Vpp" && (double)row.Value! == 2.0);
        Assert.Contains(listener.Scalars, row => row.Name == "Closed" && (double)row.Value! == 1.0);
        Assert.Contains(listener.Scalars, row => row.Name == "Totalize" && (double)row.Value! == 42);
        Assert.Contains(listener.Scalars, row => row.Name == "Fetch" && (double)row.Value! == -10.5);
        Assert.Contains(listener.Samples, row => row.Channel == "Trace" && row.Index == 0 && row.Value == -10.0);
        Assert.Contains(listener.Identities, idn => idn.Contains("Acme", StringComparison.Ordinal));
        Assert.Contains(io.Writes, command => command.Contains("LOAD", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ValueListener : ResultListener
    {
        public List<(string Name, object? Value, object? Unit)> Scalars { get; } = [];
        public List<(string Channel, int Index, double Value)> Samples { get; } = [];
        public List<string> Identities { get; } = [];

        public override void OnResultPublished(Guid stepRunId, ResultTable result)
        {
            var columns = result.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
            if (result.Name == PhaseIResults.ScalarTable)
            {
                Scalars.Add((
                    (string)columns["Name"].Data.GetValue(0)!,
                    columns["Value"].Data.GetValue(0),
                    columns["Unit"].Data.GetValue(0)));
            }
            else if (result.Name == PhaseIResults.SampleTable)
            {
                Samples.Add((
                    (string)columns["Channel"].Data.GetValue(0)!,
                    Convert.ToInt32(columns["Index"].Data.GetValue(0)),
                    Convert.ToDouble(columns["Value"].Data.GetValue(0))));
            }
            else if (result.Name == PhaseIResults.IdentityTable)
            {
                Identities.Add((string)columns["Idn"].Data.GetValue(0)!);
            }

            base.OnResultPublished(stepRunId, result);
        }
    }

    private sealed class CoverageIo : IScpiIo
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
            if (trimmed.StartsWith(":MEAS:FRES", StringComparison.OrdinalIgnoreCase))
                return "100";
            if (trimmed.StartsWith(":MEAS:CURR?", StringComparison.OrdinalIgnoreCase))
                return "0.5";
            if (trimmed.StartsWith(":MEASure:VPP?", StringComparison.OrdinalIgnoreCase))
                return "2.0";
            if (trimmed.StartsWith(":ROUTe:CLOS?", StringComparison.OrdinalIgnoreCase))
                return "1";
            if (trimmed.Equals(":COUNter:DATA?", StringComparison.OrdinalIgnoreCase))
                return "42";
            if (trimmed.Equals("FETC?", StringComparison.OrdinalIgnoreCase))
                return "-10.5";
            if (trimmed.StartsWith(":TRAC:DATA?", StringComparison.OrdinalIgnoreCase))
                return "-10.0,-12.0";
            if (trimmed.EndsWith('?'))
                return "0";
            return "";
        }

        public void Dispose()
        {
        }
    }
}

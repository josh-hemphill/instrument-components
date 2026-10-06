using InstrumentComponents.Scpi;
using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class ReviewRegressionTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void NonfiniteScalarAndSamplesFail(string reply)
    {
        var dmm = new DmmInstrument(new Io { Reply = reply });
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(new DmmMeasureScalarStep { Instrument = dmm, LimitLow = 1, LimitHigh = 2 });
        Assert.Equal(Verdict.Fail, plan.Execute().Verdict);
        var samples = new TestPlan();
        samples.ChildTestSteps.Add(new DmmMeasureVoltageDcStep { Instrument = dmm });
        Assert.Equal(Verdict.Fail, samples.Execute().Verdict);
    }

    [Fact]
    public void NonfiniteLimitsAndNullableInputsAreInvalid()
    {
        var dmm = new DmmInstrument(new Io());
        var scalar = new DmmMeasureScalarStep { Instrument = dmm, LimitLow = double.NaN };
        Assert.Contains("finite", scalar.Error);
        scalar.Run(); Assert.Equal(Verdict.Error, scalar.Verdict);
        var configure = new DmmConfigureCurrentDcStep { Instrument = dmm, Range = double.PositiveInfinity };
        Assert.Contains("finite", configure.Error);
        configure.Run(); Assert.Equal(Verdict.Error, configure.Verdict);
    }

    [Theory]
    [InlineData(false, "A")]
    [InlineData(true, "Ohm")]
    public void ConfigureAndAcquirePublishConfiguredUnitAcrossViews(bool resistance, string unit)
    {
        var dmm = new DmmInstrument(new Io());
        var plan = new TestPlan();
        plan.ChildTestSteps.Add(resistance ? new DmmConfigureResistanceStep { Instrument = dmm } : new DmmConfigureCurrentDcStep { Instrument = dmm });
        plan.ChildTestSteps.Add(new DmmFetchStep { Instrument = dmm });
        plan.ChildTestSteps.Add(new DmmReadStep { Instrument = dmm });
        var listener = new Units();
        Assert.Equal(Verdict.Pass, plan.Execute([listener]).Verdict);
        Assert.Equal(new[] { unit, unit }, listener.Values);
    }

    [Fact]
    public void TimeoutSetterFailureDuringReattachmentDisconnectsAndPermitsRetry()
    {
        var dmm = new DmmInstrument(new Io()); dmm.Open();
        Assert.True(dmm.IsConnected);
        var failed = new Io { FailTimeout = true };
        Assert.Throws<InvalidOperationException>(() => dmm.AttachSession(failed));
        Assert.False(dmm.IsConnected);
        Assert.Throws<InvalidOperationException>(() => dmm.Dmm.Fetch());
        var replacement = new Io(); dmm.AttachSession(replacement); dmm.Open();
        Assert.True(dmm.IsConnected); dmm.Close();
        Assert.Equal(0, failed.Disposals); Assert.Equal(0, replacement.Disposals);
    }

    [Fact]
    public void ShutdownDisablesPreviouslyEnabledPsuChannelAcrossOpenTapViews()
    {
        var io = new Io(); var psu = new DcPowerSupplyInstrument(io) { OutputChannelCount = 2 }; psu.Open();
        psu.Supply.OutputEnable(2, true); psu.OutputOff();
        Assert.Contains(":OUTP2 OFF", io.Writes); psu.Close();
    }

    private sealed class Units : ResultListener
    {
        public List<string> Values { get; } = [];
        public override void OnResultPublished(Guid stepRunId, ResultTable result)
        {
            if (result.Name == PhaseIResults.ScalarTable) Values.Add((string)result.Columns.Single(c => c.Name == "Unit").Data.GetValue(0)!);
        }
    }
    private sealed class Io : IScpiIo
    {
        public string Reply = "1"; public bool FailTimeout; public int Disposals; public List<string> Writes { get; } = [];
        public TimeSpan IoTimeout { get => TimeSpan.FromSeconds(5); set { if (FailTimeout) throw new InvalidOperationException("timeout setter failed"); } }
        public void Write(string command) => Writes.Add(command);
        public string Query(string command) => command == "*IDN?" ? "Acme,BOX,SN,1" : Reply;
        public void Dispose() => Disposals++;
    }
}

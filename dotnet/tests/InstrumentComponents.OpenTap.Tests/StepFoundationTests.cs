using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class StepFoundationTests
{
    [Fact]
    public void EveryStepBindsInstrumentAtDisplayOrderOne()
    {
        foreach (var type in OpenTapCatalog.StepTypes())
        {
            Assert.True(IsInstrumentBound(type), $"{type.Name} must inherit InstrumentBoundStep<T>.");
            var property = type.GetProperty("Instrument");
            Assert.NotNull(property);
            var display = OpenTapCatalog.RequireDisplay(property!);
            Assert.Equal("Instrument", display.Name);
            Assert.Equal(1, display.Order);
        }
    }

    [Fact]
    public void EveryStepErrorsWhenInstrumentIsMissing()
    {
        foreach (var type in OpenTapCatalog.StepTypes())
        {
            var step = (TestStep)OpenTapCatalog.Create(type);
            Assert.Contains("Instrument must be assigned", step.Error, StringComparison.Ordinal);
            step.Run();
            Assert.Equal(Verdict.Error, step.Verdict);
        }
    }

    [Fact]
    public void FormattedNameIncludesAssignedInstrument()
    {
        var dmm = new DmmInstrument { Name = "Bench DMM" };
        var step = new DmmMeasureVoltageDcStep { Instrument = dmm };
        Assert.Equal("DMM Measure Voltage DC @ Bench DMM", ((IFormatName)step).GetFormattedName());
    }

    [Fact]
    public void SampleCountAndLimitRulesSurfaceInTheEditor()
    {
        var samples = new DmmMeasureVoltageDcStep { SampleCount = 0, IntervalMs = -1 };
        Assert.Contains("Sample count must be at least 1.", samples.Error, StringComparison.Ordinal);
        Assert.Contains("Interval must be zero or positive.", samples.Error, StringComparison.Ordinal);

        var scalar = new DmmMeasureScalarStep { LimitLow = 5, LimitHigh = 1 };
        Assert.Contains("Limit low must not exceed limit high.", scalar.Error, StringComparison.Ordinal);

        var route = new SwitchCloseRouteStep { Channel1 = 2, Channel2 = 2 };
        Assert.Contains("Route endpoints must differ.", route.Error, StringComparison.Ordinal);
    }

    private static bool IsInstrumentBound(Type type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(InstrumentBoundStep<>))
                return true;
            current = current.BaseType;
        }

        return false;
    }
}

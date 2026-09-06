using InstrumentComponents.OpenTap;

namespace InstrumentComponents.OpenTap.Visa.Tests;

public class OpenTapVisaHardwareTests
{
    private const double MaxAbsVolts = 1_000_000;

    [HardwareFact]
    [Trait("Category", "Hardware")]
    public void OpenTapVisaDmmMeasureVoltageDcSmoke()
    {
        Assert.True(HardwareResource.TryFromEnv(out var resource, out var error), error);
        var previous = OpenTapScpiIo.Provider;
        OpenTapVisa.RegisterOverride();
        try
        {
            var dmm = new DmmInstrument
            {
                VisaAddress = resource,
                IoTimeoutMilliseconds = 10_000,
            };
            dmm.Open();
            try
            {
                var volts = dmm.Dmm.MeasureVoltageDc();
                Assert.True(
                    double.IsFinite(volts) && Math.Abs(volts) < MaxAbsVolts,
                    $"DMM reading looks like overload/sentinel: {volts}");
                Console.Error.WriteLine(
                    $"opentap visa smoke: {dmm.IdentityFields.Manufacturer} {dmm.IdentityFields.Model} @ {resource} → {volts} V DC");
            }
            finally
            {
                dmm.Close();
            }
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }
}

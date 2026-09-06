using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class TapPlanRoundTripTests
{
    [Fact]
    public void AllEightInstrumentsRoundTripWithoutBroker()
    {
        foreach (var type in OpenTapCatalog.InstrumentTypes())
        {
            var original = (ScpiInstrument)OpenTapCatalog.Create(type);
            original.Name = $"Bench {type.Name}";
            original.VisaAddress = "TCPIP0::192.0.2.10::inst0::INSTR";
            original.IoTimeoutMilliseconds = 2500;

            var serializer = new TapSerializer();
            var xml = serializer.SerializeToString(original);
            Assert.Contains(type.Name, xml, StringComparison.Ordinal);
            Assert.Contains(original.VisaAddress, xml, StringComparison.Ordinal);
            Assert.DoesNotContain("Ivi.Visa", xml, StringComparison.Ordinal);

            var loaded = serializer.DeserializeFromString(xml, TypeData.FromType(type));
            Assert.IsType(type, loaded);
            var instrument = Assert.IsAssignableFrom<ScpiInstrument>(loaded);
            Assert.Equal(original.VisaAddress, instrument.VisaAddress);
            Assert.Equal(2500, instrument.IoTimeoutMilliseconds);

            var ex = Assert.Throws<InvalidOperationException>(() => instrument.Open());
            Assert.Contains("does not open a vendor VISA", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TestPlanRoundTripsEveryStepBoundToTypedInstruments()
    {
        InstrumentSettingsScope.Run(() =>
        {
            var instruments = CreateNamedInstruments();
            foreach (var instrument in instruments.Values)
                InstrumentSettings.Current.Add(instrument);

            var plan = new TestPlan();
            foreach (var type in OpenTapCatalog.StepTypes())
                plan.ChildTestSteps.Add(CreateBoundStep(type, instruments));

            var directory = Path.Combine(Path.GetTempPath(), $"ic-opentap-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var planPath = Path.Combine(directory, "AllSteps.TapPlan");
                var settingsPath = Path.Combine(directory, "Instruments.xml");
                plan.Save(planPath);
                File.WriteAllText(settingsPath, new TapSerializer().SerializeToString(InstrumentSettings.Current));

                var xml = File.ReadAllText(planPath);
                foreach (var type in OpenTapCatalog.StepTypes())
                    Assert.Contains(type.FullName!, xml, StringComparison.Ordinal);
                foreach (var type in OpenTapCatalog.InstrumentTypes())
                    Assert.Contains($"emb:{type.FullName!}", xml, StringComparison.Ordinal);

                InstrumentSettings.Current.Clear();
                using (var stream = File.OpenRead(settingsPath))
                    ComponentSettings.SetCurrent(stream);

                var loaded = TestPlan.Load(planPath);
                Assert.Equal(OpenTapCatalog.StepTypes().Count, loaded.ChildTestSteps.Count);
                foreach (var (expectedType, loadedStep) in OpenTapCatalog.StepTypes().Zip(loaded.ChildTestSteps))
                {
                    Assert.Equal(expectedType, loadedStep.GetType());
                    var instrument = InstrumentOf(loadedStep);
                    Assert.NotNull(instrument);
                    Assert.False(string.IsNullOrWhiteSpace(instrument.VisaAddress));
                    var ex = Assert.Throws<InvalidOperationException>(() => instrument.Open());
                    Assert.Contains("does not open a vendor VISA", ex.Message, StringComparison.Ordinal);
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    private static Dictionary<Type, ScpiInstrument> CreateNamedInstruments()
    {
        var instruments = new Dictionary<Type, ScpiInstrument>();
        foreach (var type in OpenTapCatalog.InstrumentTypes())
        {
            var instrument = (ScpiInstrument)OpenTapCatalog.Create(type);
            instrument.Name = type.Name;
            instrument.VisaAddress = $"TCPIP0::{type.Name}::inst0::INSTR";
            instrument.IoTimeoutMilliseconds = 1234;
            instruments[type] = instrument;
        }

        return instruments;
    }

    private static TestStep CreateBoundStep(Type stepType, IReadOnlyDictionary<Type, ScpiInstrument> instruments)
    {
        var step = (TestStep)OpenTapCatalog.Create(stepType);
        var property = stepType.GetProperty("Instrument")
            ?? throw new InvalidOperationException($"{stepType.Name} has no Instrument property.");
        property.SetValue(step, ResolveInstrument(property.PropertyType, instruments));
        return step;
    }

    private static ScpiInstrument ResolveInstrument(Type instrumentType, IReadOnlyDictionary<Type, ScpiInstrument> instruments)
    {
        if (instrumentType == typeof(ScpiInstrument))
            return instruments[typeof(DmmInstrument)];
        if (instruments.TryGetValue(instrumentType, out var instrument))
            return instrument;
        throw new InvalidOperationException($"no instrument registered for {instrumentType.FullName}");
    }

    private static ScpiInstrument InstrumentOf(ITestStep step)
    {
        var property = step.GetType().GetProperty("Instrument")
            ?? throw new InvalidOperationException($"{step.GetType().Name} has no Instrument property.");
        return Assert.IsAssignableFrom<ScpiInstrument>(property.GetValue(step));
    }
}

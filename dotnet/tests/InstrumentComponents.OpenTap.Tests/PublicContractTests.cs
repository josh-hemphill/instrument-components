namespace InstrumentComponents.OpenTap.Tests;

public class PublicContractTests
{
    [Fact]
    public void InstrumentTypesMatchGoldenCatalog() =>
        PublicContractCatalog.AssertMatchesGolden(PublicContractCatalog.Instruments(), "instrument-types.json");

    [Fact]
    public void StepTypesMatchGoldenCatalog() =>
        PublicContractCatalog.AssertMatchesGolden(PublicContractCatalog.Steps(), "step-types.json");

    [Fact]
    public void PhaseIResultTablesMatchGoldenCatalog() =>
        PublicContractCatalog.AssertMatchesGolden(PublicContractCatalog.ResultTables(), "phase-i-results.json");

    [Fact]
    public void PackShipsEightInstrumentsAndSeventeenSteps()
    {
        Assert.Equal(8, OpenTapCatalog.InstrumentTypes().Count);
        Assert.Equal(17, OpenTapCatalog.StepTypes().Count);
        Assert.All(OpenTapCatalog.InstrumentTypes(), type =>
            Assert.StartsWith("InstrumentComponents.OpenTap.", type.FullName));
        Assert.All(OpenTapCatalog.StepTypes(), type =>
            Assert.StartsWith("InstrumentComponents.OpenTap.", type.FullName));
    }
}

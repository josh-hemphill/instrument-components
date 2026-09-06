using System.Xml.Linq;
using InstrumentComponents.OpenTap;
using InstrumentComponents.Scpi;

namespace InstrumentComponents.OpenTap.Visa.Tests;

public class OpenTapVisaCompanionTests
{
    [Fact]
    [Trait("Category", "Mock")]
    public void Register_DoesNotOverwriteExistingProvider()
    {
        var previous = OpenTapScpiIo.Provider;
        var existing = new SentinelProvider();
        OpenTapScpiIo.Provider = existing;
        try
        {
            OpenTapVisa.Register();
            Assert.Same(existing, OpenTapScpiIo.Provider);
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }

    [Fact]
    [Trait("Category", "Mock")]
    public void RegisterOverride_InstallsVisaProvider()
    {
        var previous = OpenTapScpiIo.Provider;
        OpenTapScpiIo.Provider = new SentinelProvider();
        try
        {
            OpenTapVisa.RegisterOverride();
            Assert.IsType<OpenTapVisaScpiIoProvider>(OpenTapScpiIo.Provider);
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }

    [Fact]
    [Trait("Category", "Mock")]
    public void Provider_RejectsEmptyAndUnknownAddressesBeforeVisa()
    {
        var provider = new OpenTapVisaScpiIoProvider();
        Assert.Throws<InstrumentComponents.Errors.InvalidAddressException>(
            () => provider.Open("  ", TimeSpan.FromSeconds(1)));
        Assert.Throws<InstrumentComponents.Errors.InvalidAddressException>(
            () => provider.Open("mock://unspecified", TimeSpan.FromSeconds(1)));
        Assert.Throws<InstrumentComponents.Errors.InvalidAddressException>(
            () => provider.Open("not-a-visa-resource", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    [Trait("Category", "Mock")]
    public void MainPackCsprojDoesNotReferenceTheCompanionOrVisa()
    {
        var packCsproj = Path.Combine(
            RepoRoot(),
            "dotnet",
            "src",
            "InstrumentComponents.OpenTap",
            "InstrumentComponents.OpenTap.csproj");
        var xml = File.ReadAllText(packCsproj);
        Assert.DoesNotContain("InstrumentComponents.OpenTap.Visa", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("InstrumentComponents.Visa", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("IviFoundation.Visa", xml, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Mock")]
    public void CompanionCsprojReferencesOpenTapAndVisa()
    {
        var path = Path.Combine(
            RepoRoot(),
            "dotnet",
            "src",
            "InstrumentComponents.OpenTap.Visa",
            "InstrumentComponents.OpenTap.Visa.csproj");
        var project = XDocument.Load(path);
        var includes = project
            .Descendants("ProjectReference")
            .Select(node => node.Attribute("Include")?.Value ?? "")
            .ToList();
        Assert.Contains(includes, value => value.Contains("InstrumentComponents.OpenTap.csproj", StringComparison.Ordinal));
        Assert.Contains(includes, value => value.Contains("InstrumentComponents.Visa.csproj", StringComparison.Ordinal));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "dotnet")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not locate repo root");
    }

    sealed class SentinelProvider : IOpenTapScpiIoProvider
    {
        public IScpiIo Open(string visaAddress, TimeSpan ioTimeout) =>
            throw new InvalidOperationException("sentinel provider must not open");
    }
}

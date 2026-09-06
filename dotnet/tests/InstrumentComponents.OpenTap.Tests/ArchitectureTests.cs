namespace InstrumentComponents.OpenTap.Tests;

public class ArchitectureTests
{
    [Fact]
    public void PackSourcesDoNotReferenceIviVisa()
    {
        var packDir = Path.Combine(RepoPaths.Root(), "dotnet", "src", "InstrumentComponents.OpenTap");
        Assert.True(Directory.Exists(packDir), packDir);
        string[] forbidden =
        [
            "Ivi.Visa",
            "GlobalResourceManager",
            "IviFoundation.Visa",
            "HardwareTest.Core",
            "InstrumentComponents.Visa",
            "InstrumentComponents.OpenTap.Visa",
            "OpenTapVisa",
        ];
        foreach (var file in Directory.EnumerateFiles(packDir, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                Assert.False(
                    text.Contains(token, StringComparison.Ordinal),
                    $"{file} contains forbidden '{token}'");
            }
        }
    }

    [Fact]
    public void PackageXmlBundlesPluginAndCoreWithoutVisa()
    {
        var path = Path.Combine(RepoPaths.Root(), "dotnet", "src", "InstrumentComponents.OpenTap", "package.xml");
        var xml = File.ReadAllText(path);
        Assert.Contains("InstrumentComponents.OpenTap.dll", xml, StringComparison.Ordinal);
        Assert.Contains("InstrumentComponents.dll", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("InstrumentComponents.Visa", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("InstrumentComponents.OpenTap.Visa", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ivi.Visa", xml, StringComparison.Ordinal);
    }
}

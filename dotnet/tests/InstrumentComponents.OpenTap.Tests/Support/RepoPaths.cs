namespace InstrumentComponents.OpenTap.Tests;

internal static class RepoPaths
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "spec", "scpi-vectors.json")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("could not find repo root");
    }

    public static string Golden(string fileName) =>
        Path.Combine(Root(), "dotnet", "tests", "InstrumentComponents.OpenTap.Tests", "Golden", fileName);
}

using System.Xml.Linq;
using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.OpenTap;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;
using InstrumentComponents.Transport;

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
    public void Provider_DisposesTransport_WhenScpiSessionConstructionFails()
    {
        var opener = new ThrowingConfigureOpener();
        var provider = new OpenTapVisaScpiIoProvider(opener);
        Assert.Throws<InvalidOperationException>(
            () => provider.Open("USB0::0x2A8D::0x1301::INSTR", TimeSpan.FromSeconds(1)));
        Assert.True(opener.Transport.Disposed);
    }

    [Fact]
    [Trait("Category", "Mock")]
    public void Provider_ReturnsFramedScpiSession_NotInjectedIo()
    {
        var opener = new IdleOpener();
        var provider = new OpenTapVisaScpiIoProvider(opener);
        using var io = provider.Open("USB0::0x2A8D::0x1301::INSTR", TimeSpan.FromSeconds(1));
        var session = Assert.IsType<ScpiSession>(io);
        Assert.False(session.IsInjected);
        Assert.False(session.Options.ResetOnConnect);
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

    sealed class ThrowingConfigureOpener : ISessionOpener
    {
        public DisposableTransport Transport { get; } = new() { ThrowOnConfigure = true };

        public ITransport Open(ResourceAddress address, ConnectOptions opts) => Transport;
    }

    sealed class IdleOpener : ISessionOpener
    {
        public ITransport Open(ResourceAddress address, ConnectOptions opts) => new DisposableTransport();
    }

    sealed class DisposableTransport : TransportBase, IDisposable
    {
        public bool Disposed { get; private set; }
        public bool ThrowOnConfigure { get; set; }

        public override void Write(ReadOnlySpan<byte> data)
        {
        }

        public override int Read(Span<byte> buffer) => 0;

        public override void Clear()
        {
        }

        public override void SetReadTimeout(TimeSpan timeout)
        {
        }

        public override void Configure(ConnectOptions opts)
        {
            if (ThrowOnConfigure)
                throw new InvalidOperationException("configure failed");
            base.Configure(opts);
        }

        public void Dispose() => Disposed = true;
    }
}

using InstrumentComponents.OpenTap;
using InstrumentComponents.Scpi;

namespace InstrumentComponents.OpenTap.Tests;

public class OpenTapScpiIoProviderTests
{
    [Fact]
    public void Open_UsesRegisteredProvider_WhenNoAttachSession()
    {
        var provider = new FakeProvider();
        var previous = OpenTapScpiIo.Provider;
        OpenTapScpiIo.Provider = provider;
        try
        {
            var instrument = new DmmInstrument { VisaAddress = "USB0::0x2A8D::0x1301::INSTR" };
            instrument.Open();
            try
            {
                Assert.True(instrument.IsConnected);
                Assert.Equal("USB0::0x2A8D::0x1301::INSTR", provider.LastAddress);
                Assert.Equal(1, provider.OpenCount);
                Assert.Equal("Acme", instrument.QueryIdn().Manufacturer);
            }
            finally
            {
                instrument.Close();
            }

            Assert.True(provider.LastIo!.Disposed);
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }

    [Fact]
    public void Open_AfterClose_OpensProviderIoAgain()
    {
        var provider = new FakeProvider();
        var previous = OpenTapScpiIo.Provider;
        OpenTapScpiIo.Provider = provider;
        try
        {
            var instrument = new DmmInstrument { VisaAddress = "USB0::0x2A8D::0x1301::INSTR" };
            instrument.Open();
            instrument.Close();
            Assert.True(provider.LastIo!.Disposed);

            instrument.Open();
            try
            {
                Assert.Equal(2, provider.OpenCount);
                Assert.False(provider.LastIo.Disposed);
            }
            finally
            {
                instrument.Close();
            }
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }

    [Fact]
    public void Open_PrefersAttachSession_OverProvider()
    {
        var provider = new FakeProvider();
        var previous = OpenTapScpiIo.Provider;
        OpenTapScpiIo.Provider = provider;
        try
        {
            var attached = new ScriptedIo(("*IDN?", "Keysight,34461A,SN,1.0"));
            var instrument = new DmmInstrument { VisaAddress = "USB0::0x2A8D::0x1301::INSTR" };
            instrument.AttachSession(attached);
            instrument.Open();
            try
            {
                Assert.Equal(0, provider.OpenCount);
                Assert.Equal("34461A", instrument.IdentityFields.Model);
            }
            finally
            {
                instrument.Close();
            }

            Assert.False(attached.Disposed);
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }

    [Fact]
    public void Open_WithoutProviderOrAttach_StillThrows()
    {
        var previous = OpenTapScpiIo.Provider;
        OpenTapScpiIo.Provider = null;
        try
        {
            var instrument = new DmmInstrument { VisaAddress = "USB0::0x2A8D::0x1301::INSTR" };
            var ex = Assert.Throws<InvalidOperationException>(instrument.Open);
            Assert.Contains("does not open a vendor VISA", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Provider", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }

    [Fact]
    public void Open_WithProvider_RequiresVisaAddress()
    {
        var previous = OpenTapScpiIo.Provider;
        OpenTapScpiIo.Provider = new FakeProvider();
        try
        {
            var instrument = new DmmInstrument();
            var ex = Assert.Throws<InvalidOperationException>(instrument.Open);
            Assert.Contains("VisaAddress", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            OpenTapScpiIo.Provider = previous;
        }
    }

    sealed class FakeProvider : IOpenTapScpiIoProvider
    {
        public int OpenCount { get; private set; }
        public string? LastAddress { get; private set; }
        public ScriptedIo? LastIo { get; private set; }

        public IScpiIo Open(string visaAddress, TimeSpan ioTimeout)
        {
            OpenCount++;
            LastAddress = visaAddress;
            LastIo = new ScriptedIo(("*IDN?", "Acme,DMM1,SN-1,2.0"));
            return LastIo;
        }
    }

    sealed class ScriptedIo : IScpiIo
    {
        private readonly Dictionary<string, string> _queries;
        private bool _disposed;

        public ScriptedIo(params (string Command, string Response)[] queries)
        {
            _queries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (command, response) in queries)
                _queries[command] = response;
        }

        public bool Disposed { get; private set; }
        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public string Query(string command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _queries.TryGetValue(command.Trim(), out var response) ? response : "";
        }

        public void Dispose()
        {
            _disposed = true;
            Disposed = true;
        }
    }
}

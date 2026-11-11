using InstrumentComponents.Address;
using InstrumentComponents.Identity;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;

namespace InstrumentComponents.Tests;

public class ScpiIoInjectionTests
{
    [Fact]
    public void FromIoDrivesDmmMeasureWithoutTransport()
    {
        var io = new ScriptedIo((":MEAS:VOLT:DC?", "3.300"));
        var session = InstrumentSession.FromIo(
            ResourceAddress.Parse("TCPIP0::127.0.0.1::inst0::INSTR"),
            io,
            new DeviceIdentity { Manufacturer = "Acme", Model = "DMM1" });

        var volts = ScpiSession.ParseF64(session.Scpi.Query(":MEAS:VOLT:DC?"));

        Assert.InRange(volts, 3.299, 3.301);
        Assert.Equal([":MEAS:VOLT:DC?"], io.Commands);
    }

    [Fact]
    public void FromIoDoesNotAddTerminator()
    {
        var io = new ScriptedIo(("CONF:VOLT:DC", ""));
        var session = InstrumentSession.FromIo(
            ResourceAddress.Parse("mock://dmm"),
            io,
            new DeviceIdentity());

        session.Scpi.Write("CONF:VOLT:DC");

        Assert.Equal(["CONF:VOLT:DC"], io.Commands);
        Assert.DoesNotContain("\n", io.Commands[0]);
    }

    [Fact]
    public void InjectedTimeoutIsHonoredAndRestored()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,SN,1.0"));
        io.IoTimeout = TimeSpan.FromMilliseconds(250);
        var session = InstrumentSession.FromIo(
            ResourceAddress.Parse("mock://dmm"),
            io,
            new DeviceIdentity());

        session.Scpi.IoTimeout = TimeSpan.FromSeconds(2);
        Assert.Equal(TimeSpan.FromSeconds(2), io.IoTimeout);

        var idn = session.Idn();
        Assert.Equal("Acme", idn.Manufacturer);
        Assert.Equal(TimeSpan.FromSeconds(2), io.IoTimeout);
        Assert.Contains(TimeSpan.FromSeconds(2), io.TimeoutsSeen);
    }

    [Fact]
    public void DelegateScpiIoForwardsTimeoutCallback()
    {
        TimeSpan? forwarded = null;
        var io = new DelegateScpiIo(
            write: _ => { },
            query: cmd => cmd == "*IDN?" ? "A,B,C,D" : "",
            ioTimeout: TimeSpan.FromSeconds(1),
            onTimeoutChanged: t => forwarded = t);

        Assert.Equal(TimeSpan.FromSeconds(1), forwarded);
        io.IoTimeout = TimeSpan.FromMilliseconds(400);
        Assert.Equal(TimeSpan.FromMilliseconds(400), forwarded);
        Assert.Equal("A,B,C,D", io.Query("*IDN?"));
    }

    [Fact]
    public void FromIoDisposeDisposesInner()
    {
        var io = new ScriptedIo();
        var session = InstrumentSession.FromIo(
            ResourceAddress.Parse("mock://dmm"),
            io,
            new DeviceIdentity());
        session.Dispose();
        Assert.True(io.Disposed);
        Assert.Throws<ObjectDisposedException>(() => io.Query("*IDN?"));
    }

    [Fact]
    public void FromIoOwnsIoFalseLeavesHostSessionAlive()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,SN,1.0"));
        var first = InstrumentSession.FromIo(
            ResourceAddress.Parse("mock://dmm"),
            io,
            new DeviceIdentity(),
            ownsIo: false);
        var second = InstrumentSession.FromIo(
            ResourceAddress.Parse("mock://dmm"),
            io,
            new DeviceIdentity(),
            ownsIo: false);
        first.Dispose();
        Assert.False(io.Disposed);
        Assert.Equal("Acme", second.Idn().Manufacturer);
        second.Dispose();
        Assert.False(io.Disposed);
    }

    [Fact]
    public void InjectedSessionHasNoByteTransport()
    {
        var session = InstrumentSession.FromIo(
            ResourceAddress.Parse("mock://dmm"),
            new ScriptedIo(),
            new DeviceIdentity());
        Assert.True(session.Scpi.IsInjected);
        var ex = Assert.Throws<InstrumentComponents.Errors.InstrumentUnsupportedException>(
            () => session.Scpi.Transport);
        Assert.Contains("no byte transport", ex.Message, StringComparison.Ordinal);
    }

    private sealed class ScriptedIo : IScpiIo
    {
        private readonly Dictionary<string, string> _queries;
        private bool _disposed;

        public ScriptedIo(params (string Command, string Response)[] queries)
        {
            _queries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (command, response) in queries)
                _queries[command] = response;
        }

        public List<string> Commands { get; } = [];
        public List<TimeSpan> TimeoutsSeen { get; } = [];
        public bool Disposed { get; private set; }
        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Commands.Add(command);
        }

        public string Query(string command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Commands.Add(command);
            TimeoutsSeen.Add(IoTimeout);
            return _queries.TryGetValue(command.Trim(), out var response) ? response : "";
        }

        public void Dispose()
        {
            _disposed = true;
            Disposed = true;
        }
    }
}

using InstrumentComponents.Address;
using InstrumentComponents.Classes;
using InstrumentComponents.Identity;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;

namespace InstrumentComponents.Tests;

public class IdentityShutdownTests
{
    [Fact]
    public void AllSyncClassesExposeIdentityAndShutdown()
    {
        var session = SessionWith("*IDN?", "Acme,Box,SN,1.0");
        IInstrumentIdentity[] identities =
        [
            new Dmm(session),
            new DcPowerSupply(session),
            new FunctionGenerator(session),
            new Oscilloscope(session),
            new Switch(session),
            new Counter(session),
            new PowerMeter(session),
            new SpectrumAnalyzer(session),
        ];
        foreach (var identity in identities)
            Assert.Equal("Acme", identity.QueryIdn().Manufacturer);
    }

    [Fact]
    public void DmmOutputOffIsNoOpThenResetWritesRst()
    {
        var io = new RecordingIo(("*IDN?", "Acme,DMM,SN,1.0"));
        var dmm = new Dmm(SessionFrom(io));
        dmm.OutputOff();
        Assert.Empty(io.Writes);
        dmm.Reset();
        Assert.Contains("*RST", io.Writes);
    }

    [Fact]
    public void PsuOutputOffDisablesEveryChannel()
    {
        var io = new RecordingIo();
        var session = SessionFrom(io);
        session.PowerSupplyChannelCount = 3;
        new DcPowerSupply(session).OutputOff();
        Assert.Equal(new[] { ":OUTP1 OFF", ":OUTP2 OFF", ":OUTP3 OFF" }, io.Writes);
    }

    [Fact]
    public void FunctionGeneratorOutputOffDisablesOutput()
    {
        var io = new RecordingIo();
        new FunctionGenerator(SessionFrom(io)).OutputOff();
        Assert.Contains(io.Writes, w => w.Contains("OFF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OscilloscopeOutputOffStopsAcquisition()
    {
        var io = new RecordingIo();
        new Oscilloscope(SessionFrom(io)).OutputOff();
        Assert.NotEmpty(io.Writes);
    }

    [Fact]
    public void SwitchOutputOffOpensAll()
    {
        var io = new RecordingIo();
        new Switch(SessionFrom(io)).OutputOff();
        Assert.NotEmpty(io.Writes);
    }

    [Fact]
    public void SpectrumAnalyzerOutputOffStopsSweep()
    {
        var io = new RecordingIo();
        new SpectrumAnalyzer(SessionFrom(io)).OutputOff();
        Assert.NotEmpty(io.Writes);
    }

    [Fact]
    public void CounterAndPowerMeterOutputOffAreNoOps()
    {
        var io = new RecordingIo();
        var session = SessionFrom(io);
        new Counter(session).OutputOff();
        new PowerMeter(session).OutputOff();
        Assert.Empty(io.Writes);
    }

    private static InstrumentSession SessionWith(string query, string response) =>
        SessionFrom(new RecordingIo((query, response)));

    private static InstrumentSession SessionFrom(IScpiIo io) =>
        InstrumentSession.FromIo(
            ResourceAddress.Parse("mock://dut"),
            io,
            new DeviceIdentity { Manufacturer = "Acme", Model = "Box" });

    private sealed class RecordingIo : IScpiIo
    {
        private readonly Dictionary<string, string> _queries;

        public RecordingIo(params (string Command, string Response)[] queries)
        {
            _queries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (command, response) in queries)
                _queries[command] = response;
        }

        public List<string> Writes { get; } = [];
        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command) => Writes.Add(command);

        public string Query(string command) =>
            command.Trim() == "*OPC?" ? "1" : _queries.TryGetValue(command.Trim(), out var response) ? response : "";

        public void Dispose()
        {
        }
    }
}

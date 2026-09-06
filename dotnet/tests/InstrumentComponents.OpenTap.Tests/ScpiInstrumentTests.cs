using InstrumentComponents.Enumerator;
using InstrumentComponents.Errors;
using InstrumentComponents.OpenTap;
using InstrumentComponents.Scpi;
using OpenTap;

namespace InstrumentComponents.OpenTap.Tests;

public class ScpiInstrumentTests
{
    [Fact]
    public void AllEightTypesHaveWritableVisaAddress()
    {
        ScpiInstrument[] instruments =
        [
            new DmmInstrument(),
            new DcPowerSupplyInstrument(),
            new FunctionGeneratorInstrument(),
            new OscilloscopeInstrument(),
            new SwitchInstrument(),
            new CounterInstrument(),
            new PowerMeterInstrument(),
            new SpectrumAnalyzerInstrument(),
        ];

        foreach (var instrument in instruments)
        {
            instrument.VisaAddress = "TCPIP0::192.168.0.10::inst0::INSTR";
            Assert.Equal("TCPIP0::192.168.0.10::inst0::INSTR", instrument.VisaAddress);
            Assert.True(instrument.GetType().GetProperty(nameof(ScpiInstrument.VisaAddress))!.CanWrite);
        }
    }

    [Fact]
    public void OpenWithoutSessionThrowsWithoutOpeningVisa()
    {
        var dmm = new DmmInstrument { VisaAddress = "TCPIP0::127.0.0.1::inst0::INSTR" };
        var ex = Assert.Throws<InvalidOperationException>(() => dmm.Open());
        Assert.Contains("does not open a vendor VISA", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachSessionOpenQueryIdnAndClose()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,SN-1,2.0"));
        var dmm = new DmmInstrument();
        dmm.VisaAddress = "USB0::0x2A8D::0x1301::INSTR";
        dmm.AttachSession(io);
        dmm.Open();
        try
        {
            Assert.True(dmm.IsConnected);
            var idn = dmm.QueryIdn();
            Assert.Equal("Acme", idn.Manufacturer);
            Assert.Equal("DMM1", dmm.IdentityFields.Model);
            Assert.InRange(dmm.Dmm.MeasureVoltageDc(), 1.0, 1.0);
        }
        finally
        {
            dmm.Close();
        }

        Assert.False(io.Disposed);
        Assert.Throws<InvalidOperationException>(() => dmm.QueryIdn());
    }

    [Fact]
    public void CloseDoesNotDisposeSharedHostIo()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,SN-1,2.0"));
        var dmm = new DmmInstrument(io) { VisaAddress = "mock://shared" };
        var psu = new DcPowerSupplyInstrument(io) { VisaAddress = "mock://shared" };
        dmm.Open();
        psu.Open();

        dmm.Close();

        Assert.False(io.Disposed);
        Assert.Equal("Acme", psu.QueryIdn().Manufacturer);
        psu.Close();
        Assert.False(io.Disposed);
        io.Dispose();
        Assert.True(io.Disposed);
    }

    [Fact]
    public void VisaAddressCarriesVisaAddressAttribute()
    {
        var property = typeof(ScpiInstrument).GetProperty(nameof(ScpiInstrument.VisaAddress));
        Assert.NotNull(property);
        Assert.True(Attribute.IsDefined(property!, typeof(VisaAddressAttribute)));
    }

    [Fact]
    public void PrimaryShutdownSkipsKindGuardWhenIdnIsADifferentClass()
    {
        var io = new ScriptedIo(("*IDN?", "Keysight Technologies,E36312A,SN,1.0"));
        var instrument = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        instrument.Open();
        try
        {
            instrument.OutputOff();
            instrument.Reset();
            Assert.Contains("*RST", io.Writes);
            Assert.NotNull(instrument.AsDcPowerSupply());
            Assert.Throws<UnsupportedKindException>(() => instrument.AsOscilloscope());
        }
        finally
        {
            instrument.Close();
        }
    }

    [Fact]
    public void AttachSessionRebindsLiveInstrument()
    {
        var first = new ScriptedIo(("*IDN?", "Acme,DMM1,1,1.0"));
        var second = new ScriptedIo(("*IDN?", "Keysight Technologies,34461A,SN,1.0"));
        var instrument = new DmmInstrument(first) { VisaAddress = "mock://dmm" };
        instrument.Open();
        Assert.Equal("DMM1", instrument.IdentityFields.Model);

        instrument.AttachSession(second);

        Assert.True(instrument.IsConnected);
        Assert.Equal("34461A", instrument.IdentityFields.Model);
        Assert.False(first.Disposed);
        Assert.False(second.Disposed);
        instrument.Close();
    }

    [Fact]
    public void ExtraViewsThrowWhenRegistryKindsExcludeRequestedKind()
    {
        var io = new ScriptedIo(("*IDN?", "Keysight Technologies,34461A,MY000,A.03.03"));
        var instrument = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        instrument.Open();
        try
        {
            var ex = Assert.Throws<UnsupportedKindException>(() => instrument.AsDcPowerSupply());
            Assert.Equal(InstrumentComponents.Kind.InstrumentKind.DcPowerSupply, ex.Kind);
        }
        finally
        {
            instrument.Close();
        }
    }

    [Fact]
    public void ExtraViewsAreAllowedWhenIdentityIsUnknown()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,1,1.0"));
        var instrument = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        instrument.Open();
        try
        {
            var extra = instrument.AsDcPowerSupply();
            Assert.NotNull(extra);
        }
        finally
        {
            instrument.Close();
        }
    }

    [Fact]
    public void OpenIsIdempotentWhenAlreadyConnected()
    {
        var io = new ScriptedIo(("*IDN?", "Acme,DMM1,SN-1,2.0"));
        var instrument = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        instrument.Open();
        instrument.Open();
        Assert.Equal(1, io.Queries.Count(q => q == "*IDN?"));
        instrument.Close();
    }

    [Fact]
    public void OpenFailsClosedWhenIdnThrows()
    {
        var io = new ThrowingIdnIo();
        var instrument = new DmmInstrument(io) { VisaAddress = "mock://dmm" };
        Assert.Throws<CommunicationException>(() => instrument.Open());
        Assert.False(instrument.IsConnected);
        Assert.False(io.Disposed);
        Assert.Throws<InvalidOperationException>(() => instrument.QueryIdn());
    }

    [Fact]
    public void DmmInstrumentRoundTripsVisaAddress()
    {
        var original = new DmmInstrument
        {
            Name = "Bench DMM",
            VisaAddress = "TCPIP0::192.0.2.10::inst0::INSTR",
            IoTimeoutMilliseconds = 2500,
        };
        var serializer = new TapSerializer();
        var xml = serializer.SerializeToString(original);
        Assert.Contains("DmmInstrument", xml, StringComparison.Ordinal);
        Assert.Contains("TCPIP0::192.0.2.10::inst0::INSTR", xml, StringComparison.Ordinal);

        var loaded = Assert.IsType<DmmInstrument>(
            serializer.DeserializeFromString(xml, TypeData.FromType(typeof(DmmInstrument))));
        Assert.Equal(original.VisaAddress, loaded.VisaAddress);
        Assert.Equal(2500, loaded.IoTimeoutMilliseconds);
    }

    [Fact]
    public void ConstructorInjectionOpens()
    {
        var io = new ScriptedIo(("*IDN?", "Keysight,E36312A,SN,1.0"));
        var psu = new DcPowerSupplyInstrument(io) { VisaAddress = "TCPIP0::1.2.3.4::INSTR" };
        psu.Open();
        try
        {
            psu.OutputOff();
            Assert.Contains(io.Writes, w => w.Contains("OFF", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            psu.Close();
        }

        Assert.False(io.Disposed);
    }

    [Fact]
    public void DiscoveryIsEmptyWithoutEnumeratorAndListsWhenRegistered()
    {
        var discovery = new ScpiVisaAddressDiscovery();
        var attribute = new VisaAddressAttribute();
        Assert.True(discovery.CanDetect(attribute));
        Assert.Empty(discovery.DetectDeviceAddresses(attribute));

        OpenTapResourceEnumeration.Register(StaticEnumerator.FromAddresses(["USB0::0x1::0x2::INSTR"]));
        Assert.Equal(["USB0::0x1::0x2::INSTR"], discovery.DetectDeviceAddresses(attribute));
    }

    [Fact]
    public void TestPlanRoundTripsDmmTypeWithoutBroker()
    {
        var original = new DmmInstrument
        {
            Name = "Bench DMM",
            VisaAddress = "TCPIP0::192.0.2.10::inst0::INSTR",
            IoTimeoutMilliseconds = 2500,
        };
        var plan = new TestPlan();
        var path = Path.Combine(Path.GetTempPath(), $"ic-dmm-{Guid.NewGuid():N}.TapPlan");
        InstrumentSettingsScope.Run(() =>
        {
            InstrumentSettings.Current.Add(original);
            plan.ChildTestSteps.Add(new IdentityQueryStep { Instrument = original });
            plan.ChildTestSteps.Add(new DmmMeasureVoltageDcStep { Instrument = original, SampleCount = 2 });
            plan.Save(path);
            try
            {
                var xml = File.ReadAllText(path);
                Assert.Contains("InstrumentComponents.OpenTap.DmmInstrument", xml, StringComparison.Ordinal);
                Assert.Contains("InstrumentComponents.OpenTap.DmmMeasureVoltageDcStep", xml, StringComparison.Ordinal);
                Assert.Contains("InstrumentComponents.OpenTap.IdentityQueryStep", xml, StringComparison.Ordinal);

                var loaded = TestPlan.Load(path);
                var measure = Assert.IsType<DmmMeasureVoltageDcStep>(loaded.ChildTestSteps[1]);
                Assert.Equal(2, measure.SampleCount);
                Assert.Equal("Bench DMM", measure.Instrument.Name);
                Assert.Equal(original.VisaAddress, measure.Instrument.VisaAddress);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private sealed class ScriptedIo : IScpiIo
    {
        private readonly Dictionary<string, string> _queries;
        private bool _disposed;

        public ScriptedIo(params (string Command, string Response)[] queries)
        {
            _queries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [":MEAS:VOLT:DC?"] = "1.0",
            };
            foreach (var (command, response) in queries)
                _queries[command] = response;
        }

        public List<string> Writes { get; } = [];
        public List<string> Queries { get; } = [];
        public bool Disposed { get; private set; }
        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Writes.Add(command);
        }

        public string Query(string command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Queries.Add(command);
            return _queries.TryGetValue(command.Trim(), out var response) ? response : "";
        }

        public void Dispose()
        {
            _disposed = true;
            Disposed = true;
        }
    }

    private sealed class ThrowingIdnIo : IScpiIo
    {
        public bool Disposed { get; private set; }
        public TimeSpan IoTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public void Write(string command)
        {
        }

        public string Query(string command) => throw new InstrumentTimeoutException();

        public void Dispose() => Disposed = true;
    }
}

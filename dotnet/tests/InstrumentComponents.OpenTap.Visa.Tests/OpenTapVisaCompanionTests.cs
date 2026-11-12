using System.Text;
using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Errors;
using InstrumentComponents.Runtime;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;
using InstrumentComponents.Transport;
namespace InstrumentComponents.OpenTap.Visa.Tests;
public class OpenTapVisaCompanionTests
{
    [Theory][InlineData("opaque")][InlineData("hpib")]
    public void HostCanChooseEndpointSchemeWithoutDeviceSpecificProviderRules(string scheme)
    {
        var opener=new Opener();var provider=new OpenTapVisaDeviceProvider(connector=>[Factory(connector,scheme)],_=>new("Example","Device"),opener,endpointScheme:" "+scheme+" ");
        using var handle=provider.Open("GPIB0::3::INSTR",TimeSpan.FromSeconds(1),new());
        Assert.Empty(opener.Transport.Writes);Assert.Equal(1.25,new Dmm(handle).MeasureVoltageDc(null).Value);
        handle.Close();Assert.Single(opener.Transport.Writes);Assert.Equal(1,opener.Transport.Disposals);
    }
    [Theory][InlineData(0)][InlineData(-1)]
    public void NonpositiveDeadlineRejectsBeforeTransportAcquisition(int milliseconds)
    {
        var opener=new Opener();var provider=new OpenTapVisaDeviceProvider(c=>[Factory(c)],_=>new("Example","Device"),opener);
        Assert.Equal(DriverErrorKind.InvalidArgument,Assert.Throws<DriverException>(()=>provider.Open("GPIB0::3::INSTR",TimeSpan.FromMilliseconds(milliseconds),new())).Kind);
        Assert.Null(opener.Options);Assert.Empty(opener.Transport.Writes);
    }
    [Fact]public void ExplicitFactoriesOpenWithoutProbesAndUseOneOwnedSession()
    {
        var opener=new Opener();var provider=new OpenTapVisaDeviceProvider(connector=>[Factory(connector)],_=>new("Example","Device"),opener);
        using var handle=provider.Open("TCPIP0::192.0.2.1::inst0::INSTR",TimeSpan.FromSeconds(2),new());
        Assert.Empty(opener.Transport.Writes);Assert.Equal(1.25,new Dmm(handle).MeasureVoltageDc(null).Value);handle.Close();
        Assert.Equal(new[]{":READ?\n"},opener.Transport.Writes);Assert.Equal(1,opener.Transport.Disposals);Assert.False(opener.Options!.ResetOnConnect);Assert.Equal(0u,opener.Options.Retries);Assert.False(opener.Options.ReconnectOnFailure);
    }
    [Fact]public void ConfigurationFailureReleasesTransportAndIsTranslated()
    {
        var opener=new Opener();opener.Transport.FailConfigure=true;var connector=new VisaScpiConnector(opener,TimeSpan.FromSeconds(1));
        Assert.Equal(DriverErrorKind.Transport,Assert.Throws<DriverException>(()=>connector.Open(new(new(new("scpi","TCPIP0::192.0.2.1::inst0::INSTR"),new()),ConnectionOwnership.Owned))).Kind);Assert.Equal(1,opener.Transport.Disposals);
    }
    [Fact]public void TimeoutsAreNotRetriedAndRequireExplicitRecovery()
    {
        var opener=new Opener();opener.Transport.FailRead=true;var provider=new OpenTapVisaDeviceProvider(connector=>[Factory(connector)],_=>new("Example","Device"),opener);
        using var handle=provider.Open("TCPIP0::192.0.2.1::inst0::INSTR",TimeSpan.FromSeconds(1),new());
        Assert.Equal(DriverErrorKind.Timeout,Assert.Throws<DriverException>(()=>new Dmm(handle).MeasureVoltageDc(null)).Kind);
        Assert.Equal(DriverErrorKind.SessionNeedsRecovery,Assert.Throws<DriverException>(()=>new Dmm(handle).MeasureVoltageDc(null)).Kind);Assert.Single(opener.Transport.Writes);
    }
    [Fact]public void OversizeBlockFailsBeforeBodyReadWithTypedParseAndQuarantinesHandle()
    {
        var opener=new Opener();opener.Transport.OversizeHeader=true;
        var provider=new OpenTapVisaDeviceProvider(connector=>[Factory(connector)],_=>new("Example","Device"),opener);
        using var handle=provider.Open("TCPIP0::192.0.2.1::inst0::INSTR",TimeSpan.FromSeconds(1),new());
        Assert.Equal(ScpiNumbers.MaxResponseBytes,opener.Options!.MaxResponseBytes);
        var error=Assert.Throws<DriverException>(()=>new Dmm(handle).MeasureVoltageDc(null));
        Assert.Equal(DriverErrorKind.Parse,error.Kind);Assert.IsType<ParseException>(error.InnerException);
        Assert.Equal(1,opener.Transport.Reads);
        Assert.Equal(DriverErrorKind.SessionNeedsRecovery,Assert.Throws<DriverException>(()=>new Dmm(handle).MeasureVoltageDc(null)).Kind);
        Assert.Single(opener.Transport.Writes);
    }
    [Fact]public void RegistrationIsExplicitAndDoesNotOverwriteAnExistingProvider()
    {
        var previous=OpenTapDevices.Provider;var first=new OpenTapVisaDeviceProvider(c=>[Factory(c)]);var second=new OpenTapVisaDeviceProvider(c=>[Factory(c)]);
        try{OpenTapVisa.RegisterOverride(first);OpenTapVisa.Register(second);Assert.Same(first,OpenTapDevices.Provider);OpenTapVisa.RegisterOverride(second);Assert.Same(second,OpenTapDevices.Provider);}finally{OpenTapDevices.Provider=previous;}
    }
    private static IDriverFactory Factory(IScpiConnector connector,string scheme="scpi")=>new ScpiFactory(new("synthetic.visa",null,[],[new(CatalogOperation.DmmMeasureVoltageDc,[new(":READ?",ResponseRole.Value)])]),scheme,connector,[new ExactIdentityRule(scheme,"Example","Device")]);
    private sealed class Opener:ISessionOpener{
        public Wire Transport=new();public ConnectOptions? Options;
        public ITransport Open(ResourceAddress address,ConnectOptions options){Options=options;return Transport;}
    }
    private sealed class Wire:TransportBase,IDisposable{
        public List<string> Writes=[];public int Disposals;public bool FailConfigure,FailRead,OversizeHeader;public int Reads;
        public override void Configure(ConnectOptions options){if(FailConfigure)throw new TransportException("configuration failed");base.Configure(options);}
        public override void Write(ReadOnlySpan<byte> data)=>Writes.Add(Encoding.UTF8.GetString(data));
        public override int Read(Span<byte> buffer){Reads++;if(OversizeHeader){if(Reads>1)throw new InvalidOperationException("oversize body must not be read");var header="#71048577"u8;header.CopyTo(buffer);return header.Length;}if(FailRead)throw new InstrumentTimeoutException();var data="1.25\n"u8;data.CopyTo(buffer);return data.Length;}
        public override void Clear(){}public override void SetReadTimeout(TimeSpan timeout){}
        public void Dispose()=>Disposals++;
    }
}

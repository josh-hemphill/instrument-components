using InstrumentComponents.Catalog;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
namespace InstrumentComponents.Tests;
public class DeviceCatalogTests
{
    private static DeviceCandidate Candidate=>new(new("opaque","fixture"),new("Example","Device"));
    [Fact]public void PassiveEvidenceIsFrozenAndDuplicateEndpointsAreRejected()
    {
        var provider=new OpaqueDevice();var candidates=new List<DeviceCandidate>{Candidate};var catalog=new DeviceCatalog(new([provider]),candidates);candidates.Clear();
        Assert.Single(catalog.Candidates);Assert.Equal("synthetic.opaque",catalog.Device(Candidate.Endpoint).Select().DriverId);Assert.Equal(0,provider.Opens);
        Assert.Throws<DriverException>(()=>new DeviceCatalog(new([provider]),[Candidate,Candidate with{Endpoint=new("OPAQUE","fixture")}]));
        Assert.Throws<DriverException>(()=>catalog.Device(new("opaque","missing")));
    }
    [Fact]public async Task OpaqueClassViewsComposeThroughEveryFrontend()
    {
        for(var mode=0;mode<4;mode++){
            var provider=new OpaqueDevice();var catalog=new DeviceCatalog(new([provider]),[Candidate]);var device=catalog.Device(Candidate.Endpoint);
            if(mode==0){using var h=device.Open();Assert.Equal(1.25,new Dmm(h).MeasureVoltageDc(null).Value);Assert.Equal(1.25,new DcPowerSupply(h).ReadVoltage(1).Value);h.Close();}
            else if(mode==1){await using var h=await device.OpenNativeAsync();Assert.Equal(1.25,(await new AsyncDmm(h).MeasureVoltageDcAsync(null)).Value);Assert.Equal(1.25,(await new AsyncDcPowerSupply(h).ReadVoltageAsync(1)).Value);await h.CloseAsync();}
            else{await using var h=await device.OpenWorkerAsync();if(mode==2){Assert.Equal(1.25,(await new AsyncDmm(h).MeasureVoltageDcAsync(null)).Value);Assert.Equal(1.25,(await new AsyncDcPowerSupply(h).ReadVoltageAsync(1)).Value);}else{Assert.Equal(1.25,new Dmm(h).MeasureVoltageDc(null).Value);Assert.Equal(1.25,new DcPowerSupply(h).ReadVoltage(1).Value);}await h.CloseAsync();}
            Assert.Equal(1,provider.Opens);Assert.Equal(1,provider.Closes);Assert.Equal(2,provider.Calls.Count);
        }
    }
    [Fact]public void RequiredCapabilitiesFailBeforeOpening()
    {
        var provider=new OpaqueDevice();provider.Supported.Clear();provider.Supported.Add(Operation.CatalogDmmMeasureVoltageDc);
        var catalog=new DeviceCatalog(new([provider]),[Candidate]);
        Assert.Equal(DriverErrorKind.UnsupportedOperation,Assert.Throws<DriverException>(()=>catalog.Device(Candidate.Endpoint).Open(required:new HashSet<Operation>{Operation.CatalogDcPowerSupplyReadVoltage})).Kind);Assert.Equal(0,provider.Opens);
    }
}

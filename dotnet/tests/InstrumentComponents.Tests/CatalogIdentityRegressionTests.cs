using System.Text.Json;
using InstrumentComponents.Catalog;
using InstrumentComponents.Drivers;
using InstrumentComponents.Runtime;

namespace InstrumentComponents.Tests;
public class CatalogIdentityRegressionTests
{
    [Fact]
    public void SharedCatalogKeysUseTrimmedAsciiCaseOnly()
    {
        using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"catalog-identity-cases.json")));
        foreach(var row in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            DeviceCandidate Candidate(string key)=>new(new(key,"fixture"),new());
            var left=Candidate(row.GetProperty("left").GetString()!);var right=Candidate(row.GetProperty("right").GetString()!);
            var single=new DeviceCatalog(new DriverRegistry([]),[left]);
            if(row.GetProperty("duplicate").GetBoolean())
            {
                Assert.Equal(DriverErrorKind.InvalidArgument,Assert.Throws<DriverException>(()=>new DeviceCatalog(new DriverRegistry([]),[left,right])).Kind);
                Assert.Equal(left,single.Device(right.Endpoint).Candidate);
            }
            else
            {
                var both=new DeviceCatalog(new DriverRegistry([]),[left,right]);Assert.Equal(2,both.Candidates.Count);
                Assert.Equal(right,both.Device(right.Endpoint).Candidate);
                Assert.Equal(DriverErrorKind.DriverNotFound,Assert.Throws<DriverException>(()=>single.Device(right.Endpoint)).Kind);
            }
        }
    }
}

using InstrumentComponents.Catalog;
using InstrumentComponents.Drivers;
using InstrumentComponents.Runtime;
namespace InstrumentComponents.Discovery;
/// <summary>A host supplies passive evidence from any protocol; no automatic IDN or SCPI probing.</summary>
public static class Discovery
{
    public static DeviceCatalog FromCandidates(IEnumerable<IDriverFactory> factories,IEnumerable<DeviceCandidate> candidates)=>new(new DriverRegistry(factories),candidates);
}

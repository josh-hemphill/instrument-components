using InstrumentComponents.Visa;
namespace InstrumentComponents.Visa.Tests;
public class HardwareTests {
 [HardwareFact][Trait("Category","Hardware")]
 public void EnumerateExplicitVisaResource(){Assert.True(HardwareResource.TryFromEnv(out var resource,out var error),error);var resources=VisaDiscovery.Enumerator().List("?*INSTR").ToList();Assert.Contains(resources,r=>r.Address.Raw==resource);}
}

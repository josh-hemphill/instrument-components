using InstrumentComponents.Enumerator;
namespace InstrumentComponents.Visa;
/// <summary>Opt-in VISA enumeration. The host supplies identity evidence and registered factories.</summary>
public static class VisaDiscovery
{
    public static IResourceEnumerator Enumerator()=>new VisaEnumerator();
}

using InstrumentComponents.Runtime;
using OpenTap;
namespace InstrumentComponents.OpenTap;
/// <summary>The application opens any protocol using explicitly composed factories.
/// Configure inventories and required capabilities before returning the owned handle.</summary>
public interface IOpenTapDeviceProvider
{
    DeviceHandle Open(string address,TimeSpan timeout,DriverSelection selection);
}
/// <summary>Session-local host binding; no process-wide implementation registry.</summary>
public static class OpenTapDevices
{
    private static readonly SessionLocal<IOpenTapDeviceProvider?> Binding=new(null,autoDispose:false);
    public static IOpenTapDeviceProvider? Provider{get=>Binding.Value;set=>Binding.Value=value;}
}

using InstrumentComponents.Drivers;
using InstrumentComponents.Runtime;
namespace InstrumentComponents.Catalog;

/// <summary>Immutable passive candidates and explicit factory registration; no probing.</summary>
public sealed class DeviceCatalog
{
    private readonly DriverRegistry _registry;
    private static string Scheme(string value)=>string.Concat(value.Trim().Select(c=>c is >= 'A' and <= 'Z'?(char)(c+32):c));
    public IReadOnlyList<DeviceCandidate> Candidates { get; }
    public DeviceCatalog(DriverRegistry registry,IEnumerable<DeviceCandidate> candidates)
    {
        _registry=registry;var snapshot=candidates.ToArray();
        if(snapshot.Select(c=>(Scheme(c.Endpoint.Scheme),c.Endpoint.Address)).Distinct().Count()!=snapshot.Length)
            throw new DriverException(DriverErrorKind.InvalidArgument,"Duplicate catalog endpoint");
        Candidates=Array.AsReadOnly(snapshot);
    }
    public DeviceRef Device(Endpoint endpoint)
    {
        var candidate=Candidates.SingleOrDefault(c=>Scheme(c.Endpoint.Scheme)==Scheme(endpoint.Scheme)&&c.Endpoint.Address==endpoint.Address)
            ?? throw new DriverException(DriverErrorKind.DriverNotFound,"Endpoint absent from catalog");
        return new DeviceRef(_registry,candidate);
    }
}
/// <summary>Open one handle and share it between typed class views.</summary>
public sealed class DeviceRef(DriverRegistry registry,DeviceCandidate candidate)
{
    public DeviceCandidate Candidate { get; }=candidate;
    public MatchEvidence Select(DriverSelection? selection=null)=>registry.Select(Candidate,selection);
    public DeviceHandle Open(DriverSelection? selection=null,IReadOnlySet<Operation>? required=null,ConnectionOwnership ownership=ConnectionOwnership.Owned)=>registry.Open(Candidate,selection,required,ownership:ownership);
    public Task<NativeDeviceHandle> OpenNativeAsync(DriverSelection? selection=null,IReadOnlySet<Operation>? required=null,ConnectionOwnership ownership=ConnectionOwnership.Owned,CancellationToken cancellationToken=default)=>registry.OpenNativeAsync(Candidate,selection,required,ownership,cancellationToken);
    public Task<WorkerDeviceHandle> OpenWorkerAsync(DriverSelection? selection=null,IReadOnlySet<Operation>? required=null,ConnectionOwnership ownership=ConnectionOwnership.Owned,CancellationToken cancellationToken=default)=>registry.OpenWorkerAsync(Candidate,selection,required,ownership,cancellationToken);
}

using System.Collections.Frozen;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using SenseMode = InstrumentComponents.Drivers.SenseMode;
namespace InstrumentComponents.Scpi;

/// <summary>Complete message bodies without terminators. Host owns framing and response bounds
/// before allocation. Failed/partial queries require explicit clean-boundary recovery.
/// Native cancellation must end all host access before returning. Dispose is resource-only.</summary>
public interface IMessageIo : IDisposable
{
    void Write(string message);
    string Query(string message);
    void Recover() => throw new DriverException(DriverErrorKind.SessionNeedsRecovery, "Host must resynchronize message boundary");
}
public interface IAsyncMessageIo : IDisposable
{
    ValueTask WriteAsync(string message, CancellationToken cancellationToken = default);
    ValueTask<string> QueryAsync(string message, CancellationToken cancellationToken = default);
    ValueTask RecoverAsync(CancellationToken cancellationToken = default) => ValueTask.FromException(new DriverException(DriverErrorKind.SessionNeedsRecovery, "Host must resynchronize message boundary"));
}
public interface IScpiConnector { IMessageIo Open(DriverOpenContext context); }
public interface IAsyncScpiConnector { ValueTask<IAsyncMessageIo> OpenAsync(DriverOpenContext context, CancellationToken cancellationToken = default); }
/// <summary>IEEE Std 488.2 common commands. Nothing sends these implicitly.</summary>
public static class Ieee4882 { public const string Identity = "*IDN?", Reset = "*RST", Completion = "*OPC?", Trigger = "*TRG"; }
internal sealed class EndpointProfiles
{
    private readonly ScpiProfile _default;
    private readonly FrozenDictionary<(string Scheme,string Address),ScpiProfile> _routes;
    public IReadOnlySet<Operation> Operations {get;}
    private static (string,string) Key(Endpoint e)=>(string.Concat(e.Scheme.Trim().Select(c=>c is >= 'A' and <= 'Z'?(char)(c+32):c)),e.Address);
    public EndpointProfiles(ScpiProfile profile,IEnumerable<KeyValuePair<Endpoint,ScpiProfile>>? endpoints)
    {
        _default=profile;var routes=new Dictionary<(string,string),ScpiProfile>();
        foreach(var (endpoint,value) in endpoints ?? []){
            if(routes.Count>=4096 || string.IsNullOrWhiteSpace(endpoint.Scheme) || string.IsNullOrEmpty(endpoint.Address) || value.Id!=profile.Id || !routes.TryAdd(Key(endpoint),value))throw new DriverException(DriverErrorKind.DriverContractViolation,"Invalid, duplicate or noncanonical endpoint profile");
        }
        _routes=routes.ToFrozenDictionary();Operations=routes.Values.SelectMany(p=>p.Operations).Concat(profile.Operations).ToFrozenSet();
    }
    public ScpiProfile Select(DriverOpenContext context)
    {
        var profile=_routes.GetValueOrDefault(Key(context.Candidate.Endpoint),_default);
        if(context.RequiredOperations is {} required && !required.IsSubsetOf(profile.Operations))throw new DriverException(DriverErrorKind.UnsupportedOperation,"Required capability unavailable for this endpoint");
        return profile;
    }
}
public sealed class ScpiFactory : IDriverFactory
{
    private readonly EndpointProfiles _profiles;private readonly IScpiConnector _connector;private readonly IAsyncScpiConnector? _native;
    public DriverDescriptor Descriptor {get;}
    public IReadOnlySet<ExecutionMode> ExecutionModes {get;}
    public ScpiFactory(ScpiProfile profile,string scheme,IScpiConnector connector,IEnumerable<MatchRule>? rules=null,IAsyncScpiConnector? nativeConnector=null,IEnumerable<KeyValuePair<Endpoint,ScpiProfile>>? endpointProfiles=null)
    {
        _profiles=new(profile,endpointProfiles);_connector=connector;_native=nativeConnector;
        ExecutionModes=(nativeConnector is null?new[]{ExecutionMode.Sync,ExecutionMode.WorkerAsync}:new[]{ExecutionMode.Sync,ExecutionMode.WorkerAsync,ExecutionMode.NativeAsync}).ToFrozenSet();
        Descriptor=new(profile.Id,DriverContract.Major,rules ?? [new StandardScpiRule(scheme)],_profiles.Operations);
    }
    public async ValueTask<IAsyncDeviceBackend> OpenNativeAsync(DriverOpenContext context,CancellationToken cancellationToken=default)
    {
        if(_native is null)throw new DriverException(DriverErrorKind.UnsupportedExecutionMode,"Native connector not configured");
        var profile=_profiles.Select(context);cancellationToken.ThrowIfCancellationRequested();var io=await _native.OpenAsync(context,cancellationToken).ConfigureAwait(false);
        try{cancellationToken.ThrowIfCancellationRequested();return new AsyncScpiDevice(profile,io,context.Ownership);}catch{if(context.Ownership==ConnectionOwnership.Owned)io.Dispose();throw;}
    }
    public IDeviceBackend Open(DriverOpenContext context){var profile=_profiles.Select(context);return new ScpiDevice(profile,_connector.Open(context),context.Ownership);}
}
public sealed class AsyncScpiFactory : IDriverFactory
{
    private readonly EndpointProfiles _profiles;private readonly IAsyncScpiConnector _connector;
    public DriverDescriptor Descriptor{get;}
    public IReadOnlySet<ExecutionMode> ExecutionModes=>new HashSet<ExecutionMode>{ExecutionMode.NativeAsync}.ToFrozenSet();
    public AsyncScpiFactory(ScpiProfile profile,string scheme,IAsyncScpiConnector connector,IEnumerable<MatchRule>? rules=null,IEnumerable<KeyValuePair<Endpoint,ScpiProfile>>? endpointProfiles=null)
    {_profiles=new(profile,endpointProfiles);_connector=connector;Descriptor=new(profile.Id,DriverContract.Major,rules ?? [new StandardScpiRule(scheme)],_profiles.Operations);}
    public async ValueTask<IAsyncDeviceBackend> OpenNativeAsync(DriverOpenContext context,CancellationToken cancellationToken=default)
    {
        var profile=_profiles.Select(context);cancellationToken.ThrowIfCancellationRequested();var io=await _connector.OpenAsync(context,cancellationToken).ConfigureAwait(false);
        try{cancellationToken.ThrowIfCancellationRequested();return new AsyncScpiDevice(profile,io,context.Ownership);}catch{if(context.Ownership==ConnectionOwnership.Owned)io.Dispose();throw;}
    }
}
internal sealed class ScpiDevice(ScpiProfile profile, IMessageIo io, ConnectionOwnership ownership)
    : IDeviceBackend, ICatalogBackend, IDmmMeasurement, IPsuOutput, IRemoteSense, IDeviceReset, ISafeShutdown
{
    private readonly Engine _engine = new(profile); private bool _closed;
    public IReadOnlySet<Operation> Operations => profile.Operations;
    ICatalogBackend IDeviceBackend.Catalog => this;
    IDmmMeasurement IDeviceBackend.Dmm => this;
    IPsuOutput IDeviceBackend.Psu => this;
    IRemoteSense IDeviceBackend.RemoteSense => this;
    IDeviceReset IDeviceBackend.Reset => this;
    ISafeShutdown IDeviceBackend.Shutdown => this;
    public CatalogReply Invoke(CatalogRequest request)
    {
        if (_closed) throw new DriverException(DriverErrorKind.SessionClosed, "SCPI resource closed");
        var prepared = _engine.Prepare(request); string? value = null, preamble = null; var errors = new List<Exception>();
        foreach (var step in prepared.Steps)
        {
            try
            {
                if (step.Response == ResponseRole.Write) io.Write(step.Command);
                else { var response = ScpiNumbers.Checked(io.Query(step.Command)); switch (step.Response) { case ResponseRole.Value: value = response; break; case ResponseRole.Preamble: preamble = response; break; case ResponseRole.Completion: ScpiNumbers.Completion(response); break; } }
            }
            catch (Exception failure) { _engine.Invalidate(); if (request.Operation != CatalogOperation.Shutdown) throw; errors.Add(failure); }
        }
        if (errors.Count != 0) throw new DriverException(DriverErrorKind.Shutdown, "Output shutdown incomplete", new AggregateException(errors));
        try { return _engine.Finish(request, prepared, value, preamble); } catch { _engine.Invalidate(); throw; }
    }
    private static Measurement Measure(CatalogReply r) { var q = r.IntoQuantity(); return new(q.Value, q.Status); }
    public Measurement MeasureVoltageDc(double? range = null) => Measure(Invoke(new DmmMeasureVoltageDcRequest(range)));
    public void SetVoltage(uint channel, double volts) => Invoke(new DcPowerSupplySetVoltageRequest(channel, volts)).IntoUnit();
    public Measurement ReadVoltage(uint channel) => Measure(Invoke(new DcPowerSupplyReadVoltageRequest(channel)));
    public void OutputEnable(uint channel, bool enabled) => Invoke(new DcPowerSupplyOutputEnableRequest(channel, enabled)).IntoUnit();
    public void SetMode(uint channel, SenseMode mode) => Invoke(new DcPowerSupplySetSenseModeRequest(channel, mode switch { SenseMode.Internal => Drivers.Catalog.SenseMode.Internal, SenseMode.External => Drivers.Catalog.SenseMode.External, _ => throw ContractValidation.Invalid("Invalid sense mode") })).IntoUnit();
    public void Reset() => Invoke(new ResetRequest()).IntoUnit();
    public void Shutdown() => Invoke(new ShutdownRequest()).IntoUnit();
    public void Recover() { io.Recover(); _engine.Invalidate(); }
    public void Dispose() { if (_closed) return; _closed = true; if (ownership == ConnectionOwnership.Owned) io.Dispose(); }
}
internal sealed class AsyncScpiDevice(ScpiProfile profile, IAsyncMessageIo io, ConnectionOwnership ownership)
    : IAsyncDeviceBackend, IAsyncCatalogBackend, IAsyncDmmMeasurement, IAsyncPsuOutput, IAsyncRemoteSense, IAsyncReset, IAsyncSafeShutdown
{
    private readonly Engine _engine = new(profile); private bool _closed;
    public IReadOnlySet<Operation> Operations => profile.Operations;
    IAsyncCatalogBackend IAsyncDeviceBackend.Catalog => this;
    IAsyncDmmMeasurement IAsyncDeviceBackend.Dmm => this;
    IAsyncPsuOutput IAsyncDeviceBackend.Psu => this;
    IAsyncRemoteSense IAsyncDeviceBackend.RemoteSense => this;
    IAsyncReset IAsyncDeviceBackend.Reset => this;
    IAsyncSafeShutdown IAsyncDeviceBackend.Shutdown => this;
    public async ValueTask<CatalogReply> InvokeAsync(CatalogRequest request, CancellationToken cancellationToken = default)
    {
        if (_closed) throw new DriverException(DriverErrorKind.SessionClosed, "SCPI resource closed");
        var prepared = _engine.Prepare(request); string? value = null, preamble = null; var errors = new List<Exception>();
        foreach (var step in prepared.Steps)
        {
            try
            {
                if (step.Response == ResponseRole.Write) await io.WriteAsync(step.Command, cancellationToken).ConfigureAwait(false);
                else { var response = ScpiNumbers.Checked(await io.QueryAsync(step.Command, cancellationToken).ConfigureAwait(false)); switch (step.Response) { case ResponseRole.Value: value = response; break; case ResponseRole.Preamble: preamble = response; break; case ResponseRole.Completion: ScpiNumbers.Completion(response); break; } }
            }
            catch (Exception failure) { _engine.Invalidate(); if (request.Operation != CatalogOperation.Shutdown) throw; errors.Add(failure); }
        }
        if (errors.Count != 0) throw new DriverException(DriverErrorKind.Shutdown, "Output shutdown incomplete", new AggregateException(errors));
        try { return _engine.Finish(request, prepared, value, preamble); } catch { _engine.Invalidate(); throw; }
    }
    private static Measurement Measure(CatalogReply r) { var q = r.IntoQuantity(); return new(q.Value, q.Status); }
    public async ValueTask<Measurement> MeasureVoltageDcAsync(double? range = null, CancellationToken cancellationToken = default) => Measure(await InvokeAsync(new DmmMeasureVoltageDcRequest(range), cancellationToken).ConfigureAwait(false));
    public async ValueTask SetVoltageAsync(uint channel, double volts, CancellationToken cancellationToken = default) => (await InvokeAsync(new DcPowerSupplySetVoltageRequest(channel, volts), cancellationToken).ConfigureAwait(false)).IntoUnit();
    public async ValueTask<Measurement> ReadVoltageAsync(uint channel, CancellationToken cancellationToken = default) => Measure(await InvokeAsync(new DcPowerSupplyReadVoltageRequest(channel), cancellationToken).ConfigureAwait(false));
    public async ValueTask OutputEnableAsync(uint channel, bool enabled, CancellationToken cancellationToken = default) => (await InvokeAsync(new DcPowerSupplyOutputEnableRequest(channel, enabled), cancellationToken).ConfigureAwait(false)).IntoUnit();
    public async ValueTask SetModeAsync(uint channel, SenseMode mode, CancellationToken cancellationToken = default) => (await InvokeAsync(new DcPowerSupplySetSenseModeRequest(channel, mode switch { SenseMode.Internal => Drivers.Catalog.SenseMode.Internal, SenseMode.External => Drivers.Catalog.SenseMode.External, _ => throw ContractValidation.Invalid("Invalid sense mode") }), cancellationToken).ConfigureAwait(false)).IntoUnit();
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default) => (await InvokeAsync(new ResetRequest(), cancellationToken).ConfigureAwait(false)).IntoUnit();
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default) => (await InvokeAsync(new ShutdownRequest(), cancellationToken).ConfigureAwait(false)).IntoUnit();
    public async ValueTask RecoverAsync(CancellationToken cancellationToken = default) { await io.RecoverAsync(cancellationToken).ConfigureAwait(false); _engine.Invalidate(); }
    public void Dispose() { if (_closed) return; _closed = true; if (ownership == ConnectionOwnership.Owned) io.Dispose(); }
}

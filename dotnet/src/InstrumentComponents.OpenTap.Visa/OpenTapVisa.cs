using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Drivers;
using InstrumentComponents.Errors;
using InstrumentComponents.Runtime;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;
using InstrumentComponents.Visa;

namespace InstrumentComponents.OpenTap.Visa;

/// <summary>Optional standard VISA connector; no model matching, driver data or probes.</summary>
public sealed class VisaScpiConnector(ISessionOpener opener,TimeSpan timeout,int maxResponseBytes=ScpiNumbers.MaxResponseBytes,string terminator="\n",bool allowBinaryBlocks=true) : IScpiConnector
{
    public IMessageIo Open(DriverOpenContext context)
    {
        // This adapter allocates a new session; it has no external borrowed owner.
        if(context.Ownership!=ConnectionOwnership.Owned)throw new DriverException(DriverErrorKind.InvalidArgument,"The allocating VISA connector requires Owned; use an externally owned connector for borrowed sessions");
        if(timeout<=TimeSpan.Zero)throw new DriverException(DriverErrorKind.InvalidArgument,"A positive finite operation timeout is required");
        if(maxResponseBytes<0)throw new DriverException(DriverErrorKind.InvalidArgument,"Response allocation bound cannot be negative");
        try {
        var address=ResourceAddress.Parse(context.Candidate.Endpoint.Address);
        var options=new ConnectOptions{Terminator=terminator,MaxResponseBytes=maxResponseBytes,OpenTimeout=timeout,ReadTimeout=timeout,WriteTimeout=timeout,PerOpTimeout=timeout,ResetOnConnect=false,Retries=0,ReconnectOnFailure=false};
        var transport=opener.Open(address,options);
        // ScpiSession takes ownership and disposes transport when configuration fails.
        return new Message(new ScpiSession(transport,options),allowBinaryBlocks);
        }catch(InstrumentException error){throw Convert(error);}
    }
    private static DriverException Convert(InstrumentException error){
        var kind=error is InvalidAddressException?DriverErrorKind.InvalidArgument:error is ParseException?DriverErrorKind.Parse:DriverErrorKind.Transport;
        for(Exception? current=error;current is not null;current=current.InnerException)if(current is InstrumentTimeoutException or TimeoutException)kind=DriverErrorKind.Timeout;
        return new DriverException(kind,error.Message,error);
    }
    private sealed class Message(ScpiSession session,bool allowBinaryBlocks):IMessageIo
    {
        private static T Translate<T>(Func<T> call){try{return call();}catch(InstrumentException error){throw Convert(error);}}
        public void Write(string message)=>Translate(()=>{session.Write(message);return true;});
        public string Query(string message)=>Translate(()=>session.QueryBody(message,allowBinaryBlocks));
        // Byte/session recovery requires explicit host resynchronization; no flush or reset inferred.
        public void Dispose()=>session.Dispose();
    }
}
/// <summary>Application supplies static driver registrations and passive identity evidence.
/// Native async is available through separately supplied native connectors; this adapter is sync/worker.</summary>
public sealed class OpenTapVisaDeviceProvider : IOpenTapDeviceProvider
{
    private readonly Func<IScpiConnector,IEnumerable<IDriverFactory>> _factories;
    private readonly Func<string,Drivers.DeviceIdentity> _identity;
    private readonly ISessionOpener _opener;
    private readonly string _endpointScheme;
    private readonly int _maxResponseBytes;
    private readonly string _terminator;
    private readonly bool _allowBinaryBlocks;
    public OpenTapVisaDeviceProvider(Func<IScpiConnector,IEnumerable<IDriverFactory>> factories,
        Func<string,Drivers.DeviceIdentity>? identity=null,ISessionOpener? opener=null,string endpointScheme="scpi",int maxResponseBytes=ScpiNumbers.MaxResponseBytes,string terminator="\n",bool allowBinaryBlocks=true)
    {
        if(string.IsNullOrWhiteSpace(endpointScheme))throw new DriverException(DriverErrorKind.InvalidArgument,"Endpoint scheme is required");
        if(maxResponseBytes<0)throw new DriverException(DriverErrorKind.InvalidArgument,"Response allocation bound cannot be negative");
        _factories=factories;_identity=identity ?? (_=>new());_opener=opener ?? new VisaSessionOpener();_endpointScheme=endpointScheme.Trim();_maxResponseBytes=maxResponseBytes;_terminator=terminator;_allowBinaryBlocks=allowBinaryBlocks;
    }
    public DeviceHandle Open(string address,TimeSpan timeout,DriverSelection selection)
    {
        var connector=new VisaScpiConnector(_opener,timeout,_maxResponseBytes,_terminator,_allowBinaryBlocks);
        var registry=new DriverRegistry(_factories(connector));
        return registry.Open(new(new(_endpointScheme,address),_identity(address)),selection);
    }
}
public static class OpenTapVisa
{
    public static void Register(IOpenTapDeviceProvider provider)=>OpenTapDevices.Provider ??= provider;
    public static void RegisterOverride(IOpenTapDeviceProvider provider)=>OpenTapDevices.Provider=provider;
}

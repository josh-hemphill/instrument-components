using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
using InstrumentComponents.Scpi;
using InstrumentComponents.TestKit;

namespace InstrumentComponents.Runtime.Tests;

public class OwnershipRegressionTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public async Task UndefinedOwnershipNeverInvokesAnyProvider(int raw)
    {
        var factory=new SentinelFactory();var registry=new DriverRegistry([factory]);
        var candidate=new DeviceCandidate(new("opaque","fixture"),new("Example","Fixture"));
        var ownership=(ConnectionOwnership)raw;
        Assert.Equal(DriverErrorKind.InvalidArgument,Assert.Throws<DriverException>(()=>registry.Open(candidate,ownership:ownership)).Kind);
        Assert.Equal(DriverErrorKind.InvalidArgument,(await Assert.ThrowsAsync<DriverException>(()=>registry.OpenNativeAsync(candidate,ownership:ownership))).Kind);
        Assert.Equal(DriverErrorKind.InvalidArgument,(await Assert.ThrowsAsync<DriverException>(()=>registry.OpenWorkerAsync(candidate,ownership:ownership))).Kind);
        Assert.Equal(0,factory.Opens);

        var script=new Script([]);
        var profile=new ScpiProfile("synthetic.ownership",null,[],[new(CatalogOperation.DmmMeasureVoltageDc,[new(":READ?",ResponseRole.Value)])]);
        var scpi=new ScpiFactory(profile,"scpi",script,nativeConnector:script);
        var context=new DriverOpenContext(new(new("scpi","fixture"),new()),ownership,new HashSet<Operation>());
        Assert.Equal(DriverErrorKind.InvalidArgument,Assert.Throws<DriverException>(()=>scpi.Open(context)).Kind);
        Assert.Equal(DriverErrorKind.InvalidArgument,(await Assert.ThrowsAsync<DriverException>(async()=>await scpi.OpenNativeAsync(context))).Kind);
        var native=new AsyncScpiFactory(profile,"scpi",script);
        Assert.Equal(DriverErrorKind.InvalidArgument,(await Assert.ThrowsAsync<DriverException>(async()=>await native.OpenNativeAsync(context))).Kind);
        Assert.Equal(0,script.Opens);
    }

    private sealed class SentinelFactory:IDriverFactory
    {
        public int Opens;
        public DriverDescriptor Descriptor=>new("synthetic.ownership",1,[new ExactIdentityRule("opaque","Example","Fixture")],new HashSet<Operation>());
        public IReadOnlySet<ExecutionMode> ExecutionModes=>new HashSet<ExecutionMode>{ExecutionMode.Sync,ExecutionMode.NativeAsync,ExecutionMode.WorkerAsync};
        public IDeviceBackend Open(DriverOpenContext context){Interlocked.Increment(ref Opens);throw new InvalidOperationException("Provider must not be invoked");}
        public ValueTask<IAsyncDeviceBackend> OpenNativeAsync(DriverOpenContext context,CancellationToken cancellationToken=default){Interlocked.Increment(ref Opens);throw new InvalidOperationException("Provider must not be invoked");}
    }
}

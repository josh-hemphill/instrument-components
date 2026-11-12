using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
using OpenTap;
namespace InstrumentComponents.OpenTap.Tests;
public class DeviceBoundaryTests
{
    [Fact]public void ClassViewsShareOneOpaqueHandleWithoutIdentityProbe()
    {
        var device=new OpaqueDevice();var handle=device.OpenHandle();var resource=new DmmInstrument();resource.AttachDevice(handle);
        Assert.Throws<InvalidOperationException>(()=>resource.Dmm.Read());resource.Open();
        Assert.Empty(device.Calls);Assert.Equal(1.25,resource.Dmm.MeasureVoltageDc(null).Value);Assert.Equal(1.25,resource.AsDcPowerSupply().ReadVoltage(1).Value);
        Assert.Equal(1,device.Opens);Assert.Equal(2,device.Calls.Count);resource.Close();Assert.Equal(0,device.Closes);handle.Close();Assert.Equal(1,device.Closes);
    }
    [Fact]public void ExactCapabilitiesAreCheckedAndNeverInferredFromPrimaryKind()
    {
        var device=new OpaqueDevice();device.Supported.Clear();device.Supported.Add(Operation.CatalogDmmRead);
        var resource=new DmmInstrument();resource.AttachDevice(device.OpenHandle(),true);resource.Open();
        Assert.Equal(DriverErrorKind.UnsupportedOperation,Assert.Throws<DriverException>(()=>resource.AsDcPowerSupply()).Kind);
        Assert.Equal(DriverErrorKind.UnsupportedOperation,Assert.Throws<DriverException>(()=>resource.Dmm.MeasureVoltageDc(null)).Kind);Assert.Empty(device.Calls);resource.Close();Assert.Equal(1,device.Closes);
    }
    [Fact]public void FailedOpenCleansOwnedHandleAndPreservesCleanupFailure()
    {
        var device=new OpaqueDevice{FailClose=true};device.Supported.Clear();device.Supported.Add(Operation.CatalogDmmRead);
        var resource=new DcPowerSupplyInstrument();resource.AttachDevice(device.OpenHandle(),true);
        var error=Assert.Throws<AggregateException>(()=>resource.Open());Assert.Equal(2,error.InnerExceptions.Count);Assert.False(resource.IsConnected);Assert.Equal(1,device.Closes);
    }
    [Fact]public void SafeShutdownCallsActionThenSeparateResetAndAbortsResetOnActionFailure()
    {
        foreach(var fail in new[]{false,true}){
            var device=new OpaqueDevice{FailShutdown=fail};var resource=new DmmInstrument();resource.AttachDevice(device.OpenHandle(),true);resource.Open();
            var step=new SafeShutdownStep{Instrument=resource};
            if(fail)Assert.Equal(DriverErrorKind.Shutdown,Assert.Throws<DriverException>(()=>step.Run()).Kind);else step.Run();
            Assert.Equal(fail?new[]{CatalogOperation.Shutdown}:new[]{CatalogOperation.Shutdown,CatalogOperation.Reset},device.Calls.Select(c=>c.Operation));resource.Close();
        }
    }
    [Fact]public void ProviderReceivesSelectionAndClampedTimeoutAndFailedOpenCanRetry()
    {
        var previous=OpenTapDevices.Provider;var provider=new Provider();OpenTapDevices.Provider=provider;
        try{
            var resource=new DmmInstrument{VisaAddress="opaque://bench",DriverId="synthetic.opaque",IoTimeoutMilliseconds=int.MaxValue};provider.Fail=true;
            Assert.Throws<InvalidOperationException>(()=>resource.Open());Assert.False(resource.IsConnected);
            provider.Fail=false;resource.Open();Assert.Equal("opaque://bench",provider.Address);Assert.Equal("synthetic.opaque",provider.Selection!.DriverId);Assert.Equal(TimeSpan.FromMilliseconds(DeviceInstrument.MaxIoTimeoutMilliseconds),provider.Timeout);
            using var alternative=new OpaqueDevice().OpenHandle();Assert.Throws<InvalidOperationException>(()=>resource.AttachDevice(alternative));resource.Close();Assert.Equal(1,provider.Device.Closes);
        }finally{OpenTapDevices.Provider=previous;}
    }
    private sealed class Provider:IOpenTapDeviceProvider{
        public OpaqueDevice Device=new();public bool Fail;public string? Address;public TimeSpan Timeout;public DriverSelection? Selection;
        public DeviceHandle Open(string address,TimeSpan timeout,DriverSelection selection){Address=address;Timeout=timeout;Selection=selection;if(Fail)throw new InvalidOperationException("injected provider failure");return Device.OpenHandle();}
    }
}

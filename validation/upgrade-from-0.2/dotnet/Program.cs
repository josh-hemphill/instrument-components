using InstrumentComponents.Catalog;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Runtime;
using InstrumentComponents.TestKit;
using Vendor = InstrumentComponents.Drivers.Keysight.Drivers;

// TestKit is a development fixture. This example performs no hardware IO.
for (var mode = 0; mode < 4; mode++)
{
    var script = new Script([ScriptStep.Query(":MEAS:VOLT:DC?", "1.25"), ScriptStep.Close()]);
    var candidate = new DeviceCandidate(new("scpi", "preview-mock"), new("Keysight Technologies", "34401A"));
    var registry = new DriverRegistry(Vendor.Factories(script, nativeConnector: script));
    Require(registry.Select(candidate).DriverId == "keysight.34401a", "Passive selection");
    Require(script.Opens == 0, "Selection performed IO");
    var catalog = new DeviceCatalog(registry, [candidate]);
    var device = catalog.Device(candidate.Endpoint);
    try
    {
        device.Open(required: new HashSet<Operation> { Operation.CatalogDcPowerSupplyReadVoltage });
        throw new InvalidOperationException("Unsupported requirement accepted");
    }
    catch (DriverException error) when (error.Kind == DriverErrorKind.UnsupportedOperation) { }
    Require(script.Opens == 0, "Required capability rejection performed IO");
    var required = new HashSet<Operation> { Operation.CatalogDmmMeasureVoltageDc };
    Quantity value;
    if (mode == 0)
    {
        using var handle = device.Open(required: required);
        value = new Dmm(handle).MeasureVoltageDc(null);
        handle.Close();
        try { new Dmm(handle).MeasureVoltageDc(null); throw new InvalidOperationException("Closed handle accepted"); }
        catch (DriverException error) when (error.Kind == DriverErrorKind.SessionClosed) { }
    }
    else if (mode == 1)
    {
        await using var handle = await device.OpenNativeAsync(required: required);
        value = await new AsyncDmm(handle).MeasureVoltageDcAsync(null);
        await handle.CloseAsync();
    }
    else
    {
        await using var handle = await device.OpenWorkerAsync(required: required);
        value = mode == 2 ? await new AsyncDmm(handle).MeasureVoltageDcAsync(null) : new Dmm(handle).MeasureVoltageDc(null);
        await handle.CloseAsync();
        script.AssertThreadAffinity();
    }
    Require(value.Value == 1.25 && value.Unit == Unit.Volt, "Quantity mismatch");
    script.AssertComplete();
    Require(script.Opens == 1 && script.Closes == 1, "Connection lifetime mismatch");
}
Console.WriteLine("PASS public C# four frontends: 1.25 Volt, passive matching, pre-open requirements, checked close; no hardware");
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

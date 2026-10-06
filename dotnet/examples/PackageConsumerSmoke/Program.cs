using InstrumentComponents.Catalog;
using InstrumentComponents.Kind;
using InstrumentComponents.Mock;
using InstrumentComponents.Visa;

if (Environment.Version.Major != 10)
    throw new InvalidOperationException($"Expected .NET 10, got {Environment.Version}.");

// Resolve the optional VISA assembly without opening a native resource manager.
Console.WriteLine($".NET {Environment.Version}; {typeof(VisaSessionOpener).Assembly.GetName().Name}");
var fixture = ScriptedFixture.Builder()
    .Idn("Acme", "DMM", "SN", "1")
    .Kinds(InstrumentKind.Dmm)
    .OnQuery(":MEAS:VOLT:DC?", "3.3")
    .Build();
var catalog = DeviceCatalog.FromFixture("mock://consumer", fixture);
var dmm = catalog.OpenDmm("mock://consumer");
using (dmm.Session)
{
    var value = dmm.MeasureVoltageDc();
    if (value != 3.3)
        throw new InvalidOperationException($"Unexpected sync measurement: {value}.");
    Console.WriteLine($"Sync package consumer: {value} V");
}

var asyncDmm = await catalog.Device("mock://consumer").OpenDmmAsync();
using (asyncDmm.Session)
{
    var value = await asyncDmm.MeasureVoltageDcAsync();
    if (value != 3.3)
        throw new InvalidOperationException($"Unexpected async measurement: {value}.");
    Console.WriteLine($"Async package consumer: {value} V");
}

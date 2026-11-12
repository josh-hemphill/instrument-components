using InstrumentComponents.Drivers;
using InstrumentComponents.Runtime;
using InstrumentComponents.SyntheticDriver;

static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Package consumer assertion failed."); }
static void Error(DriverErrorKind kind, Action action)
{
    try { action(); } catch (DriverException e) when (e.Kind == kind) { return; }
    throw new InvalidOperationException($"Expected {kind}.");
}
var trace = new Trace();
// Explicit static factory reference: no assembly scanning or reflection activation.
var registry = new DriverRegistry([new BinaryFactory(trace)]);
using var handle = registry.Open(BinaryFactory.Candidate, required: [Operation.MeasureVoltageDc]);
Check(handle.Evidence.DriverId == "synthetic.binary");
Check(handle.Dmm.MeasureVoltageDc().Value == 3.3);
handle.Psu.SetVoltage(2, 5);
Check(handle.Psu.ReadVoltage(2).Value == 5);
var before = trace.Events.Count;
Error(DriverErrorKind.UnsupportedOperation, () => handle.SetSenseMode(1, SenseMode.External));
Check(trace.Events.Count == before);
Error(DriverErrorKind.UnsupportedExecutionMode, () => registry.Open(BinaryFactory.Candidate, mode: ExecutionMode.NativeAsync));
trace.FailMeasure = true;
Error(DriverErrorKind.Timeout, () => handle.Dmm.MeasureVoltageDc());
Error(DriverErrorKind.SessionNeedsRecovery, () => handle.Dmm.MeasureVoltageDc());
handle.Recover(); Check(handle.Dmm.MeasureVoltageDc().Value == 3.3);
handle.Psu.OutputEnable(1, true); handle.Psu.OutputEnable(2, true);
handle.Close(true); handle.Close(true);
Check(trace.ShutdownAttempts.SequenceEqual(new uint[] { 1, 2 }) && trace.Outputs.Values.All(enabled => !enabled));
Check(trace.Closes == 1 && trace.Shutdowns == 1);
var failedCheckedClose=new Trace{FailShutdown=true,FailClose=true};
var failedCheckedHandle=new DriverRegistry([new BinaryFactory(failedCheckedClose)]).Open(BinaryFactory.Candidate);
Error(DriverErrorKind.Shutdown,()=>failedCheckedHandle.Close(true));
Error(DriverErrorKind.Shutdown,()=>failedCheckedHandle.Close(true));
Error(DriverErrorKind.Shutdown,()=>failedCheckedHandle.Close());
failedCheckedHandle.Dispose();
Check(failedCheckedClose.Closes==1&&failedCheckedClose.Disposes==1&&failedCheckedClose.Shutdowns==1);
var resourceFirst=new Trace();var resourceHandle=new DriverRegistry([new BinaryFactory(resourceFirst)]).Open(BinaryFactory.Candidate);
resourceHandle.Close();Error(DriverErrorKind.Shutdown,()=>resourceHandle.Close(true));resourceHandle.Dispose();
Check(resourceFirst.Closes==1&&resourceFirst.Shutdowns==0);
var distinctSchemes=new InstrumentComponents.Catalog.DeviceCatalog(new DriverRegistry([]),[
    new(new("k","same-address"),new()),new(new("K","same-address"),new())]);
Check(distinctSchemes.Candidates.Count==2&&distinctSchemes.Device(new(" K ","same-address")).Candidate.Endpoint.Scheme=="k");
Console.WriteLine("PASS retained checked-close outcomes and ASCII catalog keys in packed Native AOT");
Check(trace.Events.Take(2).SequenceEqual(new[] { "A1", "B2" }));
var reduced = new Trace(); reduced.DisabledOperations.Add(Operation.Shutdown);
using var reducedHandle = new DriverRegistry([new BinaryFactory(reduced)]).Open(BinaryFactory.Candidate);
Error(DriverErrorKind.Shutdown, () => reducedHandle.Close(true));
Check(reduced.Closes == 1 && reduced.Shutdowns == 0 && reduced.Recoveries == 0);
var passive = new Trace();
var familyDescriptor = new DriverDescriptor("synthetic.family", DriverContract.Major, [new FamilyRule("fake", "Acme", "X*")], BinaryFactory.Supported);
var familyRegistry = new DriverRegistry([new BinaryFactory(passive, familyDescriptor)]);
Check(familyRegistry.Select(new(new("fake", "device-1"), new("Acme", "X*100"))).DriverId == "synthetic.family");
Check(passive.Opens == 0);
Error(DriverErrorKind.DriverNotFound, () => new DriverRegistry([]).Select(BinaryFactory.Candidate));
Console.WriteLine("PASS Native AOT packaged provider: binary 3.3 V, PSU, unsupported mode/operation, recovery, shutdown, close, core-only selection");

var nativeTrace = new AsyncTrace();
var nativeRegistry = new DriverRegistry([new NativeFactory(nativeTrace)]);
await using var native = await nativeRegistry.OpenNativeAsync(NativeFactory.Candidate);
Check((await native.MeasureVoltageDcAsync()).Value == 3.3);
await native.SetVoltageAsync(1, 4.2); Check((await native.ReadVoltageAsync(1)).Value == 4.2);
await native.OutputEnableAsync(1, true);
nativeTrace.BlockMeasure = true;
using (var canceled = new CancellationTokenSource())
{
    var active = native.MeasureVoltageDcAsync(cancellationToken: canceled.Token).AsTask();
    // The first completed measurement already signaled Started; this controlled
    // call yields after its first write, so wait for the actual second A1.
    while (nativeTrace.Base.Events.Count(e => e == "A1") < 2) await Task.Yield();
    canceled.Cancel();
    try { await active; throw new InvalidOperationException("Expected cancellation."); } catch (OperationCanceledException) { }
}
Check(native.RecoveryRequired); await native.RecoverAsync(); nativeTrace.BlockMeasure = false;
nativeTrace.Base.FailMeasure = true; nativeTrace.Base.MeasureError = DriverErrorKind.SessionClosed;
try { await native.MeasureVoltageDcAsync(); throw new InvalidOperationException("Expected provider failure."); }
catch (DriverException e) when (e.Kind == DriverErrorKind.SessionClosed) { }
Check(native.RecoveryRequired); await native.RecoverAsync();
await native.CloseAsync(true); Check(nativeTrace.Base.Closes == 1 && nativeTrace.Base.Outputs.Values.All(enabled => !enabled));
var workerTrace = new WorkerTrace();
await using var worker = await new DriverRegistry([new WorkerFactory(workerTrace)]).OpenWorkerAsync(WorkerFactory.Candidate);
Check(worker.MeasureVoltageDc().Value == 3.3); Check((await worker.MeasureVoltageDcAsync()).Value == 3.3);
worker.SetVoltage(1, 7); Check((await worker.ReadVoltageAsync(1)).Value == 7);
workerTrace.Base.FailMeasure = true; workerTrace.Base.MeasureError = DriverErrorKind.SessionClosing;
Error(DriverErrorKind.SessionClosing, () => worker.MeasureVoltageDc());
Check(worker.RecoveryRequired); worker.Recover(); worker.Reset(); worker.OutputEnable(1, true);
await worker.OutputEnableAsync(2, true); await worker.CloseAsync(true);
Check(workerTrace.Base.Outputs.Values.All(enabled => !enabled));
Check(workerTrace.Base.Closes == 1 && workerTrace.Affinity.All(e => e.Thread == workerTrace.Affinity.First().Thread));
Console.WriteLine("PASS Native AOT native/worker modes: async values, cancellation quarantine, mixed worker frontends, recovery, shutdown, affinity");
await ScpiProof.Run();
await NeutralCapabilityProof.Run();
await EndpointProof.Run();
await ByteSessionProof.Run();
foreach(var malformed in new[]{"#2-1","#2+1X","#2 1X","#20 X","#1A","#x1X"})
{
    try{InstrumentComponents.Scpi.ScpiFraming.ExtractResponse(System.Text.Encoding.UTF8.GetBytes(malformed),"\n");throw new InvalidOperationException("Malformed block header accepted");}
    catch(InstrumentComponents.Errors.ParseException){}
}
using(var retrySession=new InstrumentComponents.Scpi.ScpiSession(
    new InstrumentComponents.Mock.MockTransport([
        new InstrumentComponents.Mock.WriteStep{Data=":READ?\n"},
        new InstrumentComponents.Mock.ReadStep{Data="1\n"}]).FailWrites(1),
    new InstrumentComponents.Connect.ConnectOptions{Retries=uint.MaxValue,ReconnectOnFailure=false,RetryBackoff=TimeSpan.Zero}))
    Check(retrySession.Query(":READ?")=="1");
Console.WriteLine("PASS strict SCPI block headers and saturated retry limits in packed Native AOT facade");

// Standard explicit-data helpers must also work with reflection disabled.
var transcript=InstrumentComponents.Mock.Transcript.FromJson("{\"steps\":[{\"op\":\"write\",\"data\":\"*IDN?\"}]}");
if(InstrumentComponents.Mock.Transcript.FromJson(transcript.ToJson()).Steps.Count!=1)throw new InvalidOperationException("AOT transcript source metadata missing");
var explicitHints=InstrumentComponents.Registry.ModelRegistry.FromJson("{\"entries\":[{\"manufacturer\":\"Example\",\"model\":\"Device\",\"kinds\":[\"Dmm\"]}]}");
if(explicitHints.LookupModel("Example","Device")?.Count!=1)throw new InvalidOperationException("AOT explicit registry parser failed");

using System.Collections.Frozen;
using System.Collections.Concurrent;
using InstrumentComponents.Drivers;

namespace InstrumentComponents.SyntheticDriver;

public sealed class Trace
{
    public int Opens, Closes, Disposes, Shutdowns, Recoveries;
    public bool FailMeasure, FailShutdown, FailRecover, FailClose, MissingDmm;
    public bool UnknownInventory;
    public HashSet<Operation> DisabledOperations { get; } = [];
    public ConcurrentDictionary<uint, bool> Outputs { get; } = new();
    private readonly ConcurrentQueue<uint> _shutdownAttempts = new();
    public IReadOnlyList<uint> ShutdownAttempts => _shutdownAttempts.ToArray();
    internal void RecordShutdownAttempt(uint channel) => _shutdownAttempts.Enqueue(channel);
    public Action? OnMeasure;
    public DriverErrorKind MeasureError = DriverErrorKind.Timeout;
    private readonly ConcurrentQueue<string> _events = new();
    public IReadOnlyList<string> Events => _events.ToArray();
    internal void Record(string value) => _events.Enqueue(value);
    public ManualResetEventSlim? Started { get; set; }
    public ManualResetEventSlim? Release { get; set; }
}

public sealed class BinaryFactory(Trace trace, DriverDescriptor? descriptor = null) : IDriverFactory
{
    public static readonly FrozenSet<Operation> Supported = new[] { Operation.MeasureVoltageDc,
        Operation.SetVoltage, Operation.ReadVoltage, Operation.OutputEnable, Operation.Reset, Operation.Shutdown }.ToFrozenSet();
    public static DeviceCandidate Candidate => new(new("fake", "device-1"), new("Acme", "X100"));
    public DriverDescriptor Descriptor { get; } = descriptor ?? new("synthetic.binary", DriverContract.Major,
        [new ExactIdentityRule("fake", "Acme", "X100")], Supported);
    public IDeviceBackend Open(DriverOpenContext context)
    { Interlocked.Increment(ref trace.Opens); return new BinaryDevice(trace, context.Ownership); }
}

internal sealed class BinaryDevice(Trace trace, ConnectionOwnership ownership)
    : IDeviceBackend, IDmmMeasurement, IPsuOutput, IDeviceReset, ISafeShutdown
{
    private bool _stage;
    private readonly Dictionary<uint, double> _voltages = new();
    public IReadOnlySet<Operation> Operations => BinaryFactory.Supported.Where(o => !trace.DisabledOperations.Contains(o)).ToFrozenSet();
    public IDmmMeasurement? Dmm => trace.MissingDmm ? null : this;
    public IPsuOutput Psu => this;
    public IDeviceReset Reset => this;
    public ISafeShutdown Shutdown => this;
    // Strict binary sequence: a generic SCPI query cannot accidentally pass this fixture.
    internal byte[] Exchange(ReadOnlySpan<byte> request)
    {
        trace.Record(Convert.ToHexString(request));
        if (request.SequenceEqual(new byte[] { 0xa1 }) && !_stage) { _stage = true; return [0x06]; }
        if (request.SequenceEqual(new byte[] { 0xb2 }) && _stage)
        {
            _stage = false;
            var bytes = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes, 3.3);
            return bytes;
        }
        throw new DriverException(DriverErrorKind.Device, "Unexpected binary protocol message.");
    }
    public Measurement MeasureVoltageDc(double? range = null)
    {
        var callback = trace.OnMeasure; trace.OnMeasure = null; callback?.Invoke();
        if (!Exchange([0xa1]).AsSpan().SequenceEqual(new byte[] { 0x06 })) throw new DriverException(DriverErrorKind.Parse, "Bad acknowledgement.");
        if (trace.FailMeasure) { trace.FailMeasure = false; throw new DriverException(trace.MeasureError, "Injected provider failure after first binary write."); }
        trace.Started?.Set();
        if (trace.Release is { } release && !release.Wait(TimeSpan.FromSeconds(15))) throw new DriverException(DriverErrorKind.Timeout, "Fixture release timed out.");
        var bytes = Exchange([0xb2]);
        return new(System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes));
    }
    public void SetVoltage(uint channel, double volts) { trace.Record($"set:{channel}"); _voltages[channel] = volts; }
    public Measurement ReadVoltage(uint channel) => new(_voltages.GetValueOrDefault(channel));
    public void OutputEnable(uint channel, bool enabled)
    {
        if (channel > 2) throw new DriverException(DriverErrorKind.InvalidArgument, "Unknown physical channel.");
        trace.Record($"output:{channel}:{enabled}"); trace.Outputs[channel] = enabled;
    }
    void IDeviceReset.Reset() { _voltages.Clear(); _stage = false; }
    void ISafeShutdown.Shutdown()
    {
        Interlocked.Increment(ref trace.Shutdowns);
        var failures = new List<string>();
        foreach (var channel in new uint[] { 1, 2 })
        {
            trace.RecordShutdownAttempt(channel);
            if (channel == 1 && trace.FailShutdown) failures.Add("Injected output-1 failure.");
            else OutputEnable(channel, false);
        }
        if (trace.UnknownInventory) failures.Add("Physical output inventory unknown; known outputs disabled.");
        if (failures.Count > 0) throw new DriverException(DriverErrorKind.Shutdown, string.Join("; ", failures));
    }
    public void Recover()
    {
        Interlocked.Increment(ref trace.Recoveries);
        if (trace.FailRecover) throw new DriverException(DriverErrorKind.Transport, "Injected recovery failure.");
        _stage = false;
    }
    public void Dispose()
    {
        Interlocked.Increment(ref trace.Closes);
        if (ownership == ConnectionOwnership.Owned) Interlocked.Increment(ref trace.Disposes);
        if (trace.FailClose) throw new DriverException(DriverErrorKind.Transport, "Injected release failure.");
    }
}

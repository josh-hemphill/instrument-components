using System.Collections.Frozen;
using System.Text.Json;
using InstrumentComponents.Drivers;
using InstrumentComponents.Runtime;
using InstrumentComponents.SyntheticDriver;

namespace InstrumentComponents.Runtime.Tests;

public sealed class DriverContractTests
{
    private static JsonDocument Cases()
    {
        var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "driver-contract-cases.json")));
        Assert.Equal(1, data.RootElement.GetProperty("schemaVersion").GetInt32());
        return data;
    }
    private static string Text(JsonElement e, string key) => e.GetProperty(key).GetString()!;
    private static bool Flag(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.GetBoolean();
    private static uint Channel(JsonElement e) => e.TryGetProperty("channel", out var p) ? p.GetUInt32() : 1;
    private static DriverDescriptor Descriptor(JsonElement rule, string id)
    {
        var scheme = Text(rule, "scheme");
        MatchRule match = Text(rule, "kind") switch
        {
            "exact" => new ExactIdentityRule(scheme, Text(rule, "manufacturer"), Text(rule, "model")),
            "family" => new FamilyRule(scheme, Text(rule, "manufacturer"), Text(rule, "model")),
            "product" => new ExactProductRule(scheme, rule.GetProperty("vendorId").GetUInt32(), rule.GetProperty("productId").GetUInt32()),
            "standard" => new StandardScpiRule(scheme),
            _ => throw new InvalidOperationException(),
        };
        return new($"synthetic.{id}", DriverContract.Major, [match], BinaryFactory.Supported);
    }

    [Fact]
    public void SharedSelectionCasesArePassiveAndOrderIndependent()
    {
        using var data = Cases(); var rules = data.RootElement.GetProperty("rules");
        foreach (var row in data.RootElement.GetProperty("selectionCases").EnumerateArray())
        {
            var trace = new Trace();
            var failure = Record.Exception(() =>
            {
                var factories = row.GetProperty("drivers").EnumerateArray().Select(id => new BinaryFactory(trace, Descriptor(rules.GetProperty(id.GetString()!), id.GetString()!)));
                var registry = new DriverRegistry(factories);
                var c = new DeviceCandidate(new(Text(row, "scheme"), "device-1"),
                    new(Text(row, "manufacturer"), Text(row, "model"), VendorId: row.TryGetProperty("vendorId", out var vid) ? vid.GetUInt32() : null,
                        ProductId: row.TryGetProperty("productId", out var pid) ? pid.GetUInt32() : null));
                var selection = new DriverSelection(row.TryGetProperty("explicit", out var explicitId) ? explicitId.GetString() : null,
                    row.TryGetProperty("preferences", out var prefs) ? prefs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()) : null,
                    Flag(row, "allowStandardScpi"));
                var selected = registry.Select(c, selection);
                Assert.Equal(Text(row, "expectedDriver"), selected.DriverId);
            });
            if (row.TryGetProperty("expectedError", out var expected)) Assert.Equal(expected.GetString(), Assert.IsType<DriverException>(failure).Kind.ToString());
            else Assert.Null(failure);
            Assert.Equal(0, trace.Opens);
        }
    }

    [Fact]
    public void SharedOperationsAssertValuesErrorsAndLifetime()
    {
        using var data = Cases();
        foreach (var row in data.RootElement.GetProperty("operationCases").EnumerateArray())
        {
            var trace = new Trace { MissingDmm = Flag(row, "missingDmm"), FailClose = Flag(row, "failClose") };
            trace.UnknownInventory = Flag(row, "unknownInventory");
            if (row.TryGetProperty("disabledOperations", out var disabled))
                foreach (var op in disabled.EnumerateArray()) trace.DisabledOperations.Add(Enum.Parse<Operation>(op.GetString()!));
            var supported = BinaryFactory.Supported.ToHashSet();
            if (row.TryGetProperty("envelope", out var envelope))
            {
                if (envelope.GetString() == "measureOnly") supported = [Operation.MeasureVoltageDc];
                else if (envelope.GetString() == "withSense") supported.Add(Operation.RemoteSense);
            }
            var descriptor = new DriverDescriptor("synthetic.binary", DriverContract.Major, [new ExactIdentityRule("fake", "Acme", "X100")], supported);
            var registry = new DriverRegistry([new BinaryFactory(trace, descriptor)]);
            DeviceHandle? handle = null;
            var openError = Record.Exception(() => handle = registry.Open(BinaryFactory.Candidate,
                required: Flag(row, "requiredSense") ? [Operation.RemoteSense] : [],
                mode: row.TryGetProperty("mode", out var mode) ? Enum.Parse<ExecutionMode>(mode.GetString()!) : ExecutionMode.Sync,
                ownership: row.TryGetProperty("ownership", out var ownership) ? Enum.Parse<ConnectionOwnership>(ownership.GetString()!) : ConnectionOwnership.Owned));
            if (row.TryGetProperty("openError", out var expectedOpen))
            {
                Assert.Equal(expectedOpen.GetString(), Assert.IsType<DriverException>(openError).Kind.ToString()); Assert.Equal(row.TryGetProperty("opens", out var opens) ? opens.GetInt32() : 0, trace.Opens);
            }
            else
            {
                Assert.Null(openError); Assert.NotNull(handle);
                foreach (var step in row.GetProperty("steps").EnumerateArray())
                {
                    double? result = null;
                    var failure = Record.Exception(() =>
                    {
                        switch (Text(step, "action"))
                        {
                            case "measure": result = handle.Dmm.MeasureVoltageDc(step.TryGetProperty("range", out var range) ? range.GetDouble() : null).Value; break;
                            case "set": handle.Psu.SetVoltage(Channel(step), step.GetProperty("value").GetDouble()); break;
                            case "read": result = handle.Psu.ReadVoltage(Channel(step)).Value; break;
                            case "output": handle.Psu.OutputEnable(Channel(step), true); break;
                            case "reset": handle.Reset(); break;
                            case "sense": handle.SetSenseMode(Channel(step), SenseMode.External); break;
                            case "recover": handle.Recover(); break;
                            case "close": handle.Close(Flag(step, "shutdown")); break;
                            case "fail-measure": trace.FailMeasure = true; break;
                            case "fail-shutdown": trace.FailShutdown = true; break;
                            case "fail-recover": trace.FailRecover = true; break;
                            case "fail-close": trace.FailClose = true; break;
                            default: throw new InvalidOperationException();
                        }
                    });
                    if (step.TryGetProperty("error", out var expected)) Assert.Equal(expected.GetString(), Assert.IsType<DriverException>(failure).Kind.ToString());
                    else { Assert.Null(failure); if (Text(step, "action") is "measure" or "read") Assert.Equal(step.GetProperty("value").GetDouble(), result); }
                }
            }
            Assert.Equal(row.GetProperty("closes").GetInt32(), trace.Closes);
            Assert.Equal(row.GetProperty("shutdowns").GetInt32(), trace.Shutdowns);
            Assert.Equal(row.TryGetProperty("disposes", out var disposes) ? disposes.GetInt32() : trace.Closes, trace.Disposes);
            if (row.TryGetProperty("recoveries", out var recoveries)) Assert.Equal(recoveries.GetInt32(), trace.Recoveries);
            if (row.TryGetProperty("shutdownAttempts", out var attempts)) Assert.Equal(attempts.EnumerateArray().Select(v => v.GetUInt32()), trace.ShutdownAttempts);
            if (row.TryGetProperty("outputs", out var outputs))
                foreach (var output in outputs.EnumerateObject()) Assert.Equal(output.Value.GetBoolean(), trace.Outputs[uint.Parse(output.Name)]);
            if (Flag(row, "noIo")) Assert.Empty(trace.Events);
        }
    }

    [Fact]
    public void InvalidBackendAndContractCleanUp()
    {
        var trace = new Trace { MissingDmm = true };
        var registry = new DriverRegistry([new BinaryFactory(trace)]);
        Assert.Equal(DriverErrorKind.DriverContractViolation, Assert.Throws<DriverException>(() => registry.Open(BinaryFactory.Candidate)).Kind);
        Assert.Equal(1, trace.Closes);
        var descriptor = new DriverDescriptor("synthetic.invalid", DriverContract.Major + 1, [new StandardScpiRule("fake")], BinaryFactory.Supported);
        Assert.Equal(DriverErrorKind.IncompatibleContract, Assert.Throws<DriverException>(() => new DriverRegistry([new BinaryFactory(trace, descriptor)])).Kind);
    }

    [Fact]
    public async Task ConcurrentViewsSerializeWholeOperations()
    {
        var trace = new Trace(); var registry = new DriverRegistry([new BinaryFactory(trace)]);
        using var handle = registry.Open(BinaryFactory.Candidate);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        { if (i % 2 == 0) Assert.Equal(3.3, handle.Dmm.MeasureVoltageDc().Value); else handle.Psu.SetVoltage(1, i); })));
        for (var i = 0; i < trace.Events.Count; i++) if (trace.Events[i] == "A1") Assert.Equal("B2", trace.Events[i + 1]);
    }

    [Fact]
    public async Task CloseWaitsForActiveWorkAndReleasesOnce()
    {
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var trace = new Trace { Started = started, Release = release };
        var handle = new DriverRegistry([new BinaryFactory(trace)]).Open(BinaryFactory.Candidate);
        var operation = Task.Factory.StartNew(() => handle.Dmm.MeasureVoltageDc(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var queued = Task.Factory.StartNew(() => Record.Exception(() => handle.Psu.SetVoltage(1, 7)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(SpinWait.SpinUntil(() => handle.PendingOperations == 1, TimeSpan.FromSeconds(5)));
        var close = Task.Factory.StartNew(() => handle.Close(true), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(SpinWait.SpinUntil(() => handle.ClosingRequested, TimeSpan.FromSeconds(5)));
        Assert.Equal(DriverErrorKind.SessionClosing, Assert.Throws<DriverException>(() => handle.Psu.SetVoltage(1, 7)).Kind);
        Assert.Equal(0, trace.Closes);
        var closeAgain = Task.Factory.StartNew(() => handle.Close(true), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(SpinWait.SpinUntil(() => handle.PendingOperations == 3, TimeSpan.FromSeconds(5)));
        release.Set(); Assert.Equal(3.3, (await operation).Value); await Task.WhenAll(close, closeAgain);
        Assert.Contains(Assert.IsType<DriverException>(await queued).Kind, new[] { DriverErrorKind.SessionClosing, DriverErrorKind.SessionClosed });
        Assert.Equal(0, handle.PendingOperations); Assert.Equal(1, trace.Shutdowns);
        Assert.DoesNotContain("set:1", trace.Events);
        handle.Close(); Assert.Equal(1, trace.Closes);
        Assert.Equal(DriverErrorKind.SessionClosed, Assert.Throws<DriverException>(() => handle.Dmm.MeasureVoltageDc()).Kind);
    }
    [Fact]
    public void BackendReentryIsRejectedWithoutDeadlockOrIo()
    {
        var trace = new Trace();
        using var handle = new DriverRegistry([new BinaryFactory(trace)]).Open(BinaryFactory.Candidate);
        trace.OnMeasure = handle.Reset;
        Assert.Equal(DriverErrorKind.DriverContractViolation, Assert.Throws<DriverException>(() => handle.Dmm.MeasureVoltageDc()).Kind);
        Assert.Empty(trace.Events);
        Assert.Equal(DriverErrorKind.SessionNeedsRecovery, Assert.Throws<DriverException>(() => handle.Dmm.MeasureVoltageDc()).Kind);
        handle.Recover();
    }

}

using System.Text;
using System.Text.Json;
using InstrumentComponents.Address;
using InstrumentComponents.Classifier;
using InstrumentComponents.Classes;
using InstrumentComponents.Connect;
using InstrumentComponents.Enumerator;
using InstrumentComponents.Errors;
using InstrumentComponents.Identity;
using InstrumentComponents.Mock;
using InstrumentComponents.Probe;
using InstrumentComponents.Recording;
using InstrumentComponents.Registry;
using InstrumentComponents.Scpi;
using InstrumentComponents.Session;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Tests;

public class ReviewRegressionTests
{
    private static JsonElement Vectors => JsonDocument.Parse(File.ReadAllText(RepoFiles.Spec("review-regressions.json"))).RootElement;
    private static ConnectOptions Options => new() { Retries = 0, ReconnectOnFailure = false };
    private static MockTransport ErrorTransport(JsonElement vector) => new(vector.GetProperty("replies").EnumerateArray()
        .SelectMany(reply => new ScriptStep[] { new WriteStep { Data = "SYST:ERR?" }, new ReadStep { Data = reply.GetString() + "\n" } }).ToArray());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedErrorQueuesRetainProbedEntries(bool probeFirst)
    {
        foreach (var vector in Vectors.GetProperty("errorQueues").EnumerateArray())
        {
            using var session = new ScpiSession(ErrorTransport(vector), Options);
            if (probeFirst) session.ProbeSystErr();
            Assert.Equal(vector.GetProperty("expected").EnumerateArray().Select(x => x.GetString()), session.CheckErrors());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedAsyncErrorQueuesRetainProbedEntries(bool probeFirst)
    {
        foreach (var vector in Vectors.GetProperty("errorQueues").EnumerateArray())
        {
            using var session = await AsyncScpiSession.CreateAsync(ErrorTransport(vector), Options);
            if (probeFirst) await session.ProbeSystErrAsync();
            Assert.Equal(vector.GetProperty("expected").EnumerateArray().Select(x => x.GetString()), await session.CheckErrorsAsync());
        }
    }

    private static MockTransport ErrorsAfterEmptyProbe(string zero) => new([
        new WriteStep { Data = "SYST:ERR?" }, new ReadStep { Data = zero + "\n" },
        new WriteStep { Data = "BAD" },
        new WriteStep { Data = "SYST:ERR?" }, new ReadStep { Data = "-113,\"Undefined header\"\n" },
        new WriteStep { Data = "SYST:ERR?" }, new ReadStep { Data = "0,\"No error\"\n" }
    ]);

    [Theory]
    [InlineData("0,\"No error\"")]
    [InlineData("+0,\"No error\"")]
    [InlineData("  +0 , \"No error\"  ")]
    public void EmptyProbeDoesNotHideErrorsFromLaterCommands(string zero)
    {
        using var session = new ScpiSession(ErrorsAfterEmptyProbe(zero), Options);
        Assert.True(session.ProbeSystErr());
        session.Write("BAD");
        Assert.Equal(new[] { "-113,\"Undefined header\"" }, session.CheckErrors());
    }

    [Theory]
    [InlineData("0,\"No error\"")]
    [InlineData("+0,\"No error\"")]
    [InlineData("  +0 , \"No error\"  ")]
    public async Task AsyncEmptyProbeDoesNotHideErrorsFromLaterCommands(string zero)
    {
        using var session = await AsyncScpiSession.CreateAsync(ErrorsAfterEmptyProbe(zero), Options);
        Assert.True(await session.ProbeSystErrAsync());
        await session.WriteAsync("BAD");
        Assert.Equal(new[] { "-113,\"Undefined header\"" }, await session.CheckErrorsAsync());
    }

    [Fact]
    public async Task SignedVoltageAcquisitionIdentifiesDmmWithoutOtherCapabilities()
    {
        using var session = new ScpiSession(new SignedVoltageTransport(), Options);
        Assert.Contains(Classifier.Classifier.ClassifyWithPolicy(session, ProbePolicy.Full), kind => kind.Kind == Kind.InstrumentKind.Dmm);
        using var asyncSession = await AsyncScpiSession.CreateAsync(new SyncAsAsyncTransport<SignedVoltageTransport>(new()), Options);
        Assert.Contains(await Classifier.Classifier.ClassifyWithPolicyAsync(asyncSession, ProbePolicy.Full), kind => kind.Kind == Kind.InstrumentKind.Dmm);
    }

    private sealed class SignedVoltageTransport : TransportBase
    {
        private string _command = "";
        public override void Write(ReadOnlySpan<byte> data) => _command = Encoding.UTF8.GetString(data).Trim();
        public override int Read(Span<byte> buffer)
        {
            var reply = _command.TrimStart(':') == "MEAS:VOLT:DC?" ? "-3.3\n" : "-113,\"Undefined header\"\n";
            var bytes = Encoding.UTF8.GetBytes(reply); bytes.CopyTo(buffer); return bytes.Length;
        }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) { }
    }

    [Fact]
    public void SharedProbeAndAddressShapes()
    {
        foreach (var vector in Vectors.GetProperty("probeReplies").EnumerateArray())
            Assert.Equal(vector.GetProperty("valid").GetBoolean(), CapabilityProbes.ValidProbeReply(vector.GetProperty("command").GetString()!, vector.GetProperty("reply").GetString()!));
        foreach (var vector in Vectors.GetProperty("socketAddresses").EnumerateArray())
            Assert.Equal(vector.GetProperty("port").ValueKind == JsonValueKind.Null ? (ushort?)null : vector.GetProperty("port").GetUInt16(), ResourceAddress.Parse(vector.GetProperty("address").GetString()!).Components.Port);
    }

    [Fact]
    public void LargeMockResponseSurvivesManyReads()
    {
        var value = new string('x', 1500);
        using var session = new ScpiSession(new MockTransport([new WriteStep { Data = "DATA?" }, new ReadStep { Data = value + "\n" }]), Options);
        Assert.Equal(value, session.Query("DATA?"));
    }

    [Fact]
    public async Task SplitBlockTerminatorDoesNotContaminateNextReply()
    {
        var chunks = Vectors.GetProperty("blockChunks").EnumerateArray().Select(x => x.GetString()!).ToArray();
        using var sync = new ScpiSession(new ChunkTransport(chunks), Options);
        Assert.Equal("abcde", sync.Query("BLOCK?"));
        Assert.Equal("3.3", sync.Query("NEXT?"));
        using var asyncSession = await AsyncScpiSession.CreateAsync(new SyncAsAsyncTransport<ChunkTransport>(new(chunks)), Options);
        Assert.Equal("abcde", await asyncSession.QueryAsync("BLOCK?"));
        Assert.Equal("3.3", await asyncSession.QueryAsync("NEXT?"));
    }

    [Fact]
    public void CompletionUsesFullTimeoutAndRejectsInvalidReply()
    {
        using var session = new ScpiSession(new CompletionTransport("+1\n"), Options);
        new global::InstrumentComponents.Ieee4882.Ieee4882(session).WaitComplete();
        using var invalid = new ScpiSession(new CompletionTransport("0\n"), Options);
        Assert.Throws<InstrumentUnsupportedException>(() => new global::InstrumentComponents.Ieee4882.Ieee4882(invalid).WaitComplete());
    }

    [Fact]
    public async Task AsyncCompletionUsesFullTimeoutAndRejectsInvalidReply()
    {
        using var session = await AsyncScpiSession.CreateAsync(new SyncAsAsyncTransport<CompletionTransport>(new("+1\n")), Options);
        await new global::InstrumentComponents.Ieee4882.AsyncIeee4882(session).WaitCompleteAsync();
        using var invalid = await AsyncScpiSession.CreateAsync(new SyncAsAsyncTransport<CompletionTransport>(new("0\n")), Options);
        await Assert.ThrowsAsync<InstrumentUnsupportedException>(() => new global::InstrumentComponents.Ieee4882.AsyncIeee4882(invalid).WaitCompleteAsync());
    }

    [Fact]
    public async Task ProbeCancellationDoesNotCacheUnsupported()
    {
        var transport = new MockTransport([new WriteStep { Data = "*OPC?" }, new ReadStep { Data = "1\n" }]);
        using var session = await AsyncScpiSession.CreateAsync(transport, Options);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ProbeOpcAsync(cancellation.Token));
        Assert.True(await session.ProbeOpcAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryReleasesSessionsOnSuccessAndConfigurationFailure(bool failConfigure)
    {
        var transport = new CountingTransport { FailConfigure = failConfigure };
        var opener = new Opener(transport);
        var catalog = new global::InstrumentComponents.Discovery.Discovery(StaticEnumerator.FromAddresses(["mock://review"]), opener, ModelRegistry.Embedded())
            .WithProbePolicy(ProbePolicy.None).ConnectOptions(Options).Scan();
        Assert.Equal(!failConfigure, catalog.Devices.Single().Reachable);
        Assert.Equal(1, transport.Disposals);
        if (!failConfigure) { using var reopened = catalog.Device("mock://review").OpenSession(); }
    }

    [Fact]
    public async Task AsyncConstructionFailureReleasesTransport()
    {
        var transport = new CountingTransport { FailConfigure = true };
        await Assert.ThrowsAsync<TransportException>(() => AsyncScpiSession.CreateAsync(new SyncAsAsyncTransport<CountingTransport>(transport), Options));
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public void RecorderOwnsItsTransportAndCanTransferOwnership()
    {
        var transport = new CountingTransport();
        var recorder = new RecordingTransport<CountingTransport>(transport);
        recorder.Dispose(); recorder.Dispose();
        Assert.Equal(1, transport.Disposals);
        var transferred = new CountingTransport();
        var wrapper = new RecordingTransport<CountingTransport>(transferred);
        Assert.Same(transferred, wrapper.IntoInner()); wrapper.Dispose();
        Assert.Equal(0, transferred.Disposals); transferred.Dispose();
    }

    [Fact]
    public async Task BridgeReturnsBeforeNativeReadAndRetainsBufferUntilDone()
    {
        var transport = new BlockingTransport();
        using var bridge = new SyncAsAsyncTransport<BlockingTransport>(transport);
        var buffer = new byte[4];
        var caller = Environment.CurrentManagedThreadId;
        var read = bridge.ReadAsync(buffer).AsTask();
        Assert.True(transport.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(read.IsCompleted);
            Assert.NotEqual(caller, transport.WorkerThread);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.ReadAsync(new byte[4], cancellation.Token).AsTask());
        }
        finally { transport.Release.Set(); }
        Assert.Equal(1, await read);
        Assert.Equal((byte)42, buffer[0]);
    }

    [Fact]
    public async Task ConcurrentAsyncQueriesKeepTheirOwnResponses()
    {
        using var session = await AsyncScpiSession.CreateAsync(new SyncAsAsyncTransport<EchoTransport>(new()), Options);
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => session.QueryAsync($"{i}?")));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => $"{i}?"), results);
    }

    [Fact]
    public void PsuShutdownIncludesPreviouslyEnabledChannelsAcrossViewsAndContinuesOnFailure()
    {
        var io = new MessageIo();
        using var session = InstrumentSession.FromIo(ResourceAddress.Parse("mock://psu"), io, new DeviceIdentity());
        new DcPowerSupply(session).OutputEnable(2, true);
        io.FailChannelOneOff = true;
        Assert.Throws<AggregateException>(() => new DcPowerSupply(session).OutputOff());
        Assert.Contains(":OUTP2 OFF", io.Writes);
        session.PowerSupplyChannelCount = 3; io.FailChannelOneOff = false;
        new DcPowerSupply(session).OutputOff();
        Assert.Contains(":OUTP3 OFF", io.Writes);
    }

    [Fact]
    public async Task AsyncPsuShutdownIncludesEnabledChannels()
    {
        var transport = new MockTransport([new WriteStep { Data = ":OUTP2 ON" }, new WriteStep { Data = ":OUTP1 OFF" }, new WriteStep { Data = ":OUTP2 OFF" }]);
        using var session = await AsyncInstrumentSession.CreateAsync(ResourceAddress.Parse("mock://psu"), transport, Options, new DeviceIdentity());
        session.PowerSupplyChannelCount = 2;
        await new AsyncDcPowerSupply(session).OutputEnableAsync(2, true);
        await new AsyncDcPowerSupply(session).OutputOffAsync();
    }

    [Fact]
    public void N6705RemoteSenseFailsBeforeWritingInvalidCommand()
    {
        var io = new MessageIo();
        using var session = InstrumentSession.FromIo(ResourceAddress.Parse("mock://psu"), io, new DeviceIdentity { Manufacturer = "Keysight", Model = "N6705C" });
        Assert.Throws<InstrumentUnsupportedException>(() => new DcPowerSupply(session).SenseEnable(1, true));
        Assert.Empty(io.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorOnlyAndAmbiguousOutputOnlyRespondersHaveNoCapabilities(bool outputOnly)
    {
        using var session = new ScpiSession(new ProbeTransport(outputOnly), Options);
        Assert.Empty(Classifier.Classifier.ClassifyWithPolicy(session, ProbePolicy.ReadOnly));
        using var asyncSession = await AsyncScpiSession.CreateAsync(new SyncAsAsyncTransport<ProbeTransport>(new(outputOnly)), Options);
        Assert.Empty(await Classifier.Classifier.ClassifyWithPolicyAsync(asyncSession, ProbePolicy.ReadOnly));
    }

    [Fact]
    public void TimedOutProbeIsRetriedAndCompletionTimeoutIsNotRetried()
    {
        using var probe = new ScpiSession(new TimeoutOnceIo(), false);
        Assert.False(probe.ProbeOpc());
        Assert.True(probe.ProbeOpc());
        var transport = new NeverCompletes();
        using var session = new ScpiSession(transport, new ConnectOptions { Retries = 3 });
        Assert.Throws<InstrumentTimeoutException>(() => new global::InstrumentComponents.Ieee4882.Ieee4882(session).WaitComplete());
        Assert.Equal(1, transport.Writes);
    }

    [Fact]
    public void UnknownPsuCountDisablesKnownOutputsThenReportsIncompleteShutdown()
    {
        var io = new MessageIo();
        using var session = InstrumentSession.FromIo(ResourceAddress.Parse("mock://psu"), io, new DeviceIdentity());
        new DcPowerSupply(session).OutputEnable(2, true);
        Assert.Throws<InstrumentUnsupportedException>(() => new DcPowerSupply(session).OutputOff());
        Assert.Contains(":OUTP2 OFF", io.Writes);
    }

    [Fact]
    public async Task MalformedPsuStateUsesLibraryParseException()
    {
        using var session = InstrumentSession.FromIo(ResourceAddress.Parse("mock://psu"), new MessageIo { Reply = "invalid" }, new DeviceIdentity());
        Assert.Throws<ParseException>(() => new DcPowerSupply(session).OutputStateQuery(1));
        Assert.Throws<ParseException>(() => new DcPowerSupply(session).OvpQuery(1));
        using var asyncSession = await AsyncInstrumentSession.CreateAsync(ResourceAddress.Parse("mock://psu"),
            new MockTransport([new WriteStep { Data = ":OUTP1?" }, new ReadStep { Data = "invalid\n" }]), Options, new DeviceIdentity());
        await Assert.ThrowsAsync<ParseException>(() => new AsyncDcPowerSupply(asyncSession).OutputStateQueryAsync(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InjectedConstructionFailureRespectsOwnership(bool ownsIo)
    {
        var io = new FailingTimeoutIo();
        Assert.Throws<InvalidOperationException>(() => new ScpiSession(io, ownsIo));
        Assert.Equal(ownsIo ? 1 : 0, io.Disposals);
    }
    private sealed class FailingTimeoutIo : IScpiIo
    {
        public int Disposals;
        public TimeSpan IoTimeout { get => throw new InvalidOperationException("timeout getter failed"); set { } }
        public void Write(string command) { }
        public string Query(string command) => "1";
        public void Dispose() => Disposals++;
    }

    private sealed class TimeoutOnceIo : IScpiIo
    {
        private int _reads; public TimeSpan IoTimeout { get; set; }
        public void Write(string command) { }
        public string Query(string command) { if (_reads++ == 0) throw new InstrumentTimeoutException(); return "1"; }
        public void Dispose() { }
    }
    private sealed class NeverCompletes : TransportBase
    {
        public int Writes;
        public override void Write(ReadOnlySpan<byte> data) => Writes++;
        public override int Read(Span<byte> data) => throw new InstrumentTimeoutException();
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) { }
    }
    private sealed class ProbeTransport(bool outputOnly) : TransportBase
    {
        private string _command = "";
        public override void Write(ReadOnlySpan<byte> data) => _command = Encoding.UTF8.GetString(data).Trim();
        public override int Read(Span<byte> data)
        {
            var reply = outputOnly && _command.Contains("OUTP?") ? "1\n" : "-113,\"Undefined header\"\n";
            var bytes = Encoding.UTF8.GetBytes(reply); bytes.CopyTo(data); return bytes.Length;
        }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) { }
    }

    [Fact]
    public async Task CatalogAndSeparateReferencesShareConsistentHealthCounters()
    {
        var address = ResourceAddress.Parse("mock://health");
        var catalog = global::InstrumentComponents.Catalog.DeviceCatalog.FromDevices(new EchoOpener(),
            [new DiscoveredDevice { Address = address, Identity = new DeviceIdentity(), SupportedKinds = [], Classification = [], Reachable = true }]);
        var first = catalog.Device(address.Raw); var second = catalog.Device(address.Raw);
        using var a = await first.OpenSessionAsync(); using var b = await second.OpenSessionAsync();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => (i % 2 == 0 ? a : b).Scpi.QueryAsync($"{i}?")));
        Assert.Equal(200ul, catalog.Health(address.Raw).TotalOperations);
        Assert.Equal(catalog.Health(address.Raw).TotalOperations, first.Health().TotalOperations);
        Assert.Equal(first.Health().TotalOperations, second.Health().TotalOperations);
    }

    [Fact]
    public async Task PoolCallbackHoldsSessionLockAcrossMultipleOperations()
    {
        using var session = InstrumentSession.FromIo(ResourceAddress.Parse("mock://pool"), new MessageIo(), new DeviceIdentity());
        using var pool = new SessionPool(session);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var transaction = Task.Run(() => pool.WithSession(s => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return s.Scpi.Query("FIRST?"); }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var query = Task.Run(() => session.Scpi.Query("SECOND?"));
        try { Assert.False(query.IsCompleted); }
        finally { release.Set(); }
        Assert.Equal("1", await transaction); Assert.Equal("1", await query);
    }

    private sealed class EchoOpener : ISessionOpener
    {
        public ITransport Open(ResourceAddress address, ConnectOptions opts) => new EchoTransport();
    }

    private sealed class Opener(CountingTransport first) : ISessionOpener
    {
        private bool _opened;
        public ITransport Open(ResourceAddress address, ConnectOptions opts)
        {
            if (_opened) { Assert.Equal(1, first.Disposals); return new CountingTransport(); }
            _opened = true; return first;
        }
    }
    private class CountingTransport : TransportBase, IDisposable
    {
        public bool FailConfigure; public int Disposals; private string _command = "";
        public override void Configure(ConnectOptions opts) { if (FailConfigure) throw new TransportException("configure failed"); }
        public override void Write(ReadOnlySpan<byte> data) => _command = Encoding.UTF8.GetString(data).Trim();
        public override int Read(Span<byte> buffer)
        {
            if (_command is not ("*IDN?" or "*OPT?")) throw new InstrumentTimeoutException();
            var bytes = Encoding.UTF8.GetBytes(_command == "*IDN?" ? "Acme,BOX,SN,1\n" : "0\n"); _command = ""; bytes.CopyTo(buffer); return bytes.Length;
        }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) { }
        public void Dispose() => Disposals++;
    }
    private sealed class ChunkTransport(string[] chunks) : TransportBase
    {
        private readonly Queue<byte[]> _chunks = new(chunks.Select(Encoding.UTF8.GetBytes));
        public override void Write(ReadOnlySpan<byte> data) { }
        public override int Read(Span<byte> buffer) { var chunk = _chunks.Dequeue(); chunk.CopyTo(buffer); return chunk.Length; }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) { }
    }
    private sealed class CompletionTransport(string response) : TransportBase
    {
        private TimeSpan _timeout;
        public override void Write(ReadOnlySpan<byte> data) { }
        public override int Read(Span<byte> buffer) { Assert.InRange(_timeout, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30)); var bytes = Encoding.UTF8.GetBytes(response); bytes.CopyTo(buffer); return bytes.Length; }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) => _timeout = timeout;
    }
    private sealed class BlockingTransport : TransportBase
    {
        public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new(); public int WorkerThread;
        public override int Read(Span<byte> buffer) { WorkerThread = Environment.CurrentManagedThreadId; Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); buffer[0] = 42; return 1; }
        public override void Write(ReadOnlySpan<byte> data) { }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) { }
    }
    private sealed class EchoTransport : TransportBase
    {
        private byte[] _reply = [];
        public override void Write(ReadOnlySpan<byte> data) => _reply = data.ToArray();
        public override int Read(Span<byte> buffer) { Thread.Yield(); _reply.CopyTo(buffer); return _reply.Length; }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) { }
    }
    private sealed class MessageIo : IScpiIo
    {
        public List<string> Writes { get; } = []; public bool FailChannelOneOff; public string Reply = "1";
        public TimeSpan IoTimeout { get; set; }
        public void Write(string command) { Writes.Add(command); if (FailChannelOneOff && command == ":OUTP1 OFF") throw new TransportException("failed"); }
        public string Query(string command) => Reply;
        public void Dispose() { }
    }
}

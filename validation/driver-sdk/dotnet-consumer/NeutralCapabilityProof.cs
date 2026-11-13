using InstrumentComponents.Drivers.Catalog;
using InstrumentComponents.Drivers;

// Package/AOT rooting proof for the neutral additions; no vendor commands or hardware.
internal static class NeutralCapabilityProof
{
    private sealed class Executor : ICatalogExecutor, IAsyncCatalogExecutor
    {
        private readonly Queue<(CatalogRequest Request, CatalogReply Reply)> _expected = new([
            (new DataAcquisitionMeasureVoltageDcRequest(7, null), new CatalogReply.Numeric(new(1.25, Unit.Volt))),
            (new RfSignalGeneratorConfigureRequest(30000000, -30), new CatalogReply.Empty()),
            (new RfSignalGeneratorOutputEnableRequest(true), new CatalogReply.Empty()),
            (new RfSignalGeneratorOutputStateQueryRequest(), new CatalogReply.State(true)),
            (new RfReceiverMeasureFrequencyRequest(), new CatalogReply.Numeric(new(30000000, Unit.Hertz))),
            (new RfReceiverMeasurePowerRequest(), new CatalogReply.Numeric(new(-10, Unit.Dbm))),
            (new SwitchCloseChannelRequest(7), new CatalogReply.Empty()),
            (new SwitchOpenChannelRequest(13), new CatalogReply.Empty()),
            (new SwitchChannelIsClosedRequest(29), new CatalogReply.State(true)),
            (new DigitalIoReadByteRequest(7), new CatalogReply.Count(0)),
            (new DigitalIoWriteByteRequest(13,0), new CatalogReply.Empty())
        ]);
        public CatalogReply Invoke(CatalogRequest request)
        {
            request.Validate();
            var expected = _expected.Dequeue();
            if (request != expected.Request) throw new InvalidOperationException("Neutral typed request mismatch");
            expected.Reply.Validate(request);
            return expected.Reply;
        }
        public Task<CatalogReply> InvokeAsync(CatalogRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Invoke(request)); }
        public void Complete() { if (_expected.Count != 0) throw new InvalidOperationException("Missing neutral typed request"); }
    }

    public static async Task Run()
    {
        var sync = new Executor();
        if (new DataAcquisition(sync).MeasureVoltageDc(7, null).Value != 1.25) throw new InvalidOperationException("Acquisition value");
        var source = new RfSignalGenerator(sync);
        source.Configure(30000000, -30); source.OutputEnable(true);
        if (!source.OutputStateQuery()) throw new InvalidOperationException("RF output state");
        var receiver = new RfReceiver(sync);
        if (receiver.MeasureFrequency().Unit != Unit.Hertz || receiver.MeasurePower().Unit != Unit.Dbm) throw new InvalidOperationException("Receiver units");
        var switches = new Switch(sync); switches.CloseChannel(7); switches.OpenChannel(13);
        if (!switches.ChannelIsClosed(29) || new DigitalIo(sync).ReadByte(7) != 0) throw new InvalidOperationException("Switch/byte result");
        try { new DigitalIo(sync).WriteByte(13,256); throw new InvalidOperationException("Oversized byte accepted"); }
        catch (DriverException error) when(error.Kind == DriverErrorKind.InvalidArgument) { }
        new DigitalIo(sync).WriteByte(13,0);
        try { new FunctionGeneratorSetBurstCountRequest(0).Validate(); throw new InvalidOperationException("Zero positive count accepted"); }
        catch (DriverException error) when(error.Kind == DriverErrorKind.InvalidArgument) { }
        sync.Complete();

        var asynchronous = new Executor();
        if ((await new AsyncDataAcquisition(asynchronous).MeasureVoltageDcAsync(7, null)).Unit != Unit.Volt) throw new InvalidOperationException("Async acquisition unit");
        var asyncSource = new AsyncRfSignalGenerator(asynchronous);
        await asyncSource.ConfigureAsync(30000000, -30); await asyncSource.OutputEnableAsync(true);
        if (!await asyncSource.OutputStateQueryAsync()) throw new InvalidOperationException("Async RF output state");
        var asyncReceiver = new AsyncRfReceiver(asynchronous);
        if ((await asyncReceiver.MeasureFrequencyAsync()).Unit != Unit.Hertz || (await asyncReceiver.MeasurePowerAsync()).Unit != Unit.Dbm) throw new InvalidOperationException("Async receiver units");
        var asyncSwitches = new AsyncSwitch(asynchronous); await asyncSwitches.CloseChannelAsync(7); await asyncSwitches.OpenChannelAsync(13);
        if (!await asyncSwitches.ChannelIsClosedAsync(29) || await new AsyncDigitalIo(asynchronous).ReadByteAsync(7) != 0) throw new InvalidOperationException("Async switch/byte result");
        await new AsyncDigitalIo(asynchronous).WriteByteAsync(13,0);
        asynchronous.Complete();
        Console.WriteLine("PASS neutral typed classes: eleven operations, sync/async request and unit contracts");
    }
}

using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Mock;
using InstrumentComponents.Scpi;
using InstrumentComponents.Transport;

internal static class ByteSessionProof
{
    internal static async Task Run()
    {
        MockTransport Header() => new([new WriteStep { Data = "READ?\n" }, new ReadStep { Data = "#14" }]);
        var bounded = new ConnectOptions { MaxResponseBytes = 3, Retries = 2 };
        using (var sync = new ScpiSession(Header(), bounded))
        {
            try { sync.Query("READ?"); throw new InvalidOperationException("Oversize block accepted"); }
            catch (ParseException) { }
        }
        using (var native = await AsyncScpiSession.CreateAsync(Header(), bounded))
        {
            try { await native.QueryAsync("READ?"); throw new InvalidOperationException("Oversize async block accepted"); }
            catch (ParseException) { }
        }
        foreach (var asynchronous in new[] { false, true })
        {
            var wire = new StreamingWire();
            var options = new ConnectOptions { ReadTimeout = TimeSpan.FromSeconds(1), WriteTimeout = TimeSpan.FromSeconds(1), Retries = 2, RetryBackoff = TimeSpan.Zero, ReconnectOnFailure = false };
            if (asynchronous)
            {
                using var session = await AsyncScpiSession.CreateAsync(wire, options);
                try { await session.QueryAsync("READ?"); throw new InvalidOperationException("Continuous async drain succeeded"); }
                catch (InstrumentTimeoutException) { }
            }
            else
            {
                using var session = new ScpiSession(wire, options);
                try { session.Query("READ?"); throw new InvalidOperationException("Continuous drain succeeded"); }
                catch (InstrumentTimeoutException) { }
            }
            if (wire.Writes != 1 || wire.Reads >= 100 || wire.Timeout != options.IoTimeout())
                throw new InvalidOperationException("Drain retried, exceeded finite guard, or stranded timeout");
        }
        Console.WriteLine("PASS bounded byte-session cleanup, no unsafe retry, and payload limits in packed Native AOT");
    }

    private sealed class StreamingWire : TransportBase, IAsyncTransport
    {
        internal int Reads, Writes;
        internal TimeSpan Timeout;
        public override void Write(ReadOnlySpan<byte> data) => Writes++;
        public override int Read(Span<byte> buffer)
        {
            Reads++;
            if (Reads == 1) throw new InstrumentTimeoutException();
            if (Reads >= 100) throw new InvalidOperationException("finite fake-stream guard reached");
            Thread.Sleep(2); buffer[0] = (byte)'x'; return 1;
        }
        public override void Clear() { }
        public override void SetReadTimeout(TimeSpan timeout) => Timeout = timeout;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Write(data.Span); return ValueTask.CompletedTask; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public ValueTask SetReadTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); SetReadTimeout(timeout); return ValueTask.CompletedTask; }
        public ValueTask ConfigureAsync(ConnectOptions options, CancellationToken cancellationToken = default) => SetReadTimeoutAsync(options.IoTimeout(), cancellationToken);
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ReconnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("unexpected reconnect");
    }
}

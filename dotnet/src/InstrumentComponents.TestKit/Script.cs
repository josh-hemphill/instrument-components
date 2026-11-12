using InstrumentComponents.Drivers;
using InstrumentComponents.Scpi;
namespace InstrumentComponents.TestKit;

public sealed record ScriptStep(string Kind, string Message = "", string Response = "", DriverErrorKind? Failure = null)
{
    public static ScriptStep Write(string message) => new("write", message);
    public static ScriptStep Query(string message, string response) => new("query", message, response);
    public static ScriptStep Recover() => new("recover");
    public static ScriptStep Close() => new("close");
}
public sealed class Script(IEnumerable<ScriptStep> steps) : IScpiConnector, IAsyncScpiConnector
{
    private readonly object _gate = new(); private readonly Queue<ScriptStep> _remaining = new(steps); private readonly List<string> _events = []; private readonly List<int> _threads = []; private int _violations;
    public int Opens { get; private set; } public int Closes { get; private set; }
    public IReadOnlyList<string> Events { get { lock (_gate) return _events.ToArray(); } }
    public void AssertComplete() { lock (_gate) if (_remaining.Count != 0 || _violations != 0) throw new InvalidOperationException("Unconsumed script steps"); }
    public void AssertThreadAffinity() { lock (_gate) if (_threads.Count == 0 || _threads.Any(t => t != _threads[0])) throw new InvalidOperationException("SDK thread affinity violated"); }
    private void Opened() { lock (_gate) { Opens++; _threads.Add(Environment.CurrentManagedThreadId); } }
    private string Call(string kind, string message = "")
    {
        lock (_gate)
        {
            _threads.Add(Environment.CurrentManagedThreadId); _events.Add(kind + ":" + message); if (kind == "close") Closes++;
            if (!_remaining.TryDequeue(out var next) || next.Kind != kind || next.Message != message) { _violations++; throw new DriverException(DriverErrorKind.DriverContractViolation, $"Unexpected {kind}: {message}"); }
            if (next.Failure is { } error) throw new DriverException(error, "Injected script failure");
            return next.Response;
        }
    }
    public IMessageIo Open(DriverOpenContext context) { Opened(); return new Io(this); }
    public ValueTask<IAsyncMessageIo> OpenAsync(DriverOpenContext context, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Opened(); return ValueTask.FromResult<IAsyncMessageIo>(new Io(this)); }
    private sealed class Io(Script script) : IMessageIo, IAsyncMessageIo
    {
        public void Write(string message) => script.Call("write", message);
        public string Query(string message) => script.Call("query", message);
        public void Recover() => script.Call("recover");
        public void Dispose() => script.Call("close");
        public async ValueTask WriteAsync(string message, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); script.Call("write", message); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); }
        public async ValueTask<string> QueryAsync(string message, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); var response = script.Call("query", message); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); return response; }
        public async ValueTask RecoverAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); script.Call("recover"); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); }
    }
}

public sealed record CatalogCase(string Id,
    IReadOnlyList<InstrumentComponents.Drivers.Catalog.CatalogRequest> Calls,
    IReadOnlyList<ScriptStep> Script,
    InstrumentComponents.Drivers.Catalog.CatalogReply? Expected,
    DriverErrorKind? ExpectedError = null);

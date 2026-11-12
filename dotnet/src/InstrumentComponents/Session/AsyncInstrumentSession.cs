using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Diagnostics;
using InstrumentComponents.Errors;
using InstrumentComponents.Identity;
using InstrumentComponents.Ieee4882;
using InstrumentComponents.Kind;
using InstrumentComponents.Scpi;
using InstrumentComponents.Transport;

namespace InstrumentComponents.Session;

/// <summary>Async instrument session with SCPI and cached identity.</summary>
public sealed class AsyncInstrumentSession : IDisposable
{
    public ResourceAddress Address { get; }
    public AsyncScpiSession Scpi { get; }
    private readonly DeviceIdentity _identity;
    public static async Task<AsyncInstrumentSession> CreateAsync(
        ResourceAddress address,
        IAsyncTransport transport,
        ConnectOptions opts,
        DeviceIdentity identity,
        CommsDiagnostics? diagnostics = null,
        CancellationToken cancellationToken = default)
    {
        var scpi = await AsyncScpiSession.CreateAsync(transport, opts, cancellationToken).ConfigureAwait(false);
        if (diagnostics is not null)
            scpi = scpi.WithDiagnostics(diagnostics);
        return new AsyncInstrumentSession(address, scpi, identity);
    }

    private AsyncInstrumentSession(ResourceAddress address, AsyncScpiSession scpi, DeviceIdentity identity)
    {
        Address = address;
        Scpi = scpi;
        _identity = identity;
    }

    public string AddressStr => Address.Raw;
    public DeviceIdentity Identity => _identity;

    public async Task<Idn> IdnAsync(CancellationToken cancellationToken = default) =>
        await ScpiCommAsync("idn", async scpi =>
            await new AsyncIeee4882(scpi).IdnAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task ResetAsync(CancellationToken cancellationToken = default) =>
        await ScpiCommAsync("*RST", async scpi =>
        {
            await new AsyncIeee4882(scpi).ResetAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    public async Task ClearStatusAsync(CancellationToken cancellationToken = default) =>
        await ScpiCommAsync("*CLS", async scpi =>
        {
            await new AsyncIeee4882(scpi).ClearStatusAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    public async Task WaitCompleteAsync(CancellationToken cancellationToken = default) =>
        await ScpiCommAsync("*OPC?", async scpi =>
        {
            await new AsyncIeee4882(scpi).WaitCompleteAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> CheckErrorsAsync(CancellationToken cancellationToken = default) =>
        await ScpiCommAsync("SYST:ERR?", async scpi =>
            await scpi.CheckErrorsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<T> ScpiCommAsync<T>(string command, Func<AsyncScpiSession, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(Scpi).ConfigureAwait(false);
        }
        catch (InstrumentException ex) when (ex is not CommunicationException)
        {
            throw new CommunicationException(Address.Raw, command, 1, ex);
        }
    }

    public void Dispose() => Scpi.Dispose();
}

/// <summary>Async session pool with lock.</summary>
public sealed class AsyncSessionPool
{
    private readonly AsyncInstrumentSession _session;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public AsyncSessionPool(AsyncInstrumentSession session) => _session = session;

    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public AsyncInstrumentSession Session => _session;

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => _sem.Release();
    }
}

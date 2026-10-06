using System.Reflection;
using InstrumentComponents.Address;
using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using Ivi.Visa;

namespace InstrumentComponents.Visa.Tests;

public class ReviewRegressionTests
{
    private static ResourceAddress Address => ResourceAddress.Parse("TCPIP0::localhost::inst0::INSTR");

    [Fact]
    public void UnsupportedOpenedResourceIsDisposed()
    {
        var session = DispatchProxy.Create<IVisaSession, SessionProxy>();
        var opener = new VisaSessionOpener((_, _, _) => session);
        Assert.Throws<InstrumentUnsupportedException>(() => opener.Open(Address, new ConnectOptions()));
        Assert.Equal(1, ((SessionProxy)session).Disposals);
    }

    [Fact]
    public void ArbitrarySessionWordingIsNotASessionLimit()
    {
        var failure = new InvalidOperationException("session configuration limit is invalid");
        var opener = new VisaSessionOpener((_, _, _) => throw failure);
        var error = Assert.Throws<TransportException>(() => opener.Open(Address, new ConnectOptions()));
        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public void NativeAllocationFailureHasTypedClassificationAndCause()
    {
        var failure = new NativeVisaException(-1073807300);
        var opener = new VisaSessionOpener((_, _, _) => throw failure);
        var error = Assert.Throws<SessionLimitException>(() => opener.Open(Address, new ConnectOptions()));
        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public async Task AsyncOpenOffloadsAndDisposesWhenCancelledDuringNativeOpen()
    {
        var session = DispatchProxy.Create<IMessageBasedSession, SessionProxy>();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var opener = new VisaSessionOpener((_, _, _) => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return session; });
        var open = opener.OpenAsync(Address, new ConnectOptions(), cancellation.Token).AsTask();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { Assert.False(open.IsCompleted); cancellation.Cancel(); }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Equal(1, ((SessionProxy)session).Disposals);
    }

    public class SessionProxy : DispatchProxy
    {
        public int Disposals;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "Dispose") Disposals++;
            return method?.ReturnType == typeof(void) ? null : method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}

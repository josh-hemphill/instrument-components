using InstrumentComponents.Drivers;
using InstrumentComponents.Runtime;
using InstrumentComponents.SyntheticDriver;

namespace InstrumentComponents.Runtime.Tests;
public class CheckedCloseRegressionTests
{
    [Fact]
    public void ResourceCloseCannotBeUpgradedToCheckedShutdown()
    {
        var trace=new Trace();var handle=new DriverRegistry([new BinaryFactory(trace)]).Open(BinaryFactory.Candidate);
        handle.Close();
        Assert.Equal(DriverErrorKind.Shutdown,Assert.Throws<DriverException>(()=>handle.Close(true)).Kind);
        handle.Close();handle.Dispose();Assert.Equal(1,trace.Closes);Assert.Equal(0,trace.Shutdowns);
    }

    [Fact]
    public void ResourceCloseFailureSurvivesALaterShutdownRequest()
    {
        var trace=new Trace{FailClose=true};var handle=new DriverRegistry([new BinaryFactory(trace)]).Open(BinaryFactory.Candidate);
        var first=Assert.Throws<DriverException>(()=>handle.Close());
        var second=Assert.Throws<DriverException>(()=>handle.Close(true));
        Assert.Equal(first.Message,second.Message);Assert.Same(first.InnerException,second.InnerException);
        handle.Dispose();Assert.Equal(1,trace.Closes);Assert.Equal(0,trace.Shutdowns);
    }

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    [InlineData(true,true)]
    public void CheckedCloseRetainsFailureButImplicitDisposeDoesNotMaskIt(bool shutdownFailure,bool cleanupFailure)
    {
        var trace=new Trace{FailShutdown=shutdownFailure,FailClose=cleanupFailure};var handle=new DriverRegistry([new BinaryFactory(trace)]).Open(BinaryFactory.Candidate);
        var first=Assert.Throws<DriverException>(()=>handle.Close(true));
        foreach(var requested in new[]{true,false,true})
        {
            var again=Assert.Throws<DriverException>(()=>handle.Close(requested));
            Assert.Equal(DriverErrorKind.Shutdown,again.Kind);Assert.Equal(first.Message,again.Message);Assert.Same(first.InnerException,again.InnerException);
        }
        handle.Dispose();handle.Dispose();Assert.Equal(1,trace.Closes);Assert.Equal(1,trace.Disposes);Assert.Equal(1,trace.Shutdowns);
    }

    [Theory]
    [InlineData(false,true,false,false)]
    [InlineData(true,true,true,false)]
    [InlineData(true,false,true,false)]
    [InlineData(false,true,false,true)]
    [InlineData(false,false,false,true)]
    public async Task ConcurrentCheckedClosersObserveTheFirstAcceptedIntentAndOutcome(bool firstShutdown,bool secondShutdown,bool failShutdown,bool failClose)
    {
        using var started=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var trace=new Trace{Started=started,Release=release,FailShutdown=failShutdown,FailClose=failClose};
        var handle=new DriverRegistry([new BinaryFactory(trace)]).Open(BinaryFactory.Candidate);
        var operation=Task.Factory.StartNew(()=>handle.Dmm.MeasureVoltageDc(),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var first=Task.Factory.StartNew(()=>Record.Exception(()=>handle.Close(firstShutdown)),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        Assert.True(SpinWait.SpinUntil(()=>handle.ClosingRequested,TimeSpan.FromSeconds(5)));
        var second=Task.Factory.StartNew(()=>Record.Exception(()=>handle.Close(secondShutdown)),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        Assert.True(SpinWait.SpinUntil(()=>handle.PendingOperations==2,TimeSpan.FromSeconds(5)));
        release.Set();await operation;var outcomes=await Task.WhenAll(first,second);
        if(failShutdown||failClose){Assert.All(outcomes,e=>Assert.Equal(DriverErrorKind.Shutdown,Assert.IsType<DriverException>(e).Kind));Assert.Same(outcomes[0]!.InnerException,outcomes[1]!.InnerException);}
        else{Assert.Null(outcomes[0]);Assert.Equal(DriverErrorKind.Shutdown,Assert.IsType<DriverException>(outcomes[1]).Kind);}
        handle.Dispose();Assert.Equal(1,trace.Closes);Assert.Equal(firstShutdown?1:0,trace.Shutdowns);
        }
        finally { release.Set(); }
    }
}

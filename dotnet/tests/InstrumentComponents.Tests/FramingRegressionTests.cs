using System.Text;
using System.Text.Json;
using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Mock;
using InstrumentComponents.Scpi;

namespace InstrumentComponents.Tests;

public class FramingRegressionTests
{
    [Fact]
    public async Task SharedBlockVectorsAndSessionsUseStrictTypedErrors()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"framing-cases.json")));
        foreach(var row in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            var wire=row.GetProperty("wire").GetString()!;
            if(row.TryGetProperty("error",out var error))
            {
                if(error.GetString()=="timeout")Assert.Throws<InstrumentTimeoutException>(()=>ScpiFraming.ExtractResponse(Encoding.UTF8.GetBytes(wire),"\n"));
                else
                {
                    Assert.Throws<ParseException>(()=>ScpiFraming.ExtractResponse(Encoding.UTF8.GetBytes(wire),"\n"));
                    MockTransport Transport()=>new([new WriteStep{Data=":READ?\n"},new ReadStep{Data=wire}]);
                    using var sync=new ScpiSession(Transport(),new ConnectOptions());
                    Assert.Throws<ParseException>(()=>sync.Query(":READ?"));
                    using var native=await AsyncScpiSession.CreateAsync(Transport(),new ConnectOptions());
                    await Assert.ThrowsAsync<ParseException>(()=>native.QueryAsync(":READ?"));
                }
            }
            else
            {
                var (payload,consumed)=ScpiFraming.ExtractResponse(Encoding.UTF8.GetBytes(wire),"\n");
                Assert.Equal(row.GetProperty("payload").GetString(),Encoding.UTF8.GetString(payload));
                Assert.Equal(row.GetProperty("consumed").GetInt32(),consumed);
            }
        }
    }

    [Fact]
    public void RetryAttemptsSaturateAndStillAttemptTheOperation()
    {
        Assert.Equal(1u,ScpiProtocol.MaxWriteAttempts(true,0));
        Assert.Equal(uint.MaxValue,ScpiProtocol.MaxWriteAttempts(true,uint.MaxValue));
        Assert.Equal(1u,ScpiProtocol.MaxWriteAttempts(false,uint.MaxValue));
        using var session=new ScpiSession(new MockTransport([new WriteStep{Data=":READ?\n"},new ReadStep{Data="1\n"}]),new ConnectOptions{Retries=uint.MaxValue});
        Assert.Equal("1",session.Query(":READ?"));
    }
}

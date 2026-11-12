using System.Text;
using System.Text.Json;
using InstrumentComponents.Connect;
using InstrumentComponents.Errors;
using InstrumentComponents.Mock;
using InstrumentComponents.Scpi;

namespace InstrumentComponents.Tests;

public class ResponseLimitTests
{
    [Fact]
    public async Task SharedPayloadLimitsApplyBeforeAllocationInAllFramingsAndFrontends()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "response-limit-cases.json")));
        foreach (var row in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            var wire = row.GetProperty("wire").GetString()!;
            var limit = row.GetProperty("limit").GetInt32();
            var term = row.TryGetProperty("terminator", out var t) ? t.GetString()! : "\n";
            var error = row.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (error == "timeout")
            {
                Assert.Throws<InstrumentTimeoutException>(() => ScpiFraming.ExtractResponse(Encoding.UTF8.GetBytes(wire), term, limit));
                continue;
            }
            // An empty terminator defines one available read as an ASCII response.
            foreach (var split in Enumerable.Range(0, term.Length == 0 && !wire.StartsWith("#", StringComparison.Ordinal) ? 1 : wire.Length))
            {
                MockTransport Transport()
                {
                    List<ScriptStep> steps = [new WriteStep { Data = "READ?" + term }];
                    if (split > 0) steps.Add(new ReadStep { Data = wire[..split] });
                    steps.Add(new ReadStep { Data = wire[split..] });
                    return new(steps);
                }
                ConnectOptions Options() => new() { MaxResponseBytes = limit, Terminator = term, Retries = 2 };
                using var sync = new ScpiSession(Transport(), Options());
                using var native = await AsyncScpiSession.CreateAsync(Transport(), Options());
                if (error == "parse")
                {
                    Assert.Throws<ParseException>(() => ScpiFraming.ExtractResponse(Encoding.UTF8.GetBytes(wire), term, limit));
                    Assert.Throws<ParseException>(() => sync.Query("READ?"));
                    await Assert.ThrowsAsync<ParseException>(() => native.QueryAsync("READ?"));
                }
                else
                {
                    var expected = row.GetProperty("payload").GetString();
                    Assert.Equal(expected, Encoding.UTF8.GetString(ScpiFraming.ExtractResponse(Encoding.UTF8.GetBytes(wire), term, limit).Payload));
                    Assert.Equal(expected!.Trim(), sync.Query("READ?"));
                    Assert.Equal(expected!.Trim(), await native.QueryAsync("READ?"));
                }
            }
        }
    }
}

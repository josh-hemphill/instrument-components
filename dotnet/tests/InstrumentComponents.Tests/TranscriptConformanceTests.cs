using InstrumentComponents.Mock;
using InstrumentComponents.Scpi;
using InstrumentComponents.Connect;
namespace InstrumentComponents.Tests;
public class TranscriptConformanceTests {
 [Fact]public void SyntheticTranscriptRoundTrips(){var transcript=Transcript.FromJson("{\"steps\":[{\"op\":\"write\",\"data\":\"*IDN?\\n\"},{\"op\":\"read\",\"data\":\"Example,Device,SN,1\\n\"}]}");using var session=new ScpiSession(new MockTransport(transcript.Steps),new ConnectOptions{Retries=0});Assert.Equal("Example,Device,SN,1",session.Query("*IDN?"));}
}

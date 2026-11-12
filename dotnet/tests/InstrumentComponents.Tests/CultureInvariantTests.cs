using System.Globalization;
using InstrumentComponents.Scpi;
namespace InstrumentComponents.Tests;
public class CultureInvariantTests {
 [Fact]public void ScpiNumbersStayDotDecimalUnderGermanCulture(){var old=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=new("de-DE");Assert.Equal("3.3",ScpiFormat.Double(3.3));Assert.Equal(3.3,ScpiSession.ParseF64("3.3"));}finally{CultureInfo.CurrentCulture=old;}}
}

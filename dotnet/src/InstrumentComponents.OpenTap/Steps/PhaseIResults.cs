using OpenTap;

namespace InstrumentComponents.OpenTap;

public static class PhaseIResults
{
    public static string UnitSymbol(InstrumentComponents.Drivers.Catalog.Unit unit)=>unit switch {
        Drivers.Catalog.Unit.Volt=>"V",Drivers.Catalog.Unit.Ampere=>"A",Drivers.Catalog.Unit.Ohm=>"Ohm",Drivers.Catalog.Unit.Hertz=>"Hz",Drivers.Catalog.Unit.Second=>"s",Drivers.Catalog.Unit.Watt=>"W",Drivers.Catalog.Unit.Dbm=>"dBm",Drivers.Catalog.Unit.Celsius=>"°C",Drivers.Catalog.Unit.Kelvin=>"K",Drivers.Catalog.Unit.Count=>"Count",_=>"Unknown"};

    public const string SampleTable = "Sample";
    public const string ScalarTable = "Scalar";
    public const string IdentityTable = "Identity";

    public static readonly List<string> SampleColumns = ["Channel", "Index", "Value", "Unit"];
    public static readonly List<string> ScalarColumns = ["Name", "Value", "Unit", "LimitLow", "LimitHigh"];
    public static readonly List<string> IdentityColumns = ["Idn", "DutSerial"];

    public static void PublishSample(ResultSource results,string channel,int index,InstrumentComponents.Drivers.Catalog.Quantity value)=>PublishSample(results,channel,index,value.Value,UnitSymbol(value.Unit));

    public static void PublishSample(ResultSource results, string channel, int index, double value,string unit="Unknown") =>
        results.Publish(SampleTable, SampleColumns, channel, index, value,unit);

    public static void PublishScalar(ResultSource results,string name,InstrumentComponents.Drivers.Catalog.Quantity value,string unit)=>PublishScalar(results,name,value.Value,UnitSymbol(value.Unit));

    public static void PublishScalar(
        ResultSource results,
        string name,
        double value,
        string unit,
        double? limitLow = null,
        double? limitHigh = null) =>
        results.Publish(
            ScalarTable,
            ScalarColumns,
            name,
            value,
            unit ?? string.Empty,
            limitLow ?? double.NaN,
            limitHigh ?? double.NaN);

    public static void PublishIdentity(ResultSource results, string idn, string dutSerial) =>
        results.Publish(IdentityTable, IdentityColumns, idn, dutSerial ?? string.Empty);

    public static bool IsOutOfBand(double value, double? limitLow, double? limitHigh)
    {
        if (!double.IsFinite(value) || (limitLow is { } low && !double.IsFinite(low)) ||
            (limitHigh is { } high && !double.IsFinite(high)) || limitLow > limitHigh)
            return true;
        if (limitLow is { } lo && value < lo)
            return true;
        if (limitHigh is { } hi && value > hi)
            return true;
        return false;
    }
}

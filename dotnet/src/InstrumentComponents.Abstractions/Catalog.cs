using InstrumentComponents.Drivers;
namespace InstrumentComponents.Drivers.Catalog;

public enum Unit { Volt, Ampere, Ohm, Celsius, Hertz, Second, Watt, Dbm, Count, Unknown, ConfiguredDmm, ConfiguredPower }
public enum ReplyKind { Unit, Quantity, Boolean, Count, Samples, Trace, Identity }
public readonly record struct Quantity(double Value, Unit Unit, MeasurementStatus Status = MeasurementStatus.Valid);
public sealed record Samples(IReadOnlyList<double> Values, Unit Unit, double? SampleIntervalS = null);
public sealed record DeviceIdentity(string Manufacturer, string Model, string Serial, string Revision);
public enum ArgumentKind { Number, OptionalNumber, Channel, Boolean, Symbol }
public readonly record struct Argument(ArgumentKind Kind, double? Number = null, uint ChannelValue = 0, bool Bool = false, string? Token = null)
{
    public static Argument Numeric(double v) => new(ArgumentKind.Number, v);
    public static Argument Optional(double? v) => new(ArgumentKind.OptionalNumber, v);
    public static Argument Channel(uint v) => new(ArgumentKind.Channel, ChannelValue: v);
    public static Argument Boolean(bool v) => new(ArgumentKind.Boolean, Bool: v);
    public static Argument Symbol(string v) => new(ArgumentKind.Symbol, Token: v);
}
public static class ContractValidation
{
    public static DriverException Invalid(string message) => new(DriverErrorKind.InvalidArgument, message);
    public static void Number(double? value, string constraint)
    { if (value is { } v && (!double.IsFinite(v) || (constraint == "positive" && v <= 0) || (constraint == "nonnegative" && v < 0) || (constraint == "percent" && (v < 0 || v > 100)))) throw Invalid("Invalid numeric argument"); }
    public static void Symbol(string value)
    { if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw Invalid("Invalid semantic token"); }
}
public abstract record CatalogReply
{
    public sealed record Empty : CatalogReply;
    public sealed record Numeric(Quantity Value) : CatalogReply;
    public sealed record State(bool Value) : CatalogReply;
    public sealed record Count(uint Value) : CatalogReply;
    public sealed record Trace(Samples Value) : CatalogReply;
    public sealed record Identity(DeviceIdentity Value) : CatalogReply;
    private static DriverException Contract() => new(DriverErrorKind.DriverContractViolation, "Provider reply violates operation kind/unit/value contract");
    public CatalogReply OwnedCopy() => this is Trace t ? new Trace(t.Value with { Values = Array.AsReadOnly(t.Value.Values.ToArray()) }) : this;
    public void Validate(CatalogRequest request)
    {
        var (kind, expected) = request.Result;
        bool UnitOk(Unit u) => expected switch { Unit.ConfiguredDmm => u is Unit.Volt or Unit.Ampere or Unit.Ohm or Unit.Celsius, Unit.ConfiguredPower => u is Unit.Watt or Unit.Dbm, _ => u == expected };
        var valid = (this, kind) switch
        {
            (Empty, ReplyKind.Unit) or (State, ReplyKind.Boolean) or (Count, ReplyKind.Count) => true,
            (Numeric q, ReplyKind.Quantity) => double.IsFinite(q.Value.Value) && Enum.IsDefined(q.Value.Status) && UnitOk(q.Value.Unit),
            (Trace s, ReplyKind.Samples or ReplyKind.Trace) => s.Value.Values.Count is > 0 and <= 1000000 && s.Value.Values.All(double.IsFinite) && UnitOk(s.Value.Unit) && (s.Value.SampleIntervalS is null || double.IsFinite(s.Value.SampleIntervalS.Value) && s.Value.SampleIntervalS > 0),
            (Identity i, ReplyKind.Identity) => !string.IsNullOrWhiteSpace(i.Value.Manufacturer) && !string.IsNullOrWhiteSpace(i.Value.Model),
            _ => false,
        };
        if (!valid) throw Contract();
    }
    public void IntoUnit() { if (this is not Empty) throw Contract(); }
    public Quantity IntoQuantity() => this is Numeric q ? q.Value : throw Contract();
    public bool IntoBoolean() => this is State v ? v.Value : throw Contract();
    public uint IntoCount() => this is Count v ? v.Value : throw Contract();
    public Samples IntoSamples() => this is Trace v ? v.Value : throw Contract();
    public Samples IntoTrace() => IntoSamples();
    public DeviceIdentity IntoIdentity() => this is Identity v ? v.Value : throw Contract();
}
public interface ICatalogBackend { CatalogReply Invoke(CatalogRequest request); }
public interface IAsyncCatalogBackend { ValueTask<CatalogReply> InvokeAsync(CatalogRequest request, CancellationToken cancellationToken = default); }
public interface ICatalogExecutor { CatalogReply Invoke(CatalogRequest request); }
public interface IAsyncCatalogExecutor { Task<CatalogReply> InvokeAsync(CatalogRequest request, CancellationToken cancellationToken = default); }

public enum ParameterKind { Number, OptionalNumber, Channel, Boolean, Symbol, Waveform, PowerUnit, SenseMode }
public sealed record ParameterSpec(string Name, ParameterKind Kind, string Constraint);
public sealed record OperationMetadata(string Kind, IReadOnlyList<ParameterSpec> Parameters, ReplyKind Result, Unit Unit);

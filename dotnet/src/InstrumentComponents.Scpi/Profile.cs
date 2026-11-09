using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using InstrumentComponents.Drivers;
using InstrumentComponents.Drivers.Catalog;
using DeviceIdentity = InstrumentComponents.Drivers.Catalog.DeviceIdentity;
namespace InstrumentComponents.Scpi;

public enum OutputKind { DcPowerSupply, FunctionGenerator }
public sealed record Output(OutputKind Kind, uint? Channel = null);
public enum ResponseRole { Write, Value, Completion, Preamble }
public sealed record Step(string Command, ResponseRole Response = ResponseRole.Write, string? When = null);
public sealed record Plan(CatalogOperation Operation, IReadOnlyList<Step> Steps,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Tokens = null,
    IReadOnlyDictionary<string, string>? Defaults = null);
public sealed class ScpiProfile
{
    public string Id { get; }
    public IReadOnlyList<uint>? ChannelIds => Channels;
    public IReadOnlyList<Output>? OutputChannelIds => Outputs;
    internal IReadOnlyList<uint>? Channels { get; }
    internal IReadOnlyList<Output>? Outputs { get; }
    internal FrozenDictionary<CatalogOperation, Plan> Plans { get; }
    public IReadOnlySet<Operation> Operations { get; }
    internal static DriverException Invalid(string message) => new(DriverErrorKind.DriverContractViolation, message);
    private static bool SafeToken(string s) => s.Length is > 0 and <= 128 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+');
    public static IReadOnlySet<string> Placeholders(string template)
    {
        var names = new HashSet<string>(StringComparer.Ordinal); var rest = template.AsSpan();
        while (rest.IndexOf('{') is var start && start >= 0)
        {
            if (rest[..start].Contains('}')) throw Invalid("Unmatched template brace");
            rest = rest[(start + 1)..]; var end = rest.IndexOf('}');
            if (end < 0) throw Invalid("Unmatched template brace");
            var key = rest[..end].ToString();
            if (key.Length == 0 || !char.IsAsciiLetter(key[0]) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw Invalid("Invalid placeholder");
            names.Add(key); rest = rest[(end + 1)..];
        }
        if (rest.Contains('}')) throw Invalid("Unmatched template brace");
        return names;
    }
    public ScpiProfile(string id, IEnumerable<uint>? channels, IEnumerable<Output>? outputs, IEnumerable<Plan> plans)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || !id.Contains('.') || id.Split('.').Any(string.IsNullOrEmpty) || id[0] is not (>= 'a' and <= 'z') || id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))) throw Invalid("Invalid profile id");
        Id = id; Channels = channels is null ? null : Array.AsReadOnly(channels.ToArray()); Outputs = outputs is null ? null : Array.AsReadOnly(outputs.ToArray());
        foreach (var list in new[] { Channels }.OfType<IReadOnlyList<uint>>())
            if (list.Count > 4096 || list.Any(c => c == 0) || list.Distinct().Count() != list.Count) throw Invalid("Invalid/duplicate channel inventory");
        if (Outputs is not null && (Outputs.Count > 4096 || Outputs.Distinct().Count() != Outputs.Count || Outputs.Any(o => !Enum.IsDefined(o.Kind) || o.Kind == OutputKind.DcPowerSupply && (o.Channel is null or 0 || Channels is null || !Channels.Contains(o.Channel.Value)) || o.Kind == OutputKind.FunctionGenerator && o.Channel is not null))) throw Invalid("Invalid/duplicate output inventory");
        var indexed = new Dictionary<CatalogOperation, Plan>();
        foreach (var original in plans)
        {
            var tokens = (original.Tokens ?? new Dictionary<string, IReadOnlyDictionary<string, string>>()).ToFrozenDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value.ToFrozenDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
            var defaults = (original.Defaults ?? new Dictionary<string, string>()).ToFrozenDictionary(StringComparer.Ordinal);
            var plan = original with { Steps = Array.AsReadOnly(original.Steps.ToArray()), Tokens = tokens, Defaults = defaults };
            var metadata = plan.Operation.Describe(); var parameters = metadata.Parameters.ToDictionary(p => p.Name, p => p.Kind, StringComparer.Ordinal);
            if (indexed.ContainsKey(plan.Operation) || plan.Steps.Count > 32) throw Invalid("Duplicate operation or excessive plan length");
            var used = new HashSet<string>(StringComparer.Ordinal); var values = 0; var preambles = 0;
            foreach (var step in plan.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Command) || Encoding.UTF8.GetByteCount(step.Command) > 65536 || step.Command.Any(c => c < 32 || c > 126) || !Enum.IsDefined(step.Response)) throw Invalid("Invalid message body/response role");
                var queries = step.Command.Count(c => c == '?');
                if ((step.Response == ResponseRole.Write && queries != 0) || (step.Response != ResponseRole.Write && queries != 1)) throw Invalid("Message/query response role mismatch");
                if (step.When is not null && (!parameters.TryGetValue(step.When, out var conditionalKind) || conditionalKind != ParameterKind.OptionalNumber || step.Response is ResponseRole.Value or ResponseRole.Preamble)) throw Invalid("Invalid conditional step");
                foreach (var key in Placeholders(step.Command)) { if (!parameters.ContainsKey(key)) throw Invalid("Unknown operation placeholder"); used.Add(key); }
                if (step.Response == ResponseRole.Preamble && values != 0) throw Invalid("Preamble must precede trace data");
                if (step.Response == ResponseRole.Value) values++; if (step.Response == ResponseRole.Preamble) preambles++;
            }
            foreach (var p in metadata.Parameters)
            {
                if (p.Kind != ParameterKind.OptionalNumber && !used.Contains(p.Name)) throw Invalid("Required argument ignored by plan");
                if (p.Kind == ParameterKind.Channel && Channels is null) throw Invalid("Channel operation requires explicit inventory");
                if (p.Kind is ParameterKind.Boolean or ParameterKind.Symbol or ParameterKind.Waveform or ParameterKind.PowerUnit or ParameterKind.SenseMode && !tokens.ContainsKey(p.Name)) throw Invalid("Semantic arguments require wire token mapping");
            }
            foreach (var entry in tokens)
                if (!parameters.TryGetValue(entry.Key, out var pk) || pk is not (ParameterKind.Boolean or ParameterKind.Symbol or ParameterKind.Waveform or ParameterKind.PowerUnit or ParameterKind.SenseMode) || entry.Value.Count == 0 || entry.Value.Any(p => !SafeToken(p.Key) || !SafeToken(p.Value))) throw Invalid("Invalid token mapping");
            foreach (var entry in defaults) {
                if (!parameters.TryGetValue(entry.Key, out var pk) || pk != ParameterKind.OptionalNumber || !SafeToken(entry.Value)) throw Invalid("Invalid optional numeric default");
                if (entry.Value is not ("AUTO" or "DEF" or "MIN" or "MAX")) {
                    double value; try { value=ScpiNumbers.Parse(entry.Value); } catch(DriverException) { throw Invalid("Invalid numeric default"); }
                    try { ContractValidation.Number(value, metadata.Parameters.Single(p=>p.Name==entry.Key).Constraint); } catch(DriverException) { throw Invalid("Numeric default violates argument constraint"); }
                }
            }
            var local = plan.Operation == CatalogOperation.DcPowerSupplyChannelCount; var shutdown = plan.Operation == CatalogOperation.Shutdown;
            if (local && Outputs is null) throw Invalid("Channel count requires known inventory");
            if (shutdown && Outputs is null) throw Invalid("Shutdown requires complete output inventory");
            if (shutdown && Outputs!.Count > 0 && plan.Steps.Count > 0) throw Invalid("Output shutdown derives individual disable plans");
            if (local && plan.Steps.Count != 0) throw Invalid("Channel-count metadata must perform no IO");
            if (plan.Operation == CatalogOperation.SpectrumAnalyzerWaitOpc && !plan.Steps.Any(s => s.Response == ResponseRole.Completion)) throw Invalid("Completion requires explicit completion query");
            if (!local && !shutdown && plan.Steps.Count == 0) throw Invalid("Empty operation plan");
            if (metadata.Result is ReplyKind.Quantity or ReplyKind.Boolean or ReplyKind.Samples or ReplyKind.Identity && values != 1) throw Invalid("Exactly one value query required");
            if (metadata.Result == ReplyKind.Trace && (values != 1 || preambles != 1)) throw Invalid("Trace requires preamble/data queries");
            if (metadata.Result is ReplyKind.Unit or ReplyKind.Count && values != 0) throw Invalid("Unexpected value query");
            if (metadata.Result != ReplyKind.Trace && preambles != 0) throw Invalid("Unexpected preamble query");
            indexed.Add(plan.Operation, plan);
        }
        if (Outputs is not null && indexed.ContainsKey(CatalogOperation.Shutdown)) {
            foreach (var output in Outputs) if (!indexed.ContainsKey(output.Kind == OutputKind.DcPowerSupply ? CatalogOperation.DcPowerSupplyOutputEnable : CatalogOperation.FunctionGeneratorOutputEnable)) throw Invalid("Shutdown lacks a declared output's disable capability");
            if (indexed.ContainsKey(CatalogOperation.DcPowerSupplyOutputEnable) && !Outputs.Any(o => o.Kind == OutputKind.DcPowerSupply) || indexed.ContainsKey(CatalogOperation.FunctionGeneratorOutputEnable) && !Outputs.Any(o => o.Kind == OutputKind.FunctionGenerator)) throw Invalid("Output capability cannot claim zero corresponding outputs");
        }
        if (indexed.Count == 0) throw Invalid("Profile requires operation plans");
        Plans = indexed.ToFrozenDictionary(); var operations = indexed.Keys.Select(o => o.Runtime()).ToHashSet();
        foreach (var (catalog, basic) in new[] { (CatalogOperation.DmmMeasureVoltageDc, Operation.MeasureVoltageDc), (CatalogOperation.DcPowerSupplySetVoltage, Operation.SetVoltage), (CatalogOperation.DcPowerSupplyReadVoltage, Operation.ReadVoltage), (CatalogOperation.DcPowerSupplyOutputEnable, Operation.OutputEnable), (CatalogOperation.DcPowerSupplySetSenseMode, Operation.RemoteSense), (CatalogOperation.Reset, Operation.Reset), (CatalogOperation.Shutdown, Operation.Shutdown) }) if (indexed.ContainsKey(catalog)) operations.Add(basic);
        Operations = operations.ToFrozenSet();
    }
}
internal sealed record Prepared(IReadOnlyList<Step> Steps, ReplyKind Kind, Unit Unit);
internal sealed class Engine(ScpiProfile profile)
{
    internal ScpiProfile Profile { get; } = profile;
    private Unit? _dmm, _power;
    internal void Invalidate() { _dmm = null; _power = null; }
    internal Prepared Prepare(CatalogRequest request)
    {
        request.Validate(); if (!Profile.Plans.TryGetValue(request.Operation, out var plan)) throw new DriverException(DriverErrorKind.UnsupportedOperation, "Operation not in profile");
        if (request.Operation == CatalogOperation.Shutdown && Profile.Outputs is { Count: > 0 })
        {
            var shutdownSteps = new List<Step>();
            foreach (var output in Profile.Outputs)
            { CatalogRequest disable = output.Kind == OutputKind.DcPowerSupply ? new DcPowerSupplyOutputEnableRequest(output.Channel!.Value, false) : new FunctionGeneratorOutputEnableRequest(false); shutdownSteps.AddRange(Prepare(disable).Steps); }
            return new(shutdownSteps, ReplyKind.Unit, Unit.Unknown);
        }
        var (kind, unit) = request.Result;
        unit = unit switch { Unit.ConfiguredDmm => _dmm ?? throw ContractValidation.Invalid("Configure DMM function before reading"), Unit.ConfiguredPower => _power ?? throw ContractValidation.Invalid("Configure power unit before reading"), _ => unit };
        var args = request.Arguments; var used = new HashSet<string>(StringComparer.Ordinal); var steps = new List<Step>();
        foreach (var step in plan.Steps)
        {
            if (step.When is not null && args[step.When] is { Kind: ArgumentKind.OptionalNumber, Number: null }) continue;
            var command = step.Command;
            foreach (var name in ScpiProfile.Placeholders(command))
            {
                var arg = args[name]; string text;
                if (arg.Kind == ArgumentKind.Number || arg.Kind == ArgumentKind.OptionalNumber && arg.Number is not null) text = ScpiNumbers.Format(arg.Number!.Value);
                else if (arg.Kind == ArgumentKind.Count) text = arg.CountValue.ToString(CultureInfo.InvariantCulture);
                else if (arg.Kind == ArgumentKind.OptionalNumber) text = plan.Defaults!.TryGetValue(name, out var d) ? d : throw new DriverException(DriverErrorKind.UnsupportedOperation, "Profile has no default for absent argument");
                else if (arg.Kind == ArgumentKind.Channel) { if (Profile.Channels is null || !Profile.Channels.Contains(arg.ChannelValue) || request.Operation.Describe().Kind == "DcPowerSupply" && Profile.Outputs is not null && !Profile.Outputs.Contains(new Output(OutputKind.DcPowerSupply, arg.ChannelValue))) throw ContractValidation.Invalid("Unknown physical channel"); text = arg.ChannelValue.ToString(CultureInfo.InvariantCulture); }
                else { var key = arg.Kind == ArgumentKind.Boolean ? (arg.Bool ? "true" : "false") : arg.Token!; text = plan.Tokens!.TryGetValue(name, out var map) && map.TryGetValue(key, out var token) ? token : throw new DriverException(DriverErrorKind.UnsupportedOperation, "Semantic mode not mapped"); }
                command = command.Replace("{" + name + "}", text, StringComparison.Ordinal); used.Add(name);
            }
            steps.Add(step with { Command = command, When = null });
        }
        if (args.Any(p => !(p.Value.Kind == ArgumentKind.OptionalNumber && p.Value.Number is null) && !used.Contains(p.Key))) throw new DriverException(DriverErrorKind.UnsupportedOperation, "Profile cannot honor supplied argument");
        return new(steps, kind, unit);
    }
    internal CatalogReply Finish(CatalogRequest request, Prepared prepared, string? response, string? preamble)
    {
        var raw = response ?? "";
        CatalogReply reply = prepared.Kind switch
        {
            ReplyKind.Unit => new CatalogReply.Empty(),
            ReplyKind.Count => new CatalogReply.Count((uint)(Profile.Outputs?.Count(o => o.Kind == OutputKind.DcPowerSupply) ?? throw new DriverException(DriverErrorKind.UnsupportedOperation, "Unknown channel inventory"))),
            ReplyKind.Quantity => Numeric(raw, prepared.Unit),
            ReplyKind.Boolean => new CatalogReply.State(raw.Trim().ToUpperInvariant() switch { "1" or "+1" or "ON" or "CLOSED" => true, "0" or "+0" or "OFF" or "OPEN" => false, _ => throw ScpiNumbers.ParseError("Invalid boolean response") }),
            ReplyKind.Identity => Identity(raw),
            ReplyKind.Samples or ReplyKind.Trace => Trace(raw, preamble, prepared),
            _ => throw ScpiProfile.Invalid("Unknown reply kind"),
        };
        reply.Validate(request);
        if (request.Operation == CatalogOperation.Reset) Invalidate();
        if (request.ConfiguredUnit is { } unit) { if (unit is Unit.Watt or Unit.Dbm) _power = unit; else _dmm = unit; }
        return reply;
    }
    private static CatalogReply Numeric(string raw, Unit unit)
    { var value = ScpiNumbers.Parse(raw); return new CatalogReply.Numeric(new(value, unit, Math.Abs(value) >= 9e36 ? MeasurementStatus.Overload : MeasurementStatus.Valid)); }
    private static CatalogReply Identity(string raw)
    { var parts = raw.Trim().Split(',').Select(p => p.Trim()).ToArray(); if (parts.Length != 4 || parts[0].Length == 0 || parts[1].Length == 0) throw ScpiNumbers.ParseError("Identity requires four fields"); return new CatalogReply.Identity(new DeviceIdentity(parts[0], parts[1], parts[2], parts[3])); }
    private static CatalogReply Trace(string raw, string? preamble, Prepared prepared)
    {
        double? dt = null;
        if (prepared.Kind == ReplyKind.Trace) { var p = ScpiNumbers.Csv(preamble ?? ""); if (p.Count < 5 || p[4] <= 0) throw ScpiNumbers.ParseError("Invalid ASCII preamble"); dt = p[4]; }
        return new CatalogReply.Trace(new(ScpiNumbers.Csv(raw), prepared.Unit, dt));
    }
}
public static class ScpiNumbers
{
    public const int MaxResponseBytes = 1048576;
    public static DriverException ParseError(string message) => new(DriverErrorKind.Parse, message);
    public static double Parse(string raw)
    { if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw ParseError("Invalid/nonfinite numeric response"); return value; }
    internal static IReadOnlyList<double> Csv(string raw)
    { if (string.IsNullOrWhiteSpace(raw) || Encoding.UTF8.GetByteCount(raw) > MaxResponseBytes) throw ParseError("Empty/oversize CSV"); return Array.AsReadOnly(raw.Trim().Split(',').Select(Parse).ToArray()); }
    internal static string Checked(string raw)
    { if (Encoding.UTF8.GetByteCount(raw) > MaxResponseBytes || raw.Contains('\0')) throw ParseError("Invalid/oversize response message"); return raw; }
    internal static void Completion(string raw) { if (raw.Trim() is not ("1" or "+1")) throw ParseError("Completion must be 1"); }
    public static string Format(double value)
    {
        if (!double.IsFinite(value)) throw ContractValidation.Invalid("Nonfinite number"); if (value == 0) return "0";
        var parts = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant().Split('e'); var mantissa = parts[0]; var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var negative = mantissa.StartsWith('-'); var body = mantissa.TrimStart('-'); var point = body.IndexOf('.'); if (point < 0) point = body.Length;
        var digits = body.Replace(".", "", StringComparison.Ordinal); var first = 0; while (digits[first] == '0') first++;
        var significant = digits[first..].TrimEnd('0'); var exp = exponent + point - first - 1;
        var result = (negative ? "-" : "") + significant[0] + (significant.Length > 1 ? "." + significant[1..] : ""); return exp == 0 ? result : result + "e" + exp.ToString(CultureInfo.InvariantCulture);
    }
}

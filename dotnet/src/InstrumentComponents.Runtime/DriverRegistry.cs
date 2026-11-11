using System.Collections.Frozen;
using InstrumentComponents.Drivers;

namespace InstrumentComponents.Runtime;

public sealed record DriverSelection(string? DriverId = null,
    IReadOnlyDictionary<string, int>? Preferences = null, bool AllowStandardScpi = false);
public sealed record MatchEvidence(string DriverId, int Preference, byte Rank, bool Explicit);

/// <summary>Frozen instance registry of factories referenced explicitly by the application.</summary>
public sealed partial class DriverRegistry
{
    private sealed record Registration(DriverDescriptor Descriptor, IDriverFactory Factory, FrozenSet<ExecutionMode> Modes);
    private readonly SortedDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);

    public DriverRegistry(IEnumerable<IDriverFactory> factories)
    {
        foreach (var factory in factories)
        {
            var d = factory.Descriptor;
            if (d.ContractMajor != DriverContract.Major)
                throw Error(DriverErrorKind.IncompatibleContract, d.Id);
            if (string.IsNullOrEmpty(d.Id) || d.Id.Trim() != d.Id || !d.Id.Contains('.')
                || d.Id[0] is not (>= 'a' and <= 'z') || d.Id.Split('.').Any(string.IsNullOrEmpty)
                || d.Id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
                || d.Rules.Count == 0 || d.Operations.Any(o => !Enum.IsDefined(o)))
                throw Error(DriverErrorKind.InvalidArgument, "Driver needs a namespaced lowercase ID and matching rules.");
            foreach (var rule in d.Rules)
            {
                var invalid = string.IsNullOrWhiteSpace(rule.Scheme) || rule switch
                {
                    ExactIdentityRule e => string.IsNullOrWhiteSpace(e.Manufacturer) || string.IsNullOrWhiteSpace(e.Model),
                    FamilyRule f => string.IsNullOrWhiteSpace(f.Manufacturer) || f.Manufacturer.Contains('*') || string.IsNullOrWhiteSpace(f.ModelPattern),
                    ExactProductRule or StandardScpiRule => false,
                    _ => true,
                };
                if (invalid) throw Error(DriverErrorKind.InvalidArgument, "Invalid matching rule.");
            }
            var modes = factory.ExecutionModes.ToFrozenSet();
            if (modes.Count == 0 || modes.Any(m => !Enum.IsDefined(m))) throw Error(DriverErrorKind.InvalidArgument, "Invalid execution modes.");
            if (!_registrations.TryAdd(d.Id, new Registration(d, factory, modes)))
                throw Error(DriverErrorKind.DuplicateDriver, d.Id);
        }
    }

    // ASCII folding is intentional and identical to Rust, independent of current culture.
    private static string Normalize(string s) => string.Concat(s.Trim().Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));
    private static bool Same(string a, string b) => Normalize(a) == Normalize(b);
    private static bool Pattern(string pattern, string value)
    {
        var p = Normalize(pattern); var v = Normalize(value);
        int pi = 0, vi = 0, star = -1, retry = 0;
        while (vi < v.Length)
        {
            if (pi < p.Length && p[pi] != '*' && p[pi] == v[vi]) { pi++; vi++; }
            else if (pi < p.Length && p[pi] == '*') { star = pi++; retry = vi; }
            else if (star >= 0) { vi = ++retry; pi = star + 1; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
    private static byte? Rank(MatchRule rule, DeviceCandidate c, bool standardScpi)
    {
        if (!Same(rule.Scheme, c.Endpoint.Scheme)) return null;
        return rule switch
        {
            ExactIdentityRule e when c.Identity.Manufacturer is { } mfr && c.Identity.Model is { } model
                && Same(e.Manufacturer, mfr) && Same(e.Model, model) => 3,
            ExactProductRule e when e.VendorId == c.Identity.VendorId && e.ProductId == c.Identity.ProductId => 3,
            FamilyRule f when c.Identity.Manufacturer is { } mfr && c.Identity.Model is { } model
                && Same(f.Manufacturer, mfr) && Pattern(f.ModelPattern, model) => 2,
            StandardScpiRule when standardScpi => 1,
            _ => null,
        };
    }

    /// <summary>Pure passive discovery/selection: never opens a connection or probes.</summary>
    public MatchEvidence Select(DeviceCandidate candidate, DriverSelection? selection = null)
    {
        selection ??= new();
        if (string.IsNullOrWhiteSpace(candidate.Endpoint.Scheme) || string.IsNullOrWhiteSpace(candidate.Endpoint.Address))
            throw Error(DriverErrorKind.InvalidArgument, "Endpoint scheme/address required.");
        if (selection.DriverId is { } id)
        {
            if (!_registrations.TryGetValue(id, out var registration)) throw Error(DriverErrorKind.DriverNotFound, id);
            if (!registration.Descriptor.Rules.Any(r => Same(r.Scheme, candidate.Endpoint.Scheme)))
                throw Error(DriverErrorKind.DriverNotFound, "Explicit driver does not support endpoint scheme.");
            return new(id, 0, 0, true);
        }
        var matches = new List<MatchEvidence>();
        foreach (var (driverId, registration) in _registrations)
        {
            var ranks = registration.Descriptor.Rules.Select(r => Rank(r, candidate, selection.AllowStandardScpi)).Where(r => r.HasValue).ToArray();
            if (ranks.Length == 0) continue;
            var preference = selection.Preferences?.TryGetValue(driverId, out var p) == true ? p : 0;
            matches.Add(new(driverId, preference, ranks.Max()!.Value, false));
        }
        var ordered = matches.OrderByDescending(m => m.Preference).ThenByDescending(m => m.Rank).ThenBy(m => m.DriverId, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) throw Error(DriverErrorKind.DriverNotFound, "No eligible driver.");
        var first = ordered[0];
        var tied = ordered.Where(m => m.Preference == first.Preference && m.Rank == first.Rank).Select(m => m.DriverId).ToArray();
        if (tied.Length > 1) throw Error(DriverErrorKind.AmbiguousDriver, string.Join(", ", tied));
        return first;
    }

    public DeviceHandle Open(DeviceCandidate candidate, DriverSelection? selection = null,
        IEnumerable<Operation>? required = null, ExecutionMode mode = ExecutionMode.Sync,
        ConnectionOwnership ownership = ConnectionOwnership.Owned)
    {
        if (mode != ExecutionMode.Sync)
            throw Error(DriverErrorKind.UnsupportedExecutionMode, "Direct sync Open requires Sync; use OpenNativeAsync or OpenWorkerAsync.");
        var evidence = Select(candidate, selection);
        var registration = _registrations[evidence.DriverId];
        if (!registration.Modes.Contains(ExecutionMode.Sync)) throw Error(DriverErrorKind.UnsupportedExecutionMode, "Direct sync unavailable.");
        var requested = (required ?? []).ToFrozenSet();
        if (!requested.IsSubsetOf(registration.Descriptor.Operations))
            throw Error(DriverErrorKind.UnsupportedOperation, "Required operation outside driver support envelope.");
        var backend = registration.Factory.Open(new(candidate, ownership, requested));
        try
        {
            var operations = backend.Operations.ToFrozenSet();
            var invalid = !operations.IsSubsetOf(registration.Descriptor.Operations)
                || (operations.Any(o => o > Operation.Shutdown) && backend.Catalog is null)
                || (operations.Contains(Operation.MeasureVoltageDc) && backend.Dmm is null)
                || (operations.Overlaps([Operation.SetVoltage, Operation.ReadVoltage, Operation.OutputEnable]) && backend.Psu is null)
                || (operations.Contains(Operation.RemoteSense) && backend.RemoteSense is null)
                || (operations.Contains(Operation.Reset) && backend.Reset is null)
                || (operations.Contains(Operation.Shutdown) && backend.Shutdown is null);
            if (invalid) throw Error(DriverErrorKind.DriverContractViolation, "Invalid opened capabilities.");
            if (!requested.IsSubsetOf(operations)) throw Error(DriverErrorKind.UnsupportedOperation, "Insufficient opened capabilities.");
            return new(backend, operations, evidence);
        }
        catch (Exception failure)
        {
            try { backend.Dispose(); }
            catch (Exception cleanup)
            {
                var kind = failure is DriverException d ? d.Kind : DriverErrorKind.DriverContractViolation;
                throw new DriverException(kind, "Opened capability validation and cleanup failed.", new AggregateException(failure, cleanup));
            }
            throw;
        }
    }

    internal static DriverException Error(DriverErrorKind kind, string message) => new(kind, message);
}

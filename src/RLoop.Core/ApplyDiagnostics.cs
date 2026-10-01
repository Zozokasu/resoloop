using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record ApplySourcePoint(int Offset, int Line, int Column);
public sealed record ApplySourceRange(ApplySourcePoint Start, ApplySourcePoint End);
public sealed record ApplyDiagnosticSource(string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? File = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApplySourceRange? Range = null)
{
    public static ApplyDiagnosticSource Unknown { get; } = new("unknown");
}
// Value is deliberately serialized even when null: known null is evidence.
public sealed record ApplyDiagnosticValue(string Status, object? Value)
{
    public static ApplyDiagnosticValue Unknown { get; } = new("unknown", null);
    public static ApplyDiagnosticValue Known(object? value) => new("known", value);
}
public sealed record ApplyDiagnostic(string DiagnosticVersion, string Code, string Severity, string Message,
    string Phase, string? BuildId, string? EntityKind, string? Key, string? Member, string? JsonPath,
    IReadOnlyList<object>? PathSegments, ApplyDiagnosticSource Source, IReadOnlyList<ApplyDiagnosticSource> Related,
    ApplyDiagnosticValue Expected, ApplyDiagnosticValue Observed, IReadOnlyDictionary<string, string> Completeness);
public sealed record ApplyDiagnosticReport(string DiagnosticVersion, IReadOnlyList<ApplyDiagnostic> Diagnostics);
public sealed record ApplyDetailedValidation(ApplyValidationResult Result, IReadOnlyList<ApplyDiagnostic> Diagnostics);

// Paths are assembled while walking IR, before any legacy string is flattened.
internal sealed record ApplyIssuePath(IReadOnlyList<object> Segments, string Legacy)
{
    internal static ApplyIssuePath Root { get; } = new([], "$");
    internal ApplyIssuePath Property(string name) => new(Segments.Append((object)name).ToArray(), Legacy + "." + name);
    internal ApplyIssuePath Index(int index) => new(Segments.Append((object)index).ToArray(), Legacy + $"[{index}]");
    internal string JsonPath => "$" + string.Concat(Segments.Select((s, n) => s is int i ? $"[{i}]" :
        s is string p && (n == 0 && p is "slot" or "components" or "children" ||
            n >= 2 && Segments[n - 1] is int && Segments[n - 2] is string collection &&
            (collection == "children" && p is "slot" or "components" or "children" ||
             collection == "components" && p is "type" or "fields" or "initialFields")) ? "." + p : "[" + JsonSerializer.Serialize(s) + "]"));
    public override string ToString() => Legacy;
}
internal sealed record ApplyIssueDetail(ApplyIssuePath Path, string? EntityKind, string? Key, string? Member,
    ApplyDiagnosticValue Expected, ApplyDiagnosticValue Observed);

public static class ApplyDiagnostics
{
    private static readonly ConditionalWeakTable<ApplyValidationIssue, ApplyIssueDetail> details = new();
    private static readonly ConditionalWeakTable<ApplyValidationResult, ApplyDetailedValidation> results = new();
    private static readonly ConditionalWeakTable<Exception, ApplyDiagnosticReport> failures = new();

    internal static void Add(List<ApplyValidationIssue> issues, string code, string message, ApplyIssuePath path,
        string? key, string? member, object? expected = null, bool expectedKnown = false, object? observed = null, bool observedKnown = false)
    {
        var issue = new ApplyValidationIssue(code, message, path.Legacy);
        issues.Add(issue);
        details.Add(issue, new(path, "component", key, member,
            expectedKnown ? ApplyDiagnosticValue.Known(expected) : ApplyDiagnosticValue.Unknown,
            observedKnown ? ApplyDiagnosticValue.Known(observed) : ApplyDiagnosticValue.Unknown));
    }

    internal static ApplyValidationResult Complete(ApplyDocument document, ApplyValidationResult result)
    {
        var diagnostics = result.Issues.Select(issue => Create(document, issue)).ToArray();
        results.Add(result, new(result, diagnostics));
        return result;
    }
    public static ApplyDetailedValidation ForResult(ApplyValidationResult result) => results.TryGetValue(result, out var value)
        ? value : new(result, result.Issues.Select(issue => Unknown(issue.Code, issue.Message, "validate", issue.Severity) with { JsonPath = issue.Path }).ToArray());
    public static ApplyDiagnosticReport ForException(Exception error, string phase, string? buildId = null) =>
        failures.TryGetValue(error, out var value) ? value with { Diagnostics = value.Diagnostics.Select(d => d with { Phase = phase }).ToArray() } :
        new("1", [Unknown(error is RLoopException r ? r.Code : "UNEXPECTED_ERROR", error.Message, phase, buildId: buildId)]);
    internal static void Attach(Exception error, ApplyValidationResult result) => failures.Add(error, new("1", ForResult(result).Diagnostics));
    public static ApplyDiagnostic Unknown(string code, string message, string phase, string severity = "error", string? buildId = null) =>
        new("1", code, severity, message, phase, buildId, null, null, null, null, null,
            ApplyDiagnosticSource.Unknown, [], ApplyDiagnosticValue.Unknown, ApplyDiagnosticValue.Unknown,
            Completeness(false, false));
    private static IReadOnlyDictionary<string, string> Completeness(bool location, bool catalog) => new Dictionary<string, string>
    {
        ["location"] = location ? "complete" : "unknown", ["type"] = catalog ? "partial" : "unknown",
        ["member"] = catalog ? "partial" : "unknown", ["reference"] = catalog ? "partial" : "unknown",
        ["inputs"] = "unknown", ["runtime"] = "unknown"
    };
    private static ApplyDiagnostic Create(ApplyDocument document, ApplyValidationIssue issue)
    {
        details.TryGetValue(issue, out var detail);
        var entry = detail is null || document.BuildBundle?.MatchesOriginal(document, detail.Path.Segments) != true ? null :
            document.BuildBundle.FindEntry(detail.Path.Segments, detail.EntityKind, detail.Key, detail.Member);
        // No location fallback by message or a prefix/nearest-entry guess.
        var reference = issue.Code is "APPLY_REFERENCE_NOT_FOUND" or "APPLY_MEMBER_REFERENCE_NOT_FOUND" or "APPLY_REFERENCE_TYPE_MISMATCH";
        var source = reference ? entry?.ValueSource ?? ApplyDiagnosticSource.Unknown : entry?.Source ?? ApplyDiagnosticSource.Unknown;
        var completeness = new Dictionary<string, string>(Completeness(source.Status == "known", document.GetBundleCatalog() is not null))
        { ["inputs"] = document.BuildBundle is null ? "unknown" : "complete" };
        if (detail?.Expected.Status == "known")
        {
            completeness["member"] = issue.Code == "APPLY_CATALOG_UNAVAILABLE" ? "partial" : "complete";
        }
        if (issue.Code == "APPLY_REFERENCE_TYPE_MISMATCH") { completeness["reference"] = "complete"; completeness["type"] = "complete"; }
        return new("1", issue.Code, issue.Severity, issue.Message, "validate", document.BuildBundle?.BuildId,
            detail?.EntityKind, detail?.Key, detail?.Member, detail?.Path.JsonPath ?? issue.Path, detail?.Path.Segments,
            source, entry?.Related ?? [], detail?.Expected ?? ApplyDiagnosticValue.Unknown,
            detail?.Observed ?? ApplyDiagnosticValue.Unknown, completeness);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RLoop.Core;

// Transport context is deliberately outside the Apply shape and state contracts.
internal sealed class ApplyBuildBundle
{
    internal sealed record MapEntry(string JsonPath, IReadOnlyList<object>? PathSegments, string? EntityKind,
        string? Key, string? Member, ApplyDiagnosticSource Source, ApplyDiagnosticSource ValueSource,
        IReadOnlyList<ApplyDiagnosticSource> Related);
    private readonly List<MapEntry> entries = [];
    internal string BuildId { get; private set; } = "";
    private sealed record Input(string Path, string Sha256, string Role);
    private readonly List<Input> inputs = [];
    private string[] usedTypes = [];
    internal string Ir { get; private set; } = "";
    internal ApplyCatalog Catalog { get; private set; } = null!;

    internal static ApplyBuildBundle? Read(string path, string? request)
    {
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (Exception ex) when (request is not null && ex is IOException or UnauthorizedAccessException)
        {
            Fail("uncommitted", "Cannot read requested build bundle: " + ex.Message);
            return null;
        }
        catch (DecoderFallbackException)
        {
            var damaged = File.ReadAllText(path);
            if (request is not null || damaged.Contains("\"kind\"", StringComparison.Ordinal) || damaged.Contains("\"bundleVersion\"", StringComparison.Ordinal))
                Fail("uncommitted", "Bundle is not valid UTF-8.");
            return null;
        }
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(text); }
        catch (JsonException)
        {
            // A damaged requested envelope must never reach the legacy loader.
            if (request is not null || text.Contains("\"kind\"", StringComparison.Ordinal) ||
                text.Contains("\"bundleVersion\"", StringComparison.Ordinal)) Fail("uncommitted", "Bundle JSON is incomplete or invalid.");
            return null;
        }
        using (parsed)
        {
            var root = parsed.RootElement;
            var isBundle = root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("kind", out _) || root.TryGetProperty("bundleVersion", out _));
            if (!isBundle)
            {
                if (request is not null) throw new RLoopException("INVALID_OPTION", "--build-id requires a build bundle.", ExitCodes.InvalidArguments);
                return null;
            }
            if (string.IsNullOrWhiteSpace(request)) throw new RLoopException("OPTION_REQUIRED", "Bundle input requires --build-id R.", ExitCodes.InvalidArguments);
            try
            {
                if (String(root, "kind") != "resoloop-build-bundle" || String(root, "bundleVersion") != "1")
                    Fail("uncommitted", "Unsupported build bundle format.");
                if (String(root, "buildId") != request) Fail("requestMismatch", "Bundle belongs to another build request.");
                if (String(root, "completion") != "committed") Fail("uncommitted", "Build bundle was not committed.");
                var stages = root.GetProperty("buildStages");
                foreach (var stage in new[] { "typecheck", "emit", "evaluate", "inputs" })
                    if (String(stages, stage) != "passed") Fail("uncommitted", $"Build stage '{stage}' did not pass.");
                var bundle = new ApplyBuildBundle { BuildId = request };
                bundle.Ir = Payload(root, "ir");
                var mapText = Payload(root, "map");
                var catalogText = Payload(root, "catalog");
                using var map = JsonDocument.Parse(mapText);
                if (String(map.RootElement, "version") != "1" || String(map.RootElement, "buildId") != request ||
                    String(map.RootElement, "irSha256") != Hash(bundle.Ir) ||
                    map.RootElement.GetProperty("entries").ValueKind != JsonValueKind.Array)
                    Fail("mixed", "Map does not match this build/IR or has an unsupported format.");
                foreach (var entry in map.RootElement.GetProperty("entries").EnumerateArray())
                {
                    if (string.IsNullOrWhiteSpace(String(entry, "jsonPath")) ||
                        String(entry.GetProperty("source"), "status") is not ("unknown" or "known"))
                        Fail("mixed", "Invalid map entry.");
                }
                try { bundle.Catalog = JsonSerializer.Deserialize<ApplyCatalog>(catalogText, ApplyCatalog.Json)!; }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                { CatalogUnavailable("Cannot read embedded catalog: " + ex.Message); }
                if (bundle.Catalog is null) CatalogUnavailable("Embedded catalog is empty.");
                var snapshot = root.GetProperty("inputs");
                if (String(snapshot, "status") != "complete") Fail("inputUnknown", "Input graph is not complete.");
                var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var input in snapshot.GetProperty("files").EnumerateArray())
                {
                    var file = new Input(String(input, "path"), String(input, "sha256"), String(input, "role"));
                    if (!System.IO.Path.IsPathFullyQualified(file.Path) || !seen.Add(file.Path) || !IsHash(file.Sha256) ||
                        file.Role is not ("source" or "catalog")) Fail("inputUnknown", "Invalid or duplicate input record.");
                    bundle.inputs.Add(file);
                }
                if (!bundle.inputs.Any(i => i.Role == "source") || bundle.inputs.Count(i => i.Role == "catalog") != 1)
                    Fail("inputUnknown", "Source/catalog input records are missing.");
                var sources = map.RootElement.GetProperty("sources").EnumerateArray()
                    .Select(s => (Path: String(s, "path"), Hash: String(s, "sha256"))).OrderBy(s => s.Path, StringComparer.Ordinal).ToArray();
                if (!sources.SequenceEqual(bundle.inputs.Where(i => i.Role == "source")
                    .Select(i => (Path: i.Path, Hash: i.Sha256)).OrderBy(s => s.Path, StringComparer.Ordinal)))
                    Fail("mixed", "Map source hashes do not match this input graph.");
                if (bundle.inputs.Single(i => i.Role == "catalog").Sha256 != Hash(catalogText))
                    Fail("mixed", "Embedded catalog does not match its input snapshot.");
                bundle.usedTypes = root.GetProperty("usedTypes").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
                if (bundle.usedTypes.Any(string.IsNullOrWhiteSpace) || !bundle.usedTypes.SequenceEqual(bundle.usedTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
                    Fail("mixed", "Used types must be an ordinal sorted set.");
                bundle.VerifyInputs();
                foreach (var entry in map.RootElement.GetProperty("entries").EnumerateArray())
                    bundle.ReadEntry(entry);
                return bundle;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException)
            { Fail("uncommitted", "Build bundle is missing or has invalid required payloads: " + ex.Message); return null; }
        }
    }

    internal MapEntry? FindEntry(IReadOnlyList<object> path, string? kind, string? key, string? member)
    {
        var matches = entries.Where(e => e.PathSegments is not null && e.PathSegments.SequenceEqual(path) &&
            e.EntityKind == kind && e.Key == key && e.Member == member).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal bool MatchesOriginal(ApplyDocument document, IReadOnlyList<object> segments)
    {
        using var original = JsonDocument.Parse(Ir);
        var current = JsonSerializer.SerializeToElement(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var before = original.RootElement;
        foreach (var segment in segments)
        {
            if (segment is string property)
            {
                if (before.ValueKind != JsonValueKind.Object || current.ValueKind != JsonValueKind.Object ||
                    !before.TryGetProperty(property, out before) || !current.TryGetProperty(property, out current)) return false;
            }
            else if (segment is int index)
            {
                if (before.ValueKind != JsonValueKind.Array || current.ValueKind != JsonValueKind.Array ||
                    index >= before.GetArrayLength() || index >= current.GetArrayLength()) return false;
                before = before[index]; current = current[index];
            }
        }
        return JsonElement.DeepEquals(before, current);
    }

    private void ReadEntry(JsonElement entry)
    {
        ApplyDiagnosticSource ReadSource(JsonElement source)
        {
            var status = String(source, "status");
            if (status == "unknown") return ApplyDiagnosticSource.Unknown;
            if (status != "known") Fail("mixed", "Invalid source status.");
            var file = String(source, "file"); var hash = String(source, "sha256");
            if (!Path.IsPathFullyQualified(file) || !inputs.Any(i => i.Role == "source" && i.Path == file && i.Sha256 == hash))
                Fail("mixed", "Known location file/hash is not a source input.");
            if (!source.TryGetProperty("range", out var rangeJson) || rangeJson.ValueKind != JsonValueKind.Object)
                Fail("mixed", "Known location requires a range.");
            ApplySourcePoint Point(string name)
            {
                int o = 0, l = 0, c = 0;
                if (!rangeJson.TryGetProperty(name, out var point) || point.ValueKind != JsonValueKind.Object ||
                    !point.TryGetProperty("offset", out var offset) || offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt32(out o) ||
                    !point.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number || !line.TryGetInt32(out l) ||
                    !point.TryGetProperty("column", out var column) || column.ValueKind != JsonValueKind.Number || !column.TryGetInt32(out c))
                    Fail("mixed", "Source point requires integer offset/line/column.");
                return new(o, l, c);
            }
            var range = new ApplySourceRange(Point("start"), Point("end"));
            var text = File.ReadAllText(file, new UTF8Encoding(false, true));
            if (text.StartsWith('\uFEFF')) text = text[1..];
            void Check(ApplySourcePoint point)
            {
                if (point.Offset < 0 || point.Offset > text.Length || point.Line < 1 || point.Column < 1)
                    Fail("mixed", "Source range is outside the original file.");
                var line = 1; var start = 0;
                for (var i = 0; i < point.Offset; i++)
                {
                    if (text[i] == '\r') { if (i + 1 < point.Offset && text[i + 1] == '\n') i++; line++; start = i + 1; }
                    else if (text[i] is '\n' or '\u2028' or '\u2029') { line++; start = i + 1; }
                }
                if (point.Line != line || point.Column != point.Offset - start + 1)
                    Fail("mixed", "Source line/column does not match its offset.");
            }
            Check(range.Start); Check(range.End);
            if (range.End.Offset < range.Start.Offset) Fail("mixed", "Source range is reversed.");
            return new("known", file, hash, range);
        }
        string? Optional(string name) => entry.TryGetProperty(name, out var value) ? value.GetString() : null;
        IReadOnlyList<object>? segments = null;
        if (entry.TryGetProperty("pathSegments", out var path))
        {
            segments = path.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? (object)p.GetString()! : p.GetInt32()).ToArray();
            if (segments.Any(p => p is int i && i < 0) || new ApplyIssuePath(segments, "$").JsonPath != String(entry, "jsonPath"))
                Fail("mixed", "Invalid structured map path.");
        }
        entries.Add(new(String(entry, "jsonPath"), segments, Optional("entityKind"), Optional("key"), Optional("member"),
            ReadSource(entry.GetProperty("source")), entry.TryGetProperty("valueSource", out var valueSource) ? ReadSource(valueSource) : ApplyDiagnosticSource.Unknown,
            entry.TryGetProperty("related", out var related) ? related.EnumerateArray().Select(ReadSource).ToArray() : []));
    }

    internal void ValidateDocument(ApplyDocument document)
    {
        var validation = ApplyDocumentValidator.ValidateAsync(document, catalog: Catalog).GetAwaiter().GetResult();
        ApplyDocumentValidator.ThrowIfInvalid(validation);
        if (!ApplyCatalogValidator.UsedTypes(document, Catalog).SetEquals(usedTypes))
            Fail("mixed", "Used types do not match validated IR/catalog.");
        if (document.Authoring is not { } authoring ||
            !inputs.Any(i => i.Role == "source" && (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Equals(System.IO.Path.GetFullPath(i.Path), System.IO.Path.GetFullPath(authoring.Source, authoring.ProjectRoot))))
            Fail("inputUnknown", "Authoring entry is missing from the input snapshot.");
    }

    internal void VerifyInputs()
    {
        foreach (var input in inputs)
        {
            try
            {
                if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input.Path))) != input.Sha256)
                    Fail("inputChanged", $"Build input changed: {input.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { Fail("inputChanged", $"Cannot read build input '{input.Path}': {ex.Message}"); }
        }
    }

    internal void VerifySession(SessionInfo session, string? clientPackageVersion = null)
    {
        if (Catalog.Synthetic || Catalog.UnavailableReason() is not null ||
            Catalog.Identity!.ResoniteVersion != session.ResoniteVersion || Catalog.Identity.ResoniteLinkVersion != session.ResoniteLinkVersion ||
            clientPackageVersion is not null && Catalog.Identity.ClientPackageVersion != clientPackageVersion)
            CatalogUnavailable("Embedded catalog is synthetic or does not match the connected session/client package.");
    }

    private static string Payload(JsonElement root, string name)
    {
        var payload = root.GetProperty(name);
        var text = String(payload, "text");
        if (String(payload, "sha256") != Hash(text)) Fail("mixed", $"{name} bytes do not match their hash.");
        return text;
    }
    private static string String(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw new JsonException(name + " is null.");
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    [DoesNotReturn]
    internal static void Fail(string reason, string message) => throw new RLoopException("APPLY_BUILD_BUNDLE_INVALID", message,
        ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["reason"] = reason });
    [DoesNotReturn]
    private static void CatalogUnavailable(string message) => throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", message, ExitCodes.ValidationFailed);
}

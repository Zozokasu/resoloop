using System.Diagnostics;
using System.Text.Json;

namespace RLoop.Core;

public sealed record ReflectionExpectation(string? Kind = null, string? ValueType = null, string? TargetType = null,
    IReadOnlyDictionary<string, long>? EnumValues = null);
public sealed record ReflectionSelection(string Type, IReadOnlyList<string> Members,
    IReadOnlyList<string>? Enums = null, IReadOnlyDictionary<string, ReflectionExpectation>? Expect = null);
public sealed record ReflectionRequest(IReadOnlyList<ReflectionSelection> Types)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    public static ReflectionRequest Load(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new JsonException("Request exceeds 1 MiB.");
            var request = JsonSerializer.Deserialize<ReflectionRequest>(File.ReadAllText(path), Json)
                ?? throw new JsonException("Expected a request object.");
            request.Validate();
            return request;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw new RLoopException("REFLECTION_REQUEST_INVALID", ex.Message, ExitCodes.InvalidArguments, innerException: ex); }
    }

    public void Validate()
    {
        if (Types is not { Count: > 0 and <= 64 } || Types.Any(t => t is null || string.IsNullOrWhiteSpace(t.Type) ||
            t.Members is not { Count: > 0 and <= 64 } || t.Members.Any(string.IsNullOrWhiteSpace)) || Types.Sum(t => t.Members.Count) > 256)
            throw new RLoopException("REFLECTION_REQUEST_INVALID", "Use 1..64 types, 1..64 explicit member names per selection and at most 256 members total.", ExitCodes.InvalidArguments);
        foreach (var type in Types)
            if ((type.Enums ?? []).Any(name => !type.Members.Contains(name, StringComparer.Ordinal)) ||
                (type.Expect ?? new Dictionary<string, ReflectionExpectation>()).Any(p => !type.Members.Contains(p.Key, StringComparer.Ordinal) || p.Value is null))
                throw new RLoopException("REFLECTION_REQUEST_INVALID", "enums and expect must refer to selected members; expectations must be objects.", ExitCodes.InvalidArguments);
    }
}

public sealed record ReflectionDifference(string Type, string? Member, string Code, string? Expected, string? Actual);
public sealed record ReflectionMember(string Name, string Kind, string? MemberType, string? ValueType, string? TargetType,
    IReadOnlyDictionary<string, long>? EnumValues, bool? IsFlags);
/// <summary>
/// <paramref name="Status"/> is "verified", "unknown" (the definition could not be read, so nothing is claimed about the type)
/// or "notFound" (the type list positively lacks it). <paramref name="Verified"/> stays true only for "verified".
/// </summary>
public sealed record ReflectionSelectionResult(string RequestedType, string? FullTypeName, bool Verified,
    string Source, DateTimeOffset? ObservedAt, IReadOnlyList<ReflectionMember> Members, string Status = ReflectionStatus.Verified);

public static class ReflectionStatus
{
    public const string Verified = "verified";
    public const string Unknown = "unknown";
    public const string NotFound = "notFound";
}
public sealed record ReflectionQueryReport(bool Complete, bool Verified, bool? Compatible, int RequestedTypes,
    int RequestedMembers, IReadOnlyList<ReflectionSelectionResult> Types, IReadOnlyList<ReflectionDifference> Differences,
    int DiskHits, int DiskMisses, int CacheWriteFailures, double ElapsedMs, ClientMetrics? Profile);

public static class ReflectionQuery
{
    // Connection-level failures and cancellation affect every type, so they still abort the whole query.
    private static bool IsolatesToType(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        RLoopException coded => coded.ExitCode is not (ExitCodes.ConnectionFailed or ExitCodes.Timeout) &&
            coded.Code is not ("CONNECTION_GENERATION_CHANGED" or "ALREADY_CONNECTED"),
        _ => true
    };

    public static async Task<ReflectionQueryReport> RunAsync(IResoniteClient client, ReflectionRequest request,
        ReflectionCacheOptions? cacheOptions = null, bool check = false, bool profile = false, CancellationToken ct = default)
    {
        request.Validate();
        cacheOptions?.Validate();
        var metadataClient = client as IReflectionMetadataClient;
        if (cacheOptions is not null) metadataClient?.ConfigureReflectionCache(cacheOptions);
        var timer = Stopwatch.StartNew();
        var diagnostics = client as IResoniteClientDiagnostics;
        if (profile) diagnostics?.ResetMetrics();
        var components = new Dictionary<string, ReflectionMetadata<ComponentTypeInfo>>(StringComparer.Ordinal);
        var valueTypes = new Dictionary<string, ReflectionMetadata<TypeInfo>>(StringComparer.Ordinal);
        var results = new List<ReflectionSelectionResult>();
        var differences = new List<ReflectionDifference>();

        async Task<ReflectionMetadata<ComponentTypeInfo>> Component(string name, bool fresh)
        {
            if (components.TryGetValue(name, out var value) && (!fresh || value.Live)) return value;
            value = metadataClient is null
                ? new(await client.DescribeComponentTypeAsync(name, ct), DateTimeOffset.UtcNow, true)
                : await metadataClient.DescribeComponentMetadataAsync(name, fresh, ct);
            components[name] = value;
            return value;
        }

        async Task<ReflectionMetadata<TypeInfo>> ValueType(string name, bool fresh)
        {
            if (valueTypes.TryGetValue(name, out var value) && (!fresh || value.Live)) return value;
            value = metadataClient is null
                ? new(await client.DescribeTypeAsync(name, ct), DateTimeOffset.UtcNow, true)
                : await metadataClient.DescribeTypeMetadataAsync(name, fresh, ct);
            valueTypes[name] = value;
            return value;
        }

        foreach (var selection in request.Types)
        {
            ct.ThrowIfCancellationRequested();
            // One live retry for a missing member/contract mismatch found in a version-matched disk entry.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var local = new List<ReflectionDifference>();
                var members = new List<ReflectionMember>();
                var verified = true;
                var status = ReflectionStatus.Verified;
                var diskUsed = false;
                DateTimeOffset? observedAt = null;
                string? fullType = null;
                try
                {
                    var metadata = await Component(selection.Type, attempt > 0);
                    verified = true; observedAt = metadata.ObservedAt; fullType = metadata.Value.FullTypeName;
                    diskUsed |= !metadata.Live;
                    foreach (var name in selection.Members.Distinct(StringComparer.Ordinal))
                    {
                        var member = metadata.Value.Members.FirstOrDefault(m => m.Name == name);
                        if (member is null) { local.Add(new(selection.Type, name, "MEMBER_NOT_FOUND", "exists", null)); continue; }
                        var expected = selection.Expect?.GetValueOrDefault(name);
                        IReadOnlyDictionary<string, long>? enumValues = null;
                        bool? isFlags = null;
                        var enumUnavailable = false;
                        if (selection.Enums?.Contains(name, StringComparer.Ordinal) == true || expected?.EnumValues is not null)
                        {
                            if (member.ValueType is not { Length: > 0 }) local.Add(new(selection.Type, name, "ENUM_VALUE_TYPE_MISSING", "enum", member.Kind));
                            else
                            {
                                try
                                {
                                    var type = await ValueType(ReflectedMemberType.UnwrapNullable(member.ValueType), attempt > 0);
                                    diskUsed |= !type.Live;
                                    if (type.ObservedAt < observedAt) observedAt = type.ObservedAt;
                                    if (!type.Value.IsEnum) local.Add(new(selection.Type, name, "ENUM_EXPECTED", "enum", type.Value.FullTypeName));
                                    enumValues = type.Value.EnumValues; isFlags = type.Value.IsFlags;
                                }
                                catch (Exception ex) when (IsolatesToType(ex))
                                {
                                    // The component itself was resolved; only this member's value type is unreadable (unknown, not absent).
                                    enumUnavailable = true; verified = false; status = ReflectionStatus.Unknown;
                                    local.Add(new(selection.Type, name, "ENUM_VALUE_TYPE_UNAVAILABLE", "readable",
                                        ex is RLoopException coded ? coded.Code + ": " + ex.Message : ex.GetType().Name + ": " + ex.Message));
                                }
                            }
                        }
                        members.Add(new(name, member.Kind, member.MemberType, member.ValueType, member.TargetType, enumValues, isFlags));
                        void Compare(string code, string? wanted, string? actual)
                        { if (wanted is not null && wanted != actual) local.Add(new(selection.Type, name, code, wanted, actual)); }
                        Compare("MEMBER_KIND_MISMATCH", expected?.Kind, member.Kind);
                        Compare("VALUE_TYPE_MISMATCH", expected?.ValueType, member.ValueType);
                        Compare("TARGET_TYPE_MISMATCH", expected?.TargetType, member.TargetType);
                        if (!enumUnavailable)
                        foreach (var pair in expected?.EnumValues ?? new Dictionary<string, long>())
                            if (enumValues is null || !enumValues.TryGetValue(pair.Key, out var actual) || actual != pair.Value)
                                local.Add(new(selection.Type, name, "ENUM_VALUE_MISMATCH", pair.Key + "=" + pair.Value,
                                    enumValues?.TryGetValue(pair.Key, out var found) == true ? pair.Key + "=" + found : null));
                    }
                }
                catch (RLoopException ex) when (ex.ExitCode == ExitCodes.NotFound)
                { verified = false; status = ReflectionStatus.NotFound; local.Add(new(selection.Type, null, ex.Code, "exists", ex.Message)); }
                catch (Exception ex) when (IsolatesToType(ex))
                {
                    // One unreadable type must not abort the other selections; "unknown" is never reported as "absent".
                    verified = false; status = ReflectionStatus.Unknown;
                    local.Add(new(selection.Type, null, "TYPE_DEFINITION_UNAVAILABLE", "readable",
                        ex is RLoopException coded ? coded.Code + ": " + ex.Message : ex.GetType().Name + ": " + ex.Message));
                }
                if (local.Count > 0 && diskUsed && attempt == 0) continue;
                differences.AddRange(local);
                results.Add(new(selection.Type, fullType, verified, diskUsed ? "version-cache" : fullType is null ? "unavailable" : "live", observedAt, members, status));
                break;
            }
        }
        var complete = differences.Count == 0;
        var allVerified = results.All(r => r.Verified);
        var cache = metadataClient?.SnapshotReflectionCache() ?? new(0, 0, 0);
        return new(complete, allVerified, complete,
            request.Types.Count, request.Types.Sum(t => t.Members.Distinct(StringComparer.Ordinal).Count()), results, differences,
            cache.DiskHits, cache.DiskMisses, cache.WriteFailures, timer.Elapsed.TotalMilliseconds, profile ? diagnostics?.SnapshotMetrics() : null);
    }
}

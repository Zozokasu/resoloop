using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record CreationState(
    string Kind,
    int SchemaVersion,
    string CreationId,
    CreationSession Session,
    string OwnerNonce,
    CreationActiveRoot? ActiveRoot = null,
    CreationPending? Pending = null);

public sealed record CreationSession(string NormalizedUrl, string? DiscoverSessionId, string IdentityStatus);

public sealed record CreationActiveRoot(
    string Id,
    string ParentId,
    string GenerationId,
    string Stamp,
    string InputHash,
    string? BuildId);

public sealed record CreationPending(
    string OperationId,
    string Stage,
    CreationScope Scope,
    string ReplaceOrder,
    CreationSession Session,
    string? ConnectionGeneration,
    string? BuildId,
    string? InputHash,
    string? CatalogHash,
    CreationActiveRoot? PreviousRoot,
    CreationPendingRoot? CandidateRoot,
    IReadOnlyList<CreationStep> Steps,
    JsonElement? Evidence);

public sealed record CreationScope(
    string Kind,
    string? UserId,
    string? PreviousObjectId,
    string? CandidateObjectId,
    string? ParentId = null);

public sealed record CreationPendingRoot(
    string? Id,
    string? ParentId,
    string? GenerationId,
    string? Stamp,
    string? InputHash,
    string? BuildId);

public sealed record CreationStep(
    string? Kind,
    string? TargetId,
    string? ParentId,
    string? Type,
    string? SendStatus,
    bool? ResponseReceived,
    bool? ResponseAccepted,
    JsonElement? Expected,
    JsonElement? Observed,
    bool? Confirmed,
    JsonElement? Completeness);

public static class CreationStateStore
{
    private const string StateKind = "creation";
    private const int StateVersion = 4;
    private const string Hex = "0123456789abcdef";
    private static readonly HashSet<string> PendingStages = new(StringComparer.Ordinal)
    {
        "prepared", "creatingRoot", "markingRoot", "creatingTree", "deployingScripts", "staged",
        "disablingPrevious", "activatingCandidate", "verifyingActive", "startingScripts", "deletingPrevious",
        "cleanupCandidate", "unresolved"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static string ResolvePath(CreationDocument document, string? requestedPath = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (requestedPath is not null) return Path.GetFullPath(requestedPath);
        ValidateCreationId(document.CreationId);
        var definition = document.Definition;
        if (document.Authoring is not null && definition.Authoring != document.Authoring)
            definition = definition with { Authoring = document.Authoring, ResolvedProjectContext = null };
        var projectRoot = definition.ProjectContext.ProjectRoot;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document.CreationId)));
        return Path.Combine(projectRoot, ".resoloop", "creation-state", hash + ".json");
    }

    public static CreationState? Load(string path, string creationId, bool requireState = false)
    {
        ValidateCreationId(creationId);
        string json;
        try { json = CheckpointFiles.Read(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!requireState) return null;
            throw new RLoopException("CREATION_STATE_REQUIRED", $"Creation state file '{path}' is required.", ExitCodes.NotFound, innerException: ex);
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw Invalid(path, ex); }

        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid(path, "Root must be an object.");
            if (!root.TryGetProperty("schemaVersion", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
                throw Invalid(path, "schemaVersion must be an integer.");
            if (version is >= 1 and <= 3)
                throw new RLoopException("CREATION_STATE_MIGRATION_REQUIRED",
                    $"State file '{path}' uses schemaVersion {version}; creation state migration is required.", ExitCodes.ValidationFailed);
            if (version != StateVersion) ApplyStateStore.RequireSupportedVersion(version, path, StateVersion);

            RejectDuplicateProperties(root, path);
            RequireShape(root, "state", ["kind", "schemaVersion", "creationId", "session", "ownerNonce", "activeRoot", "pending"],
                ["kind", "schemaVersion", "creationId", "session", "ownerNonce", "activeRoot", "pending"], path);
            RequireSession(root.GetProperty("session"), "session", path);
            if (root.TryGetProperty("activeRoot", out var activeRoot) && activeRoot.ValueKind != JsonValueKind.Null)
                RequireShape(activeRoot, "activeRoot", ["id", "parentId", "generationId", "stamp", "inputHash", "buildId"],
                    ["id", "parentId", "generationId", "stamp", "inputHash", "buildId"], path);
            if (root.TryGetProperty("pending", out var pending) && pending.ValueKind != JsonValueKind.Null)
                RequirePending(pending, path);

            var state = JsonSerializer.Deserialize<CreationState>(json, JsonOptions)
                ?? throw Invalid(path, "State was empty.");
            if (state.Kind != StateKind) throw Invalid(path, "kind must be exactly 'creation'.");
            if (state.CreationId != creationId)
                throw new RLoopException("APPLY_STATE_OWNERSHIP_MISMATCH",
                    $"State file '{path}' belongs to creationId '{state.CreationId}', not '{creationId}'.", ExitCodes.ValidationFailed);
            ValidateState(state, path);
            return state;
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException or NullReferenceException)
        { throw Invalid(path, ex); }
    }

    public static void Save(string path, CreationState state)
    {
        ValidateState(state, path);
        string json;
        try { json = JsonSerializer.Serialize(state, JsonOptions) + "\n"; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        { throw Invalid(path, ex); }
        try { CheckpointFiles.Write(path, json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new RLoopException("APPLY_STATE_WRITE_FAILED", $"Could not write creation state '{path}': {ex.Message}",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex);
        }
    }

    private static void RequirePending(JsonElement pending, string path)
    {
        RequireShape(pending, "pending",
            ["operationId", "stage", "scope", "replaceOrder", "session", "connectionGeneration", "buildId", "inputHash", "catalogHash", "previousRoot", "candidateRoot", "steps", "evidence"],
            ["operationId", "stage", "scope", "replaceOrder", "session", "connectionGeneration", "buildId", "inputHash", "catalogHash", "previousRoot", "candidateRoot", "steps", "evidence"], path);
        RequireShape(pending.GetProperty("scope"), "pending.scope",
            ["kind", "userId", "previousObjectId", "candidateObjectId"],
            ["kind", "userId", "previousObjectId", "candidateObjectId", "parentId"], path);
        RequireSession(pending.GetProperty("session"), "pending.session", path);
        var previous = pending.GetProperty("previousRoot");
        if (previous.ValueKind != JsonValueKind.Null)
            RequireShape(previous, "pending.previousRoot", ["id", "parentId", "generationId", "stamp", "inputHash", "buildId"],
                ["id", "parentId", "generationId", "stamp", "inputHash", "buildId"], path);
        var candidate = pending.GetProperty("candidateRoot");
        if (candidate.ValueKind != JsonValueKind.Null)
            RequireShape(candidate, "pending.candidateRoot", ["id", "parentId", "generationId", "stamp", "inputHash", "buildId"],
                ["id", "parentId", "generationId", "stamp", "inputHash", "buildId"], path);
        var steps = pending.GetProperty("steps");
        if (steps.ValueKind != JsonValueKind.Array) throw Invalid(path, "pending.steps must be an array.");
        foreach (var step in steps.EnumerateArray())
            RequireShape(step, "pending.steps[]",
                ["kind", "targetId", "parentId", "type", "sendStatus", "responseReceived", "responseAccepted", "expected", "observed", "confirmed", "completeness"],
                ["kind", "targetId", "parentId", "type", "sendStatus", "responseReceived", "responseAccepted", "expected", "observed", "confirmed", "completeness"], path);
    }

    private static void RequireSession(JsonElement session, string name, string path) =>
        RequireShape(session, name, ["normalizedUrl", "discoverSessionId", "identityStatus"], ["normalizedUrl", "discoverSessionId", "identityStatus"], path);

    private static void RequireShape(JsonElement value, string name, string[] required, string[] allowed, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid(path, $"{name} must be an object.");
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name)) throw Invalid(path, $"Unknown {name} property '{property.Name}'.");
            present.Add(property.Name);
        }
        foreach (var property in required)
            if (!present.Contains(property)) throw Invalid(path, $"{name}.{property} is required.");
    }

    private static void RejectDuplicateProperties(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid(path, $"Duplicate JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value, path);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item, path);
    }

    private static void ValidateState(CreationState state, string path)
    {
        if (state is null) throw Invalid(path, "State is required.");
        if (state.Kind != StateKind) throw Invalid(path, "kind must be exactly 'creation'.");
        if (state.SchemaVersion != StateVersion) throw Invalid(path, "schemaVersion must be exactly 4.");
        ValidateCreationId(state.CreationId, path);
        if (state.Session is null || string.IsNullOrWhiteSpace(state.Session.NormalizedUrl) || string.IsNullOrWhiteSpace(state.Session.IdentityStatus))
            throw Invalid(path, "session.normalizedUrl and session.identityStatus are required.");
        if (!IsLowerHex(state.OwnerNonce, 64)) throw Invalid(path, "ownerNonce must be 64 lowercase hexadecimal characters.");
        if (state.ActiveRoot is { } root) ValidateActiveRoot(root, "activeRoot", path);
        if (state.Pending is { } pending) ValidatePending(pending, path);
    }

    private static void ValidateActiveRoot(CreationActiveRoot root, string name, string path)
    {
        if (string.IsNullOrWhiteSpace(root.Id) || string.IsNullOrWhiteSpace(root.ParentId) ||
            string.IsNullOrWhiteSpace(root.GenerationId) || string.IsNullOrWhiteSpace(root.Stamp) || !IsLowerHex(root.InputHash, 64) ||
            root.BuildId is { Length: 0 })
            throw Invalid(path, $"{name} requires id, parentId, generationId, stamp, and a lowercase SHA-256 inputHash.");
    }

    private static void ValidatePending(CreationPending pending, string path)
    {
        if (string.IsNullOrWhiteSpace(pending.OperationId) || !PendingStages.Contains(pending.Stage) || pending.Scope is null ||
            pending.Scope.Kind is not ("creation" or "object") || pending.ReplaceOrder is not ("delete-first" or "stage-inactive") ||
            pending.Session is null || pending.Steps is null)
            throw Invalid(path, "pending has invalid required fields or stage.");
        if (string.IsNullOrWhiteSpace(pending.Session.NormalizedUrl) || string.IsNullOrWhiteSpace(pending.Session.IdentityStatus))
            throw Invalid(path, "pending.session requires normalizedUrl and identityStatus.");
        if (pending.PreviousRoot is { } previous) ValidateActiveRoot(previous, "pending.previousRoot", path);
        if ((pending.CandidateRoot is { } candidate &&
             new[] { candidate.Id, candidate.ParentId, candidate.GenerationId, candidate.Stamp, candidate.BuildId }.Any(value => value is { Length: 0 })) ||
            (pending.CandidateRoot?.InputHash is { } hash && !IsLowerHex(hash, 64)))
            throw Invalid(path, "pending.candidateRoot contains an empty value or invalid inputHash.");
        foreach (var step in pending.Steps)
            if (step is null) throw Invalid(path, "pending.steps cannot contain null entries.");
    }

    private static void ValidateCreationId(string? creationId, string path = "")
    {
        if (creationId is null || creationId.Length is < 1 or > 128 || creationId.Any(char.IsControl))
            throw Invalid(path, "creationId must be 1..128 characters and contain no control characters.");
    }

    private static bool IsLowerHex(string? value, int length) => value is { Length: var actual } && actual == length && value.All(ch => Hex.Contains(ch));

    private static RLoopException Invalid(string path, Exception exception) => new(
        "APPLY_STATE_INVALID", $"Could not read creation state '{path}': {exception.Message}", ExitCodes.ValidationFailed,
        new Dictionary<string, object?> { ["stateFile"] = path }, innerException: exception);

    private static RLoopException Invalid(string path, string message) => new(
        "APPLY_STATE_INVALID", $"Invalid creation state '{path}': {message}", ExitCodes.ValidationFailed,
        new Dictionary<string, object?> { ["stateFile"] = path });
}

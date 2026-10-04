using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

// Flux deploy state v2 (ROADMAP-9 unit 2). ResoLoop's own JSON; nothing here references Flux-SDK or
// ResoniteLink types, and nothing here contacts the world.

/// <summary>Where a deployment was sent: the lock key URL and the announced <c>S-</c> ID. Never the per-connection counter.</summary>
/// <param name="IdentityStatus"><c>matched</c> when the <c>S-</c> ID was matched to the URL, otherwise <c>unknown</c>.</param>
public sealed record FluxDeploySessionRecord(string NormalizedUrl, string? DiscoverSessionId, string IdentityStatus)
{
    public static FluxDeploySessionRecord From(ApplySessionObservation observation) =>
        new(observation.NormalizedUrl, observation.DiscoverSessionId, observation.IdentityStatus);
}

/// <param name="Mode"><c>source</c> or <c>drive</c>.</param>
public sealed record FluxDeployBindingRecord(string Port, string Mode, string TargetId);

/// <summary>The recorded root of one module. Only a <see cref="FluxDeployOrigins.Deployed"/> record is ownership evidence.</summary>
public sealed record FluxDeployModuleRecord
{
    /// <summary>Null only for a <see cref="FluxDeployOrigins.MigratedV1"/> record whose v1 state had no parent.</summary>
    public string? ParentSlotId { get; init; }
    /// <summary>Null only for a <see cref="FluxDeployOrigins.MigratedV1"/> record whose v1 state had no Slot ID.</summary>
    public string? RootSlotId { get; init; }
    /// <summary>The name in the source's <c>module</c> declaration. Null for a migrated record: v1 did not store it.</summary>
    public string? ModuleName { get; init; }
    public string InputHash { get; init; } = string.Empty;
    public string? SdkVersion { get; init; }
    public DateTimeOffset? DeployedAt { get; init; }
    public IReadOnlyList<FluxDeployBindingRecord> Bindings { get; init; } = [];
    public string Origin { get; init; } = FluxDeployOrigins.Deployed;

    /// <summary>
    /// False for a record copied from a v1 state: its ID was found by name and has not been checked against the world.
    /// The deploy guard decides whether to trust it (the Slot still exists, with this parent and the declared name).
    /// </summary>
    [JsonIgnore] public bool IsVerified => Origin == FluxDeployOrigins.Deployed;
}

public static class FluxDeployOrigins
{
    /// <summary>The root ID came from the creation answer of a deployment recorded by this state format.</summary>
    public const string Deployed = "deployed";
    /// <summary>Copied from a v1 state: the ID was found by name. Unverified until the guard confirms it.</summary>
    public const string MigratedV1 = "migrated-v1";
}

/// <summary>One deployment whose result is not settled. Saved before the deployer is called.</summary>
public sealed record FluxDeployPending
{
    public string OperationId { get; init; } = string.Empty;
    /// <summary>Key into <see cref="FluxDeployState.Modules"/>.</summary>
    public string Module { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public FluxDeploySessionRecord Session { get; init; } = null!;
    public string ParentSlotId { get; init; } = string.Empty;
    public string ModuleName { get; init; } = string.Empty;
    public string? PreviousRootSlotId { get; init; }
    /// <summary>IDs of every direct child of the parent, read just before the deployment.</summary>
    public IReadOnlyList<string> ObservedChildIds { get; init; } = [];
    public string InputHash { get; init; } = string.Empty;
    public string? RequestedRootId { get; init; }
    public FluxDeployStage Stage { get; init; } = FluxDeployStage.Unknown;
    public FluxDeploySendStatus SendStatus { get; init; } = FluxDeploySendStatus.Unknown;
    /// <summary>IDs that may be the new module root. Reported to the user; never adopted automatically.</summary>
    public IReadOnlyList<string> CandidateRootIds { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; init; }

    /// <summary>
    /// A new pending record with a fresh operation ID. Stage and send status start as Unknown: if the process dies
    /// after the record is saved, nothing proves that no message was sent.
    /// </summary>
    public static FluxDeployPending Begin(string module, FluxDeploySessionRecord session, string parentSlotId,
        string moduleName, string? previousRootSlotId, IEnumerable<string> observedChildIds, string inputHash,
        DateTimeOffset createdAt) => new()
    {
        OperationId = Guid.NewGuid().ToString("N"), Module = module, CreatedAt = createdAt, Session = session,
        ParentSlotId = parentSlotId, ModuleName = moduleName, PreviousRootSlotId = previousRootSlotId,
        ObservedChildIds = observedChildIds.ToArray(), InputHash = inputHash
    };
}

public sealed class FluxDeployState
{
    public const string KindValue = "flux-deploy";
    public const int CurrentSchemaVersion = 2;

    public string Kind { get; set; } = KindValue;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>Session of the last settled deployment. Null until one settles (including after a v1 migration).</summary>
    public FluxDeploySessionRecord? Session { get; set; }
    // Required in the file: an absent list must not read as "nothing recorded" or "nothing pending".
    [JsonRequired] public Dictionary<string, FluxDeployModuleRecord> Modules { get; set; } = new(StringComparer.Ordinal);
    [JsonRequired] public List<FluxDeployPending> Pending { get; set; } = [];
}

/// <summary>
/// Reads and changes a Flux deploy state file. Reading is public; every change except
/// <see cref="FluxDeployPendingDiscard"/> is reserved for the deploy guard in RLoop.Core.
/// </summary>
public static class FluxDeployStateStore
{
    /// <summary>
    /// Default state of the single-module <c>flux deploy</c>:
    /// <c>&lt;project&gt;/.resoloop/flux-state/deploy/&lt;safe module path&gt;.json</c>. Module paths that map to
    /// the same file name share one file and stay apart by their <see cref="SingleDeployModuleKey"/>.
    /// </summary>
    public static string ResolveSingleDeployStatePath(string projectDirectory, string module) => Path.Combine(
        Path.GetFullPath(projectDirectory), ".resoloop", "flux-state", "deploy", SafeFileName(module) + ".json");

    /// <summary>The <see cref="FluxDeployState.Modules"/> key of a single-module deploy: the module path as given.</summary>
    public static string SingleDeployModuleKey(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("A module path is required.", nameof(module));
        return module.Trim();
    }

    /// <summary>
    /// Reads a v2 state, or a v1 state as an unconfirmed migration (every module gets origin <c>migrated-v1</c>; the
    /// v1 session counter is dropped). A missing file is an empty state unless <paramref name="requireState"/>.
    /// The file is not rewritten.
    /// </summary>
    public static FluxDeployState Load(string path, bool requireState = false)
    {
        path = Path.GetFullPath(path);
        string content;
        try { content = CheckpointFiles.Read(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (requireState) throw NotFound(path, ex);
            return new FluxDeployState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Invalid(path, ex.Message, ex); }
        return Parse(content, path);
    }

    internal static FluxDeployState Parse(string content, string path)
    {
        try
        {
            bool hasKind;
            int version;
            using (var json = JsonDocument.Parse(content))
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The state is not a JSON object.");
                hasKind = root.TryGetProperty("kind", out var kind);
                if (hasKind && (kind.ValueKind != JsonValueKind.String || kind.GetString() != FluxDeployState.KindValue))
                    throw new JsonException($"kind is {kind.GetRawText()}, not \"{FluxDeployState.KindValue}\".");
                if (!hasKind && root.TryGetProperty("ownershipKey", out _))
                    throw new JsonException("This is an apply state, not a Flux deploy state.");
                if (!root.TryGetProperty("schemaVersion", out var schemaVersion))
                {
                    // The v1 writer always wrote schemaVersion and the v1 reader defaulted it to 1.
                    if (hasKind) throw new JsonException("schemaVersion is missing.");
                    version = 1;
                }
                else if (schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out version))
                    throw new JsonException("schemaVersion is not an integer.");
            }
            if (version is < 1 or > FluxDeployState.CurrentSchemaVersion)
                throw new RLoopException("FLUX_STATE_VERSION_UNSUPPORTED",
                    $"Flux deploy state '{path}' has unsupported schemaVersion {version}. Use a ResoLoop version that knows it; the state is not downgraded.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["stateFile"] = path, ["schemaVersion"] = version });
            if (hasKind != (version == FluxDeployState.CurrentSchemaVersion))
                throw new JsonException(hasKind ? $"schemaVersion {version} cannot carry kind." : $"schemaVersion {version} requires kind.");
            if (version == 1) return Migrate(JsonSerializer.Deserialize<V1State>(content, Options) ?? throw new JsonException("State was empty."));
            var state = JsonSerializer.Deserialize<FluxDeployState>(content, Options) ?? throw new JsonException("State was empty.");
            if (state.Modules is not null) state.Modules = new(state.Modules, StringComparer.Ordinal);
            Validate(state);
            return state;
        }
        catch (JsonException ex) { throw Invalid(path, ex.Message, ex); }
    }

    // v1 IDs were found by name, so the copy is marked unverified. The v1 state had one parent for all modules.
    private static FluxDeployState Migrate(V1State v1)
    {
        var state = new FluxDeployState();
        foreach (var (key, module) in v1.Modules ?? [])
        {
            if (module?.Hash is null) throw new JsonException($"modules.{key}.hash is missing.");
            state.Modules[key] = new FluxDeployModuleRecord
            {
                ParentSlotId = Blank(v1.ParentSlotId), RootSlotId = Blank(module.SlotId), InputHash = module.Hash,
                Origin = FluxDeployOrigins.MigratedV1
            };
        }
        return state;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void Validate(FluxDeployState state)
    {
        static void Require(string? value, string name)
        { if (string.IsNullOrWhiteSpace(value)) throw new JsonException($"{name} is missing."); }
        static void RequireSession(FluxDeploySessionRecord? session, string name)
        {
            if (session is null) throw new JsonException($"{name} is missing.");
            Require(session.NormalizedUrl, name + ".normalizedUrl");
            Require(session.IdentityStatus, name + ".identityStatus");
        }
        static void RequireIds(IReadOnlyList<string>? ids, string name)
        {
            if (ids is null) throw new JsonException($"{name} is missing.");
            if (ids.Any(string.IsNullOrWhiteSpace)) throw new JsonException($"{name} contains an empty ID.");
        }

        if (state.Kind != FluxDeployState.KindValue) throw new JsonException("kind is not \"flux-deploy\".");
        if (state.SchemaVersion != FluxDeployState.CurrentSchemaVersion) throw new JsonException("schemaVersion is not 2.");
        if (state.Session is not null) RequireSession(state.Session, "session");
        if (state.Modules is null) throw new JsonException("modules is missing.");
        foreach (var (key, module) in state.Modules)
        {
            var name = $"modules.{key}";
            if (module is null) throw new JsonException($"{name} is null.");
            if (module.InputHash is null) throw new JsonException($"{name}.inputHash is missing.");
            if (module.Origin == FluxDeployOrigins.Deployed)
            {
                Require(module.ParentSlotId, name + ".parentSlotId");
                Require(module.RootSlotId, name + ".rootSlotId");
                Require(module.ModuleName, name + ".moduleName");
            }
            else if (module.Origin != FluxDeployOrigins.MigratedV1)
                throw new JsonException($"{name}.origin '{module.Origin}' is not known.");
            if (module.Bindings is null) throw new JsonException($"{name}.bindings is missing.");
            foreach (var binding in module.Bindings)
            {
                if (binding is null) throw new JsonException($"{name}.bindings contains null.");
                Require(binding.Port, name + ".bindings.port");
                Require(binding.TargetId, name + ".bindings.targetId");
                if (binding.Mode is not ("source" or "drive")) throw new JsonException($"{name}.bindings.mode '{binding.Mode}' is not source or drive.");
            }
        }
        if (state.Pending is null) throw new JsonException("pending is missing.");
        foreach (var pending in state.Pending)
        {
            if (pending is null) throw new JsonException("pending contains null.");
            Require(pending.OperationId, "pending.operationId");
            var name = $"pending[{pending.OperationId}]";
            Require(pending.Module, name + ".module");
            RequireSession(pending.Session, name + ".session");
            Require(pending.ParentSlotId, name + ".parentSlotId");
            Require(pending.ModuleName, name + ".moduleName");
            if (pending.InputHash is null) throw new JsonException($"{name}.inputHash is missing.");
            if (pending.PreviousRootSlotId is not null) Require(pending.PreviousRootSlotId, name + ".previousRootSlotId");
            RequireIds(pending.ObservedChildIds, name + ".observedChildIds");
            RequireIds(pending.CandidateRootIds, name + ".candidateRootIds");
        }
        if (state.Pending.GroupBy(p => p.OperationId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("pending contains a repeated operationId.");
    }

    /// <summary>Atomic: a temporary file replaces the state. A v1 file becomes v2 here and older CLIs then reject it.</summary>
    internal static void Save(string path, FluxDeployState state)
    {
        path = Path.GetFullPath(path);
        state.Kind = FluxDeployState.KindValue;
        state.SchemaVersion = FluxDeployState.CurrentSchemaVersion;
        try { Validate(state); }
        catch (JsonException ex) { throw Invalid(path, ex.Message, ex); }
        try
        {
            SaveFault.Value?.Invoke(path, state);
            CheckpointFiles.Write(path, JsonSerializer.Serialize(state, Options) + "\n");
            AfterSaveFault.Value?.Invoke(path, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw WriteFailed(path, ex); }
    }

    // Per async execution; tests can fail each persistence phase (same shape as ApplyStateStore).
    internal static readonly AsyncLocal<Action<string, FluxDeployState>?> SaveFault = new();
    internal static readonly AsyncLocal<Action<string, FluxDeployState>?> AfterSaveFault = new();

    /// <summary>
    /// Records a deployment as unsettled. Call before the deployer. Refuses with FLUX_DEPLOY_PENDING while the state
    /// already holds any pending deployment.
    /// </summary>
    internal static FluxDeployState AddPending(string path, FluxDeployPending pending) => Mutate(path, false, (file, state) =>
    {
        if (state.Pending.Count > 0) throw PendingExists(file, state);
        state.Pending.Add(pending);
    });

    /// <summary>Records how far the deployer got. Candidate IDs are evidence for the user, not bindings.</summary>
    internal static FluxDeployState UpdatePending(string path, string operationId, FluxDeployStage stage,
        FluxDeploySendStatus sendStatus, string? requestedRootId = null, IEnumerable<string>? candidateRootIds = null,
        string? reason = null) => Mutate(path, true, (file, state) =>
    {
        var index = IndexOf(file, state, operationId);
        var current = state.Pending[index];
        state.Pending[index] = current with
        {
            Stage = stage, SendStatus = sendStatus,
            RequestedRootId = requestedRootId ?? current.RequestedRootId,
            CandidateRootIds = candidateRootIds?.Distinct(StringComparer.Ordinal).ToArray() ?? current.CandidateRootIds,
            Reason = reason ?? current.Reason
        };
    });

    /// <summary>
    /// Settles a deployment as created and verified: stores <paramref name="module"/> (origin <c>deployed</c>) under
    /// the pending record's key, removes the pending record, and makes its session the state's session. Other
    /// deployed records survive only when the old and new sessions prove the same epoch.
    /// </summary>
    internal static FluxDeployState ResolvePending(string path, string operationId, FluxDeployModuleRecord module) =>
        Mutate(path, true, (file, state) =>
        {
            var pending = state.Pending[IndexOf(file, state, operationId)];
            if (!string.Equals(module.ParentSlotId, pending.ParentSlotId, StringComparison.Ordinal) ||
                !string.Equals(module.ModuleName, pending.ModuleName, StringComparison.Ordinal))
                throw new ArgumentException("The settled module must have the parent and declared name of its pending record.", nameof(module));
            // Session is shared by all deployed records. Never relabel IDs from an unproven or different epoch
            // with this deployment's identity. P6 migration candidates retain their separate runtime checks.
            if (!HaveSameProvenEpoch(state.Session, pending.Session))
                foreach (var key in state.Modules.Where(pair => pair.Value.Origin == FluxDeployOrigins.Deployed)
                    .Select(pair => pair.Key).ToArray())
                    state.Modules.Remove(key);
            state.Modules[pending.Module] = module with { Origin = FluxDeployOrigins.Deployed };
            state.Pending.RemoveAll(p => p.OperationId == operationId);
            state.Session = pending.Session;
        });

    /// <summary>
    /// Settles a deployment whose result is known and created nothing. With <paramref name="forgetModule"/> the
    /// module's record is dropped too (its recorded root is proven gone); otherwise the record is kept unchanged.
    /// Not for unknown results: those stay pending until the user discards them.
    /// </summary>
    internal static FluxDeployState ReleasePending(string path, string operationId, bool forgetModule) =>
        Mutate(path, true, (file, state) =>
        {
            var pending = state.Pending[IndexOf(file, state, operationId)];
            state.Pending.RemoveAll(p => p.OperationId == operationId);
            if (forgetModule) state.Modules.Remove(pending.Module);
        });

    /// <summary>
    /// Records the declared name after the guard checked the exact migrated ID and parent (P6). Keeps the
    /// migration origin: a not-sent or failed replacement does not establish deployed epoch evidence.
    /// </summary>
    internal static FluxDeployState ConfirmMigratedModule(string path, string module, string parentSlotId, string moduleName) =>
        Mutate(path, true, (file, state) =>
        {
            if (!state.Modules.TryGetValue(module, out var record) || record.Origin != FluxDeployOrigins.MigratedV1 ||
                record.RootSlotId is null || record.ParentSlotId != parentSlotId)
                throw new InvalidOperationException($"Module '{module}' in '{file}' is not a migrated record with a root ID.");
            state.Modules[module] = record with { ModuleName = moduleName };
        });

    // A URL match alone, absent announcement evidence, never proves session-scoped IDs belong to this world.
    internal static bool HaveSameProvenEpoch(FluxDeploySessionRecord? recorded, FluxDeploySessionRecord current) =>
        recorded is { IdentityStatus: "matched", DiscoverSessionId: not null } &&
        current is { IdentityStatus: "matched", DiscoverSessionId: not null } &&
        recorded.DiscoverSessionId.StartsWith("S-", StringComparison.Ordinal) &&
        string.Equals(recorded.NormalizedUrl, current.NormalizedUrl, StringComparison.Ordinal) &&
        string.Equals(recorded.DiscoverSessionId, current.DiscoverSessionId, StringComparison.Ordinal);

    /// <summary>Drops a module's record: the guard found that its recorded root cannot be trusted (P6) or is gone.</summary>
    internal static FluxDeployState ForgetModule(string path, string module) =>
        Mutate(path, true, (_, state) => state.Modules.Remove(module));

    internal static FluxDeployState Mutate(string path, bool requireState, Action<string, FluxDeployState> change)
    {
        path = Path.GetFullPath(path);
        // Do not create directories or lock files for a state that must already exist.
        if (requireState && !File.Exists(path)) throw NotFound(path, null);
        using var writer = AcquireWriter(path);
        var state = Load(path, requireState);
        change(path, state);
        Save(path, state);
        return state;
    }

    private static int IndexOf(string path, FluxDeployState state, string operationId)
    {
        var index = state.Pending.FindIndex(p => p.OperationId == operationId);
        return index >= 0 ? index : throw new RLoopException("INVALID_OPTION",
            $"No pending operation '{operationId}' exists in '{path}'.", ExitCodes.InvalidArguments,
            new Dictionary<string, object?> { ["stateFile"] = path, ["operationId"] = operationId });
    }

    private static FileStream AcquireWriter(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            throw new RLoopException("FLUX_STATE_BUSY", "Another ResoLoop process is changing this Flux deploy state. Wait for it to finish.",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw WriteFailed(path, ex); }
    }

    /// <summary>
    /// The FLUX_DEPLOY_PENDING stop for a state that holds unsettled deployments. Public so the entries (manifest,
    /// watch) can stop before building anything; the guard raises the same error itself.
    /// </summary>
    public static RLoopException PendingExists(string path, FluxDeployState state) => new("FLUX_DEPLOY_PENDING",
        $"Flux deploy state '{path}' holds {state.Pending.Count} unsettled deployment(s); nothing was written.", ExitCodes.OperationFailed,
        new Dictionary<string, object?>
        {
            ["stateFile"] = path,
            ["pending"] = state.Pending.Select(p => new Dictionary<string, object?>
            {
                ["operationId"] = p.OperationId, ["module"] = p.Module, ["moduleName"] = p.ModuleName,
                ["parentSlotId"] = p.ParentSlotId, ["previousRootSlotId"] = p.PreviousRootSlotId,
                ["requestedRootId"] = p.RequestedRootId, ["candidateRootIds"] = p.CandidateRootIds,
                ["stage"] = p.Stage, ["sendStatus"] = p.SendStatus
            }).ToArray()
        },
        ["Inspect the parent and each candidate by exact ID, delete unwanted Slots only by exact ID with --yes, then discard the pending record with its OPERATION_ID and --yes. Discarding does not write to the world."]);

    private static RLoopException NotFound(string path, Exception? inner) => new("FLUX_STATE_NOT_FOUND",
        $"Flux deploy state '{path}' does not exist.", ExitCodes.NotFound,
        new Dictionary<string, object?> { ["stateFile"] = path }, innerException: inner);
    private static RLoopException Invalid(string path, string detail, Exception inner) => new("FLUX_STATE_INVALID",
        $"Invalid Flux deploy state '{path}': {detail}", ExitCodes.ValidationFailed,
        new Dictionary<string, object?> { ["stateFile"] = path }, innerException: inner);
    private static RLoopException WriteFailed(string path, Exception inner) => new("FLUX_STATE_WRITE_FAILED",
        $"Could not write Flux deploy state '{path}': {inner.Message}", ExitCodes.OperationFailed,
        new Dictionary<string, object?> { ["stateFile"] = path }, innerException: inner);

    private static string SafeFileName(string module)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(SingleDeployModuleKey(module).Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray()).Trim('_', '.');
        return string.IsNullOrWhiteSpace(safe) ? "module" : safe;
    }

    private sealed class V1State
    {
        public int SchemaVersion { get; set; } = 1;
        public string? ParentSlotId { get; set; }
        public string? SessionId { get; set; }
        public Dictionary<string, V1Module?>? Modules { get; set; }
    }
    private sealed record V1Module(string? Hash, string? SlotId);

    // Unknown members are rejected in both versions: a state this CLI does not fully understand is not used.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record FluxDeployPendingDiscardResult(string StateFile, string OperationId, string Module, string ModuleName,
    string ParentSlotId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PreviousRootSlotId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RequestedRootId,
    IReadOnlyList<string> CandidateRootIds, FluxDeployStage Stage, FluxDeploySendStatus SendStatus, string Warning);

// An offline state operation: it cannot obtain a client or send a world request.
public static class FluxDeployPendingDiscard
{
    /// <summary>
    /// Removes one pending deployment from the state after the user inspected the world. Module records are left
    /// unchanged and no candidate ID is adopted. Other pending records stay.
    /// </summary>
    public static FluxDeployPendingDiscardResult Discard(string statePath, string operationId, bool confirmed)
    {
        if (!confirmed)
            throw new RLoopException("CONFIRMATION_REQUIRED", "Discarding a pending Flux deployment requires --yes after inspecting the world.", ExitCodes.ValidationFailed);
        if (string.IsNullOrWhiteSpace(operationId))
            throw new RLoopException("INVALID_OPTION", "An operation ID is required to discard a pending Flux deployment.", ExitCodes.InvalidArguments);
        FluxDeployPending? discarded = null;
        var path = Path.GetFullPath(statePath);
        FluxDeployStateStore.Mutate(path, true, (file, state) =>
        {
            discarded = state.Pending.FirstOrDefault(p => p.OperationId == operationId) ?? throw new RLoopException(
                "INVALID_OPTION", $"No pending operation '{operationId}' exists in '{file}'.", ExitCodes.InvalidArguments,
                new Dictionary<string, object?> { ["stateFile"] = file, ["operationId"] = operationId });
            state.Pending.RemoveAll(p => p.OperationId == operationId);
        });
        var pending = discarded!;
        return new(path, operationId, pending.Module, pending.ModuleName, pending.ParentSlotId, pending.PreviousRootSlotId,
            pending.RequestedRootId, pending.CandidateRootIds, pending.Stage, pending.SendStatus,
            "Discarding deployment evidence does not write to the world and does not adopt any candidate root; the module's recorded root is unchanged. " +
            "The previous module may already be removed, and a whole or partial new module may exist outside management. " +
            "Inspect the parent's direct children and each candidate by exact ID (inspect EXACT_SLOT_ID); delete unwanted Slots only by exact ID with --yes. " +
            "A child with the module's name that is not the recorded root stops the next deploy.");
    }
}

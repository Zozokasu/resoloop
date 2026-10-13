using System.Text.Json;

namespace RLoop.Core;

public sealed record CreationRecoveryOptions(string StateFile, string Action, string? RequestedSessionId = null, bool Yes = false);
public sealed record CreationRecoveryResult(string StateFile, string? OperationId, string Action,
    string? ActiveRootId, object? Pending, CreationPlanEvidence Evidence, IReadOnlyList<object?> Diagnostics, bool Atomic, int EngineAddedComponents = 0);

/// <summary>Reconciles a retained creation transaction from exact state and runtime ownership evidence.</summary>
public sealed class CreationRecoveryService(IResoniteClient client)
{
    public async Task<CreationRecoveryResult> RecoverAsync(CreationRecoveryOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.StateFile)) throw StateRequired("A creation state file is required.");
        if (options.Action is not ("rollback" or "finish")) throw ModeMismatch("Creation recovery action must be rollback or finish.");
        if (!options.Yes) throw Confirmation();
        var path = Path.GetFullPath(options.StateFile);
        ValidateRecoveryInput(path, options.Action, options.Yes);
        var state = LoadByRootCreationId(path, true)!;
        var pending = RequirePending(state, path);
        ValidateScope(pending, path);
        var initialOperationId = pending.OperationId;

        SessionWriteLock sessionLease;
        try
        {
            sessionLease = await SessionWriteLock.AcquireAsync(client, path, ct)
                ?? throw CreationOwnershipVerifier.SessionUnproven("Creation recovery requires a connected ResoniteLink session.");
        }
        catch (RLoopException ex) { throw WithContext(ex, state, path); }
        using var sessionLeaseScope = sessionLease;
        IDisposable writerLease;
        try { writerLease = CheckpointFiles.AcquireWriter(path); }
        catch (RLoopException ex) { throw WithContext(ex, state, path); }
        using var writerLeaseScope = writerLease;
        try { state = LoadByRootCreationId(path, true)!; }
        catch (RLoopException ex) { throw WithContext(ex, state, path); }
        pending = RequirePending(state, path);
        ValidateScope(pending, path);
        var verifier = new CreationOwnershipVerifier(client);
        CreationSessionProof proof;
        try
        {
            proof = await verifier.VerifySessionAsync(state.Session, verifier.CreateContext(ct), null);
            if (!string.Equals(sessionLease.NormalizedUrl, proof.NormalizedUrl, StringComparison.Ordinal))
                throw CreationOwnershipVerifier.SessionUnproven("The connected session URL differs from the URL whose write lease was acquired.");
            if (pending.Session.IdentityStatus != "matched" || pending.Session.NormalizedUrl != proof.NormalizedUrl ||
                pending.Session.DiscoverSessionId != proof.DiscoverSessionId || state.Session.DiscoverSessionId != proof.DiscoverSessionId)
                throw CreationOwnershipVerifier.SessionUnproven("The state and pending operation do not prove the currently connected discovered session.");
            if (options.RequestedSessionId is { } requested && requested != proof.DiscoverSessionId)
                throw CreationOwnershipVerifier.SessionUnproven("The requested --session does not match the connected discovered session.");
            if (proof.ConnectionGeneration is null || client is not IApplyConnectionGuard || client is not IApplyDeletionObservation)
                throw CreationOwnershipVerifier.SessionUnproven("Creation recovery requires connection-generation and exact Slot observation support.");
        }
        catch (RLoopException ex) { throw WithContext(ex, state, path); }
        if (!string.Equals(pending.OperationId, initialOperationId, StringComparison.Ordinal))
            throw Unverified("The pending operation changed while recovery locks were being acquired.", state, path);

        try { sessionLease.RecordState(path); }
        catch (RLoopException ex) { throw WithContext(ex, state, path); }
        var tx = new CreationTransaction(client, path, state, proof.ConnectionGeneration, ct);
        tx.ReplacePending(pending with { Evidence = AddRecoveryProof(pending.Evidence, proof) });
        tx.Save();
        try
        {
            var result = options.Action == "rollback"
                ? await RollbackAsync(client, verifier, tx, proof, path, ct)
                : await FinishAsync(client, verifier, tx, proof, path, ct);
            return result;
        }
        catch (RLoopException ex) { throw tx.Contextualize(RecoveryError(ex, tx.State, path)); }
        catch (OperationCanceledException ex)
        { throw tx.Contextualize(new RLoopException("APPLY_CANCELLED", "Creation recovery was cancelled; pending evidence was retained.", ExitCodes.OperationFailed, innerException: ex)); }
        catch (TimeoutException ex)
        { throw tx.Contextualize(new RLoopException("REQUEST_TIMEOUT", "Creation recovery timed out; pending evidence was retained.", ExitCodes.Timeout, innerException: ex)); }
    }

    /// <summary>Performs strict local checks that must finish before the CLI connects to ResoniteLink.</summary>
    public static void ValidateRecoveryInput(string stateFile, string action, bool yes)
    {
        if (string.IsNullOrWhiteSpace(stateFile)) throw StateRequired("A creation state file is required.");
        if (action is not ("rollback" or "finish")) throw ModeMismatch("Creation recovery action must be rollback or finish.");
        if (!yes) throw Confirmation();
        var path = Path.GetFullPath(stateFile);
        var state = LoadByRootCreationId(path, true)!;
        var pending = RequirePending(state, path);
        ValidateScope(pending, path);
    }

    public static CreationRecoveryResult DiscardPending(string stateFile, string operationId, bool yes)
    {
        if (string.IsNullOrWhiteSpace(stateFile)) throw StateRequired("A creation state file is required.");
        if (string.IsNullOrWhiteSpace(operationId)) throw new RLoopException("ARGUMENT_REQUIRED", "--discard-pending requires an operation ID.", ExitCodes.InvalidArguments);
        if (!yes) throw Confirmation();
        var path = Path.GetFullPath(stateFile);
        using var writer = CheckpointFiles.AcquireWriter(path);
        var state = LoadByRootCreationId(path, true)!;
        var pending = RequirePending(state, path);
        if (!string.Equals(operationId, pending.OperationId, StringComparison.Ordinal))
            throw Unverified("The supplied operation ID does not exactly match the pending creation operation.", state, path);
        var ids = new[] { pending.CandidateRoot?.Id, pending.PreviousRoot?.Id, state.ActiveRoot?.Id }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        var warning = $"Pending was discarded locally. Possible remaining roots: {(ids.Length == 0 ? "unknown" : string.Join(", ", ids))}. No world cleanup was performed.";
        try { CreationStateStore.Save(path, state with { Pending = null }); }
        catch (RLoopException ex) { throw WithContext(ex, state, path); }
        return new(path, pending.OperationId, "discard", state.ActiveRoot?.Id, null,
            new("unknown", "unknown", "unknown", "unknown", "unknown", "unknown", "unknown"),
            [new { warning, possibleRemainingRootIds = ids }], false);
    }

    private static async Task<CreationRecoveryResult> RollbackAsync(IResoniteClient client, CreationOwnershipVerifier verifier,
        CreationTransaction tx, CreationSessionProof proof, string path, CancellationToken ct)
    {
        var state = tx.State; var pending = state.Pending!;
        CreationActiveRoot? restored = null;
        var diagnostics = new List<object?>();
        if (pending.PreviousRoot is { } recordedPrevious && state.ActiveRoot is { } recordedActive &&
            recordedPrevious.Id != recordedActive.Id)
            throw CreationOwnershipVerifier.OwnerUnproven("activeRoot and pending.previousRoot identify different old roots; recovery will not guess which one to restore.");
        var previous = pending.PreviousRoot ?? state.ActiveRoot;
        if (previous is not null)
        {
            var priorState = state with { ActiveRoot = previous, Session = pending.Session };
            if (await IsExactAbsentAsync(client, verifier, priorState, proof, previous.Id, ct)) restored = null;
            else
            {
                await VerifySession(verifier, priorState, proof, ct);
                await verifier.VerifyAsync(priorState, verifier.CreateContext(ct));
                restored = previous;
            }
        }

        var candidate = pending.CandidateRoot;
        if (candidate?.Id is null)
        {
            var rootIntent = pending.Steps.Any(step => step.Kind is "create-root" or "create-slot" ||
                step.TargetId is null && step.SendStatus is "intent" or "unknown");
            if (rootIntent || restored is null)
                throw Unverified("Candidate root ID is unknown; rollback cannot prove absence or clean it safely. Keep pending and inspect an exact target, or use --discard-pending OP --yes for local discard.", state, path);
            diagnostics.Add(new { code = "APPLY_WRITE_UNVERIFIED", warning = "Candidate root ID was not recorded. The exact old root was retained; no unknown root was searched for or deleted." });
        }
        else
        {
            if (previous?.Id == candidate.Id || candidate.Id.Equals("Root", StringComparison.OrdinalIgnoreCase))
                throw CreationOwnershipVerifier.OwnerUnproven("Candidate and previous root IDs collide or target Root; no delete was sent.");
            if (!await IsExactAbsentAsync(client, verifier, state with { Session = pending.Session }, proof, candidate.Id, ct))
            {
                if (!CandidateMetadataMatches(pending, candidate))
                    throw Unverified("Candidate ownership metadata is incomplete; no delete was sent.", state, path);
                if (candidate is not { ParentId: { } parentId, GenerationId: { } generationId,
                    Stamp: { } stamp, InputHash: { } inputHash })
                    throw Unverified("Candidate ownership metadata is incomplete; no delete was sent.", state, path);
                var candidateRoot = new CreationActiveRoot(candidate.Id, parentId, generationId,
                    stamp, inputHash, candidate.BuildId);
                var candidateState = state with { Session = pending.Session, ActiveRoot = candidateRoot };
                await VerifySession(verifier, candidateState, proof, ct);
                await verifier.VerifyAsync(candidateState, verifier.CreateContext(ct));
                tx.SetStage("cleanupCandidate");
                var observer = (IApplyDeletionObservation)client;
                await tx.SendAsync("cleanupCandidate", "delete-slot", candidate.Id, parentId, null,
                    new { targetId = candidate.Id }, async () => { await client.DeleteSlotAsync(candidate.Id, ct); return candidate.Id; },
                    async id =>
                    {
                        await VerifySession(verifier, candidateState, proof, ct);
                        try
                        {
                            _ = await observer.ObserveDeletionSlotAsync(id, ct);
                            await VerifySession(verifier, candidateState, proof, ct);
                            return false;
                        }
                        catch (RLoopException ex) when (ex.Code == "SLOT_NOT_FOUND")
                        {
                            await VerifySession(verifier, candidateState, proof, ct);
                            throw;
                        }
                    }, absenceIsSuccess: true);
            }
        }
        await VerifySession(verifier, state, proof, ct);
        var final = tx.State with { ActiveRoot = restored, Pending = null };
        tx.SaveFinal(final);
        return new(path, pending.OperationId, "rollback", restored?.Id, null,
            new("complete", "complete", "complete", restored is null ? "unknown" : "complete",
                restored is null ? "unknown" : "complete", restored is null ? "unknown" : "complete",
                restored is null ? "unknown" : "complete"), diagnostics, false);
    }

    private static async Task<CreationRecoveryResult> FinishAsync(IResoniteClient client, CreationOwnershipVerifier verifier,
        CreationTransaction tx, CreationSessionProof proof, string path, CancellationToken ct)
    {
        var state = tx.State; var pending = state.Pending!;
        var candidate = pending.CandidateRoot;
        if (pending.Stage != "staged" || candidate is null || candidate.Id is not { } id ||
            !TryExpectedCounts(pending.Evidence, out var counts))
            throw Unverified("Finish requires stage staged, an exact candidate ID, treeComplete=true, and safe expectedCounts. Use --action rollback where exact ownership permits.", state, path);
        if (!CandidateMetadataMatches(pending, candidate))
            throw Unverified("Candidate exact ownership metadata is incomplete; finish was not applied.", state, path);
        if (candidate is not { ParentId: { } parentId, GenerationId: { } generationId,
            Stamp: { } stamp, InputHash: { } inputHash })
            throw Unverified("Candidate exact ownership metadata is incomplete; finish was not applied.", state, path);
        var active = new CreationActiveRoot(id, parentId, generationId, stamp, inputHash, candidate.BuildId);
        var candidateState = state with { Session = pending.Session, ActiveRoot = active };
        await VerifySession(verifier, candidateState, proof, ct);
        await verifier.VerifyAsync(candidateState, verifier.CreateContext(ct));
        var engineAdded = await CreationTreeVerification.VerifyAsync(client, id, counts, ct);
        await VerifySession(verifier, candidateState, proof, ct);
        var final = tx.State with { ActiveRoot = active, Pending = null };
        tx.SaveFinal(final);
        return new(path, pending.OperationId, "finish", id, null,
            new("complete", "complete", "complete", "complete", "complete", "complete", "complete"), [], false, engineAdded);
    }

    private static async Task VerifySession(CreationOwnershipVerifier verifier, CreationState state,
        CreationSessionProof proof, CancellationToken ct)
    {
        var context = verifier.CreateContext(ct);
        await verifier.VerifySessionAsync(state.Session, context, proof);
    }

    private static Task<bool> IsExactAbsentAsync(IResoniteClient client, CreationOwnershipVerifier verifier,
        CreationState state, CreationSessionProof proof, string id, CancellationToken ct)
    {
        return CreationRootAbsence.IsExactAbsentAsync(client, verifier, state.Session, proof, id, ct);
    }

    private static bool TryExpectedCounts(JsonElement? evidence, out CreationPlanCounts counts)
    {
        counts = default!;
        if (!evidence.HasValue || evidence.Value.ValueKind != JsonValueKind.Object)
            return false;
        var value = evidence.Value;
        if (
            !value.TryGetProperty("treeComplete", out var complete) || complete.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("expectedCounts", out var expected) || expected.ValueKind != JsonValueKind.Object ||
            !expected.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Number || !slots.TryGetInt32(out var slotCount) || slotCount <= 0 ||
            !expected.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Number || !components.TryGetInt32(out var componentCount) || componentCount < 0 ||
            slotCount > CreationTreeVerification.MaxSlots || componentCount > CreationTreeVerification.MaxComponents) return false;
        counts = new(slotCount, componentCount); return true;
    }

    private static bool CandidateMetadataMatches(CreationPending pending, CreationPendingRoot candidate) =>
        candidate.Id is not null && candidate.ParentId is not null && candidate.GenerationId is not null &&
        candidate.Stamp is not null && candidate.InputHash is not null && pending.InputHash is not null &&
        candidate.ParentId == pending.Scope.ParentId && candidate.InputHash == pending.InputHash &&
        candidate.BuildId == pending.BuildId;

    private static JsonElement AddRecoveryProof(JsonElement? evidence, CreationSessionProof proof)
    {
        var source = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (evidence.HasValue && evidence.Value.ValueKind == JsonValueKind.Object)
            foreach (var property in evidence.Value.EnumerateObject()) source[property.Name] = property.Value.Clone();
        source["recoveryProof"] = JsonSerializer.SerializeToElement(new { normalizedUrl = proof.NormalizedUrl, discoverSessionId = proof.DiscoverSessionId, connectionGeneration = proof.ConnectionGeneration });
        return JsonSerializer.SerializeToElement(source);
    }

    private static CreationState? LoadByRootCreationId(string path, bool requireState)
    {
        string json;
        try { json = CheckpointFiles.Read(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { if (!requireState) return null; throw StateRequired($"Creation state file '{path}' is required.", ex, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new RLoopException("APPLY_STATE_INVALID", $"Could not read creation state '{path}': {ex.Message}", ExitCodes.ValidationFailed,
            new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex); }
        string creationId;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("schemaVersion", out var version) &&
                version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var schemaVersion) && schemaVersion is >= 1 and <= 3)
                throw new RLoopException("CREATION_STATE_MIGRATION_REQUIRED",
                    $"State file '{path}' uses schemaVersion {schemaVersion}; creation state migration is required.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["stateFile"] = path });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("creationId", out var id) || id.ValueKind != JsonValueKind.String)
                throw new RLoopException("APPLY_STATE_INVALID", $"Creation state '{path}' has no root creationId.", ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["stateFile"] = path });
            creationId = id.GetString()!;
        }
        catch (JsonException ex) { throw new RLoopException("APPLY_STATE_INVALID", $"Could not parse creation state '{path}': {ex.Message}", ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex); }
        return CreationStateStore.Load(path, creationId, requireState);
    }

    private static CreationPending RequirePending(CreationState state, string path) => state.Pending ??
        throw new RLoopException("CREATION_STATE_REQUIRED", "Creation state has no pending operation to recover.", ExitCodes.NotFound,
            new Dictionary<string, object?> { ["stateFile"] = path, ["pending"] = null });

    private static void ValidateScope(CreationPending pending, string path)
    {
        if (pending.Scope.Kind != "creation" || pending.ReplaceOrder != "delete-first")
            throw new RLoopException("CREATION_MODE_MISMATCH", "Recovery supports only creation-scope, delete-first pending operations in this version.", ExitCodes.InvalidArguments,
                new Dictionary<string, object?> { ["stateFile"] = path, ["operationId"] = pending.OperationId, ["stage"] = pending.Stage, ["pending"] = pending });
    }

    private static RLoopException RecoveryError(RLoopException error, CreationState state, string path) => WithContext(error, state, path);
    private static RLoopException WithContext(RLoopException error, CreationState state, string path)
    {
        var pending = state.Pending;
        var context = error.Context.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        context["stateFile"] = path; context["operationId"] = pending?.OperationId; context["stage"] = pending?.Stage;
        context["previousRootId"] = pending?.PreviousRoot?.Id; context["candidateRootId"] = pending?.CandidateRoot?.Id;
        context["pending"] = pending; context["completeness"] = error.Code == "APPLY_STATE_WRITE_FAILED" ? "partial" : "unknown";
        var suggestions = error.Suggestions.ToList();
        if (pending is not null)
        {
            suggestions.Add($"Retry only after inspecting exact evidence: resoloop creation recover --state \"{path}\" --action rollback --url auto --session {pending.Session.DiscoverSessionId ?? "S-ID"} --yes");
            suggestions.Add($"For local discard only, use the exact operation ID: resoloop creation recover --state \"{path}\" --discard-pending {pending.OperationId} --yes");
        }
        return new(error.Code, error.Message, error.ExitCode, context, suggestions.Distinct(StringComparer.Ordinal).ToArray(), error);
    }
    private static RLoopException Unverified(string message, CreationState state, string path) => WithContext(
        new RLoopException("APPLY_WRITE_UNVERIFIED", message, ExitCodes.OperationFailed), state, path);
    private static RLoopException Confirmation() => new("CONFIRMATION_REQUIRED", "Creation recovery requires --yes after inspecting the retained operation.", ExitCodes.ValidationFailed);
    private static RLoopException StateRequired(string message, Exception? inner = null, string? stateFile = null) => new(
        "CREATION_STATE_REQUIRED", message, ExitCodes.NotFound,
        stateFile is null ? null : new Dictionary<string, object?> { ["stateFile"] = stateFile }, innerException: inner);
    private static RLoopException ModeMismatch(string message) => new("CREATION_MODE_MISMATCH", message, ExitCodes.InvalidArguments);
}

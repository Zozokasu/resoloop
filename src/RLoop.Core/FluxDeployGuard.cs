namespace RLoop.Core;

// The deploy guard (ROADMAP-9 unit 3a): the one entry through which deploy-manifest, the single-module deploy
// and watch place a ProtoGraph module. It owns the session lock, the Flux deploy state, the observations
// before and after the deployer, and the pending record. Nothing here references Flux-SDK or ResoniteLink types.

/// <param name="Module">Module path as given to the deployer.</param>
/// <param name="ModuleKey">Key into <see cref="FluxDeployState.Modules"/> (manifest module name, or <see cref="FluxDeployStateStore.SingleDeployModuleKey"/>).</param>
/// <param name="ParentSlotId">Exact ID of the parent Slot. Root is refused.</param>
/// <param name="Url">URL the deployer connects to. Must normalize to the URL of the guard's own connection.</param>
/// <param name="InputHash">Hash of source, dependencies and bindings; stored with the settled record.</param>
/// <param name="Bindings">Bindings to store with the settled record; when null they are derived from the maps.</param>
/// <param name="Deadline">Time the deployer is allowed (see <see cref="FluxDeployExecuteRequest.Deadline"/>).</param>
/// <param name="ExpectedModuleName">When given, the declared module name must equal it (FLUX_MODULE_NAME_MISMATCH otherwise).</param>
/// <param name="RequireAllPortsBound">
/// When true, every port the compile reports must have a binding (a manifest declares a complete one-to-one map);
/// otherwise an unbound port is reported as a warning and is created unwired. Either way a binding for a port the
/// compile does not report stops with FLUX_BINDING_PORTS_MISMATCH before anything is written.
/// </param>
public sealed record FluxDeployGuardRequest(
    string ProjectDirectory,
    string Module,
    string ModuleKey,
    string ParentSlotId,
    Uri Url,
    string? LibraryPath,
    string? HelperPath,
    IReadOnlyDictionary<string, string>? InputMap,
    IReadOnlyDictionary<string, string>? OutputMap,
    string InputHash,
    IReadOnlyList<FluxDeployBindingRecord>? Bindings,
    string StatePath,
    string? SdkVersion,
    TimeSpan Deadline,
    string? ExpectedModuleName = null,
    bool RequireAllPortsBound = false)
{
    /// <summary>Source declarations used by the manifest's target-type preflight, joined to actual preparation operations.</summary>
    public IReadOnlyList<FluxDeclaredPortExpectation>? DeclaredPorts { get; init; }
}

/// <summary>What the guard observed and relied on before it let the deployer write.</summary>
/// <param name="RecordBasis">One of <see cref="FluxDeployRecordBasis"/>: why <paramref name="PreviousRootSlotId"/> is (or is not) the recorded previous module.</param>
/// <param name="PreviousRootSlotId">The recorded previous module root handed to the deployer for exact removal, or null.</param>
/// <param name="SameNameChildIds">Direct children of the parent named like the declared module, read just before the deployer.</param>
/// <param name="ObservedChildIds">Every direct child of the parent, read just before the deployer.</param>
/// <param name="IdentityStatus"><c>matched</c> when the <c>S-</c> ID was matched to the URL, otherwise <c>unknown</c> (recorded, not a reason to stop).</param>
/// <param name="WriterCheck">
/// <see cref="FluxDeployCheckStatus"/> of the output-writer check: <c>noOutputs</c>, <c>observedRangeClear</c> or
/// <c>unknown</c>. None of them means "no other writer exists"; see <see cref="WriterObservation"/>.
/// </param>
public sealed record FluxDeployGuardPreconditions(
    string RecordBasis,
    string? PreviousRootSlotId,
    IReadOnlyList<string> SameNameChildIds,
    IReadOnlyList<string> ObservedChildIds,
    string IdentityStatus,
    string WriterCheck)
{
    /// <summary>What the output-writer check read and what it could not read. Null only when the check did not run.</summary>
    public FluxDeployWriterObservation? WriterObservation { get; init; }

    /// <summary>The binding keys compared with the compiled ports before anything was written (unit 5). Null only when the check did not run.</summary>
    public FluxDeployBindingPortsCheck? BindingPorts { get; init; }
}

public static class FluxDeployRecordBasis
{
    /// <summary>The state has no record for the module.</summary>
    public const string None = "none";
    /// <summary>A <c>deployed</c> record whose root is a direct child of the parent with the declared name.</summary>
    public const string Deployed = "deployed";
    /// <summary>A <c>migrated-v1</c> record whose recorded parent and exact ID match the child with the declared name (P6).</summary>
    public const string MigratedConfirmed = "migratedConfirmed";
    /// <summary>A <c>migrated-v1</c> record that does not match the world; treated as no record and dropped.</summary>
    public const string MigratedUntrusted = "migratedUntrusted";
    /// <summary>The recorded root no longer exists; treated as no record.</summary>
    public const string RecordedRootGone = "recordedRootGone";
    /// <summary>The state's URL and matched <c>S-</c> ID do not prove the current epoch; its deployed IDs are not used.</summary>
    public const string RecordFromOtherSession = "recordFromOtherSession";
}

public static class FluxDeployCheckStatus
{
    /// <summary>The check does not exist yet or did not run. Never read this as "passed".</summary>
    public const string NotPerformed = "notPerformed";
    /// <summary>Writer check: the request drives no output, so there is nothing to check.</summary>
    public const string NoOutputs = "noOutputs";
    /// <summary>
    /// Writer check: the observed range (the parent's own components and the components of its direct children)
    /// was read in full and holds no other possible writer. Writers outside that range remain unknown.
    /// </summary>
    public const string ObservedRangeClear = "observedRangeClear";
    /// <summary>Writer check: the observed range could not be read in full; no conflict was found in what was read.</summary>
    public const string Unknown = "unknown";
    /// <summary>Writer check: a possible writer was found. Only reported with FLUX_OUTPUT_WRITER_CONFLICT; such a run never deploys.</summary>
    public const string Conflict = "conflict";
    /// <summary>Binding readback: the request declares no binding, so nothing was read. Not the same as <see cref="NotPerformed"/>.</summary>
    public const string NoBindings = "noBindings";
    /// <summary>Binding readback: every declared binding was read back wired to its requested target.</summary>
    public const string Verified = "verified";
}

/// <summary>A settled deployment: the creation was answered cleanly, read back, and stored in the state.</summary>
/// <param name="BindingReadback">
/// <see cref="FluxDeployCheckStatus"/> of the binding readback: <c>noBindings</c> or <c>verified</c>. A readback that
/// failed or stayed unknown never settles, so it never appears here.
/// </param>
public sealed record FluxDeployGuardResult(
    string Module,
    string StateFile,
    string OperationId,
    string ModuleName,
    string ParentSlotId,
    string NewRootSlotId,
    FluxPreviousRootRemoval PreviousRootRemoval,
    FluxDeployStage Stage,
    FluxDeploySessionRecord Session,
    FluxDeployGuardPreconditions Preconditions,
    string BindingReadback,
    FluxDeployModuleRecord Record,
    IReadOnlyList<FluxDiagnostic> Diagnostics,
    IReadOnlyList<FluxModulePortInfo>? Ports,
    string StandardOutput,
    string StandardError)
{
    /// <summary>One entry per declared binding, as read back from the new module. Empty when there is no binding.</summary>
    public IReadOnlyList<FluxDeployBindingReadback> BindingEvidence { get; init; } = [];
    public IReadOnlyList<FluxWriterIdentityObservation> WriterObservations { get; init; } = [];
    public FluxDeployWriterIdentity? ExpectedWriterIdentity { get; init; }
}

public sealed partial class FluxDeployGuard(IResoniteClient client, IFluxDeployer deployer)
{
    /// <summary>
    /// How long past the request's deadline the guard waits for the deployer to answer by itself (the deployer
    /// closes its own connection at the deadline) before it gives up and records the result as unknown.
    /// </summary>
    internal TimeSpan DeadlineGrace { get; init; } = TimeSpan.FromSeconds(30);

    private sealed record Child(string Id, string Name);
    private sealed record Identity(ApplySessionObservation Observation, string? ResoniteVersion, string? LinkVersion, string? Generation);
    private sealed class ReadFailure(string reason, string message, Exception? inner = null) : Exception(message, inner)
    { public string Reason { get; } = reason; }

    /// <summary>Everything one run knows; handed to the extension points below.</summary>
    private sealed class Run
    {
        public required FluxDeployGuardRequest Request { get; init; }
        public required string StatePath { get; init; }
        public required string ModuleName { get; init; }
        public SessionWriteLock Lock { get; set; } = null!;
        public Identity Identity { get; set; } = null!;
        public string RecordBasis { get; set; } = FluxDeployRecordBasis.None;
        public string? PreviousRootSlotId { get; set; }
        public IReadOnlyList<Child> ChildrenBefore { get; set; } = [];
        public string WriterCheck { get; set; } = FluxDeployCheckStatus.NotPerformed;
        public FluxDeployWriterObservation? WriterObservation { get; set; }
        /// <summary>Output target ID to the Slot and component that held it in the writer check's read (unit 3b).</summary>
        public IReadOnlyDictionary<string, (string SlotId, string ComponentId)> TargetOwners { get; set; } =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        public IReadOnlyList<FluxDeployBindingReadback> BindingEvidence { get; set; } = [];
        public FluxDeployBindingPortsCheck? BindingPorts { get; set; }
        public IReadOnlyList<FluxModulePortInfo>? PreparedPorts { get; set; }
        public FluxDeployPending? Pending { get; set; }
        public FluxDeployExecution? Execution { get; set; }
    }

    /// <summary>
    /// Places one module under the same checks as apply. Returns only for a settled deployment; every other end
    /// is an <see cref="RLoopException"/> whose context states <c>worldWrites</c> (<c>none</c> or <c>possible</c>)
    /// and <c>pending</c> (<c>none</c>, <c>released</c> or <c>kept</c>).
    /// </summary>
    public async Task<FluxDeployGuardResult> DeployModuleAsync(FluxDeployGuardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ModuleKey) || string.IsNullOrWhiteSpace(request.StatePath) || request.Url is null)
            throw new RLoopException("INVALID_OPTION", "A Flux deployment needs a module key, a state path and a URL.", ExitCodes.InvalidArguments);
        if (request.Deadline <= TimeSpan.Zero)
            throw new RLoopException("INVALID_OPTION", "The Flux deployment deadline must be positive.", ExitCodes.InvalidArguments);
        var statePath = Path.GetFullPath(request.StatePath);

        // 1. Parent must be an explicit, non-Root Slot (P8). Nothing is contacted.
        if (string.IsNullOrWhiteSpace(request.ParentSlotId) || IsRootId(request.ParentSlotId))
            throw ParentRootRefused(request, statePath, string.IsNullOrWhiteSpace(request.ParentSlotId) ? "parentMissing" : "parentIsRoot");

        // 2. Compile only. The declared module name is the name every later comparison uses (P2).
        var preparation = await PrepareAsync(request, statePath, cancellationToken);
        var moduleName = preparation.DeclaredModuleName!;
        var run = new Run { Request = request, StatePath = statePath, ModuleName = moduleName };
        //    The binding keys must name the compiled ports (unit 5). Checked on the same compile, before the lock.
        run.BindingPorts = CheckBindingPorts(run, preparation.Ports);
        run.PreparedPorts = preparation.Ports;

        // 3. Exclusive writer for this URL. Another project's pending (apply or Flux) and a busy lock stop here
        //    with the existing APPLY_WRITE_UNVERIFIED / APPLY_SESSION_BUSY. Held until the result is settled or
        //    left pending.
        using var sessionLock = await SessionWriteLock.AcquireAsync(client, statePath, cancellationToken);
        if (sessionLock is null)
            throw SessionChanged(run, "sessionLockUnavailable", pendingKept: false,
                "The guard's connection is not a ResoniteLink (ws) URL, so the deployer's target cannot be locked.");
        run.Lock = sessionLock;
        var deployerUrl = ApplySessionObservation.NormalizeUrl(request.Url.AbsoluteUri);
        if (!string.Equals(deployerUrl, sessionLock.NormalizedUrl, StringComparison.Ordinal))
            throw SessionChanged(run, "deployerUrlMismatch", pendingKept: false,
                "The deployer's URL is not the URL whose lock is held.",
                new() { ["lockedUrl"] = sessionLock.NormalizedUrl, ["deployerUrl"] = deployerUrl });

        // 4. Identity of the connection the observations are made on.
        run.Identity = await ObserveIdentityAsync(run, null, pendingKept: false, cancellationToken);

        // 5. A pending deployment in this state stops everything until the user discards it.
        var state = FluxDeployStateStore.Load(statePath);
        if (state.Pending.Count > 0) throw FluxDeployStateStore.PendingExists(statePath, state);

        // 6. Observe the parent's direct children and reconcile them with the record (P2, P6).
        var confirmation = await ReconcileRecordAsync(run, state, cancellationToken);
        run.WriterCheck = await CheckWritersBeforeDeployAsync(run, cancellationToken);

        // 7. Point the lock at this state (same order as apply: just before the first pending record), bring the
        //    record in line with what was read, and save the pending record. A failure here deploys nothing.
        var pending = SavePending(run, confirmation);

        // 8. Last look before the deployer: identity and the same-name children must be what step 6 saw.
        IReadOnlyList<Child> baseline;
        try { baseline = await RecheckBeforeDeployAsync(run, cancellationToken); }
        catch (Exception error)
        {
            var releaseError = TryRelease(run);
            if (error is RLoopException known && releaseError is not null) throw WithReleaseFailure(known, run, releaseError);
            throw;
        }

        // 9. The deployer writes on its own connection. Whatever it answers is stored before it is judged.
        var execution = await ExecuteWithDeadlineAsync(run, cancellationToken);
        run.Execution = execution;
        var candidates = CandidateIds(execution);
        var updateError = TryUpdatePending(run, execution.Stage, execution.SendStatus, execution.RequestedRootSlotId, candidates,
            execution.Outcome.ToString());
        var provenUnsent = execution is { SendStatus: FluxDeploySendStatus.NotSentProven, Stage: FluxDeployStage.NotSent };
        switch (execution.Outcome)
        {
            case FluxDeployOutcome.CompileFailed or FluxDeployOutcome.ModuleNameMismatch or FluxDeployOutcome.NotSent when provenUnsent:
                throw NotSent(run, execution, TryRelease(run));
            case FluxDeployOutcome.RemoveFailed or FluxDeployOutcome.RemovedNotCreated or FluxDeployOutcome.PartialCreated:
                throw PendingKept(run, "FLUX_DEPLOY_PARTIAL",
                    $"The deployment of module '{moduleName}' stopped at stage '{Camel(execution.Stage)}' ({Camel(execution.Outcome)}). It is left pending and is not settled automatically.",
                    Camel(execution.Outcome), updateError);
            case FluxDeployOutcome.Created:
                break;
            default: // Unknown, or an answer that claims "not sent" without proving it.
                throw PendingKept(run, "FLUX_DEPLOY_UNVERIFIED",
                    $"The result of deploying module '{moduleName}' is unknown. It is left pending and is not settled automatically.",
                    Camel(execution.Outcome), updateError);
        }
        if (updateError is not null)
            throw PendingKept(run, updateError.Code,
                "The module was created, but the Flux deploy state could not be updated. The deployment stays pending; the state does not know the new root.",
                "stateUpdateFailed", updateError, updateError.ExitCode);

        // 10. Read back. Any doubt leaves the pending record in place.
        string bindingReadback;
        try
        {
            await VerifyAfterDeployAsync(run, baseline, cancellationToken);
            bindingReadback = await VerifyBindingsAfterDeployAsync(run, execution.NewRootSlotId!, cancellationToken);
        }
        catch (RLoopException error) when (error.Context.ContainsKey(GuardMarker))
        {
            TryUpdatePending(run, execution.Stage, execution.SendStatus, execution.RequestedRootSlotId, candidates, error.Code);
            throw;
        }
        catch (Exception error)
        {
            TryUpdatePending(run, execution.Stage, execution.SendStatus, execution.RequestedRootSlotId, candidates, "FLUX_READBACK_FAILED");
            throw PendingKept(run, "FLUX_READBACK_FAILED",
                $"The deployed module '{moduleName}' could not be read back: {error.Message}", "readFailed", error);
        }

        // 11. Settle: the record becomes the new root and the pending record goes away. If the state cannot be
        //     written, the pending record saved in step 7 is still in the file, so the next run stops.
        var record = new FluxDeployModuleRecord
        {
            ParentSlotId = request.ParentSlotId, RootSlotId = execution.NewRootSlotId, ModuleName = moduleName,
            InputHash = request.InputHash, SdkVersion = request.SdkVersion, DeployedAt = DateTimeOffset.UtcNow,
            Bindings = request.Bindings ?? BindingsFromMaps(request), Origin = FluxDeployOrigins.Deployed
        };
        try { FluxDeployStateStore.ResolvePending(statePath, pending.OperationId, record); }
        catch (Exception error) when (error is RLoopException or ArgumentException or InvalidOperationException)
        {
            throw PendingKept(run, (error as RLoopException)?.Code ?? "FLUX_STATE_WRITE_FAILED",
                "The module was created and read back, but the Flux deploy state could not be settled. The world has the new module; the state still records the previous root and the pending deployment.",
                "settleFailed", error, (error as RLoopException)?.ExitCode ?? ExitCodes.OperationFailed);
        }
        // P10: the lock keeps pointing at this Flux state after success.

        return new FluxDeployGuardResult(request.ModuleKey, statePath, pending.OperationId, moduleName, request.ParentSlotId,
            execution.NewRootSlotId!, execution.PreviousRootRemoval, execution.Stage, pending.Session,
            new FluxDeployGuardPreconditions(run.RecordBasis, run.PreviousRootSlotId,
                SameName(baseline, moduleName), baseline.Select(child => child.Id).ToArray(),
                run.Identity.Observation.IdentityStatus, run.WriterCheck)
                { WriterObservation = run.WriterObservation, BindingPorts = run.BindingPorts },
            bindingReadback, record, execution.Diagnostics, execution.Ports, execution.StandardOutput, execution.StandardError)
            { BindingEvidence = run.BindingEvidence, WriterObservations = execution.WriterObservations,
                ExpectedWriterIdentity = WriterIdentity(run.Identity) };
    }

    // The output-writer check (before the pending record) and the binding readback (before the state is settled)
    // are in FluxDeployGuard.Checks.cs: CheckWritersBeforeDeployAsync and VerifyBindingsAfterDeployAsync.

    // ---- Steps ---------------------------------------------------------------------------------------------

    /// <summary>The checked preparation: success, no error diagnostic, a declared name (and the expected one, when given).</summary>
    private async Task<FluxDeployPreparation> PrepareAsync(FluxDeployGuardRequest request, string statePath, CancellationToken ct)
    {
        FluxDeployPreparation preparation;
        try
        {
            preparation = await deployer.PrepareAsync(new(request.ProjectDirectory, request.Module, request.LibraryPath), ct)
                ?? throw new InvalidOperationException("The deployer returned no preparation.");
        }
        catch (Exception error) when (error is not (RLoopException or OperationCanceledException))
        {
            throw CompileFailed(request, statePath, error.Message, [], null, error);
        }
        var errors = preparation.Diagnostics.Where(IsError).ToArray();
        // P9: only error diagnostics fail; warnings are carried to the result.
        if (!preparation.Success || errors.Length > 0 || string.IsNullOrWhiteSpace(preparation.DeclaredModuleName))
            throw CompileFailed(request, statePath,
                preparation.Error ?? errors.FirstOrDefault()?.Message ?? "The module has no declared name.",
                preparation.Diagnostics, preparation);
        if (request.ExpectedModuleName is { } expected && !string.Equals(expected, preparation.DeclaredModuleName, StringComparison.Ordinal))
            throw new RLoopException("FLUX_MODULE_NAME_MISMATCH",
                $"Module '{request.Module}' declares the name '{preparation.DeclaredModuleName}', not the expected '{expected}'. Nothing was written.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?>
                {
                    [GuardMarker] = true, ["module"] = request.ModuleKey, ["modulePath"] = request.Module, ["stateFile"] = statePath,
                    ["expectedModuleName"] = expected, ["declaredModuleName"] = preparation.DeclaredModuleName,
                    ["worldWrites"] = "none", ["pending"] = "none"
                });
        return preparation;
    }

    private enum RecordChange { None, ConfirmMigrated, Forget }

    private async Task<RecordChange> ReconcileRecordAsync(Run run, FluxDeployState state, CancellationToken ct)
    {
        var request = run.Request;
        IReadOnlyList<Child> children;
        try { children = await ReadChildrenAsync(request.ParentSlotId, refuseRoot: true, ct); }
        catch (ReadFailure failure) when (failure.Reason == "parentIsRoot")
        { throw ParentRootRefused(request, run.StatePath, "parentIsRoot"); }
        catch (ReadFailure failure) { throw ParentUnreadable(run, failure); }
        run.ChildrenBefore = children;
        var sameName = SameName(children, run.ModuleName);

        var change = RecordChange.None;
        state.Modules.TryGetValue(request.ModuleKey, out var record);
        var sameEpoch = FluxDeployStateStore.HaveSameProvenEpoch(state.Session,
            FluxDeploySessionRecord.From(run.Identity.Observation));
        var recordedChild = record?.RootSlotId is { } rootId ? children.FirstOrDefault(child => child.Id == rootId) : null;
        if (record is null) run.RecordBasis = FluxDeployRecordBasis.None;
        else if (record.Origin == FluxDeployOrigins.MigratedV1)
        {
            // P6: a v1 ID was found by name. Believe it only while it is this parent's child with the declared name.
            if (record.ParentSlotId == request.ParentSlotId && recordedChild is not null && recordedChild.Name == run.ModuleName)
            {
                (run.RecordBasis, run.PreviousRootSlotId, change) =
                    (FluxDeployRecordBasis.MigratedConfirmed, recordedChild.Id, RecordChange.ConfirmMigrated);
            }
            else (run.RecordBasis, change) = (FluxDeployRecordBasis.MigratedUntrusted, RecordChange.Forget);
        }
        else if (!sameEpoch) run.RecordBasis = FluxDeployRecordBasis.RecordFromOtherSession; // Unproven epochs cannot establish ownership.
        else if (recordedChild is not null)
        {
            if (recordedChild.Name != run.ModuleName)
                throw RecordStale(run, record, "nameMismatch", request.ParentSlotId, recordedChild.Name);
            (run.RecordBasis, run.PreviousRootSlotId) = (FluxDeployRecordBasis.Deployed, recordedChild.Id);
        }
        else
        {
            // Not below this parent: either gone (no record) or somewhere else (stale).
            SlotInfo? elsewhere;
            try { elsewhere = await client.GetSlotAsync(record.RootSlotId!, 0, false, ct); }
            catch (RLoopException error) when (error.Code == "SLOT_NOT_FOUND") { elsewhere = null; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw ParentUnreadable(run, new ReadFailure("recordedRootUnreadable",
                    $"The recorded module root '{record.RootSlotId}' could not be read: {error.Message}", error));
            }
            if (elsewhere is not null)
                throw RecordStale(run, record, "parentMismatch", elsewhere.ParentId, elsewhere.IsReferenceOnly ? null : elsewhere.Name);
            run.RecordBasis = FluxDeployRecordBasis.RecordedRootGone;
        }

        // P2: the same-name children must be exactly the recorded previous module (none, or that one ID).
        var unrecorded = sameName.Where(id => id != run.PreviousRootSlotId).ToArray();
        if (unrecorded.Length > 0)
            throw new RLoopException("FLUX_MODULE_UNRECORDED_SIBLING",
                $"The parent '{request.ParentSlotId}' has {unrecorded.Length} child Slot(s) named '{run.ModuleName}' that this state did not deploy. Nothing was written.",
                ExitCodes.ValidationFailed, Context(run, "none", "none", new()
                {
                    ["candidateIds"] = unrecorded, ["sameNameChildIds"] = sameName, ["recordedRootSlotId"] = record?.RootSlotId,
                    ["recordBasis"] = run.RecordBasis
                }),
                ["Inspect each candidate by exact ID. Rename it, or delete it by exact ID with --yes, then deploy again. A module deployed through another state (manifest vs. single deploy) appears here too; existing Slots are never adopted by name."]);
        return change;
    }

    private FluxDeployPending SavePending(Run run, RecordChange change)
    {
        var request = run.Request;
        var pending = FluxDeployPending.Begin(request.ModuleKey, FluxDeploySessionRecord.From(run.Identity.Observation),
            request.ParentSlotId, run.ModuleName, run.PreviousRootSlotId, run.ChildrenBefore.Select(child => child.Id),
            request.InputHash, DateTimeOffset.UtcNow);
        try
        {
            run.Lock.RecordState(run.StatePath);
            if (change == RecordChange.ConfirmMigrated)
                FluxDeployStateStore.ConfirmMigratedModule(run.StatePath, request.ModuleKey, request.ParentSlotId, run.ModuleName);
            else if (change == RecordChange.Forget)
                FluxDeployStateStore.ForgetModule(run.StatePath, request.ModuleKey);
            FluxDeployStateStore.AddPending(run.StatePath, pending);
        }
        catch (RLoopException error) when (error.Code != "FLUX_DEPLOY_PENDING")
        {
            // The deployer was not called. If the record did reach the file, the next run stops on it.
            throw new RLoopException(error.Code, error.Message + " The deployer was not called; nothing was written to the world.",
                error.ExitCode, Merge(error.Context, Context(run, "none", "unknown", new()
                {
                    ["operationId"] = pending.OperationId, ["stage"] = FluxDeployStage.NotSent,
                    ["sendStatus"] = FluxDeploySendStatus.NotSentProven
                })),
                [.. error.Suggestions, $"If the state now holds pending operation {pending.OperationId}, nothing was sent for it: discard it with {Discard(pending.OperationId)}."],
                error);
        }
        run.Pending = pending;
        return pending;
    }

    private async Task<IReadOnlyList<Child>> RecheckBeforeDeployAsync(Run run, CancellationToken ct)
    {
        await ObserveIdentityAsync(run, run.Identity, pendingKept: false, ct);
        IReadOnlyList<Child> children;
        try { children = await ReadChildrenAsync(run.Request.ParentSlotId, refuseRoot: false, ct); }
        catch (ReadFailure failure) { throw ParentUnreadable(run, failure, pending: "released"); }
        var before = SameName(run.ChildrenBefore, run.ModuleName);
        var now = SameName(children, run.ModuleName);
        if (!before.ToHashSet(StringComparer.Ordinal).SetEquals(now))
            throw new RLoopException("FLUX_CHILDREN_CHANGED",
                $"The children of '{run.Request.ParentSlotId}' named '{run.ModuleName}' changed between the observation and the deployment. Nothing was written.",
                ExitCodes.ValidationFailed, Context(run, "none", "released", new()
                {
                    ["observedSameNameChildIds"] = before, ["currentSameNameChildIds"] = now,
                    ["stage"] = FluxDeployStage.NotSent, ["sendStatus"] = FluxDeploySendStatus.NotSentProven
                }));
        return children;
    }

    private async Task<FluxDeployExecution> ExecuteWithDeadlineAsync(Run run, CancellationToken ct)
    {
        var request = run.Request;
        var execute = new FluxDeployExecuteRequest(request.ProjectDirectory, request.Module, request.ParentSlotId, request.Url,
            request.LibraryPath, request.HelperPath, request.InputMap, request.OutputMap, run.ModuleName,
            run.PreviousRootSlotId, request.Deadline)
        {
            ExpectedPorts = run.PreparedPorts, RequireAllPortsBound = request.RequireAllPortsBound,
            ExpectedWriterIdentity = WriterIdentity(run.Identity)
        };
        // An exception, a cancellation or a deployer that never answers all mean the same: a message may have left.
        FluxDeployExecution Unknown(string error) => new(FluxDeployOutcome.Unknown, FluxDeployStage.Unknown,
            FluxDeploySendStatus.Unknown,
            run.PreviousRootSlotId is null ? FluxPreviousRootRemoval.NotAttempted : FluxPreviousRootRemoval.Unknown,
            null, null, null, null, [], [], null, error, string.Empty, string.Empty);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<FluxDeployExecution> work;
        try { work = deployer.ExecuteAsync(execute, cancellation.Token); }
        catch (Exception error) { return Unknown($"The deployer failed: {error.Message}"); }
        var limit = request.Deadline + DeadlineGrace;
        if (await Task.WhenAny(work, Task.Delay(limit, CancellationToken.None)) != work)
        {
            cancellation.Cancel();
            _ = work.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return Unknown($"The deployer did not answer within {limit.TotalSeconds:0.###} s (deadline plus grace).");
        }
        try { return await work ?? Unknown("The deployer returned no result."); }
        catch (Exception error) { return Unknown($"The deployer failed: {error.Message}"); }
    }

    /// <summary>
    /// Minimal readback (unit 3a): identity unchanged, the new root is one new child with the declared name, the
    /// recorded previous module is gone, and no unrelated child disappeared.
    /// </summary>
    private async Task VerifyAfterDeployAsync(Run run, IReadOnlyList<Child> baseline, CancellationToken ct)
    {
        var execution = run.Execution!;
        await ObserveIdentityAsync(run, run.Identity, pendingKept: true, ct);
        RLoopException Readback(string reason, string message, Dictionary<string, object?>? extra = null) =>
            PendingKept(run, "FLUX_READBACK_FAILED", message, reason, extra: extra);

        var newRoot = execution.NewRootSlotId;
        if (string.IsNullOrWhiteSpace(newRoot))
            throw Readback("newRootIdMissing", "The deployer answered Created without a new root ID.");
        if (!string.Equals(execution.DeclaredModuleName, run.ModuleName, StringComparison.Ordinal))
            throw Readback("declaredNameChanged", $"The deployer created module '{execution.DeclaredModuleName}', not '{run.ModuleName}'.");
        IReadOnlyList<Child> children;
        try { children = await ReadChildrenAsync(run.Request.ParentSlotId, refuseRoot: false, ct); }
        catch (ReadFailure failure) { throw Readback(failure.Reason, $"The parent could not be read after the deployment: {failure.Message}"); }

        var known = baseline.Concat(run.ChildrenBefore).Select(child => child.Id).ToHashSet(StringComparer.Ordinal);
        var created = children.FirstOrDefault(child => child.Id == newRoot);
        if (created is null)
            throw Readback("newRootMissing", $"The new module root '{newRoot}' is not a child of '{run.Request.ParentSlotId}'.");
        if (created.Name != run.ModuleName)
            throw Readback("newRootNameMismatch", $"The new module root '{newRoot}' is named '{created.Name}', not '{run.ModuleName}'.");
        if (known.Contains(newRoot))
            throw Readback("newRootNotNew", $"The reported new root '{newRoot}' already existed before the deployment.");
        if (run.PreviousRootSlotId is { } previous && children.Any(child => child.Id == previous))
            throw Readback("previousRootStillPresent", $"The previous module root '{previous}' is still a child of '{run.Request.ParentSlotId}'.");
        var sameName = SameName(children, run.ModuleName);
        if (sameName.Count != 1)
            throw Readback("newRootAmbiguous",
                $"{sameName.Count} children of '{run.Request.ParentSlotId}' are named '{run.ModuleName}'; the new module cannot be settled as one root.",
                new() { ["sameNameChildIds"] = sameName });

        var present = children.Select(child => child.Id).ToHashSet(StringComparer.Ordinal);
        var removed = baseline.Select(child => child.Id).Where(id => id != run.PreviousRootSlotId && !present.Contains(id)).ToArray();
        if (removed.Length > 0)
            throw PendingKept(run, "FLUX_UNEXPECTED_REMOVAL",
                $"{removed.Length} child Slot(s) of '{run.Request.ParentSlotId}' that are not the previous module disappeared during the deployment.",
                "unrelatedChildRemoved", extra: new() { ["removedIds"] = removed });
    }

    // ---- Observations --------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the parent's direct children with their names. The parent and every child must be full records:
    /// a reference-only entry carries an ID without a name, so it cannot be compared. A full Slot without listed
    /// children has none (a leaf's child list is null on the wire and empty in the mapped model).
    /// </summary>
    private async Task<IReadOnlyList<Child>> ReadChildrenAsync(string parentId, bool refuseRoot, CancellationToken ct)
    {
        SlotInfo parent;
        try { parent = await client.GetSlotAsync(parentId, 1, false, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new ReadFailure("parentReadFailed", error.Message, error); }
        if (parent is null || parent.IsReferenceOnly || !string.Equals(parent.Id, parentId, StringComparison.Ordinal))
            throw new ReadFailure("parentIncomplete", $"The read of '{parentId}' did not return that Slot in full.");
        if (refuseRoot && (IsRootId(parent.Id) || parent.ParentId is null))
            throw new ReadFailure("parentIsRoot", $"'{parentId}' is the Root Slot.");
        var children = new List<Child>();
        foreach (var child in parent.Children ?? [])
        {
            if (child is null || child.IsReferenceOnly || string.IsNullOrWhiteSpace(child.Id) ||
                !string.Equals(child.ParentId, parentId, StringComparison.Ordinal))
                throw new ReadFailure("childrenIncomplete", $"A child of '{parentId}' was not returned in full.");
            children.Add(new(child.Id, child.Name));
        }
        if (children.GroupBy(child => child.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ReadFailure("childrenIncomplete", $"The children of '{parentId}' repeat an ID.");
        return children;
    }

    /// <summary>
    /// URL, announced <c>S-</c> ID, versions and connection generation of the guard's connection. With a baseline,
    /// any difference is FLUX_SESSION_CHANGED. An <c>S-</c> ID that cannot be matched is recorded as
    /// <c>unknown</c> and does not stop the deployment (same as apply, F1); a change of it does.
    /// </summary>
    private async Task<Identity> ObserveIdentityAsync(Run run, Identity? baseline, bool pendingKept, CancellationToken ct)
    {
        SessionInfo session;
        try { session = await client.GetSessionInfoAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw SessionChanged(run, "sessionUnreadable", pendingKept, $"The session could not be read: {error.Message}", inner: error); }
        var observation = (client as IApplySessionObservation)?.ObserveApplySession() ?? ApplySessionObservation.Observe(session.Url);
        var now = new Identity(observation, session.ResoniteVersion, session.ResoniteLinkVersion, session.ConnectionGeneration);
        var reason = !session.Connected ? "disconnected"
            : !string.Equals(observation.NormalizedUrl, run.Lock.NormalizedUrl, StringComparison.Ordinal) ? "sessionUrlChanged"
            : baseline is null ? null
            : observation != baseline.Observation ? "sessionIdChanged"
            : now.ResoniteVersion != baseline.ResoniteVersion || now.LinkVersion != baseline.LinkVersion ? "versionChanged"
            : now.Generation != baseline.Generation ? "connectionChanged"
            : null;
        if (reason is not null)
            throw SessionChanged(run, reason, pendingKept, "The session the deployment was checked against is not the session observed now.",
                new() { ["lockedUrl"] = run.Lock.NormalizedUrl, ["expected"] = Describe(baseline), ["observed"] = Describe(now) });
        return now;
    }

    private static Dictionary<string, object?>? Describe(Identity? identity) => identity is null ? null : new()
    {
        ["normalizedUrl"] = identity.Observation.NormalizedUrl, ["discoverSessionId"] = identity.Observation.DiscoverSessionId,
        ["identityStatus"] = identity.Observation.IdentityStatus, ["resoniteVersion"] = identity.ResoniteVersion,
        ["resoniteLinkVersion"] = identity.LinkVersion, ["connectionGeneration"] = identity.Generation
    };

    private static FluxDeployWriterIdentity WriterIdentity(Identity identity) => new(identity.Observation.NormalizedUrl,
        identity.Observation.DiscoverSessionId, identity.Observation.IdentityStatus, identity.ResoniteVersion, identity.LinkVersion);

    // ---- State helpers -------------------------------------------------------------------------------------

    /// <summary>Removes the pending record of a deployment proven not to have sent anything. Returns the failure, if any.</summary>
    private static Exception? TryRelease(Run run)
    {
        if (run.Pending is null) return null;
        try { FluxDeployStateStore.ReleasePending(run.StatePath, run.Pending.OperationId, forgetModule: false); return null; }
        catch (Exception error) when (error is RLoopException or IOException or UnauthorizedAccessException) { return error; }
    }

    private static RLoopException? TryUpdatePending(Run run, FluxDeployStage stage, FluxDeploySendStatus sendStatus,
        string? requestedRootId, IReadOnlyList<string> candidates, string reason)
    {
        try
        {
            FluxDeployStateStore.UpdatePending(run.StatePath, run.Pending!.OperationId, stage, sendStatus, requestedRootId, candidates, reason);
            return null;
        }
        catch (RLoopException error) { return error; }
    }

    private static IReadOnlyList<FluxDeployBindingRecord> BindingsFromMaps(FluxDeployGuardRequest request) =>
    [
        .. (request.InputMap ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new FluxDeployBindingRecord(pair.Key, "source", pair.Value)),
        .. (request.OutputMap ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new FluxDeployBindingRecord(pair.Key, "drive", pair.Value))
    ];

    private static IReadOnlyList<string> CandidateIds(FluxDeployExecution execution) =>
        new[] { execution.RequestedRootSlotId, execution.NewRootSlotId }.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> SameName(IEnumerable<Child> children, string moduleName) =>
        children.Where(child => string.Equals(child.Name, moduleName, StringComparison.Ordinal)).Select(child => child.Id).ToArray();

    private static bool IsRootId(string id) => id.Trim().Equals("Root", StringComparison.OrdinalIgnoreCase);
    private static bool IsError(FluxDiagnostic diagnostic) => string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase);
    private static string Camel<TEnum>(TEnum value) where TEnum : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static string Discard(string operationId) => $"--discard-pending {operationId} --yes";

    // ---- Diagnostics ---------------------------------------------------------------------------------------

    // Marks exceptions built here, so the readback step can tell its own verdicts from read errors.
    private const string GuardMarker = "fluxDeployGuard";

    private static Dictionary<string, object?> Context(Run run, string worldWrites, string pending, Dictionary<string, object?>? extra = null)
    {
        var context = new Dictionary<string, object?>
        {
            [GuardMarker] = true, ["module"] = run.Request.ModuleKey, ["moduleName"] = run.ModuleName,
            ["parentSlotId"] = run.Request.ParentSlotId, ["stateFile"] = run.StatePath,
            ["worldWrites"] = worldWrites, ["pending"] = pending
        };
        if (run.Pending is { } saved) context["operationId"] = saved.OperationId;
        if (run.Identity is not null) context["expectedWriterIdentity"] = WriterIdentity(run.Identity);
        if (run.Execution is { } execution) context["writerObservations"] = execution.WriterObservations;
        if (extra is not null) foreach (var (key, value) in extra) context[key] = value;
        return context;
    }

    private static Dictionary<string, object?> Merge(IReadOnlyDictionary<string, object?> first, Dictionary<string, object?> second)
    {
        var merged = new Dictionary<string, object?>(first);
        foreach (var (key, value) in second) merged[key] = value;
        return merged;
    }

    private static RLoopException ParentRootRefused(FluxDeployGuardRequest request, string statePath, string reason) => new(
        "FLUX_PARENT_ROOT_REFUSED", "A ProtoGraph module is not deployed directly below Root. Name an exact parent Slot ID. Nothing was written.",
        ExitCodes.ValidationFailed, new Dictionary<string, object?>
        {
            [GuardMarker] = true, ["module"] = request.ModuleKey, ["parentSlotId"] = request.ParentSlotId, ["stateFile"] = statePath,
            ["reason"] = reason, ["worldWrites"] = "none", ["pending"] = "none"
        });

    private static RLoopException CompileFailed(FluxDeployGuardRequest request, string statePath, string detail,
        IReadOnlyList<FluxDiagnostic> diagnostics, FluxDeployPreparation? preparation, Exception? inner = null) => new(
        "FLUX_COMPILE_FAILED", $"Module '{request.Module}' did not compile: {detail} Nothing was written.",
        ExitCodes.ExternalToolFailed, new Dictionary<string, object?>
        {
            [GuardMarker] = true, ["module"] = request.ModuleKey, ["modulePath"] = request.Module, ["stateFile"] = statePath,
            ["diagnostics"] = diagnostics, ["errorCount"] = diagnostics.Count(IsError),
            ["standardOutput"] = preparation?.StandardOutput, ["standardError"] = preparation?.StandardError,
            ["worldWrites"] = "none", ["pending"] = "none"
        }, innerException: inner);

    private static RLoopException ParentUnreadable(Run run, ReadFailure failure, string pending = "none") => new(
        "FLUX_PARENT_UNREADABLE",
        $"The children of parent '{run.Request.ParentSlotId}' could not be read completely before the deployment: {failure.Message} Nothing was written.",
        ExitCodes.ValidationFailed, Context(run, "none", pending, new() { ["reason"] = failure.Reason }), innerException: failure.InnerException);

    private static RLoopException RecordStale(Run run, FluxDeployModuleRecord record, string reason, string? currentParentId, string? currentName) => new(
        "FLUX_MODULE_RECORD_STALE",
        $"The recorded root '{record.RootSlotId}' of module '{run.Request.ModuleKey}' still exists but is no longer the child of '{run.Request.ParentSlotId}' named '{run.ModuleName}'. Nothing was written.",
        ExitCodes.ValidationFailed, Context(run, "none", "none", new()
        {
            ["reason"] = reason, ["recordedRootSlotId"] = record.RootSlotId, ["recordedParentSlotId"] = record.ParentSlotId,
            ["recordedModuleName"] = record.ModuleName, ["currentParentSlotId"] = currentParentId, ["currentName"] = currentName
        }),
        ["Inspect the recorded root by exact ID. Move or rename it back, or delete it by exact ID with --yes; a root that no longer exists is treated as no record."]);

    private RLoopException SessionChanged(Run run, string reason, bool pendingKept, string message,
        Dictionary<string, object?>? extra = null, Exception? inner = null)
    {
        extra ??= new();
        extra["reason"] = reason;
        if (pendingKept) return PendingKept(run, "FLUX_SESSION_CHANGED", message, reason, inner, extra: extra);
        return new("FLUX_SESSION_CHANGED", message + " Nothing was written.", ExitCodes.ValidationFailed,
            Context(run, "none", run.Pending is null ? "none" : "released", extra), innerException: inner);
    }

    /// <summary>A deployment that proved it sent nothing: the pending record is released and the world is unchanged.</summary>
    private static RLoopException NotSent(Run run, FluxDeployExecution execution, Exception? releaseError)
    {
        var (code, exit) = execution.Outcome switch
        {
            FluxDeployOutcome.CompileFailed => ("FLUX_COMPILE_FAILED", ExitCodes.ExternalToolFailed),
            FluxDeployOutcome.ModuleNameMismatch => ("FLUX_MODULE_NAME_MISMATCH", ExitCodes.ValidationFailed),
            _ => ("FLUX_DEPLOY_NOT_SENT", ExitCodes.OperationFailed)
        };
        var error = new RLoopException(code,
            $"The deployer stopped before sending anything for module '{run.ModuleName}': {execution.Error ?? Camel(execution.Outcome)} Nothing was written.",
            exit, Context(run, "none", "released", new()
            {
                ["outcome"] = execution.Outcome, ["stage"] = execution.Stage, ["sendStatus"] = execution.SendStatus,
                ["declaredModuleName"] = execution.DeclaredModuleName, ["expectedModuleName"] = run.ModuleName,
                ["diagnostics"] = execution.Diagnostics, ["standardOutput"] = execution.StandardOutput,
                ["standardError"] = execution.StandardError
            }));
        return releaseError is null ? error : WithReleaseFailure(error, run, releaseError);
    }

    /// <summary>Nothing was sent, but the pending record could not be removed from the state.</summary>
    private static RLoopException WithReleaseFailure(RLoopException error, Run run, Exception releaseError) => new(
        error.Code, error.Message + " Its pending record could not be removed from the state.", error.ExitCode,
        Merge(error.Context, new() { ["pending"] = "kept", ["pendingReleaseError"] = releaseError.Message, ["discardPending"] = Discard(run.Pending!.OperationId) }),
        [.. error.Suggestions, $"Nothing was sent for operation {run.Pending.OperationId}: discard it with {Discard(run.Pending.OperationId)}."],
        error);

    /// <summary>
    /// A deployment whose result is not settled. The pending record stays; the next run of any writer on this URL
    /// stops until the user inspects the world and discards it. Candidates are reported, never adopted.
    /// </summary>
    private static RLoopException PendingKept(Run run, string code, string message, string reason, Exception? inner = null,
        int exitCode = ExitCodes.OperationFailed, Dictionary<string, object?>? extra = null)
    {
        var execution = run.Execution;
        var operationId = run.Pending!.OperationId;
        var context = Context(run, "possible", "kept", new()
        {
            ["reason"] = reason, ["discardPending"] = Discard(operationId),
            ["previousRootSlotId"] = run.PreviousRootSlotId,
            ["outcome"] = execution?.Outcome, ["stage"] = execution?.Stage ?? FluxDeployStage.Unknown,
            ["sendStatus"] = execution?.SendStatus ?? FluxDeploySendStatus.Unknown,
            ["previousRootRemoval"] = execution?.PreviousRootRemoval,
            ["requestedRootId"] = execution?.RequestedRootSlotId, ["newRootSlotId"] = execution?.NewRootSlotId,
            ["candidateRootIds"] = execution is null ? Array.Empty<string>() : CandidateIds(execution),
            ["batchFailures"] = execution?.BatchFailures, ["deployerError"] = execution?.Error
        });
        if (inner is not null) context["detail"] = inner.Message;
        if (extra is not null) foreach (var (key, value) in extra) context[key] = value;
        return new(code, message, exitCode, context,
            [$"Inspect the direct children of '{run.Request.ParentSlotId}' and each candidate root by exact ID. The previous module may already be removed and a whole or partial new module may exist. Delete unwanted Slots only by exact ID with --yes, then discard the pending record with {Discard(operationId)} (discarding does not write to the world). Until then every deployment through this state stops with FLUX_DEPLOY_PENDING, and no candidate is adopted by name."],
            inner);
    }
}

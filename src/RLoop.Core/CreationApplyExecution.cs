using System.Text.Json;

namespace RLoop.Core;

public sealed partial class CreationApplyService
{
    public async Task<CreationApplyResult> ApplyAsync(CreationDocument document, CreationApplyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        if (document is null) throw Invalid("Creation document is required.");

        var hash = document.ValidatePlanInput();
        var preflight = CreationRegeneratePreflight.Validate(document);
        if (preflight.Count > 0)
            throw new RLoopException("CREATION_DOCUMENT_INVALID", preflight[0].Message, ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["issues"] = preflight });
        var validation = await document.ValidateAsync(cancellationToken);
        if (!validation.Valid)
            throw new RLoopException("CREATION_DOCUMENT_INVALID", validation.Issues.FirstOrDefault()?.Message ?? "Creation document is invalid.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["issues"] = validation.Issues });
        if (options.ReplaceOrder != "delete-first")
            throw ModeMismatch($"--replace-order {options.ReplaceOrder} is not available in this version; use delete-first.");
        if (!options.Yes)
            throw new RLoopException("CONFIRMATION_REQUIRED", "Creation apply requires --yes after reviewing the plan.", ExitCodes.ValidationFailed);

        var source = options.GeneratedContentSource ?? GeneratedContentMetadata.SourceForVersion(
            typeof(CreationApplyService).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        var definition = GeneratedContentMetadata.AddToGeneratedRoots(document.Definition, source);
        var statePath = CreationStateStore.ResolvePath(document, options.StateFile);
        var state = CreationStateStore.Load(statePath, document.CreationId, options.RequireState);
        if (state?.Pending is { } initialPending)
            throw Pending(initialPending, statePath);
        using var sessionLease = await SessionWriteLock.AcquireAsync(client, statePath, cancellationToken)
            ?? throw CreationOwnershipVerifier.SessionUnproven("Creation apply requires a connected ResoniteLink session.");
        using var writerLease = CheckpointFiles.AcquireWriter(statePath);
        state = CreationStateStore.Load(statePath, document.CreationId, options.RequireState);
        if (state?.Pending is { } lockedPending)
            throw Pending(lockedPending, statePath);

        var verifier = new CreationOwnershipVerifier(client);
        var context = verifier.CreateContext(cancellationToken);
        var sessionProof = await verifier.VerifySessionAsync(state?.Session, context, expectedSession: null);
        if (!string.Equals(sessionLease.NormalizedUrl, sessionProof.NormalizedUrl, StringComparison.Ordinal))
            throw CreationOwnershipVerifier.SessionUnproven("The connected session URL changed after its write lease was acquired.");
        var connectionGeneration = sessionProof.ConnectionGeneration;
        if (options.RequestedSessionId is { } requested && requested != sessionProof.DiscoverSessionId)
            throw CreationOwnershipVerifier.SessionUnproven("The requested --session does not match the connected discovered session.");
        if (client is not IApplyConnectionGuard || connectionGeneration is null ||
            client is not IApplyDeletionObservation)
            throw CreationOwnershipVerifier.SessionUnproven("Creation apply requires connection-generation and exact deletion-observation support.");

        var recorded = state?.ActiveRoot;
        var previousAbsent = recorded is not null &&
            await CreationRootAbsence.IsExactAbsentAsync(client, verifier, state!.Session, sessionProof, recorded.Id, cancellationToken);
        var previous = previousAbsent ? null : recorded;
        IReadOnlyList<object?> diagnostics = previousAbsent ? [CreationRootAbsence.Diagnostic(recorded!.Id)] : [];
        CreationPlanEvidence evidence;
        string? previousRootId = null;
        string parentId;
        if (previous is null)
        {
            var selector = options.Parent ?? (previousAbsent ? recorded!.ParentId : definition.Slot?.Parent ?? "Root");
            parentId = await ResolveParentAsync(selector, context);
            await verifier.ObserveInitialParentAsync(parentId, document.CreationId, context);
            await VerifyAncestorBoundaryAsync(parentId, document.CreationId, context);
            evidence = new("complete", "complete", "unknown", "complete", "unknown", "complete", "unknown");
        }
        else
        {
            if (options.Parent is not null)
            {
                var requestedParent = await ResolveParentAsync(options.Parent, context);
                if (requestedParent != previous.ParentId)
                    throw CreationOwnershipVerifier.OwnerUnproven("The requested parent differs from the recorded activeRoot parent.");
            }
            await verifier.VerifyAsync(state!, context);
            await verifier.VerifySessionAsync(state!.Session, context, sessionProof);
            parentId = previous.ParentId;
            previousRootId = previous.Id;
            evidence = new("complete", "complete", "complete", "complete", "complete", "complete", "complete");
        }
        var prepared = await new CreationTreeExecutor(client).PrepareAsync(definition, cancellationToken);
        var root = prepared.Definition.Slot ?? throw Invalid("Creation tree root Slot is missing.");
        await verifier.VerifySessionAsync(state?.Session, context, sessionProof);
        var counts = CountDefinitions(prepared.Definition, root);
        var ownerNonce = state?.OwnerNonce ?? CreationStamp.CreateOwnerNonce();
        var stamp = CreationStamp.Create(document.CreationId, ownerNonce);
        var stateSession = new CreationSession(sessionProof.NormalizedUrl, sessionProof.DiscoverSessionId, "matched");
        using var evidenceDoc = JsonDocument.Parse("{}");
        var pending = new CreationPending(Guid.NewGuid().ToString("N"), "prepared",
            new CreationScope("creation", null, previous?.Id, null, parentId), "delete-first", stateSession,
            sessionProof.ConnectionGeneration, null, hash, null, previous, new CreationPendingRoot(null,
                parentId, stamp.GenerationId, stamp.Serialize(), hash, null), [], evidenceDoc.RootElement.Clone());
        var workingState = (state ?? new CreationState("creation", 4, document.CreationId, stateSession, ownerNonce)) with
        {
            Session = stateSession,
            OwnerNonce = ownerNonce,
            Pending = pending
        };
        var transaction = new CreationTransaction(client, statePath, workingState, connectionGeneration, cancellationToken);
        sessionLease.RecordState(statePath);
        transaction.Save();

        try
        {
        if (previous is not null)
        {
            if (client is not IApplyDeletionObservation observer)
                throw CreationOwnershipVerifier.ObservationIncomplete("Exact deletion observation is unavailable.");
            await transaction.SendAsync("deletingPrevious", "delete-slot", previous.Id, previous.ParentId, null,
                new { targetId = previous.Id }, async () => { await client.DeleteSlotAsync(previous.Id, cancellationToken); return previous.Id; },
                async id =>
                {
                    _ = await observer.ObserveDeletionSlotAsync(id, cancellationToken);
                    return false;
                }, absenceIsSuccess: true,
                unknownReadback: async () => { _ = await observer.ObserveDeletionSlotAsync(previous.Id, cancellationToken); });
        }

        transaction.SetStage("creatingRoot");
        string? candidateId = null;
        var rootRequest = new SlotCreateRequest(parentId, root.Name,
            root.Position is null ? null : new Vector3Value(root.Position[0], root.Position[1], root.Position[2]),
            root.Rotation is null ? null : new QuaternionValue(root.Rotation[0], root.Rotation[1], root.Rotation[2], root.Rotation[3]),
            root.Scale is null ? null : new Vector3Value(root.Scale[0], root.Scale[1], root.Scale[2]), Tag: root.Tag);
        candidateId = await transaction.SendAsync("creatingRoot", "create-root", null, parentId, "Slot",
            new { parentId, generationId = stamp.GenerationId, stamp = stamp.Serialize() },
            () => client.CreateSlotAsync(rootRequest, cancellationToken),
            async id =>
            {
                var observed = await ObserveExactAsync(id, cancellationToken);
                return id != "Root" && observed.ParentId == parentId && observed.Name == root.Name && SlotMatches(observed, root);
            }, id =>
            {
                if (string.IsNullOrWhiteSpace(id) || id.Equals("Root", StringComparison.OrdinalIgnoreCase))
                    throw Unverified("Root creation returned an unsafe exact ID.");
                candidateId = id;
                var current = transaction.State;
                transaction.ReplacePending(current.Pending! with
                {
                    CandidateRoot = new CreationPendingRoot(id, parentId, stamp.GenerationId, stamp.Serialize(), hash, null),
                    Scope = current.Pending.Scope with { CandidateObjectId = id }
                });
                transaction.SetLastTargetId(id);
            });

        transaction.SetStage("markingRoot");
        var stampFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VariableName"] = CreationOwnershipVerifier.VariableName,
            ["Value"] = stamp.Serialize()
        };
        await transaction.SendAsync("markingRoot", "add-stamp", candidateId, null,
            CreationOwnershipVerifier.VariableComponentType,
            new { targetId = candidateId, type = CreationOwnershipVerifier.VariableComponentType, generationId = stamp.GenerationId },
            () => client.AddComponentAsync(candidateId!, CreationOwnershipVerifier.VariableComponentType, stampFields, cancellationToken),
            async added =>
            {
                var component = await client.GetComponentAsync(added.Id, cancellationToken);
                if (added.Type != CreationOwnershipVerifier.VariableComponentType || component.Id != added.Id ||
                    component.Type != CreationOwnershipVerifier.VariableComponentType ||
                    !MemberMatches(component, "VariableName", CreationOwnershipVerifier.VariableName) ||
                    !MemberMatches(component, "Value", stamp.Serialize())) return false;
                var candidate = await ObserveExactAsync(candidateId!, cancellationToken);
                if (candidate.Components is null) return false;
                var markerContext = verifier.CreateContext(cancellationToken);
                await verifier.VerifySessionAsync(stateSession, markerContext, sessionProof);
                var markers = await CreationOwnershipVerifier.ReadMarkersAsync(candidate, markerContext);
                await verifier.VerifySessionAsync(stateSession, markerContext, sessionProof);
                return candidate.Id == candidateId && candidate.ParentId == parentId &&
                    candidate.Components.Count(value => value.Id == added.Id) == 1 && markers.Count == 1 &&
                    markers[0].Raw == stamp.Serialize() && markers[0].Stamp == stamp;
            });

        transaction.SetStage("creatingTree");
        try
        {
            var treeDiagnostics = await new CreationTreeExecutor(client).ExecuteAsync(prepared, candidateId!,
                send => transaction.SendTreeAsync(send, cancellationToken), cancellationToken);
            diagnostics = [.. diagnostics, .. treeDiagnostics];
        }
        catch
        {
            transaction.SaveTreeEvidenceAfterFailure();
            throw;
        }
        var treeEvidence = JsonSerializer.SerializeToElement(new
        {
            treeComplete = true,
            expectedCounts = new { slots = counts.Slots, components = counts.Components }
        });
        transaction.ReplacePending(transaction.State.Pending! with { Stage = "staged", Evidence = treeEvidence });
        transaction.Save();
        var engineAdded = await CreationTreeVerification.VerifyAsync(client, candidateId!, counts, cancellationToken);

        var active = new CreationActiveRoot(candidateId!, parentId, stamp.GenerationId, stamp.Serialize(), hash, null);
        var finalState = transaction.State with { ActiveRoot = active, Pending = null };
        var finalContext = verifier.CreateContext(cancellationToken);
        await verifier.VerifySessionAsync(stateSession, finalContext, sessionProof);
        await verifier.VerifyAsync(finalState, finalContext);
        await verifier.VerifySessionAsync(stateSession, finalContext, sessionProof);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.SaveFinal(finalState);
        return new CreationApplyResult("1", "regenerate", document.CreationId, statePath, null, hash,
            previousRootId, candidateId, previous is not null, true, "delete-first", "staged", counts, null,
            new CreationPlanEvidence("complete", "complete", "complete", "complete", "complete", "complete", "complete"), diagnostics, false, engineAdded);
        }
        catch (RLoopException ex) { throw transaction.Contextualize(ex); }
        catch (OperationCanceledException ex)
        {
            throw transaction.Contextualize(new RLoopException("APPLY_CANCELLED",
                "Creation apply was cancelled; pending evidence was retained.", ExitCodes.OperationFailed, innerException: ex));
        }
        catch (TimeoutException ex)
        {
            throw transaction.Contextualize(new RLoopException("REQUEST_TIMEOUT",
                "Creation apply timed out; pending evidence was retained.", ExitCodes.Timeout, innerException: ex));
        }

        async Task<SlotInfo> ObserveExactAsync(string id, CancellationToken ct)
        {
            if (client is not IApplyDeletionObservation exactObserver)
                throw CreationOwnershipVerifier.ObservationIncomplete("Exact Slot readback is unavailable.");
            var observed = await exactObserver.ObserveDeletionSlotAsync(id, ct);
            if (observed.Slot is null || observed.Slot.Id != id || !observed.ChildrenObserved ||
                !observed.ComponentsObserved || observed.Slot.IsReferenceOnly)
                throw CreationOwnershipVerifier.ObservationIncomplete("Root readback was not complete for its exact ID.");
            return observed.Slot;
        }
    }

    private static bool SlotMatches(SlotInfo observed, ApplySlotSpec expected) => CreationTreeExecutor.SlotValuesMatch(observed, expected);

    private static bool MemberMatches(ComponentInfo component, string name, string expected) =>
        component.Members.TryGetValue(name, out var member) && WorldService.MemberMatchesRaw(member, expected);

    private static RLoopException Invalid(string message) => new("CREATION_DOCUMENT_INVALID", message, ExitCodes.ValidationFailed);
    private static RLoopException Unverified(string message) => new("APPLY_WRITE_UNVERIFIED", message, ExitCodes.OperationFailed);
    private static RLoopException Pending(CreationPending pending, string statePath) => new("APPLY_WRITE_UNVERIFIED",
        "Creation state contains a pending operation that must be reconciled before applying.", ExitCodes.OperationFailed,
        new Dictionary<string, object?>
        {
            ["stateFile"] = statePath, ["operationId"] = pending.OperationId, ["stage"] = pending.Stage,
            ["previousRootId"] = pending.PreviousRoot?.Id, ["candidateRootId"] = pending.CandidateRoot?.Id,
            ["pending"] = pending, ["completeness"] = "unknown"
        },
        [$"Resolve the retained creation state with: resoloop creation recover --state \"{statePath}\" --action rollback|finish --url auto --session {pending.Session.DiscoverSessionId ?? "S-ID"} --yes"]);
}

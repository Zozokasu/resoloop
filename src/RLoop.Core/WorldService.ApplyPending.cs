using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed partial class WorldService
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static JsonNode? EvidenceNode(object value) => JsonSerializer.SerializeToNode(value, EvidenceJson);
    private ApplyPendingWrite Pending(PreparedApply prepared, string kind, string key, string? id = null,
        string? parentId = null, string? type = null)
    {
        if (parentId is null && id is not null)
            parentId = prepared.Safety!.ObservedSlot(id)?.ParentId ??
                prepared.SnapshotSlots.FirstOrDefault(s => s.Components.Any(c => c.Id == id))?.Id;
        var pending = new ApplyPendingWrite
        {
            Kind = kind, Key = key, Id = id, ParentId = parentId,
            Type = type ?? (kind == "deleteSlot" ? "Slot" : prepared.SnapshotSlots.SelectMany(s => s.Components).FirstOrDefault(c => c.Id == id)?.Type),
            OwnershipKey = prepared.State.OwnershipKey,
            Session = (client as IApplySessionObservation)?.ObserveApplySession() ?? ApplySessionObservation.Observe(prepared.Session.Url),
            ConnectionGeneration = prepared.Session.ConnectionGeneration,
            BuildId = prepared.Document.BuildBundle?.BuildId,
            BuildIrHash = prepared.Document.BuildBundle is { } bundle ? Hash(bundle.Ir) : null,
            CatalogHash = prepared.Document.BuildBundle is { } catalogBundle ? Hash(JsonSerializer.Serialize(catalogBundle.Catalog)) : null,
            InputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(prepared.Document)))),
            Precondition = prepared.Safety!.Expected(id),
            Completeness = new() { ["identity"] = "unknown", ["writer"] = "partial", ["writerOutsideObservation"] = "unknown", ["readback"] = "unknown" },
        };
        pending.Completeness["identity"] = pending.Session.IdentityStatus;
        // Preserve exact ownership observations, never recover by name or ordinal.
        var cursor = parentId;
        var visited = new HashSet<string>();
        while (cursor is not null && cursor != "Root" && visited.Add(cursor))
        {
            var slot = prepared.Safety!.ObservedSlot(cursor) ?? prepared.SnapshotSlots.FirstOrDefault(s => s.Id == cursor);
            if (slot is null) break;
            pending.OwnershipSlots[cursor] = new(slot.Id, slot.Name, slot.ParentId);
            cursor = slot.ParentId;
        }
        return pending;
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task ExecutePendingAsync(PreparedApply prepared, ApplyPendingWrite pending,
        Func<Task> check, Func<Task> send, CancellationToken ct)
    {
        prepared.SessionWriter?.RecordState(prepared.StatePath);
        var intent = ApplyStateStore.Copy(prepared.State);
        intent.Pending.Add(pending);
        try { ApplyStateStore.Save(prepared.StatePath, intent); }
        catch (RLoopException error)
        {
            pending.SendStatus = "notSentProven";
            throw WithPendingEvidence(error, prepared.StatePath, pending);
        }
        prepared.State = intent;
        var enteredSend = false;
        var boundary = client as IApplySendEvidence;
        try
        {
            await check();
            ct.ThrowIfCancellationRequested();
            boundary?.BeginApplySend();
            enteredSend = true;
            await send();
            pending.ResponseReceived = true;
            pending.ResponseAccepted = true;
            pending.SendStatus = "responseReceived";
            // Store response evidence before readback, including the exact returned ID/type.
            ApplyStateStore.Save(prepared.StatePath, prepared.State);
            var readback = await ReadbackPendingAsync(pending, ct);
            readback = readback with { ReferenceTargets = PendingReferenceTargets(pending,
                selector => ResolveCheckpointReferenceId(prepared, selector)) };
            await prepared.Safety!.CheckConnectionAsync(pending.Key, ct);
            CommitReadback(prepared.StatePath, prepared.State, pending, readback);
            if (!readback.Complete) throw WriteUnverified(prepared.StatePath, pending, "readbackMismatch");
            prepared.Safety!.AcceptReadback(readback.Slot, readback.Component, pending.Members.Keys, pending.SlotValues);
        }
        catch (Exception ex)
        {
            if (!pending.ResponseReceived && boundary?.ApplyResponseReceived == true)
            {
                pending.ResponseReceived = true;
                pending.ResponseAccepted = boundary.ApplyResponseAccepted;
                pending.SendStatus = "responseReceived";
                try { ApplyStateStore.Save(prepared.StatePath, prepared.State); }
                catch (RLoopException saveError) { throw WithPendingEvidence(saveError, prepared.StatePath, pending); }
            }
            if (pending.ResponseReceived && !pending.ResponseAccepted)
            {
                // A negative response settles the request, but an update may have applied partially.
                // Read once for diagnosis only; never commit its candidate correspondence here.
                if (pending.Kind is "setMembers" or "updateSlot")
                {
                    try { await ReadbackPendingAsync(pending, ct); }
                    catch (Exception) { pending.Completeness["readback"] = "unknown"; }
                }
                var cleared = ApplyStateStore.Copy(prepared.State);
                cleared.Pending.RemoveAll(p => p.OperationId == pending.OperationId);
                try { ApplyStateStore.Save(prepared.StatePath, cleared); }
                catch (RLoopException saveError) { throw WithPendingEvidence(saveError, prepared.StatePath, pending); }
                prepared.State = cleared;
            }
            if (!enteredSend || boundary is not null && !boundary.ApplySendStarted && !pending.ResponseReceived)
            {
                // This process proved that the client mutation entry point was never reached.
                pending.SendStatus = "notSentProven";
                var cleared = ApplyStateStore.Copy(prepared.State);
                cleared.Pending.RemoveAll(p => p.OperationId == pending.OperationId);
                try { ApplyStateStore.Save(prepared.StatePath, cleared); }
                catch (RLoopException saveError) { throw WithPendingEvidence(saveError, prepared.StatePath, pending); }
                prepared.State = cleared;
            }
            if (ex is RLoopException error)
                throw WithPendingEvidence(error, prepared.StatePath, pending);
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw WithPendingEvidence(new RLoopException("APPLY_CANCELLED", "Apply was cancelled; inspect confirmed results and pending evidence before continuing.",
                    ExitCodes.OperationFailed, innerException: ex), prepared.StatePath, pending);
            if (ex is TimeoutException)
                throw WithPendingEvidence(new RLoopException("REQUEST_TIMEOUT", "The write or readback timed out; its result remains pending.",
                    ExitCodes.Timeout, innerException: ex), prepared.StatePath, pending);
            throw WriteUnverified(prepared.StatePath, pending, pending.ResponseReceived ? "readbackIncomplete" : "responseLost", ex);
        }
    }

    private sealed record PendingReadback(bool Complete, bool TargetConfirmed, SlotInfo? Slot = null, ComponentInfo? Component = null,
        IReadOnlyDictionary<string, string>? ReferenceTargets = null);

    private static IReadOnlyDictionary<string, string> PendingReferenceTargets(ApplyPendingWrite p, Func<string, string?> resolve)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in p.ComponentBinding?.ReferenceSelectors ?? new Dictionary<string, string>())
            if (!p.Members.ContainsKey(reference.Key) && resolve(reference.Value) is { } target)
                targets[reference.Key] = target;
        return targets;
    }

    private async Task<PendingReadback> VerifyResumedReferenceTargetsAsync(string path, ApplyState state,
        ApplyPendingWrite p, PendingReadback readback, CancellationToken ct)
    {
        if (!readback.TargetConfirmed || readback.Component is null) return readback;
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in p.ComponentBinding?.ReferenceSelectors ?? new Dictionary<string, string>())
        {
            if (p.Members.ContainsKey(reference.Key) ||
                state.Components.GetValueOrDefault(p.Key)?.ReferenceSelectors?.GetValueOrDefault(reference.Key) != reference.Value) continue;
            // Old journals may contain declaration-only selectors. An unchanged precondition target is
            // not proof of that selector: resolve it through verified current ownership before committing.
            // This member was never sent, so it cannot keep a pending write with a known result (F9): a selector
            // that no longer resolves or no longer matches is left out and CommitReadback drops it. The exact
            // recorded ID stays the owner; nothing is replayed or written.
            string targetId;
            try
            {
                targetId = (await ResolveStableReferenceCoreAsync(path, reference.Value, null,
                    new HashSet<string>(StringComparer.Ordinal), ct)).Id;
            }
            catch (RLoopException e) when (ReferenceSelectorUnresolved(e)) { continue; }
            if (readback.Component.Members.TryGetValue(reference.Key, out var actual) &&
                actual.Kind == "reference" && actual.TargetId == targetId)
                targets[reference.Key] = targetId;
        }
        return readback with { ReferenceTargets = targets };
    }

    // Only outcomes proving that the selector does not identify one verified target right now. Cancellation,
    // timeout, transport, connection and state-file failures are not listed and still stop reconciliation.
    private static bool ReferenceSelectorUnresolved(RLoopException e) => e.Code switch
    {
        // A stored Slot ID whose read failed is a transport result, not evidence about the selector.
        "APPLY_STORED_ID_UNVERIFIED" => e.Context.GetValueOrDefault("reason") as string != "storedIdReadFailed",
        "STABLE_SELECTOR_INVALID" or "STABLE_SLOT_NOT_FOUND" or "STABLE_COMPONENT_NOT_FOUND" or "STABLE_COMPONENT_AMBIGUOUS" or
        "STABLE_RELOCATABLE_EVIDENCE_MISSING" or "STABLE_RELOCATABLE_SLOT_NOT_FOUND" or "STABLE_RELOCATABLE_SLOT_AMBIGUOUS" or
        "FLUX_BINDING_COMPONENT_NOT_FOUND" or "FLUX_BINDING_MEMBER_NOT_FOUND" or "APPLY_MEMBER_REFERENCE_NOT_FOUND" or
        "SLOT_NOT_FOUND" or "SLOT_AMBIGUOUS" or "SLOT_PATH_NOT_FOUND" or "SLOT_PATH_AMBIGUOUS" or "COMPONENT_NOT_FOUND" => true,
        _ => false,
    };

    private async Task<PendingReadback> ReadbackPendingAsync(ApplyPendingWrite p, CancellationToken ct, bool resuming = false)
    {
        p.Confirmed.Clear();
        p.Completeness["readback"] = "unknown";
        if (p.Kind == "importAsset")
        {
            // Import has no world target/member read API. Only the completed import response supplies its URL.
            var complete = p.ResponseReceived && p.ResponseAccepted && p.AssetBinding is not null;
            p.Completeness["readback"] = "notApplicable";
            return new(complete, complete);
        }
        if (string.IsNullOrWhiteSpace(p.Id)) return new(false, false);
        if (p.Kind is "deleteSlot" or "removeComponent")
        {
            try
            {
                if (p.Kind == "deleteSlot")
                {
                    var remaining = await client.GetSlotAsync(p.Id, 0, false, ct);
                    p.Observed = EvidenceNode(remaining);
                    p.Completeness["readback"] = "complete";
                    return new(false, resuming && remaining.Id == p.Id && !remaining.IsReferenceOnly && remaining.ParentId == p.ParentId &&
                        (p.Type is null or "Slot"));
                }
                var remainingComponent = await client.GetComponentAsync(p.Id, ct);
                p.Observed = EvidenceNode(remainingComponent);
                var target = false;
                if (resuming && p.ParentId is not null)
                {
                    var parent = await client.GetSlotAsync(p.ParentId, 0, false, ct);
                    var matches = parent.Components.Where(c => c.Id == p.Id).ToArray();
                    target = parent.Id == p.ParentId && !parent.IsReferenceOnly && remainingComponent.Id == p.Id &&
                        TypeNamesEquivalent(remainingComponent.Type, p.Type ?? p.Precondition?["type"]?.GetValue<string>() ?? "") &&
                        matches.Length == 1 && TypeNamesEquivalent(matches[0].Type, remainingComponent.Type);
                }
                p.Completeness["readback"] = "complete";
                return new(false, target);
            }
            catch (RLoopException e) when (e.Code == (p.Kind == "deleteSlot" ? "SLOT_NOT_FOUND" : "COMPONENT_NOT_FOUND"))
            {
                p.Observed = JsonValue.Create("absent");
                p.Confirmed.Add("absence");
                p.Completeness["readback"] = "complete";
                return new(true, true);
            }
        }
        if (p.Kind is "createSlot" or "updateSlot")
        {
            var slot = await client.GetSlotAsync(p.Id, 0, false, ct);
            var values = p.SlotValues!;
            var attributes = new Dictionary<string, object?>();
            if (values.Name is not null) attributes["name"] = slot.Name;
            if (values.ParentId is not null) attributes["parent"] = slot.ParentId;
            if (values.Position is not null) attributes["position"] = slot.Position;
            if (values.Rotation is not null) attributes["rotation"] = slot.Rotation;
            if (values.Scale is not null) attributes["scale"] = slot.Scale;
            p.Observed = EvidenceNode(new { slot.Id, type = "Slot", slot.ParentId, attributes });
            var target = slot.Id == p.Id && !slot.IsReferenceOnly && p.Type == "Slot";
            bool Check(string name, bool same) { if (same && target) p.Confirmed.Add(name); return same; }
            var complete = target;
            if (values.Name is not null) complete &= Check("name", slot.Name == values.Name);
            if (values.ParentId is not null) complete &= Check("parent", slot.ParentId == values.ParentId);
            if (values.Position is not null) complete &= Check("position", VectorEquals(slot.Position, [values.Position.X, values.Position.Y, values.Position.Z]));
            if (values.Rotation is not null) complete &= Check("rotation", QuaternionEquals(slot.Rotation, [values.Rotation.X, values.Rotation.Y, values.Rotation.Z, values.Rotation.W]));
            if (values.Scale is not null) complete &= Check("scale", VectorEquals(slot.Scale, [values.Scale.X, values.Scale.Y, values.Scale.Z]));
            if (resuming) target &= p.ParentId is not null && slot.ParentId == p.ParentId;
            p.Completeness["readback"] = "complete";
            var bindingConfirmed = target && (values.Name is null || p.Confirmed.Contains("name")) &&
                (values.ParentId is null || p.Confirmed.Contains("parent"));
            return new(complete && target, resuming ? target : bindingConfirmed, slot);
        }
        var component = await client.GetComponentAsync(p.Id, ct);
        var parentConfirmed = true;
        var checkParent = p.Kind == "addComponent" || resuming;
        if (checkParent)
        {
            // Membership is structural evidence; do not request other Components' member data.
            var parent = await client.GetSlotAsync(p.ParentId!, 0, false, ct);
            var matches = parent.Components.Where(c => c.Id == p.Id).ToArray();
            parentConfirmed = parent.Id == p.ParentId && !parent.IsReferenceOnly && matches.Length == 1 && TypeNamesEquivalent(matches[0].Type, component.Type);
        }
        p.Completeness["parent"] = checkParent ? parentConfirmed ? "complete" : "unknown" : "notChecked";
        p.Observed = EvidenceNode(new { component.Id, component.Type,
            parentId = checkParent && parentConfirmed ? p.ParentId : null,
            parentConfirmed = checkParent ? (bool?)parentConfirmed : null,
            members = component.Members.Where(m => p.Members.ContainsKey(m.Key)).ToDictionary() });
        var targetConfirmed = parentConfirmed && component.Id == p.Id && TypeNamesEquivalent(component.Type, p.Type!) &&
            (p.Kind != "addComponent" || TypeNamesEquivalent(component.Type, p.ComponentBinding!.Type));
        foreach (var member in p.Members)
            if (targetConfirmed && component.Members.TryGetValue(member.Key, out var actual) &&
                MemberMatchesRaw(actual, member.Value))
                p.Confirmed.Add(member.Key);
        p.Completeness["readback"] = p.Members.Keys.All(component.Members.ContainsKey) ? "complete" : "partial";
        return new(targetConfirmed && p.Confirmed.Count == p.Members.Count, targetConfirmed, Component: component);
    }

    private static void CommitReadback(string path, ApplyState state, ApplyPendingWrite p, PendingReadback readback, bool resolvedNotApplied = false)
    {
        var next = ApplyStateStore.Copy(state);
        if (readback.TargetConfirmed && p.Kind is not ("deleteSlot" or "removeComponent"))
        {
            if (p.SlotBinding is not null)
            {
                var binding = p.SlotBinding with { Id = p.Id! };
                if (resolvedNotApplied && readback.Slot is { } observedSlot)
                {
                    var segments = (binding.PathSegments ?? SlotPaths.LegacySegments(binding.Path)).SkipLast(1).Append(observedSlot.Name).ToArray();
                    binding = binding with { Path = string.Join('/', segments), PathSegments = segments };
                }
                next.Slots[p.Key] = binding;
            }
            if (p.ComponentBinding is not null)
            {
                var binding = p.ComponentBinding with { Id = p.Id!, Type = p.Type! };
                // Only readback-confirmed identity fields enter the confirmed correspondence.
                if (binding.IdentityValues is not null)
                    binding = binding with { IdentityValues = binding.IdentityValues.Where(v => !p.Members.ContainsKey(v.Key) || p.Confirmed.Contains(v.Key))
                        .ToDictionary(v => v.Key, v => readback.Component!.Members.TryGetValue(v.Key, out var m) ? MemberRaw(m) : v.Value) };
                if (binding.ReferenceSelectors is not null)
                    binding = binding with { ReferenceSelectors = binding.ReferenceSelectors.Where(v =>
                        readback.Component!.Members.TryGetValue(v.Key, out var actual) && actual.Kind == "reference" &&
                        (p.Members.TryGetValue(v.Key, out var sent)
                            ? p.Confirmed.Contains(v.Key) && actual.TargetId == sent
                            : state.Components.GetValueOrDefault(p.Key)?.ReferenceSelectors?.GetValueOrDefault(v.Key) == v.Value &&
                              readback.ReferenceTargets?.TryGetValue(v.Key, out var target) == true && actual.TargetId == target &&
                              p.Precondition?["members"]?[v.Key]?["kind"]?.GetValue<string>() == "reference" &&
                              p.Precondition?["members"]?[v.Key]?["targetId"]?.GetValue<string>() == actual.TargetId)).ToDictionary() };
                next.Components[p.Key] = binding;
            }
            if (p.AssetBinding is not null) next.Assets[p.Key] = p.AssetBinding;
        }
        if (readback.Complete)
        {
            foreach (var key in p.RemoveSlots) next.Slots.Remove(key);
            foreach (var key in p.RemoveComponents) next.Components.Remove(key);
            next.Pending.RemoveAll(item => item.OperationId == p.OperationId);
        }
        else if (resolvedNotApplied) next.Pending.RemoveAll(item => item.OperationId == p.OperationId);
        ApplyStateStore.Save(path, next);
        // The confirmed in-memory snapshot changes only after persistence succeeded.
        state.SchemaVersion = next.SchemaVersion;
        state.Slots = next.Slots; state.Components = next.Components; state.Assets = next.Assets; state.Pending = next.Pending;
    }

    private async Task ReconcilePendingAsync(string path, ApplyState state, SessionInfo session, List<ApplyDiagnostic> diagnostics, CancellationToken ct)
    {
        if (ApplyPendingDiscard.LegacyPending(state).FirstOrDefault() is { } legacy)
            throw WriteUnverified(path, legacy, "pendingUnresolved");
        var identity = (client as IApplySessionObservation)?.ObserveApplySession() ?? ApplySessionObservation.Observe(session.Url);
        foreach (var p in state.Pending.ToArray())
        {
            if (p.Session?.IdentityStatus != "matched" || identity.IdentityStatus != "matched" ||
                string.IsNullOrWhiteSpace(p.Session.DiscoverSessionId) || p.Session.DiscoverSessionId != identity.DiscoverSessionId ||
                p.Session.NormalizedUrl != identity.NormalizedUrl)
                throw WriteUnverified(path, p, "identityUnproven");
            if (!p.ResponseReceived || !p.ResponseAccepted || (p.Kind != "importAsset" && string.IsNullOrWhiteSpace(p.Id)))
                throw WriteUnverified(path, p, "pendingUnresolved");
            try
            {
                if (p.OwnershipKey != state.OwnershipKey) throw WriteUnverified(path, p, "pendingUnresolved");
                if (p.SlotBinding is not null && state.Slots.Any(s => s.Key != p.Key && s.Value.Id == p.Id) ||
                    p.ComponentBinding is not null && state.Components.Any(c => c.Key != p.Key && c.Value.Id == p.Id))
                    throw WriteUnverified(path, p, "pendingUnresolved");
                foreach (var evidence in p.OwnershipSlots.Values)
                {
                    var slot = await client.GetSlotAsync(evidence.Id, 0, false, ct);
                    if (slot.Id != evidence.Id || slot.IsReferenceOnly || slot.Name != evidence.Name || slot.ParentId != evidence.ParentId)
                        throw WriteUnverified(path, p, "pendingUnresolved");
                    if (evidence.ParentId is not null && evidence.ParentId != "Root" && !p.OwnershipSlots.ContainsKey(evidence.ParentId))
                        throw WriteUnverified(path, p, "pendingUnresolved");
                }
                // A managed non-Root parent must have an exact observation in the journal.
                if (p.ParentId is not null && p.ParentId != "Root" && !p.OwnershipSlots.ContainsKey(p.ParentId))
                    throw WriteUnverified(path, p, "pendingUnresolved");
                var readback = await ReadbackPendingAsync(p, ct, resuming: true);
                readback = await VerifyResumedReferenceTargetsAsync(path, state, p, readback, ct);
                var current = await client.GetSessionInfoAsync(ct);
                var now = (client as IApplySessionObservation)?.ObserveApplySession() ?? ApplySessionObservation.Observe(current.Url);
                if (now != identity || !current.Connected || current.ConnectionGeneration != session.ConnectionGeneration)
                    throw WriteUnverified(path, p, "identityUnproven");
                // Missing member data is not evidence that the requested value was not applied.
                var resolved = !readback.Complete && readback.TargetConfirmed && p.Completeness["readback"] == "complete";
                CommitReadback(path, state, p, readback, resolved);
                if (resolved) diagnostics.AddRange(ResolvedDiagnostics(p));
                else if (!readback.Complete) throw WriteUnverified(path, p, "pendingUnresolved");
            }
            catch (RLoopException e) when (e.Code is not ("APPLY_WRITE_UNVERIFIED" or "APPLY_STATE_WRITE_FAILED" or "CONNECTION_GENERATION_CHANGED" or "REQUEST_TIMEOUT"))
            { throw WriteUnverified(path, p, "pendingUnresolved", e); }
            catch (RLoopException e) { throw WithPendingEvidence(e, path, p); }
            catch (OperationCanceledException e)
            { throw WithPendingEvidence(new RLoopException("APPLY_CANCELLED", "Pending reconciliation was cancelled; no new write was sent.", ExitCodes.OperationFailed, innerException: e), path, p); }
            catch (TimeoutException e)
            { throw WithPendingEvidence(new RLoopException("REQUEST_TIMEOUT", "Pending reconciliation timed out; no new write was sent.", ExitCodes.Timeout, innerException: e), path, p); }
            catch (Exception e) { throw WriteUnverified(path, p, "pendingUnresolved", e); }
        }
    }

    private static IEnumerable<ApplyDiagnostic> ResolvedDiagnostics(ApplyPendingWrite p)
    {
        var values = p.Kind is "deleteSlot" or "removeComponent"
            ? new Dictionary<string, object?> { ["absence"] = "absent" }
            : p.SlotValues is { } slot
                ? new Dictionary<string, object?> { ["name"] = slot.Name, ["parent"] = slot.ParentId,
                    ["position"] = slot.Position, ["rotation"] = slot.Rotation, ["scale"] = slot.Scale }
                    .Where(v => v.Value is not null).ToDictionary()
                : p.Members.ToDictionary(v => v.Key, v => (object?)v.Value);
        foreach (var value in values.Where(v => !p.Confirmed.Contains(v.Key)))
            yield return ApplyDiagnostics.Unknown("APPLY_PENDING_RESOLVED_NOT_APPLIED",
                $"Pending '{p.OperationId}' at '{p.Key}.{value.Key}' (exact ID {p.Id}) was resolved as not applied; planning uses the current observation.", "apply") with
            {
                Severity = "warning", BuildId = p.BuildId, Key = p.Key, Member = value.Key, OperationId = p.OperationId,
                Expected = ApplyDiagnosticValue.Known(value.Value),
                Observed = ApplyDiagnosticValue.Known(p.Kind is "deleteSlot" or "removeComponent" ? p.Observed :
                    p.Observed?[p.SlotValues is null ? "members" : "attributes"]?[value.Key]),
                Completeness = new Dictionary<string, string>(p.Completeness),
            };
    }

    private static Dictionary<string, object?> PendingContext(string path, ApplyPendingWrite p, string reason) => new()
    {
        ["stateFile"] = path, ["reason"] = reason, ["operationId"] = p.OperationId, ["sendStatus"] = p.SendStatus,
        ["pending"] = p, ["confirmed"] = p.Confirmed, ["expected"] = new { p.Id, p.Type, p.ParentId, p.SlotValues, p.Members },
        ["observed"] = p.Observed, ["completeness"] = p.Completeness,
    };
    private static RLoopException WriteUnverified(string path, ApplyPendingWrite p, string reason, Exception? inner = null) =>
        WithPendingEvidence(new RLoopException("APPLY_WRITE_UNVERIFIED", $"Write '{p.OperationId}' remains unverified ({reason}); no replay was attempted.",
            ExitCodes.OperationFailed, PendingContext(path, p, reason),
            [PendingSuggestion(path, p)], inner), path, p);
    private static string PendingSuggestion(string path, ApplyPendingWrite p) =>
        $"Inspect exact target ID {p.Id ?? "unknown"} ({p.Kind}, key '{p.Key}') and state '{path}'. After inspection, explicitly discard with: resoloop apply FILE --state \"{path}\" --discard-pending \"{p.OperationId}\" --yes. {ApplyPendingDiscard.WarningFor(p.Kind)} Delete unwanted objects only by their exact IDs with --yes.";
    private static RLoopException WithPendingEvidence(RLoopException e, string path, ApplyPendingWrite p)
    {
        var context = new Dictionary<string, object?>(e.Context);
        foreach (var pair in PendingContext(path, p, e.Context.GetValueOrDefault("reason")?.ToString() ??
            (p.SendStatus == "notSentProven" ? "notSent" : p.ResponseReceived ? "pendingUnresolved" : "responseLost"))) context.TryAdd(pair.Key, pair.Value);
        object persistence = "unknown";
        object bindings = "unknown";
        try
        {
            var persisted = ApplyStateStore.Load(path, p.OwnershipKey);
            var saved = persisted.Pending.FirstOrDefault(item => item.OperationId == p.OperationId);
            persistence = new { pendingSaved = saved is not null, responseEvidenceSaved = saved?.ResponseReceived == true,
                readbackEvidenceSaved = saved?.Observed is not null };
            bindings = new { persisted.Slots, persisted.Components, persisted.Assets };
        }
        catch (RLoopException) { }
        context["evidencePersistence"] = persistence;
        context["confirmedBindings"] = bindings;
        var result = new RLoopException(e.Code, e.Message, e.ExitCode, context,
            e.Suggestions.Concat([PendingSuggestion(path, p)]).Distinct().ToArray(), e.InnerException ?? e);
        var earlier = ApplyDiagnostics.ForException(e, "apply").Diagnostics;
        var diagnostic = (earlier.LastOrDefault() ?? ApplyDiagnostics.Unknown(e.Code, e.Message, "apply")) with
        {
            BuildId = earlier.LastOrDefault()?.BuildId ?? p.BuildId,
            Message = e.Message + " " + PendingSuggestion(path, p),
            Key = p.Key, Member = context.GetValueOrDefault("member")?.ToString(), Expected = ApplyDiagnosticValue.Known(context["expected"]),
            Observed = ApplyDiagnosticValue.Known(context["observed"]),
            Completeness = context["completeness"] as IReadOnlyDictionary<string, string> ?? p.Completeness,
            OperationId = p.OperationId, SendStatus = p.SendStatus, Pending = p, Confirmed = p.Confirmed,
            EvidencePersistence = persistence, ConfirmedBindings = bindings,
        };
        ApplyDiagnostics.AttachRuntime(result, [.. earlier.Take(Math.Max(0, earlier.Count - 1)), diagnostic]);
        return result;
    }
}

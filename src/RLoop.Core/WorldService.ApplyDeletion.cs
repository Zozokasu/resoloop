namespace RLoop.Core;

public sealed partial class WorldService
{
    private static RLoopException DeletionRejected(ApplyPendingWrite p, string reason) => new(
        "APPLY_PRECONDITION_FAILED", "Apply deletion stopped before sending: " + reason, ExitCodes.ValidationFailed,
        new Dictionary<string, object?> { ["reason"] = reason, ["key"] = p.Key, ["id"] = p.Id, ["parentId"] = p.ParentId });

    private async Task<SlotInfo> ObserveDeletionSlotAsync(ApplyPendingWrite p, string id, CancellationToken ct)
    {
        if (client is not IApplyDeletionObservation observer) throw DeletionRejected(p, "deletionObservationUnavailable");
        var observed = await observer.ObserveDeletionSlotAsync(id, ct);
        if (observed.Slot.Id != id || observed.Slot.IsReferenceOnly || !observed.ChildrenObserved || !observed.ComponentsObserved)
            throw DeletionRejected(p, "deletionObservationIncomplete");
        return observed.Slot;
    }

    private static void VerifyDeletionSlotOwner(PreparedApply prepared, ApplyPendingWrite p, string key, SlotInfo slot)
    {
        if (slot.Id.Equals("Root", StringComparison.OrdinalIgnoreCase))
            throw new RLoopException("DELETE_ROOT_FORBIDDEN", "The Root Slot can never be pruned.", ExitCodes.ValidationFailed);
        var state = prepared.DeletionState;
        var expected = prepared.Safety!.ObservedSlot(slot.Id) ?? prepared.SnapshotSlots.FirstOrDefault(s => s.Id == slot.Id);
        if (!state.Slots.TryGetValue(key, out var binding) || string.IsNullOrWhiteSpace(binding.Id) || binding.Id != slot.Id ||
            state.Slots.Count(s => s.Value.Id == slot.Id) != 1 || expected is null || expected.IsReferenceOnly ||
            expected.Name != slot.Name || expected.ParentId != slot.ParentId)
            throw DeletionRejected(p, "deletionOwnershipChanged");
    }

    private async Task CheckComponentDeletionAsync(PreparedApply prepared, ApplyPendingWrite p, CancellationToken ct)
    {
        await prepared.Safety!.CheckConnectionAsync(p.Key, ct);
        // Preserve the frozen pipe backend's pre-existing deletion workflow.
        if (new Uri(prepared.Session.Url).Scheme == "pipe") return;
        await VerifyDeletionAncestorsAsync(prepared, p, ct);
        var state = prepared.DeletionState;
        if (!state.Components.TryGetValue(p.Key, out var binding) || binding.Id != p.Id || string.IsNullOrWhiteSpace(binding.Id) ||
            state.Components.Count(c => c.Value.Id == p.Id) != 1 || !state.Slots.TryGetValue(binding.SlotKey, out var owner) || owner.Id != p.ParentId)
            throw DeletionRejected(p, "deletionOwnershipChanged");
        var slot = await ObserveDeletionSlotAsync(p, owner.Id, ct);
        VerifyDeletionSlotOwner(prepared, p, binding.SlotKey, slot);
        var matches = slot.Components.Where(c => c.Id == p.Id).ToArray();
        var component = await client.GetComponentAsync(p.Id!, ct);
        if (matches.Length != 1 || component.Id != p.Id || !TypeNamesEquivalent(binding.Type, component.Type) ||
            !TypeNamesEquivalent(matches[0].Type, component.Type)) throw DeletionRejected(p, "deletionOwnershipChanged");
        await prepared.Safety.CheckConnectionAsync(p.Key, ct);
    }

    private async Task CheckSlotDeletionAsync(PreparedApply prepared, ApplyPendingWrite p, CancellationToken ct)
    {
        await prepared.Safety!.CheckConnectionAsync(p.Key, ct);
        if (new Uri(prepared.Session.Url).Scheme == "pipe") return;
        await VerifyDeletionAncestorsAsync(prepared, p, ct);
        var state = prepared.DeletionState;
        var queue = new Queue<(string Id, string? Parent)>();
        queue.Enqueue((p.Id!, p.ParentId));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var target))
        {
            ct.ThrowIfCancellationRequested();
            if (!visited.Add(target.Id) || visited.Count > 10000) throw DeletionRejected(p, "deletionObservationIncomplete");
            var slot = await ObserveDeletionSlotAsync(p, target.Id, ct);
            var claims = state.Slots.Where(s => s.Value.Id == slot.Id && p.RemoveSlots.Contains(s.Key)).ToArray();
            if (claims.Length != 1 || slot.ParentId != target.Parent) throw DeletionRejected(p, "unmanagedDeletionContent");
            VerifyDeletionSlotOwner(prepared, p, claims[0].Key, slot);
            var componentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in slot.Components)
            {
                var owners = state.Components.Where(c => c.Value.Id == component.Id).ToArray();
                if (!componentIds.Add(component.Id) || owners.Length != 1 || !p.RemoveComponents.Contains(owners[0].Key) ||
                    owners[0].Value.SlotKey != claims[0].Key || !TypeNamesEquivalent(owners[0].Value.Type, component.Type))
                    throw DeletionRejected(p, "unmanagedDeletionContent");
            }
            foreach (var child in slot.Children)
            {
                if (string.IsNullOrWhiteSpace(child.Id) || child.ParentId != slot.Id) throw DeletionRejected(p, "deletionObservationIncomplete");
                queue.Enqueue((child.Id, slot.Id));
            }
        }
        await prepared.Safety.CheckConnectionAsync(p.Key, ct);
    }

    private async Task VerifyDeletionAncestorsAsync(PreparedApply prepared, ApplyPendingWrite p, CancellationToken ct)
    {
        foreach (var evidence in p.OwnershipSlots.Values)
        {
            var current = await client.GetSlotAsync(evidence.Id, 0, false, ct);
            // Own successful moves/renames are reflected by readback in the safety snapshot.
            var expected = prepared.Safety!.ObservedSlot(evidence.Id) ?? prepared.SnapshotSlots.FirstOrDefault(s => s.Id == evidence.Id);
            if (expected is null || current.Id != expected.Id || current.IsReferenceOnly ||
                current.Name != expected.Name || current.ParentId != expected.ParentId)
                throw DeletionRejected(p, "deletionOwnershipChanged");
        }
    }
}

using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record ApplyPendingDiscardResult(string StateFile, string OperationId, string Kind, string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Id, string Warning);

// An offline state operation: it cannot obtain a client or send a world request.
public static class ApplyPendingDiscard
{
    public static ApplyPendingDiscardResult Discard(ApplyDocument document, string operationId, bool confirmed,
        string? stateFile = null)
    {
        if (!confirmed)
            throw new RLoopException("CONFIRMATION_REQUIRED", "apply --discard-pending requires --yes after inspecting the world.", ExitCodes.ValidationFailed);
        if (string.IsNullOrWhiteSpace(document.Ownership?.Key))
            throw new RLoopException("APPLY_OWNERSHIP_MISSING", "ownership.key is required.", ExitCodes.ValidationFailed);
        var path = document.ResolveStatePath(stateFile);
        using var writer = CheckpointFiles.AcquireWriter(path);
        var state = ApplyStateStore.Load(path, document.Ownership.Key, requireState: true);
        var pending = state.Pending.SingleOrDefault(p => p.OperationId == operationId);
        var legacy = pending is null ? LegacyPending(state).FirstOrDefault(p => p.OperationId == operationId) : null;
        if (pending is null && legacy is null)
            throw new RLoopException("INVALID_OPTION", $"No pending operation '{operationId}' exists in '{path}'.", ExitCodes.InvalidArguments);
        var discarded = pending ?? legacy!;
        var next = ApplyStateStore.Copy(state);
        if (pending is not null) next.Pending.RemoveAll(p => p.OperationId == operationId);
        else if (legacy!.Kind == "legacyCreateSlot") next.Slots.Remove(legacy.Key);
        else next.Components.Remove(legacy.Key);
        // Candidate bindings are deliberately never copied into confirmed dictionaries.
        ApplyStateStore.Save(path, next);
        return new(path, operationId, discarded.Kind, discarded.Key, discarded.Id,
            WarningFor(discarded.Kind) + " " +
            "First inspect the world (inspect EXACT_SLOT_ID --members or component inspect EXACT_COMPONENT_ID); " +
            "delete unwanted objects only by their exact IDs with --yes. An unknown ID must be located and inspected manually.");
    }

    internal static string WarningFor(string kind) => kind switch
    {
        "createSlot" or "addComponent" or "legacyCreateSlot" or "legacyAddComponent" =>
            "Discarding creation evidence does not adopt the candidate binding; confirmed bindings remain unchanged. An unconfirmed created target is outside management. If creation succeeded, the next apply may create the same object again (a duplicate).",
        "deleteSlot" or "removeComponent" =>
            "Discarding deletion evidence preserves confirmed bindings. The target may already be absent; inspect its exact ID and re-plan before any further deletion.",
        "importAsset" => "Discarding import evidence preserves confirmed asset bindings. A completed unconfirmed import may be repeated by the next apply.",
        _ => "Discarding update evidence preserves confirmed bindings. The world may contain some or all of the attempted values; inspect the exact target and re-plan before further writes.",
    };

    internal static IEnumerable<ApplyPendingWrite> LegacyPending(ApplyState state)
    {
        foreach (var slot in state.Slots.Where(s => string.IsNullOrWhiteSpace(s.Value.Id)))
            yield return new() { OperationId = "legacy:slot:" + Uri.EscapeDataString(slot.Key), Kind = "legacyCreateSlot",
                Key = slot.Key, OwnershipKey = state.OwnershipKey, SlotBinding = slot.Value };
        foreach (var component in state.Components.Where(c => string.IsNullOrWhiteSpace(c.Value.Id)))
            yield return new() { OperationId = "legacy:component:" + Uri.EscapeDataString(component.Key), Kind = "legacyAddComponent",
                Key = component.Key, OwnershipKey = state.OwnershipKey, ComponentBinding = component.Value };
    }
}

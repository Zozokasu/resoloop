namespace RLoop.Core;

/// <summary>Immediate child/component coverage, before lossy model mapping. No atomicity claim.</summary>
public sealed record ApplyDeletionObservation(SlotInfo Slot, bool ChildrenObserved, bool ComponentsObserved);

public interface IApplyDeletionObservation
{
    Task<ApplyDeletionObservation> ObserveDeletionSlotAsync(string id, CancellationToken cancellationToken = default);
}

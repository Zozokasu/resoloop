using System.Diagnostics;

namespace RLoop.Core;

internal static class CreationTreeVerification
{
    private sealed record Pending(string Id, string ParentId, int Depth);
    internal const int MaxDepth = 64;
    internal const int MaxSlots = 10_000;
    internal const int MaxComponents = 10_000;
    internal const int MaxQueuedSlots = 10_000;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(10);

    /// <summary>Verifies the tree and returns how many Components exceeded the expected count (engine-added).</summary>
    public static async Task<int> VerifyAsync(IResoniteClient client, string root, CreationPlanCounts expected,
        CancellationToken ct)
    {
        if (client is not IApplyDeletionObservation observer)
            throw Incomplete("Exact Slot coverage is unavailable for creation verification.");
        var watch = Stopwatch.StartNew();
        var queue = new Queue<Pending>();
        queue.Enqueue(new Pending(root, "", 0));
        var seenSlots = new HashSet<string>(StringComparer.Ordinal);
        var seenComponents = new HashSet<string>(StringComparer.Ordinal);
        var slots = 0;
        var components = 0;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed >= MaxDuration) throw Incomplete("Creation structure observation exceeded its 10 second limit.");
            var pending = queue.Dequeue();
            if (pending.Depth > MaxDepth) throw Incomplete("Creation structure observation exceeded depth 64.");
            if (string.IsNullOrWhiteSpace(pending.Id) || seenComponents.Contains(pending.Id) || !seenSlots.Add(pending.Id))
                throw Incomplete("Creation structure contains an empty or duplicate Slot ID.");
            var observation = await ReadAsync(observer, pending.Id, ct, watch);
            if (observation is null) throw Incomplete($"Slot '{pending.Id}' returned no observation data.");
            var slot = observation.Slot;
            if (slot is null || slot.Id != pending.Id || slot.IsReferenceOnly ||
                !observation.ChildrenObserved || !observation.ComponentsObserved || slot.Children is null || slot.Components is null)
                throw Incomplete($"Slot '{pending.Id}' did not have complete exact-ID structure coverage.");
            if (pending.ParentId.Length > 0 && slot.ParentId != pending.ParentId)
                throw Incomplete($"Slot '{pending.Id}' did not report its expected exact parent ID.");
            slots++;
            if (slots > MaxSlots) throw Incomplete("Creation structure observation reached the 10000 Slot limit.");

            foreach (var component in slot.Components)
            {
                if (component is null || string.IsNullOrWhiteSpace(component.Id) || seenSlots.Contains(component.Id) || !seenComponents.Add(component.Id))
                    throw Incomplete($"Slot '{pending.Id}' contains an empty or duplicate Component ID.");
                components++;
                if (components > MaxComponents) throw Incomplete("Creation structure observation reached the 10000 Component limit.");
            }
            if (pending.Depth == MaxDepth && slot.Children.Count > 0)
                throw Incomplete("Creation structure observation reached depth 64 before all descendants were inspected.");
            foreach (var child in slot.Children)
            {
                if (child is null || string.IsNullOrWhiteSpace(child.Id) || child.IsReferenceOnly || child.ParentId != slot.Id)
                    throw Incomplete($"Slot '{slot.Id}' has a reference-only, unidentified, or mismatched child.");
                if (seenSlots.Contains(child.Id) || queue.Any(item => item.Id == child.Id))
                    throw Incomplete("Creation structure contains a duplicate or cyclic child ID.");
                if (queue.Count >= MaxQueuedSlots || (long)slots + queue.Count + 1 > MaxSlots)
                    throw Incomplete("Creation structure observation reached its Slot or queue limit.");
                queue.Enqueue(new Pending(child.Id, slot.Id, pending.Depth + 1));
            }
        }

        // Slots must match exactly. Resonite may add Components on its own, so only fewer Components than expected fail.
        if (slots != expected.Slots || components < expected.Components)
            throw Unverified($"Creation structure counts differed: expected {expected.Slots} Slots (exact) and at least {expected.Components} Components, observed {slots} Slots/{components} Components.");
        return components - expected.Components;
    }

    private static async Task<ApplyDeletionObservation> ReadAsync(IApplyDeletionObservation observer, string id,
        CancellationToken ct, Stopwatch watch)
    {
        var remaining = MaxDuration - watch.Elapsed;
        if (remaining <= TimeSpan.Zero) throw Incomplete("Creation structure observation exceeded its 10 second limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(remaining);
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var read = observer.ObserveDeletionSlotAsync(id, deadline.Token);
            var timer = Task.Delay(remaining, timerCancellation.Token);
            if (await Task.WhenAny(read, timer) != read)
            {
                ct.ThrowIfCancellationRequested();
                deadline.Cancel();
                throw Incomplete("An exact Slot read exceeded the remaining creation verification time.");
            }
            timerCancellation.Cancel();
            var result = await read;
            if (watch.Elapsed >= MaxDuration) throw Incomplete("Creation structure observation exceeded its 10 second limit.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Incomplete("An exact Slot read exceeded the remaining creation verification time.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RLoopException ex) when (ex.Code != "APPLY_WRITE_UNVERIFIED")
        {
            throw Incomplete("Could not completely observe exact Slot '" + id + "': " + ex.Message);
        }
        catch (TimeoutException ex)
        {
            throw Incomplete("Could not completely observe exact Slot '" + id + "': " + ex.Message);
        }
        catch (Exception ex)
        {
            throw Incomplete("Could not completely observe exact Slot '" + id + "': " + ex.Message);
        }
    }

    private static RLoopException Incomplete(string message) => new("APPLY_WRITE_UNVERIFIED", message, ExitCodes.OperationFailed);
    private static RLoopException Unverified(string message) => new("APPLY_WRITE_UNVERIFIED", message, ExitCodes.OperationFailed);
}

using System.Diagnostics;

namespace RLoop.Core;

internal sealed record CreationSessionProof(string NormalizedUrl, string DiscoverSessionId, string? ConnectionGeneration);
internal sealed record CreationObservedStamp(string Raw, CreationStamp Stamp);

/// <summary>One deadline and read budget shared by every ownership observation in a creation plan.</summary>
internal sealed class CreationObservationContext
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _slotReads;
    private int _componentReads;

    public CreationObservationContext(IResoniteClient client, IApplyDeletionObservation? observer,
        CreationOwnershipOptions options, CancellationToken cancellationToken)
    {
        Client = client;
        Observer = observer;
        Options = options;
        CancellationToken = cancellationToken;
    }

    public IResoniteClient Client { get; }
    public IApplyDeletionObservation? Observer { get; }
    public CreationOwnershipOptions Options { get; }
    public CancellationToken CancellationToken { get; }
    public List<string> ReadSlotIds { get; } = [];
    public int SlotReads => _slotReads;
    public int ComponentReads => _componentReads;

    public void CheckBudget()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (_watch.Elapsed >= Options.MaxDuration)
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership observation exceeded its time limit.");
    }

    public async Task<T> AwaitAsync<T>(Func<CancellationToken, Task<T>> startRead)
    {
        CheckBudget();
        var remaining = Options.MaxDuration!.Value - _watch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership observation exceeded its time limit.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        deadline.CancelAfter(remaining);
        var pending = startRead(deadline.Token);
        remaining = Options.MaxDuration.Value - _watch.Elapsed;
        if (remaining <= TimeSpan.Zero || deadline.IsCancellationRequested)
        {
            CancellationToken.ThrowIfCancellationRequested();
            deadline.Cancel();
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership observation exceeded its time limit.");
        }
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var timer = Task.Delay(remaining, timerCancellation.Token);
        if (await Task.WhenAny(pending, timer) != pending)
        {
            CancellationToken.ThrowIfCancellationRequested();
            deadline.Cancel();
            throw CreationOwnershipVerifier.ObservationIncomplete("A read exceeded the remaining creation ownership time limit.");
        }
        timerCancellation.Cancel();
        try
        {
            var value = await pending;
            CheckBudget();
            return value;
        }
        catch (OperationCanceledException) when (!CancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw CreationOwnershipVerifier.ObservationIncomplete("A read exceeded the remaining creation ownership time limit.");
        }
    }

    public void EnsureSlotReadCapacity(int requestedReads)
    {
        CheckBudget();
        if (requestedReads > Options.MaxSlots - _slotReads)
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership Slot read limit would be exceeded by the observed child set.");
    }

    public void EnsureComponentReadCapacity(int requestedReads)
    {
        CheckBudget();
        if (requestedReads > Options.MaxComponents - _componentReads)
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership Component read limit would be exceeded by the observed Component set.");
    }

    public void EnsureQueueCapacity(int queuedCount, int newChildren)
    {
        CheckBudget();
        if ((long)queuedCount + newChildren > Options.MaxQueuedSlots || newChildren > Options.MaxSlots - _slotReads)
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership queue or Slot read limit was reached before scheduling descendants.");
    }

    public async Task<SlotInfo> ReadSlotAsync(string id, bool expectedRoot = false)
    {
        CheckBudget();
        var observer = Observer;
        if (observer is null) throw CreationOwnershipVerifier.ObservationIncomplete("Exact Slot observation is unavailable on this client.");
        if (string.IsNullOrWhiteSpace(id)) throw CreationOwnershipVerifier.ObservationIncomplete("Cannot observe a Slot with an empty ID.");
        if (_slotReads >= Options.MaxSlots) throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership Slot read limit was reached.");
        _slotReads++;
        ApplyDeletionObservation observed;
        try { observed = await AwaitAsync(token => observer.ObserveDeletionSlotAsync(id, token)); }
        catch (OperationCanceledException) { throw; }
        catch (RLoopException ex) when (ex.ExitCode == ExitCodes.Timeout) { throw; }
        catch (TimeoutException) { throw; }
        catch (RLoopException ex) when (expectedRoot && ex.Code == "SLOT_NOT_FOUND")
        { throw CreationOwnershipVerifier.OwnerUnproven("The active root Slot no longer exists."); }
        catch (Exception ex) { throw CreationOwnershipVerifier.ObservationIncomplete("Could not completely observe Slot '" + id + "': " + ex.Message); }
        CheckBudget();
        if (observed is null || observed.Slot is null || observed.Slot.Id != id)
            throw CreationOwnershipVerifier.ObservationIncomplete("The exact-ID Slot read returned an unknown or different Slot.");
        if (!observed.ChildrenObserved || !observed.ComponentsObserved || observed.Slot.IsReferenceOnly)
            throw CreationOwnershipVerifier.ObservationIncomplete($"Slot '{id}' was not observed with complete child and Component coverage.");
        ReadSlotIds.Add(id);
        return observed.Slot;
    }

    public async Task<ComponentInfo> ReadComponentAsync(string id)
    {
        CheckBudget();
        if (_componentReads >= Options.MaxComponents)
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation ownership Component read limit was reached.");
        _componentReads++;
        ComponentInfo component;
        try { component = await AwaitAsync(token => Client.GetComponentAsync(id, token)); }
        catch (OperationCanceledException) { throw; }
        catch (RLoopException ex) when (ex.ExitCode == ExitCodes.Timeout) { throw; }
        catch (TimeoutException) { throw; }
        catch (Exception ex) { throw CreationOwnershipVerifier.ObservationIncomplete("Could not completely observe Component '" + id + "': " + ex.Message); }
        CheckBudget();
        if (component is null) throw CreationOwnershipVerifier.ObservationIncomplete("A Component read returned no data.");
        return component;
    }
}

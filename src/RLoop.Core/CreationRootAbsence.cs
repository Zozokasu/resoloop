namespace RLoop.Core;

/// <summary>
/// The single definition of "exactly absent": the matched session is proven before and after an exact-ID observation
/// that fails with SLOT_NOT_FOUND. Anything else (partial data, another ID, timeout, disconnect, cancellation) is not absence.
/// </summary>
internal static class CreationRootAbsence
{
    internal const string DiagnosticCode = "CREATION_PREVIOUS_ROOT_ABSENT";

    internal static object Diagnostic(string previousRootId) => new Dictionary<string, object?>
    {
        ["code"] = DiagnosticCode,
        ["severity"] = "warning",
        ["previousRootId"] = previousRootId,
        ["message"] = "The recorded root was absent by exact ID in the matched session; a new root will be created."
    };

    public static async Task<bool> IsExactAbsentAsync(IResoniteClient client, CreationOwnershipVerifier verifier,
        CreationSession? session, CreationSessionProof proof, string id, CancellationToken ct,
        CreationObservationContext? context = null)
    {
        if (client is not IApplyDeletionObservation observer)
            throw CreationOwnershipVerifier.ObservationIncomplete("Exact Slot observation is unavailable.");
        context ??= verifier.CreateContext(ct);
        await VerifySession(verifier, session, proof, context);
        try
        {
            var result = await observer.ObserveDeletionSlotAsync(id, ct);
            if (result is null || result.Slot is null || result.Slot.Id != id || !result.ChildrenObserved || !result.ComponentsObserved || result.Slot.IsReferenceOnly)
                throw CreationOwnershipVerifier.ObservationIncomplete("The exact Slot observation is partial or has a different ID.");
            await VerifySession(verifier, session, proof, context);
            return false;
        }
        catch (RLoopException ex) when (ex.Code == "SLOT_NOT_FOUND")
        {
            await VerifySession(verifier, session, proof, context);
            return true;
        }
        catch (RLoopException) { await VerifySession(verifier, session, proof, context); throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await VerifySession(verifier, session, proof, context);
            throw CreationOwnershipVerifier.ObservationIncomplete("The exact Slot observation is unknown: " + ex.Message);
        }
    }

    private static async Task VerifySession(CreationOwnershipVerifier verifier, CreationSession? session,
        CreationSessionProof proof, CreationObservationContext context) =>
        await verifier.VerifySessionAsync(session, context, proof);
}

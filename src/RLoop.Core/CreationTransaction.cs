using System.Text.Json;

namespace RLoop.Core;

internal sealed class CreationTransaction(IResoniteClient client, string path, CreationState initial,
    string generation, CancellationToken cancellationToken)
{
    internal static readonly AsyncLocal<Action<string, CreationState>?> SaveFault = new();
    private CreationState state = initial;
    private bool failed;

    internal CreationState State => state;

    internal RLoopException Contextualize(RLoopException error) => WithContext(error, state);

    internal void ReplacePending(CreationPending pending) => state = state with { Pending = pending };

    internal void SetLastTargetId(string? targetId)
    {
        var pending = state.Pending!;
        var steps = pending.Steps.ToArray();
        steps[^1] = steps[^1] with { TargetId = targetId };
        state = state with { Pending = pending with { Steps = steps } };
    }

    internal void SetStage(string stage)
    {
        state = state with { Pending = state.Pending! with { Stage = stage } };
        Save();
    }

    internal void Save()
    {
        try
        {
            SaveFault.Value?.Invoke(path, state);
            CreationStateStore.Save(path, state);
        }
        catch (RLoopException ex) { failed = true; throw WithContext(ex, state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = true;
            throw WithContext(new RLoopException("APPLY_STATE_WRITE_FAILED", "Could not persist creation transaction state.",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex), state);
        }
    }

    internal void SaveFinal(CreationState finalState)
    {
        try
        {
            SaveFault.Value?.Invoke(path, finalState);
            CreationStateStore.Save(path, finalState);
            state = finalState;
        }
        catch (RLoopException ex) { failed = true; throw WithContext(ex, state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed = true;
            throw WithContext(new RLoopException("APPLY_STATE_WRITE_FAILED", "Could not persist final creation transaction state.",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["stateFile"] = path }, innerException: ex), state);
        }
    }

    internal void SaveTreeEvidenceAfterFailure()
    {
        if (failed || state.Pending?.Evidence is not JsonElement evidence ||
            !evidence.TryGetProperty("lastTreeSend", out _)) return;
        Save();
    }

    internal async Task<T> SendAsync<T>(string stage, string kind, string? targetId, string? parentId,
        string? type, object expected, Func<Task<T>> send, Func<T, Task<bool>> readback,
        Action<T>? responseObserved = null, bool absenceIsSuccess = false, Func<Task>? unknownReadback = null)
    {
        if (failed) throw Unverified("A previous creation transaction step failed; no later send is allowed.");
        cancellationToken.ThrowIfCancellationRequested();
        Guard();
        var pending = state.Pending!;
        var step = new CreationStep(kind, targetId, parentId, type, "intent", false, false,
            JsonSerializer.SerializeToElement(expected), null, null, null);
        state = state with { Pending = pending with { Stage = stage, Steps = pending.Steps.Append(step).ToArray() } };
        Save();
        T response;
        var enteredSend = false;
        try
        {
            (client as IApplySendEvidence)?.BeginApplySend();
            using var writeGuard = AcquireGuard();
            GuardCurrent();
            enteredSend = true;
            response = await send();
            var evidence = client as IApplySendEvidence;
            var received = evidence is null || evidence.ApplySendStarted && evidence.ApplyResponseReceived;
            var accepted = evidence is null || evidence.ApplyResponseAccepted;
            if (evidence is not null && (!received || !accepted))
                throw Unverified("The mutation response was not received and accepted.");
            responseObserved?.Invoke(response);
            ReplaceLast(state.Pending! with { Stage = stage }, "response", received, accepted);
            Save();
        }
        catch (Exception ex)
        {
            if (failed) throw Contextualize(ex as RLoopException ?? Unverified("Creation state save failed; no later send is allowed.", ex));
            failed = true;
            var evidence = client as IApplySendEvidence;
            var received = evidence?.ApplySendStarted == true && evidence.ApplyResponseReceived;
            var accepted = evidence?.ApplyResponseAccepted == true;
            if (ex is OperationCanceledException)
            {
                if (enteredSend && evidence?.ApplySendStarted != false)
                {
                    ReplaceLast(state.Pending! with { Stage = stage }, received && !accepted ? "rejected" : "unknown",
                        received, accepted, confirmed: false);
                    Save();
                }
                throw Cancelled(ex);
            }
            if (!enteredSend || evidence?.ApplySendStarted == false)
            {
                if (ex is RLoopException { ExitCode: ExitCodes.Timeout } || ex is TimeoutException) throw Timeout(ex);
                if (ex is RLoopException unsent) throw unsent;
                throw Unverified("Creation send was not entered.", ex);
            }
            ReplaceLast(state.Pending! with { Stage = stage }, received && !accepted ? "rejected" : "unknown",
                received, accepted, confirmed: false);
            Save();
            if (received && !accepted && ex is RLoopException rejected && rejected.Code != "APPLY_WRITE_UNVERIFIED") throw rejected;
            if (unknownReadback is not null)
            {
                try { cancellationToken.ThrowIfCancellationRequested(); await unknownReadback(); }
                catch (OperationCanceledException cancel) { throw Cancelled(cancel); }
                catch (RLoopException timeout) when (timeout.ExitCode == ExitCodes.Timeout) { throw Timeout(timeout); }
                catch (TimeoutException timeout) { throw Timeout(timeout); }
                catch { /* Diagnostic readback cannot turn an unknown delete into success. */ }
            }
            if (ex is RLoopException { ExitCode: ExitCodes.Timeout } || ex is TimeoutException) throw Timeout(ex);
            throw Unverified("The mutation response is unknown: " + ex.Message, ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool confirmed;
        try { confirmed = await readback(response); }
        catch (RLoopException ex) when (ex.Code == "SLOT_NOT_FOUND") { confirmed = absenceIsSuccess; }
        catch (Exception ex)
        {
            if (failed) throw Contextualize(ex as RLoopException ?? Unverified("Creation state save failed; no later send is allowed.", ex));
            failed = true;
            ReplaceLast(state.Pending! with { Stage = stage }, "readback-unknown", true, true, confirmed: false);
            Save();
            if (ex is OperationCanceledException) throw Cancelled(ex);
            if (ex is RLoopException { ExitCode: ExitCodes.Timeout } || ex is TimeoutException) throw Timeout(ex);
            throw Unverified("The exact mutation readback is unknown: " + ex.Message, ex);
        }
        ReplaceLast(state.Pending! with { Stage = stage }, confirmed ? "confirmed" : "readback-mismatch",
            true, true, confirmed: confirmed);
        Save();
        if (!confirmed)
        {
            failed = true;
            throw Unverified("The exact mutation readback did not confirm the requested change.");
        }
        return response;
    }

    internal async Task SendTreeAsync(Func<Task> send, CancellationToken ct)
    {
        if (failed) throw Unverified("A previous creation transaction step failed; no later send is allowed.");
        ct.ThrowIfCancellationRequested();
        Guard();
        var enteredSend = false;
        try
        {
            (client as IApplySendEvidence)?.BeginApplySend();
            using var writeGuard = AcquireGuard();
            GuardCurrent();
            enteredSend = true;
            await send();
            var evidence = client as IApplySendEvidence;
            if (evidence is not null && (!evidence.ApplySendStarted || !evidence.ApplyResponseReceived || !evidence.ApplyResponseAccepted))
                throw Unverified("A creation tree send did not receive an accepted response.");
            var pending = state.Pending!;
            state = state with { Pending = pending with { Stage = "creatingTree", Evidence = JsonSerializer.SerializeToElement(new
            {
                lastTreeSend = "accepted",
                responseReceived = true,
                responseAccepted = true
            }) } };
        }
        catch (Exception ex)
        {
            if (failed) throw Contextualize(ex as RLoopException ?? Unverified("Creation state save failed; no later send is allowed.", ex));
            failed = true;
            var evidence = client as IApplySendEvidence;
            var received = evidence?.ApplySendStarted == true && evidence.ApplyResponseReceived;
            var accepted = evidence?.ApplyResponseAccepted == true;
            if (enteredSend)
            {
                var pending = state.Pending!;
                state = state with { Pending = pending with { Stage = "creatingTree", Evidence = JsonSerializer.SerializeToElement(new
                {
                    lastTreeSend = received ? accepted ? "accepted" : "rejected" : "unknown",
                    responseReceived = received,
                    responseAccepted = accepted
                }) } };
                Save();
            }
            if (ex is OperationCanceledException) throw Cancelled(ex);
            if (ex is RLoopException { ExitCode: ExitCodes.Timeout } || ex is TimeoutException) throw Timeout(ex);
            if (!enteredSend || evidence?.ApplySendStarted == false)
            {
                if (ex is RLoopException unsent) throw unsent;
                throw Unverified("Creation tree send was not entered.", ex);
            }
            if (enteredSend && received && !accepted && ex is RLoopException rejected && rejected.Code != "APPLY_WRITE_UNVERIFIED") throw rejected;
            if (!enteredSend) throw Unverified("Creation tree send was not entered.", ex);
            throw Unverified("A creation tree send has unknown outcome: " + ex.Message, ex);
        }
    }

    private void ReplaceLast(CreationPending pending, string status, bool received, bool accepted, bool? confirmed = null)
    {
        var steps = pending.Steps.ToArray();
        var step = steps[^1];
        var readback = confirmed switch { true => "complete", false => "partial", null => "unknown" };
        var receipt = received ? accepted ? "accepted" : "rejected" : "unknown";
        steps[^1] = step with { SendStatus = status, ResponseReceived = received, ResponseAccepted = accepted,
            Confirmed = confirmed,
            Observed = JsonSerializer.SerializeToElement(new
            {
                targetId = step.TargetId,
                parentId = step.ParentId,
                type = step.Type,
                responseReceived = received,
                responseAccepted = accepted,
                readbackComplete = confirmed == true
            }),
            Completeness = JsonSerializer.SerializeToElement(new { send = receipt, readback }) };
        state = state with { Pending = pending with { Steps = steps } };
    }

    private void Guard()
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireGuard();
        GuardCurrent();
    }

    private IDisposable AcquireGuard() => client is IApplyConnectionGuard guard
        ? guard.GuardApplyWrites(generation)
        : throw Unverified("The client has no connection generation guard.");

    private void GuardCurrent()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (client is not IApplyConnectionGuard guard) throw Unverified("The client has no connection generation guard.");
        var current = guard.ObserveApplyConnection();
        if (!current.Connected || current.Generation != generation)
            throw new RLoopException("APPLY_PRECONDITION_FAILED", "Connection generation changed before a creation write.", ExitCodes.ValidationFailed);
    }

    private RLoopException WithContext(RLoopException error, CreationState snapshot)
    {
        var pending = snapshot.Pending;
        var context = error.Context.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        context["stateFile"] = path;
        context["operationId"] = pending?.OperationId;
        context["stage"] = pending?.Stage;
        context["previousRootId"] = pending?.PreviousRoot?.Id;
        context["candidateRootId"] = pending?.CandidateRoot?.Id;
        context["pending"] = pending;
        context["completeness"] = error.Code == "APPLY_STATE_WRITE_FAILED" ? "partial" : "unknown";
        var recovery = $"Resolve the retained creation state with: resoloop creation recover --state \"{path}\" --action rollback|finish --url auto --session {pending?.Session.DiscoverSessionId ?? "S-ID"} --yes";
        return new RLoopException(error.Code, error.Message, error.ExitCode, context,
            error.Suggestions.Append(recovery).ToArray(), error);
    }

    private static RLoopException Unverified(string message, Exception? inner = null) => new(
        "APPLY_WRITE_UNVERIFIED", message, ExitCodes.OperationFailed, innerException: inner);
    private static RLoopException Cancelled(Exception? inner = null) => new(
        "APPLY_CANCELLED", "Creation apply was cancelled; pending evidence was retained.", ExitCodes.OperationFailed, innerException: inner);
    private static RLoopException Timeout(Exception? inner = null) => new(
        "REQUEST_TIMEOUT", "Creation apply timed out; pending evidence was retained.", ExitCodes.Timeout, innerException: inner);
}

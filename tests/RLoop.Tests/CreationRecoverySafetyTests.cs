using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationRecoverySafetyTests
{
    [Fact]
    public async Task UnknownCandidateIdIsNotTreatedAsAbsence()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(UnknownCandidateIdIsNotTreatedAsAbsence));
        var unknownCreate = new CreationStep("create-root", null, CreationOwnershipFixture.ParentId, "Slot",
            "unknown", false, false, null, null, false, null);
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending("creatingRoot", steps: [unknownCreate]));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("operation-4", CreationStateStore.Load(path, state.CreationId, true)!.Pending!.OperationId);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.DeletionObservationIds);
    }

    [Fact]
    public async Task BusyWriterIsNeverStolenAndBlocksRecoveryBeforeWorldMutation()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(BusyWriterIsNeverStolenAndBlocksRecoveryBeforeWorldMutation));
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        using var held = CheckpointFiles.AcquireWriter(path);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("APPLY_STATE_BUSY", error.Code);
        Assert.Empty(client.SendLog);
        Assert.NotNull(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
    }

    [Fact]
    public async Task UrlLeaseProofMismatchStopsBeforeRecoveryObservationOrMutation()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(UrlLeaseProofMismatchStopsBeforeRecoveryObservationOrMutation));
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending());
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.BeforeSessionRead = read =>
        {
            if (read == 2) client.SessionUrl = "ws://localhost:47611/";
        };

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("CREATION_SESSION_UNPROVEN", error.Code);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.DeletionObservationIds);
    }

    [Fact]
    public async Task MismatchedRequestedSessionStopsBeforeWorldObservation()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(MismatchedRequestedSessionStopsBeforeWorldObservation));
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending());
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", "S-different", true)));

        Assert.Equal("CREATION_SESSION_UNPROVEN", error.Code);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.DeletionObservationIds);
    }

    [Fact]
    public async Task PartialOldRootReadIsNotTreatedAsCompleteAbsence()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(PartialOldRootReadIsNotTreatedAsCompleteAbsence));
        var previous = CreationOwnershipFixture.State().ActiveRoot!;
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending("deletingPrevious", candidate, previous), previous);
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state, previous.Stamp);
        client.MakeObservationPartialFor(previous.Id);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
        var retained = CreationStateStore.Load(path, state.CreationId, true)!;
        Assert.Equal("operation-4", retained.Pending!.OperationId);
        Assert.Equal(previous.Id, retained.ActiveRoot!.Id);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task UnknownCandidateDeleteStopsWithoutAnyFollowingWorldRead()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(UnknownCandidateDeleteStopsWithoutAnyFollowingWorldRead));
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, candidate.Stamp);
        client.ThrowOnMutation = 1;

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal(new[] { "delete-slot:candidate-root" }, client.RequestHistory);
        Assert.Equal("cleanupCandidate", CreationStateStore.Load(path, state.CreationId, true)!.Pending!.Stage);
    }

    [Fact]
    public async Task FinalStateSaveFailureRetainsCleanupPendingAfterCandidateDeletion()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(FinalStateSaveFailureRetainsCleanupPendingAfterCandidateDeletion));
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, candidate.Stamp);
        CreationTransaction.SaveFault.Value = (_, final) => { if (final.Pending is null) throw new IOException("injected final save failure"); };
        try
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
                .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
        }
        finally { CreationTransaction.SaveFault.Value = null; }

        Assert.DoesNotContain(candidate.Id, client.Slots.Keys);
        Assert.Equal("cleanupCandidate", CreationStateStore.Load(path, state.CreationId, true)!.Pending!.Stage);
    }

    [Fact]
    public async Task PartialCandidateObservationRetainsPendingAndSendsNothing()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(PartialCandidateObservationRetainsPendingAndSendsNothing));
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, candidate.Stamp);
        client.MakeObservationPartialFor(candidate.Id);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
        Assert.Equal("operation-4", CreationStateStore.Load(path, state.CreationId, true)!.Pending!.OperationId);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task MissingCandidateMarkerIsNotOwnershipAndNeverDeletes()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(MissingCandidateMarkerIsNotOwnershipAndNeverDeletes));
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, candidate.Stamp);
        client.Slots[candidate.Id] = client.Slots[candidate.Id] with { Components = [] };
        client.Components.Remove("seed-stamp");

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Equal("operation-4", CreationStateStore.Load(path, state.CreationId, true)!.Pending!.OperationId);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task MarkerMismatchRetainsPendingAndNeverDeletesCandidate()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(MarkerMismatchRetainsPendingAndNeverDeletesCandidate));
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        var wrongStamp = candidate.Stamp.Replace(CreationOwnershipFixture.GenerationId,
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", StringComparison.Ordinal);
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, wrongStamp);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Empty(client.SendLog);
        Assert.NotNull(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
    }

    [Fact]
    public async Task NewConnectionGenerationIsRecoveryProofAndNeedNotEqualOldPendingGeneration()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(NewConnectionGenerationIsRecoveryProofAndNeedNotEqualOldPendingGeneration));
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.ConnectionGeneration = "new-client-generation";

        await new CreationRecoveryService(client).RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true));

        Assert.Null(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
        Assert.Empty(client.SendLog);
        Assert.Equal("old-process-generation", state.Pending!.ConnectionGeneration);
        Assert.Equal("new-client-generation", client.ConnectionGeneration);
    }
}

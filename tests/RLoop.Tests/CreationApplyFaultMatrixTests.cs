using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyFaultMatrixTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-faults-" + Guid.NewGuid().ToString("N"));
    private readonly string statePath;

    public CreationApplyFaultMatrixTests()
    {
        Directory.CreateDirectory(root);
        statePath = Path.Combine(root, "state.json");
    }

    [Fact]
    public void PreparedSaveFailurePreservesPriorBytesAndDoesNotCountPendingAsSaved()
    {
        var initial = CreationOwnershipFixture.State();
        CreationStateStore.Save(statePath, initial);
        var priorBytes = File.ReadAllBytes(statePath);
        var pendingState = PendingState(initial);
        var transaction = new CreationTransaction(new CreationMutationTestClient(), statePath, pendingState,
            "generation", CancellationToken.None);
        CreationTransaction.SaveFault.Value = (_, state) =>
        {
            Assert.Equal("prepared", state.Pending!.Stage);
            throw new IOException("prepared write failed");
        };
        try
        {
            var error = Assert.Throws<RLoopException>(() => transaction.Save());
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
            Assert.Equal(priorBytes, File.ReadAllBytes(statePath));
            Assert.Null(CreationStateStore.Load(statePath, "room", true)!.Pending);
        }
        finally { CreationTransaction.SaveFault.Value = null; }
    }

    [Fact]
    public async Task OldDeleteUnknownAfterExactDeletionIsNeverConfirmedAndStopsReplacement()
    {
        var state = CreationOwnershipFixture.State();
        var previous = state.ActiveRoot!;
        state = PendingState(state);
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, previous.Stamp);
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync<string>("deletingPrevious", "delete-slot",
            previous.Id, previous.ParentId, null, new { targetId = previous.Id }, async () =>
            {
                await client.DeleteSlotAsync(previous.Id);
                client.MarkNextMutationResponseUnknown();
                throw new RLoopException("SEND_UNKNOWN", "delete response was lost", ExitCodes.OperationFailed);
            }, _ => Task.FromResult(false), unknownReadback: async () =>
            {
                _ = await client.ObserveDeletionSlotAsync(previous.Id);
            }));

        var saved = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.DoesNotContain(previous.Id, client.Slots.Keys);
        Assert.Equal(new[] { previous.Id }, client.DeletionObservationIds);
        Assert.Equal("deletingPrevious", saved.Stage);
        Assert.Equal(previous.Id, saved.PreviousRoot!.Id);
        Assert.Equal(CreationOwnershipFixture.ParentId, saved.CandidateRoot!.ParentId);
        Assert.Equal("unknown", saved.Steps[^1].SendStatus);
        Assert.False(saved.Steps[^1].ResponseReceived);
        Assert.False(saved.Steps[^1].Confirmed);
        Assert.Equal("partial", saved.Steps[^1].Completeness!.Value.GetProperty("readback").GetString());
        Assert.DoesNotContain(client.RequestHistory, value => value.StartsWith("create-slot:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NewRootUnknownReceiptRetainsCandidateContextAndNoLaterFrames()
    {
        var state = PendingState(CreationOwnershipFixture.State());
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);
        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync<string>("creatingRoot", "create-root", null,
            CreationOwnershipFixture.ParentId, "Slot", new { parentId = CreationOwnershipFixture.ParentId }, async () =>
            {
                _ = await client.CreateSlotAsync(new SlotCreateRequest(CreationOwnershipFixture.ParentId, "Room"));
                client.MarkNextMutationResponseUnknown();
                throw new RLoopException("SEND_UNKNOWN", "root response lost", ExitCodes.OperationFailed);
            }, _ => Task.FromResult(false)));
        var saved = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("creatingRoot", saved.Stage);
        Assert.Equal("unknown", saved.Steps[^1].SendStatus);
        Assert.False(saved.Steps[^1].Confirmed);
        Assert.Equal(new[] { "create-slot" }, client.SendLog);
        await Assert.ThrowsAsync<RLoopException>(() => transaction.SendTreeAsync(() => Task.CompletedTask, CancellationToken.None));
        Assert.Single(client.SendLog);
    }

    [Fact]
    public async Task StampRejectedReceiptStopsBeforeTreeAndRetainsCandidateId()
    {
        var state = PendingState(CreationOwnershipFixture.State()) with
        {
            Pending = PendingState(CreationOwnershipFixture.State()).Pending! with
            {
                Stage = "markingRoot",
                CandidateRoot = new CreationPendingRoot("candidate-1", CreationOwnershipFixture.ParentId,
                    CreationOwnershipFixture.GenerationId, CreationOwnershipFixture.Stamp.Serialize(), CreationOwnershipFixture.InputHash, null)
            }
        };
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        client.AddChild(CreationOwnershipFixture.ParentId, new SlotInfo("candidate-1", "Room", CreationOwnershipFixture.ParentId,
            null, null, null, true, true, null, false, [], []));
        client.RejectNextResponse();
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync("markingRoot", "add-stamp",
            "candidate-1", null, CreationOwnershipVerifier.VariableComponentType, new { targetId = "candidate-1" },
            () => client.AddComponentAsync("candidate-1", CreationOwnershipVerifier.VariableComponentType,
                new Dictionary<string, string> { ["VariableName"] = CreationOwnershipVerifier.VariableName,
                    ["Value"] = CreationOwnershipFixture.Stamp.Serialize() }), _ => Task.FromResult(false)));

        var saved = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("TEST_REJECTED", error.Code);
        Assert.Equal("candidate-1", saved.CandidateRoot!.Id);
        Assert.Equal("rejected", saved.Steps[^1].SendStatus);
        Assert.True(saved.Steps[^1].ResponseReceived);
        Assert.False(saved.Steps[^1].ResponseAccepted);
        Assert.DoesNotContain("create-slot", client.SendLog);
    }

    [Fact]
    public async Task TreeFailureRetainsCreatingTreeEvidenceAndStopsLaterFrames()
    {
        var state = PendingState(CreationOwnershipFixture.State()) with
            { Pending = PendingState(CreationOwnershipFixture.State()).Pending! with { Stage = "creatingTree" } };
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendTreeAsync(async () =>
        {
            await client.CreateSlotAsync(new SlotCreateRequest(CreationOwnershipFixture.ParentId, "Child"));
            throw new RLoopException("TREE_SEND_FAILED", "child create failed", ExitCodes.OperationFailed);
        }, CancellationToken.None));

        var pending = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("creatingTree", pending.Stage);
        Assert.Equal("accepted", pending.Evidence!.Value.GetProperty("lastTreeSend").GetString());
        Assert.Single(client.SendLog);
    }

    [Fact]
    public void FinalSaveFailureKeepsPersistedPendingAndDoesNotExposeSuccess()
    {
        var state = PendingState(CreationOwnershipFixture.State()) with
            { Pending = PendingState(CreationOwnershipFixture.State()).Pending! with { Stage = "staged" } };
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);
        CreationTransaction.SaveFault.Value = (_, final) =>
        {
            Assert.Null(final.Pending);
            throw new IOException("final save failed");
        };
        try
        {
            var error = Assert.Throws<RLoopException>(() => transaction.SaveFinal(state with
                { ActiveRoot = new CreationActiveRoot("candidate", "parent-slot", CreationOwnershipFixture.GenerationId,
                    CreationOwnershipFixture.Stamp.Serialize(), CreationOwnershipFixture.InputHash, null), Pending = null }));
            var persisted = CreationStateStore.Load(statePath, "room", true)!;
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
            Assert.NotNull(persisted.Pending);
            Assert.NotNull(transaction.State.Pending);
            Assert.Equal(CreationOwnershipFixture.RootId, persisted.ActiveRoot!.Id);
            Assert.Equal("staged", persisted.Pending!.Stage);
            Assert.Empty(client.SendLog);
        }
        finally { CreationTransaction.SaveFault.Value = null; }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    public async Task CancelAndTimeoutRetainPendingEvidence(string fault)
    {
        using var cts = new CancellationTokenSource();
        var state = PendingState(CreationOwnershipFixture.State());
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        var transaction = new CreationTransaction(client, statePath, state, "generation", cts.Token);
        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync<string>("creatingRoot", "create-root", null,
            CreationOwnershipFixture.ParentId, "Slot", new { parentId = CreationOwnershipFixture.ParentId }, async () =>
            {
                _ = await client.CreateSlotAsync(new SlotCreateRequest(CreationOwnershipFixture.ParentId, "Room"));
                client.MarkNextMutationResponseUnknown();
                if (fault == "cancel") { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
                throw new RLoopException("REQUEST_TIMEOUT", "send timed out", ExitCodes.Timeout);
            }, _ => Task.FromResult(false)));
        var pending = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal(fault == "cancel" ? "APPLY_CANCELLED" : "REQUEST_TIMEOUT", error.Code);
        Assert.Equal("creatingRoot", pending.Stage);
        Assert.False(pending.Steps[^1].Confirmed);
        Assert.False(pending.Steps[^1].ResponseReceived);
        Assert.Equal(new[] { "create-slot" }, client.SendLog);
    }

    private static CreationState PendingState(CreationState state)
    {
        var previous = state.ActiveRoot!;
        return state with { Pending = new CreationPending("fault-operation", "prepared",
            new CreationScope("creation", null, previous.Id, null, previous.ParentId), "delete-first", state.Session,
            "generation", null, previous.InputHash, null, previous,
            new CreationPendingRoot(null, previous.ParentId, CreationStamp.CreateGenerationId(),
                CreationStamp.Create(state.CreationId, state.OwnerNonce).Serialize(), previous.InputHash, null), [], null) };
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

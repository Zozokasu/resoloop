using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyInterruptionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-interrupt-" + Guid.NewGuid().ToString("N"));
    private readonly string statePath;

    public CreationApplyInterruptionTests()
    {
        Directory.CreateDirectory(root);
        statePath = Path.Combine(root, "state.json");
    }

    [Fact]
    public async Task UnknownSendIsSavedAsPendingAndBlocksEveryLaterSend()
    {
        var state = CreationOwnershipFixture.State();
        var previous = state.ActiveRoot!;
        var pending = new CreationPending("operation-a", "prepared",
            new CreationScope("creation", null, previous.Id, null, previous.ParentId), "delete-first", state.Session,
            "generation", null, previous.InputHash, null, previous, new CreationPendingRoot(null,
                previous.ParentId, CreationStamp.CreateGenerationId(), CreationStamp.Create(state.CreationId, state.OwnerNonce).Serialize(),
                previous.InputHash, null), [], null);
        state = state with { Pending = pending };
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, previous.Stamp);
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);
        var sends = 0;

        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync<string>("creatingRoot", "create-root", null,
            previous.ParentId, "Slot", new { parentId = previous.ParentId }, async () =>
            {
                sends++;
                _ = await client.CreateSlotAsync(new SlotCreateRequest(previous.ParentId, "Room"));
                client.MarkNextMutationResponseUnknown();
                throw new RLoopException("SEND_UNKNOWN", "unknown", ExitCodes.OperationFailed);
            }, _ => Task.FromResult(false)));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        var stored = CreationStateStore.Load(statePath, state.CreationId, true)!;
        Assert.Equal("creatingRoot", stored.Pending!.Stage);
        Assert.False(stored.Pending.Steps[^1].Confirmed);
        Assert.False(stored.Pending.Steps[^1].ResponseReceived);
        await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync("creatingRoot", "create-root", null,
            previous.ParentId, "Slot", new { parentId = previous.ParentId }, () =>
            {
                sends++;
                return Task.FromResult("should-not-send");
            }, _ => Task.FromResult(true)));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task ConnectionGenerationMismatchStopsBeforeIntentAndMutation()
    {
        var state = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient { ConnectionGeneration = "changed-generation" };
        var transaction = new CreationTransaction(client, statePath, state with
        {
            Pending = new CreationPending("operation-b", "prepared",
                new CreationScope("creation", null, null, null, CreationOwnershipFixture.ParentId),
                "delete-first", state.Session, "generation", null, CreationOwnershipFixture.InputHash,
                null, null, null, [], null)
        }, "generation", CancellationToken.None);
        var sends = 0;
        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync("deletingPrevious", "delete-slot",
            CreationOwnershipFixture.RootId, CreationOwnershipFixture.ParentId, null,
            new { targetId = CreationOwnershipFixture.RootId }, () =>
            {
                sends++;
                return Task.FromResult(CreationOwnershipFixture.RootId);
            }, _ => Task.FromResult(true)));
        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task ExactSlotNotFoundIsTheOnlyDeletionAbsenceSuccess()
    {
        var state = CreationOwnershipFixture.State();
        var previous = state.ActiveRoot! with { Id = "root-created" };
        var pending = new CreationPending("operation-c", "prepared",
            new CreationScope("creation", null, previous.Id, null, previous.ParentId), "delete-first", state.Session,
            "generation", null, previous.InputHash, null, previous, null, [], null);
        state = state with { ActiveRoot = previous, Pending = pending };
        var client = new CreationMutationTestClient();
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);

        var result = await transaction.SendAsync("deletingPrevious", "delete-slot", previous.Id, previous.ParentId,
            null, new { targetId = previous.Id }, async () =>
            {
                await client.DeleteSlotAsync(previous.Id);
                return previous.Id;
            }, async id =>
            {
                await client.ObserveDeletionSlotAsync(id);
                return false;
            }, absenceIsSuccess: true);

        Assert.Equal(previous.Id, result);
        Assert.True(transaction.State.Pending!.Steps[^1].Confirmed);
        Assert.Equal("confirmed", transaction.State.Pending.Steps[^1].SendStatus);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyTransactionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-creation-transaction-" + Guid.NewGuid().ToString("N"));
    private readonly string statePath;

    public CreationApplyTransactionTests()
    {
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(directory, "state.json");
    }

    [Fact]
    public async Task ResponseSaveFailureIsNotRetriedAndPreventsLaterSend()
    {
        var state = PendingState();
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);
        var saves = 0;
        var sends = 0;
        CreationTransaction.SaveFault.Value = (_, _) =>
        {
            if (++saves == 2) throw new IOException("response persistence failed");
        };
        try
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync("creatingRoot", "create-root", null,
                CreationOwnershipFixture.ParentId, "Slot", new { parentId = CreationOwnershipFixture.ParentId }, () =>
                {
                sends++;
                return client.CreateSlotAsync(new SlotCreateRequest(CreationOwnershipFixture.ParentId, "Room"));
                }, _ => Task.FromResult(true)));
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
            Assert.Equal(2, saves);
            Assert.Equal(1, sends);
            await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync("creatingTree", "create-slot", null,
                "exact-root-id", "Slot", new { }, () =>
                {
                    sends++;
                    return Task.FromResult("unexpected");
                }, _ => Task.FromResult(true)));
            Assert.Equal(1, sends);
        }
        finally { CreationTransaction.SaveFault.Value = null; }
    }

    private static CreationState PendingState()
    {
        var state = CreationOwnershipFixture.State();
        var previous = state.ActiveRoot!;
        var pending = new CreationPending("transaction-operation", "prepared",
            new CreationScope("creation", null, previous.Id, null, previous.ParentId), "delete-first", state.Session,
            "generation", null, previous.InputHash, null, previous,
            new CreationPendingRoot(null, previous.ParentId, CreationStamp.CreateGenerationId(),
                CreationStamp.Create(state.CreationId, state.OwnerNonce).Serialize(), previous.InputHash, null), [], null);
        return state with { Pending = pending };
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

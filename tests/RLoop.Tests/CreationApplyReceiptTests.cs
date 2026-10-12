using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyReceiptTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-creation-receipt-" + Guid.NewGuid().ToString("N"));
    private readonly string statePath;

    public CreationApplyReceiptTests()
    {
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(directory, "state.json");
    }

    [Fact]
    public async Task StartedSendWithUnknownReceiptMapsSdkErrorToUnverifiedAndBlocksLaterSends()
    {
        var state = PendingState();
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);
        var sends = 0;

        var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync<string>("creatingRoot", "create-root", null,
            CreationOwnershipFixture.ParentId, "Slot", new { parentId = CreationOwnershipFixture.ParentId }, async () =>
            {
                sends++;
                _ = await client.CreateSlotAsync(new SlotCreateRequest(CreationOwnershipFixture.ParentId, "Room"));
                client.MarkNextMutationResponseUnknown();
                throw new RLoopException("SDK_SEND_FAILED", "response lost", ExitCodes.OperationFailed);
            }, _ => Task.FromResult(false)));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal(1, sends);
        var persisted = CreationStateStore.Load(statePath, state.CreationId, true)!.Pending!;
        Assert.Equal("unknown", persisted.Steps[^1].SendStatus);
        Assert.False(persisted.Steps[^1].ResponseReceived);
        Assert.False(persisted.Steps[^1].Confirmed);
        Assert.Equal(new[] { "create-slot" }, client.SendLog);
        await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync("creatingRoot", "create-root", null,
            CreationOwnershipFixture.ParentId, "Slot", new { parentId = CreationOwnershipFixture.ParentId }, () =>
            {
                sends++;
                return Task.FromResult("unexpected");
            }, _ => Task.FromResult(true)));
        Assert.Equal(1, sends);
    }

    private static CreationState PendingState()
    {
        var state = CreationOwnershipFixture.State();
        var previous = state.ActiveRoot!;
        var pending = new CreationPending("receipt-operation", "prepared",
            new CreationScope("creation", null, previous.Id, null, previous.ParentId), "delete-first", state.Session,
            "generation", null, previous.InputHash, null, previous,
            new CreationPendingRoot(null, previous.ParentId, CreationStamp.CreateGenerationId(),
                CreationStamp.Create(state.CreationId, state.OwnerNonce).Serialize(), previous.InputHash, null), [], null);
        return state with { Pending = pending };
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

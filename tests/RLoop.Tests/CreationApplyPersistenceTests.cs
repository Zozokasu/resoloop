using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-persistence-" + Guid.NewGuid().ToString("N"));
    private readonly string statePath;

    public CreationApplyPersistenceTests()
    {
        Directory.CreateDirectory(root);
        statePath = Path.Combine(root, "state.json");
    }

    [Fact]
    public async Task IntentSaveFailureStopsBeforeMutationAndKeepsRecoveryContext()
    {
        var state = CreationOwnershipFixture.State();
        var previous = state.ActiveRoot!;
        var pending = new CreationPending("operation-a", "prepared",
            new CreationScope("creation", null, previous.Id, null, previous.ParentId), "delete-first", state.Session,
            "generation", null, previous.InputHash, null, previous, null, [], null);
        state = state with { Pending = pending };
        CreationStateStore.Save(statePath, state);
        var client = new CreationMutationTestClient();
        var transaction = new CreationTransaction(client, statePath, state, "generation", CancellationToken.None);
        var sends = 0;
        CreationTransaction.SaveFault.Value = (_, _) => throw new IOException("injected save fault");
        try
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => transaction.SendAsync("deletingPrevious", "delete-slot",
                previous.Id, previous.ParentId, null, new { targetId = previous.Id }, () =>
                {
                    sends++;
                    return Task.FromResult(previous.Id);
                }, _ => Task.FromResult(true)));
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
            Assert.Equal("deletingPrevious", error.Context["stage"]);
            Assert.Equal("operation-a", error.Context["operationId"]);
            Assert.Equal(previous.Id, error.Context["previousRootId"]);
            Assert.Equal(0, sends);
            Assert.Equal("prepared", CreationStateStore.Load(statePath, state.CreationId, true)!.Pending!.Stage);
        }
        finally { CreationTransaction.SaveFault.Value = null; }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

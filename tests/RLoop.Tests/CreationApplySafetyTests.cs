using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplySafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-safety-" + Guid.NewGuid().ToString("N"));

    public CreationApplySafetyTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task SessionLeaseBlocksASecondStateWhenPreviousCreationIsPending()
    {
        var previousPath = Path.Combine(root, "previous.json");
        var nextPath = Path.Combine(root, "next.json");
        var state = CreationOwnershipFixture.State();
        var previous = state.ActiveRoot!;
        var pending = new CreationPending("operation-a", "deletingPrevious",
            new CreationScope("creation", null, previous.Id, null, previous.ParentId), "delete-first", state.Session,
            "generation", null, previous.InputHash, null, previous, null, [], null);
        CreationStateStore.Save(previousPath, state with { Pending = pending });
        var lockDirectory = Path.Combine(root, "locks");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, lockDirectory);
        using (var first = SessionWriteLock.Acquire(state.Session.NormalizedUrl, lockDirectory))
            first.RecordState(previousPath);
        var error = await Assert.ThrowsAsync<RLoopException>(async () =>
            await SessionWriteLock.AcquireAsync(client, nextPath, CancellationToken.None));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("previousCreationStatePending", error.Context["reason"]);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task RootCanNeverBeTheExactDeletionTarget()
    {
        var statePath = Path.Combine(root, "root-owner.json");
        var lockDirectory = Path.Combine(root, "root-owner-locks");
        var state = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, lockDirectory);
        state = state with
        {
            Session = new CreationSession(ApplySessionObservation.NormalizeUrl(CreationTestClient.Url), "S-test", "matched"),
            ActiveRoot = state.ActiveRoot! with { Id = "Root" }
        };
        CreationStateStore.Save(statePath, state);
        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(
                """{"schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"room","definition":{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[]},"diagnostics":[]}"""),
                new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test")));
        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Empty(client.SendLog);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

internal static class CreationRecoveryTestData
{
    internal const string SessionId = CreationTestClient.SessionId;
    internal static readonly string Root = Path.Combine(Path.GetTempPath(), "resoloop-recover-" + Guid.NewGuid().ToString("N"));
    internal static string PathFor(string name) { Directory.CreateDirectory(Root); return Path.Combine(Root, name + ".json"); }
    internal static CreationActiveRoot Candidate(string id = "candidate-root", string generation = CreationOwnershipFixture.GenerationId) =>
        new(id, CreationOwnershipFixture.ParentId, generation,
            new CreationStamp(1, CreationOwnershipFixture.CreationId, CreationOwnershipFixture.OwnerNonce, generation).Serialize(),
            CreationOwnershipFixture.InputHash, null);
    internal static CreationPending Pending(string stage = "creatingTree", CreationActiveRoot? candidate = null,
        CreationActiveRoot? previous = null, JsonElement? evidence = null, IReadOnlyList<CreationStep>? steps = null) =>
        new("operation-4", stage, new CreationScope("creation", null, previous?.Id, candidate?.Id, CreationOwnershipFixture.ParentId),
            "delete-first", CreationOwnershipFixture.State().Session, "old-process-generation", null,
            CreationOwnershipFixture.InputHash, null, previous,
            candidate is null ? null : new(candidate.Id, candidate.ParentId, candidate.GenerationId, candidate.Stamp, candidate.InputHash, candidate.BuildId),
            steps ?? [], evidence);
    internal static CreationState State(CreationPending pending, CreationActiveRoot? active = null) =>
        CreationOwnershipFixture.State() with { ActiveRoot = active, Pending = pending };
    internal static CreationMutationTestClient Client() => new() { SessionUrl = CreationTestClient.Url, SessionId = SessionId };
}

public sealed class CreationRecoveryTests
{
    [Fact]
    public async Task RollbackClearsActiveRootOnlyAfterExactOldAndCandidateAbsence()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(RollbackClearsActiveRootOnlyAfterExactOldAndCandidateAbsence));
        var previous = CreationOwnershipFixture.State().ActiveRoot!;
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending("deletingPrevious", candidate, previous), previous);
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();

        await new CreationRecoveryService(client).RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true));

        var final = CreationStateStore.Load(path, state.CreationId, true)!;
        Assert.Null(final.ActiveRoot);
        Assert.Null(final.Pending);
        Assert.Empty(client.SendLog);
        Assert.Contains(previous.Id, client.DeletionObservationIds);
        Assert.Contains(candidate.Id, client.DeletionObservationIds);
    }

    [Fact]
    public async Task RollbackKeepsAnExactlyOwnedPreviousRootWhenItStillExists()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(RollbackKeepsAnExactlyOwnedPreviousRootWhenItStillExists));
        var previous = CreationOwnershipFixture.State().ActiveRoot!;
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending("deletingPrevious", previous: previous), previous);
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state, previous.Stamp);

        var result = await new CreationRecoveryService(client).RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true));

        Assert.Equal(previous.Id, result.ActiveRootId);
        Assert.Equal(previous.Id, CreationStateStore.Load(path, state.CreationId, true)!.ActiveRoot!.Id);
        Assert.Null(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task RollbackDeletesOnlyAnExactlyOwnedCandidateAndClearsPending()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(RollbackDeletesOnlyAnExactlyOwnedCandidateAndClearsPending));
        var root = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: root));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = root }, root.Stamp);

        var result = await new CreationRecoveryService(client).RecoverAsync(new(path, "rollback", CreationRecoveryTestData.SessionId, true));

        Assert.Equal("rollback", result.Action);
        Assert.Null(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
        Assert.Equal("delete-slot:" + root.Id, Assert.Single(client.SendLog));
        var final = CreationStateStore.Load(path, state.CreationId, true)!;
        Assert.Null(final.ActiveRoot);
        Assert.Null(final.Pending);
    }
}

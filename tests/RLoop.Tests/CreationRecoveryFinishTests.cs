using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationRecoveryFinishTests
{
    [Fact]
    public async Task FinishAcceptsStagedExactCandidateOnlyWhenTreeEvidenceAndCountsMatch()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(FinishAcceptsStagedExactCandidateOnlyWhenTreeEvidenceAndCountsMatch));
        var candidate = CreationRecoveryTestData.Candidate();
        var evidence = JsonSerializer.SerializeToElement(new { treeComplete = true, expectedCounts = new { slots = 1, components = 1 } });
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending("staged", candidate, evidence: evidence));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, candidate.Stamp);

        var result = await new CreationRecoveryService(client).RecoverAsync(new(path, "finish", CreationRecoveryTestData.SessionId, true));

        Assert.Equal(candidate.Id, result.ActiveRootId);
        var final = CreationStateStore.Load(path, state.CreationId, true)!;
        Assert.Equal(candidate.Id, final.ActiveRoot!.Id);
        Assert.Null(final.Pending);
        Assert.Empty(client.SendLog);
    }

    [Theory]
    [InlineData("creatingTree", "{\"treeComplete\":true,\"expectedCounts\":{\"slots\":1,\"components\":1}}")]
    [InlineData("staged", "{\"treeComplete\":false,\"expectedCounts\":{\"slots\":1,\"components\":1}}")]
    [InlineData("staged", "{\"treeComplete\":true,\"expectedCounts\":{\"slots\":2,\"components\":1}}")]
    [InlineData("staged", "{\"treeComplete\":true,\"expectedCounts\":{\"slots\":\"1\",\"components\":1}}")]
    [InlineData("staged", "{\"treeComplete\":true,\"expectedCounts\":{\"slots\":1,\"components\":null}}")]
    public async Task IncompleteStageOrCountEvidenceRetainsPendingWithoutWorldWrites(string stage, string rawEvidence)
    {
        var path = CreationRecoveryTestData.PathFor(nameof(IncompleteStageOrCountEvidenceRetainsPendingWithoutWorldWrites) + stage + rawEvidence.Length);
        var candidate = CreationRecoveryTestData.Candidate();
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(stage, candidate,
            evidence: JsonDocument.Parse(rawEvidence).RootElement.Clone()));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, candidate.Stamp);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationRecoveryService(client)
            .RecoverAsync(new(path, "finish", CreationRecoveryTestData.SessionId, true)));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("operation-4", CreationStateStore.Load(path, state.CreationId, true)!.Pending!.OperationId);
        Assert.Empty(client.SendLog);
    }
}

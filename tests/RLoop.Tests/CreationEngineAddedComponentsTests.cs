using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationEngineAddedComponentsTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-engine-added-" + Guid.NewGuid().ToString("N"));

    public CreationEngineAddedComponentsTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task MoreComponentsThanExpectedSucceedAndReportTheExtraCount(int extra)
    {
        var client = ClientWithComponents(2 + extra);

        var added = await CreationTreeVerification.VerifyAsync(client, "root-created", new CreationPlanCounts(1, 2), CancellationToken.None);

        Assert.Equal(extra, added);
    }

    [Fact]
    public async Task ExactComponentCountReportsZeroExtra()
    {
        var added = await CreationTreeVerification.VerifyAsync(ClientWithComponents(2), "root-created",
            new CreationPlanCounts(1, 2), CancellationToken.None);

        Assert.Equal(0, added);
    }

    [Fact]
    public async Task FewerComponentsThanExpectedFail()
    {
        var error = await Assert.ThrowsAsync<RLoopException>(() => CreationTreeVerification.VerifyAsync(
            ClientWithComponents(1), "root-created", new CreationPlanCounts(1, 2), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
    }

    [Fact]
    public async Task OneMoreSlotFails()
    {
        var client = ClientWithComponents(0);
        var child = new SlotInfo("child-1", "Child", "root-created", null, null, null, true, true, null, false, [], []);
        client.Slots[child.Id] = child;
        client.Slots["root-created"] = client.Slots["root-created"] with { Children = [child] };

        var error = await Assert.ThrowsAsync<RLoopException>(() => CreationTreeVerification.VerifyAsync(
            client, "root-created", new CreationPlanCounts(1, 0), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
    }

    [Fact]
    public async Task OneFewerSlotFails()
    {
        var error = await Assert.ThrowsAsync<RLoopException>(() => CreationTreeVerification.VerifyAsync(
            ClientWithComponents(0), "root-created", new CreationPlanCounts(2, 0), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
    }

    [Fact]
    public async Task ApplySucceedsWhenTheEngineAddsComponentsAndReportsTheCount()
    {
        var statePath = Path.Combine(root, "apply.json");
        var client = new CreationMutationTestClient { EngineAddedComponentsPerAdd = 2 };
        SessionLockTestIsolation.Share(client, Path.Combine(root, "apply-locks"));

        var result = await new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test",
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

        Assert.Equal(2, result.EngineAddedComponents);
        Assert.Contains("\"engineAddedComponents\":2", JsonSerializer.Serialize(result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), StringComparison.Ordinal);
        var state = CreationStateStore.Load(statePath, "room", true)!;
        Assert.Null(state.Pending);
        Assert.Equal("slot-1", state.ActiveRoot!.Id);
    }

    [Fact]
    public async Task FinishAcceptsExtraComponentsAndReportsTheCount()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(FinishAcceptsExtraComponentsAndReportsTheCount));
        var candidate = CreationRecoveryTestData.Candidate();
        var evidence = JsonSerializer.SerializeToElement(new { treeComplete = true, expectedCounts = new { slots = 1, components = 0 } });
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending("staged", candidate, evidence: evidence));
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state with { ActiveRoot = candidate }, candidate.Stamp);

        var result = await new CreationRecoveryService(client).RecoverAsync(new(path, "finish", CreationRecoveryTestData.SessionId, true));

        Assert.True(result.EngineAddedComponents > 0);
        Assert.Null(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
    }

    private static CreationMutationTestClient ClientWithComponents(int count)
    {
        var client = new CreationMutationTestClient();
        client.Slots["root-created"] = client.Slots["root-created"] with
        {
            Components = Enumerable.Range(1, count).Select(n => new ComponentSummary("c-" + n, "Example.Component")).ToArray()
        };
        return client;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-apply-" + Guid.NewGuid().ToString("N"));
    private const string Envelope = """{"schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"room","definition":{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[]},"diagnostics":[]}""";

    public CreationApplyTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task YesIsRequiredBeforeAnySessionObservationOrSend()
    {
        var client = new CreationMutationTestClient();
        var document = CreationDocument.Parse(Envelope);
        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).ApplyAsync(document,
            new CreationApplyOptions(StateFile: Path.Combine(root, "state.json"))));
        Assert.Equal("CONFIRMATION_REQUIRED", error.Code);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.SlotReadIds);
    }

    [Fact]
    public async Task PlanCountsGeneratedSourceMarkerAndReservedStamp()
    {
        var client = new CreationMutationTestClient();
        var document = CreationDocument.Parse(Envelope);
        var result = await new CreationApplyService(client).PlanAsync(document,
            new CreationPlanOptions(StateFile: Path.Combine(root, "plan-counts-state.json"),
                GeneratedContentSource: "[resoloop test]"));
        Assert.Equal(2, result.Counts.Components);
    }

    [Fact]
    public async Task FreshApplyPersistsOwnershipOnlyAfterTreeVerification()
    {
        var statePath = Path.Combine(root, "fresh-state.json");
        var lockDirectory = Path.Combine(root, "locks");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, lockDirectory);
        var result = await new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test",
                GeneratedContentSource: "[resoloop test]"));
        var state = CreationStateStore.Load(statePath, "room", true)!;
        Assert.Equal(new[] { "create-slot", "add-component", "add-component" }, client.SendLog);
        Assert.Null(state.Pending);
        Assert.Equal(result.RootId, state.ActiveRoot!.Id);
        Assert.Equal("staged", result.Phase);
        Assert.Equal(2, result.Counts.Components);
        Assert.False(result.Atomic);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

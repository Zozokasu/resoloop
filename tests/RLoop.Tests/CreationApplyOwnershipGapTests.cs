using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyOwnershipGapTests : IDisposable
{
    private const string FixtureSessionId = "S-creation-test";
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-owner-gap-" + Guid.NewGuid().ToString("N"));

    public CreationApplyOwnershipGapTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task RecordedGenerationMismatchSendsNothing()
    {
        var original = CreationOwnershipFixture.State();
        var state = original with
        { ActiveRoot = original.ActiveRoot! with { GenerationId = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" } };
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(original, original.ActiveRoot!.Stamp);
        var error = await ApplyAndAssertNoSends(client, state, "generation");

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
    }

    [Fact]
    public async Task DuplicateStampComponentOnRootSendsNothing()
    {
        var state = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        var duplicate = CreationOwnershipFixture.StampComponent("duplicate-stamp", state.ActiveRoot.Stamp);
        client.Components[duplicate.Id] = duplicate;
        var slot = client.Slots[state.ActiveRoot.Id];
        client.Slots[state.ActiveRoot.Id] = slot with
        { Components = slot.Components.Append(CreationOwnershipFixture.Summary(duplicate)).ToArray() };
        var error = await ApplyAndAssertNoSends(client, state, "duplicate-stamp");

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
    }

    [Theory]
    [InlineData("rootComponents")]
    [InlineData("childChildren")]
    public async Task MissingListsSendNothing(string missingList)
    {
        var state = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        client.AddChild(state.ActiveRoot.Id, new SlotInfo("owned-child", "Child", state.ActiveRoot.Id,
            null, null, null, true, true, null, false, [], []));
        client.SlotReadOverride = (slot, _) => missingList switch
        {
            "rootComponents" when slot.Id == state.ActiveRoot.Id => slot with { Components = null! },
            "childChildren" when slot.Id == "owned-child" => slot with { Children = null! },
            _ => slot
        };
        var error = await ApplyAndAssertNoSends(client, state, missingList);

        Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
    }

    [Theory]
    [InlineData("sessionId")]
    [InlineData("sessionUrl")]
    public async Task DifferentSessionSendsNothing(string changedSession)
    {
        var state = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        if (changedSession == "sessionId") client.SessionId = "S-other";
        else client.SessionUrl = "ws://other:1/";
        var error = await ApplyAndAssertNoSends(client, state, changedSession);

        Assert.Equal("CREATION_SESSION_UNPROVEN", error.Code);
    }

    private async Task<RLoopException> ApplyAndAssertNoSends(CreationMutationTestClient client,
        CreationState state, string name)
    {
        var statePath = Path.Combine(root, name + ".json");
        CreationStateStore.Save(statePath, state);
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-" + name));
        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath));
        Assert.Empty(client.SendLog);
        Assert.Empty(client.RequestHistory);
        return error;
    }

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string statePath,
        string requestedSessionId = FixtureSessionId) =>
        new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: requestedSessionId,
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    public void Dispose() => Directory.Delete(root, recursive: true);
}

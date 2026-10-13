using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyReplacementTests : IDisposable
{
    private const string FixtureSessionId = "S-creation-test";
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private const string InputHash = "8d44fcd6c980f46af1b961f08206e2272290ff32115d6a286174dad7fe93e1b0";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-replacement-" + Guid.NewGuid().ToString("N"));

    public CreationApplyReplacementTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task FreshApplyUsesExplicitSourceAndPersistsVerifiedActiveRoot()
    {
        var client = new CreationMutationTestClient();
        var statePath = Path.Combine(root, "fresh.json");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-fresh"));
        var result = await Apply(client, statePath);
        var state = CreationStateStore.Load(statePath, "room", true)!;

        Assert.Equal(InputHash, result.InputHash);
        Assert.Equal("slot-1", result.RootId);
        Assert.Equal("staged", result.Phase);
        Assert.False(result.Atomic);
        Assert.Equal(1, result.Counts.Slots);
        Assert.Equal(2, result.Counts.Components);
        Assert.Equal(new[] { "create-slot", "add-component", "add-component" }, client.SendLog);
        Assert.Contains(client.AddRequests, request => request.SlotId == "slot-1" &&
            request.Type == GeneratedContentMetadata.ComponentType && request.Fields[GeneratedContentMetadata.SourceMember] == "[resoloop test]");
        Assert.Equal("slot-1", state.ActiveRoot!.Id);
        Assert.Null(state.Pending);
        Assert.Equal(InputHash, state.ActiveRoot.InputHash);
    }

    [Fact]
    public async Task ReplacementDeletesExactOldRootBeforeCreateEvenWhenInputHashIsUnchanged()
    {
        var statePath = Path.Combine(root, "replacement.json");
        var prior = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(prior, prior.ActiveRoot!.Stamp);
        CreationStateStore.Save(statePath, prior);
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-replace"));

        var result = await Apply(client, statePath, FixtureSessionId);

        Assert.Equal(InputHash, result.InputHash);
        Assert.Equal(CreationOwnershipFixture.RootId, result.PreviousRootId);
        Assert.Equal("slot-1", result.RootId);
        Assert.True(result.Replaced);
        Assert.Equal(new[] { "delete-slot:created-root", "create-slot:parent-slot:Room" }, client.RequestHistory.Take(2));
        Assert.Equal("slot-1", client.AddRequests[0].SlotId);
        Assert.Equal(CreationOwnershipVerifier.VariableComponentType, client.AddRequests[0].Type);
        Assert.Equal(CreationOwnershipVerifier.VariableName, client.AddRequests[0].Fields["VariableName"]);
        Assert.Equal(CreationOwnershipFixture.OwnerNonce,
            CreationStamp.Parse(client.AddRequests[0].Fields["Value"]).OwnerNonce);
        Assert.Equal("delete-slot:created-root", client.SendLog[0]);
        Assert.Equal("create-slot", client.SendLog[1]);
        Assert.DoesNotContain("created-root", client.Slots.Keys);
        Assert.Equal("slot-1", CreationStateStore.Load(statePath, "room", true)!.ActiveRoot!.Id);
        Assert.Null(CreationStateStore.Load(statePath, "room", true)!.Pending);
    }

    [Fact]
    public async Task SameHashSecondReplacementStillDeletesOldExactIdFirstAndKeepsOwnerNonce()
    {
        var statePath = Path.Combine(root, "same-hash.json");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-same"));
        await Apply(client, statePath);
        var first = CreationStateStore.Load(statePath, "room", true)!;
        client.SendLog.Clear();
        client.RequestHistory.Clear();
        client.SeedOwnedRoot(first, first.ActiveRoot!.Stamp);

        var second = await Apply(client, statePath);

        var final = CreationStateStore.Load(statePath, "room", true)!;
        Assert.Equal(first.ActiveRoot!.InputHash, second.InputHash);
        Assert.Equal(first.OwnerNonce, final.OwnerNonce);
        Assert.Equal("delete-slot:" + first.ActiveRoot.Id, client.SendLog[0]);
        Assert.Equal("create-slot", client.SendLog[1]);
        Assert.NotEqual(first.ActiveRoot.GenerationId, final.ActiveRoot!.GenerationId);
        Assert.Null(final.Pending);
    }

    [Fact]
    public async Task ReplacementRechecksOwnershipAfterPreparationBeforeDeletingOldRoot()
    {
        var statePath = Path.Combine(root, "stamp-removed-during-prepare.json");
        var prior = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        client.RegisterType("Probe");
        client.SeedOwnedRoot(prior, prior.ActiveRoot!.Stamp);
        CreationStateStore.Save(statePath, prior);
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-stamp-removed"));
        client.BeforeDescribeComponentType = type =>
        {
            if (type != "Probe") return;
            client.Slots[prior.ActiveRoot.Id] = client.Slots[prior.ActiveRoot.Id] with
            {
                Components = []
            };
            client.Components.Remove("seed-stamp");
        };
        var document = CreationDocument.Parse(Envelope.Replace("\"components\":[]",
            "\"components\":[{\"type\":\"Probe\",\"key\":\"probe\"}]", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).ApplyAsync(document,
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: FixtureSessionId,
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test"))));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Empty(client.SendLog);
        Assert.Contains(prior.ActiveRoot.Id, client.Slots.Keys);
        Assert.Null(CreationStateStore.Load(statePath, "room", true)!.Pending);
    }

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string statePath,
        string requestedSessionId = "S-test") =>
        new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: requestedSessionId,
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    public void Dispose() => Directory.Delete(root, recursive: true);
}

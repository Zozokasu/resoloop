using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationRootSameNameTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-root-name-" + Guid.NewGuid().ToString("N"));

    public CreationRootSameNameTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task FreshApplyDoesNotSendToUnownedSameNamedRootChild()
    {
        var client = new CreationMutationTestClient();
        client.AddChild("Root", new SlotInfo("unowned-room", "Room", "Root", null, null, null,
            true, true, null, false, [], []));
        var statePath = Path.Combine(root, "fresh.json");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-fresh"));

        await Apply(client, statePath, "S-test");

        Assert.Equal(new[] { "create-slot", "add-component", "add-component" }, client.SendLog);
        Assert.Equal("create-slot:Root:Room", client.RequestHistory[0]);
        Assert.Equal(2, client.AddRequests.Count);
        Assert.Equal("slot-1", client.AddRequests[0].SlotId);
        Assert.Equal(CreationOwnershipVerifier.VariableComponentType, client.AddRequests[0].Type);
        Assert.Equal(CreationOwnershipVerifier.VariableName, client.AddRequests[0].Fields["VariableName"]);
        Assert.Equal("slot-1", client.AddRequests[1].SlotId);
        Assert.Equal(GeneratedContentMetadata.ComponentType, client.AddRequests[1].Type);
        Assert.Equal("[resoloop test]", client.AddRequests[1].Fields[GeneratedContentMetadata.SourceMember]);
        AssertNoMutationTargetsUnownedRoom(client);
        Assert.Contains("unowned-room", client.Slots);
        Assert.Equal("Root", client.Slots["unowned-room"].ParentId);
    }

    [Fact]
    public async Task ReplacementDoesNotSendToUnownedSameNamedChildOfActiveParent()
    {
        var prior = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(prior, prior.ActiveRoot!.Stamp);
        client.AddChild(CreationOwnershipFixture.ParentId, new SlotInfo("unowned-room", "Room",
            CreationOwnershipFixture.ParentId, null, null, null, true, true, null, false, [], []));
        var statePath = Path.Combine(root, "replacement.json");
        CreationStateStore.Save(statePath, prior);
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-replace"));

        await Apply(client, statePath, "S-creation-test");

        Assert.Equal(new[] { "delete-slot:created-root", "create-slot", "add-component", "add-component" }, client.SendLog);
        Assert.Equal(new[] { "delete-slot:created-root", "create-slot:parent-slot:Room" }, client.RequestHistory.Take(2));
        Assert.Equal(new[] { "delete-slot:created-root" }, client.RequestHistory.Where(value => value.StartsWith("delete-slot:", StringComparison.Ordinal)));
        Assert.Equal(2, client.AddRequests.Count);
        Assert.Equal("slot-1", client.AddRequests[0].SlotId);
        Assert.Equal(CreationOwnershipVerifier.VariableComponentType, client.AddRequests[0].Type);
        Assert.Equal(CreationOwnershipVerifier.VariableName, client.AddRequests[0].Fields["VariableName"]);
        Assert.Equal("slot-1", client.AddRequests[1].SlotId);
        Assert.Equal(GeneratedContentMetadata.ComponentType, client.AddRequests[1].Type);
        Assert.Equal("[resoloop test]", client.AddRequests[1].Fields[GeneratedContentMetadata.SourceMember]);
        AssertNoMutationTargetsUnownedRoom(client);
        Assert.Contains("unowned-room", client.Slots);
        Assert.Equal(CreationOwnershipFixture.ParentId, client.Slots["unowned-room"].ParentId);
    }

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string statePath,
        string requestedSessionId) =>
        new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: requestedSessionId,
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    private static void AssertNoMutationTargetsUnownedRoom(CreationMutationTestClient client)
    {
        Assert.DoesNotContain(client.RequestHistory, value => value.Contains("unowned-room", StringComparison.Ordinal));
        Assert.DoesNotContain(client.AddRequests, request => request.SlotId == "unowned-room");
        Assert.DoesNotContain(client.SetRequests, request => request.ComponentId.Contains("unowned-room", StringComparison.Ordinal));
        Assert.DoesNotContain(client.RequestHistory.Where(value => value.StartsWith("delete-slot:", StringComparison.Ordinal)),
            value => value.Contains("unowned-room", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

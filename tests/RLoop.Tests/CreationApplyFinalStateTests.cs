using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyFinalStateTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private const string Hash = "8d44fcd6c980f46af1b961f08206e2272290ff32115d6a286174dad7fe93e1b0";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-final-" + Guid.NewGuid().ToString("N"));

    public CreationApplyFinalStateTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task CompleteSuccessJsonHasStableFieldOrderAndExactFixedValues()
    {
        var statePath = Path.Combine(root, "success.json");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, Path.Combine(root, "success-locks"));
        var result = await Apply(client, statePath);
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var actual = JsonSerializer.Serialize(result, jsonOptions);
        var expected = "{\"resultVersion\":\"1\",\"mode\":\"regenerate\",\"creationId\":\"room\",\"stateFile\":" +
            JsonSerializer.Serialize(statePath) + ",\"buildId\":null,\"inputHash\":\"" + Hash +
            "\",\"previousRootId\":null,\"rootId\":\"slot-1\",\"replaced\":false,\"definitionsReset\":true,\"replaceOrder\":\"delete-first\",\"phase\":\"staged\",\"counts\":{\"slots\":1,\"components\":2},\"pending\":null,\"evidence\":{\"overall\":\"complete\",\"session\":\"complete\",\"exactId\":\"complete\",\"parent\":\"complete\",\"rootStamp\":\"complete\",\"siblingUniqueness\":\"complete\",\"descendantBoundary\":\"complete\"},\"diagnostics\":[],\"atomic\":false,\"engineAddedComponents\":0}";

        Assert.Equal(expected, actual);
        Assert.Equal("slot-1", CreationStateStore.Load(statePath, "room", true)!.ActiveRoot!.Id);
        Assert.Null(CreationStateStore.Load(statePath, "room", true)!.Pending);
    }

    [Fact]
    public async Task PartialFinalObservationLeavesStagedPendingAndNeverClaimsSuccess()
    {
        var statePath = Path.Combine(root, "partial-final.json");
        var client = new CreationMutationTestClient();
        client.AfterAddComponent = (_, type, _) =>
        {
            if (type == GeneratedContentMetadata.ComponentType) client.IncompleteSlotId = "slot-1";
        };
        SessionLockTestIsolation.Share(client, Path.Combine(root, "partial-locks"));

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath));

        var pending = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("staged", pending.Stage);
        Assert.True(pending.Evidence!.Value.GetProperty("treeComplete").GetBoolean());
        Assert.Null(CreationStateStore.Load(statePath, "room", true)!.ActiveRoot);
        Assert.Equal(new[] { "create-slot", "add-component", "add-component" }, client.SendLog);
    }

    [Fact]
    public async Task FinalStructureExtraSlotRetainsStagedPendingAndNeverClaimsSuccess()
    {
        var statePath = Path.Combine(root, "count-mismatch.json");
        var client = new CreationMutationTestClient();
        client.AfterAddComponent = (slotId, type, _) =>
        {
            if (type != GeneratedContentMetadata.ComponentType) return;
            var slot = client.Slots[slotId];
            var extra = new SlotInfo("unexpected-slot", "Extra", slotId, null, null, null, true, true, null, false, [], []);
            client.Slots[extra.Id] = extra;
            client.Slots[slotId] = slot with { Children = slot.Children.Append(extra).ToArray() };
        };
        SessionLockTestIsolation.Share(client, Path.Combine(root, "count-mismatch-locks"));

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath));

        var persisted = CreationStateStore.Load(statePath, "room", true)!;
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Contains("expected 1 Slots (exact) and at least 2 Components, observed 2 Slots/2 Components", error.Message, StringComparison.Ordinal);
        Assert.Equal("staged", persisted.Pending!.Stage);
        Assert.True(persisted.Pending.Evidence!.Value.GetProperty("treeComplete").GetBoolean());
        Assert.Null(persisted.ActiveRoot);
        Assert.Equal(new[] { "create-slot", "add-component", "add-component" }, client.SendLog);
    }

    [Fact]
    public async Task TreeSuccessPersistsOnlyTheCreatingTreeBoundaryBeforeStaged()
    {
        var statePath = Path.Combine(root, "tree-boundaries.json");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, Path.Combine(root, "tree-boundary-locks"));
        var savedTreeStages = new List<string>();
        CreationTransaction.SaveFault.Value = (_, state) =>
        {
            if (state.Pending is { } pending && pending.Stage is "creatingTree" or "staged")
                savedTreeStages.Add(pending.Stage);
        };
        try
        {
            const string nested = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[{\"slot\":{\"key\":\"child\",\"name\":\"Child\"},\"components\":[]}]},\"diagnostics\":[]}";
            await new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(nested),
                new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test",
                    GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));
        }
        finally { CreationTransaction.SaveFault.Value = null; }

        Assert.Equal(new[] { "creatingTree", "staged" }, savedTreeStages);
        Assert.Contains("create-slot", client.SendLog);
    }

    [Fact]
    public async Task TreeReadbackFailureRetainsLastAcceptedSendEvidenceInCreatingTree()
    {
        var statePath = Path.Combine(root, "tree-readback.json");
        var client = new CreationMutationTestClient
        {
            SlotReadOverride = (slot, _) => slot.Name == "Child" ? slot with { Name = "Mismatch" } : slot
        };
        SessionLockTestIsolation.Share(client, Path.Combine(root, "tree-readback-locks"));
        const string nested = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[{\"slot\":{\"key\":\"child\",\"name\":\"Child\"},\"components\":[]}]},\"diagnostics\":[]}";

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).ApplyAsync(
            CreationDocument.Parse(nested), new CreationApplyOptions(StateFile: statePath, Yes: true,
                RequestedSessionId: "S-test", GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test"))));

        var pending = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("creatingTree", pending.Stage);
        Assert.Equal("accepted", pending.Evidence!.Value.GetProperty("lastTreeSend").GetString());
        Assert.Equal(new[] { "create-slot", "add-component", "create-slot" }, client.SendLog);
        Assert.Equal("create-slot:Root:Room", client.RequestHistory[0]);
        Assert.Equal("create-slot:slot-1:Child", client.RequestHistory[2]);
        Assert.Equal("slot-1", Assert.Single(client.AddRequests).SlotId);
        Assert.DoesNotContain(client.AddRequests, request => request.Type == GeneratedContentMetadata.ComponentType);
    }

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string statePath) =>
        new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test",
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    public void Dispose() => Directory.Delete(root, recursive: true);
}

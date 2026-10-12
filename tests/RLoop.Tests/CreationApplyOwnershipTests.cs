using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyOwnershipTests : IDisposable
{
    private const string FixtureSessionId = "S-creation-test";
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-owner-" + Guid.NewGuid().ToString("N"));

    public CreationApplyOwnershipTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("stamp")]
    [InlineData("parent")]
    [InlineData("duplicate-sibling")]
    [InlineData("foreign-descendant")]
    [InlineData("partial")]
    [InlineData("session")]
    public async Task OwnershipAndEvidenceFailuresStopBeforeAnyMutation(string fault)
    {
        var state = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        var originalParentId = state.ActiveRoot!.ParentId;
        var stamp = state.ActiveRoot!.Stamp;
        if (fault == "stamp") stamp = (CreationStamp.Parse(stamp) with
            { GenerationId = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" }).Serialize();
        if (fault == "session") client.SessionIdentityStatus = "unknown";
        if (fault == "partial") client.SlotReadOverride = (slot, _) => slot.Id == state.ActiveRoot!.Id ? slot with { Children = null! } : slot;
        if (fault == "parent")
        {
            state = state with { ActiveRoot = state.ActiveRoot! with { ParentId = "other-parent" } };
            client.AddChild("Root", new SlotInfo("other-parent", "Other", "Root", null, null, null, true, true, null, false, [], []));
        }
        client.SeedOwnedRoot(state, stamp, originalParentId);
        if (fault == "duplicate-sibling")
        {
            var duplicate = new CreationStamp(1, "room", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                state.ActiveRoot!.GenerationId).Serialize();
            AddMarker(client, "same-generation-sibling", state.ActiveRoot!.ParentId, duplicate);
        }
        if (fault == "foreign-descendant")
        {
            var foreign = new CreationStamp(1, "other-room", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff").Serialize();
            AddMarker(client, "foreign-child", state.ActiveRoot!.Id, foreign);
        }
        var statePath = Path.Combine(root, fault + ".json");
        CreationStateStore.Save(statePath, state);
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-" + fault));

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath, FixtureSessionId));

        Assert.Equal(fault == "partial" ? "CREATION_OBSERVATION_INCOMPLETE" :
            fault == "session" ? "CREATION_SESSION_UNPROVEN" :
            fault == "duplicate-sibling" ? "CREATION_OWNER_AMBIGUOUS" : "CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.RequestHistory);
    }

    [Fact]
    public async Task ConnectionGenerationChangeImmediatelyBeforeCreateStopsBeforeSend()
    {
        var client = new CreationMutationTestClient();
        var statePath = Path.Combine(root, "generation.json");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-generation"));
        client.ChangeGenerationBeforeWriteGuard(2, "changed-generation");

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath, "S-test"));

        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.RequestHistory);
    }

    [Fact]
    public async Task RejectedOldDeletePreservesKnownRejectionAndStopsReplacement()
    {
        var state = CreationOwnershipFixture.State();
        var statePath = Path.Combine(root, "delete-rejected.json");
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        client.RejectNextResponse();
        SessionLockTestIsolation.Share(client, Path.Combine(root, "delete-rejected-locks"));
        CreationStateStore.Save(statePath, state);

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath, FixtureSessionId));

        var pending = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("TEST_REJECTED", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal("deletingPrevious", pending.Stage);
        Assert.Equal("rejected", pending.Steps[^1].SendStatus);
        Assert.True(pending.Steps[^1].ResponseReceived);
        Assert.False(pending.Steps[^1].ResponseAccepted);
        Assert.False(pending.Steps[^1].Confirmed);
        Assert.Equal(new[] { "delete-slot:" + state.ActiveRoot!.Id }, client.RequestHistory);
        Assert.Contains(state.ActiveRoot.Id, client.Slots.Keys);
    }

    [Fact]
    public async Task GenerationChangeAtReplacementDeleteGuardPreventsDeleteAndCreate()
    {
        var state = CreationOwnershipFixture.State();
        var statePath = Path.Combine(root, "delete-generation.json");
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(state, state.ActiveRoot!.Stamp);
        client.ChangeGenerationBeforeWriteGuard(2, "changed-generation");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "delete-generation-locks"));
        CreationStateStore.Save(statePath, state);

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath, FixtureSessionId));

        var pending = CreationStateStore.Load(statePath, "room", true)!.Pending!;
        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal("deletingPrevious", pending.Stage);
        Assert.Equal("intent", pending.Steps[^1].SendStatus);
        Assert.Empty(client.RequestHistory);
        Assert.Contains(state.ActiveRoot!.Id, client.Slots.Keys);
    }

    private static void AddMarker(CreationMutationTestClient client, string id, string parentId, string raw)
    {
        var componentId = id + "-stamp";
        client.Components[componentId] = new ComponentInfo(componentId, CreationOwnershipVerifier.VariableComponentType,
            new Dictionary<string, MemberValue>(StringComparer.Ordinal)
            {
                ["VariableName"] = new MemberValue("field", Value: JsonValue.Create(CreationOwnershipVerifier.VariableName)),
                ["Value"] = new MemberValue("field", Value: JsonValue.Create(raw))
            });
        client.AddChild(parentId, new SlotInfo(id, id, parentId, null, null, null, true, true, null, false,
            [new ComponentSummary(componentId, CreationOwnershipVerifier.VariableComponentType)], []));
    }

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string statePath,
        string requestedSessionId = FixtureSessionId) =>
        new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: requestedSessionId,
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    public void Dispose() => Directory.Delete(root, recursive: true);
}

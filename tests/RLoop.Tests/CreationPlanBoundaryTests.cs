using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationPlanBoundaryTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-boundary-" + Guid.NewGuid().ToString("N"));

    public CreationPlanBoundaryTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task RequireStateAndPendingFailBeforeAnySessionOrWorldObservation()
    {
        var missingClient = InitialClient();
        var missingPath = Path.Combine(root, "required-missing.json");
        var missing = await Assert.ThrowsAsync<RLoopException>(() => Plan(missingClient, missingPath, requireState: true));
        Assert.Equal("CREATION_STATE_REQUIRED", missing.Code);
        Assert.Equal(ExitCodes.NotFound, missing.ExitCode);
        Assert.Empty(missingClient.SlotReads);
        Assert.Equal(0, missingClient.SessionReads);

        var pendingClient = InitialClient();
        var statePath = Path.Combine(root, "pending.json");
        var current = CreationOwnershipFixture.State();
        var pending = new CreationPending("op-pending", "prepared", new("creation", null, null, null), "delete-first",
            current.Session, null, null, null, null, null, null, [], null);
        CreationStateStore.Save(statePath, current with { Pending = pending });
        var before = File.ReadAllBytes(statePath);
        var error = await Assert.ThrowsAsync<RLoopException>(() => Plan(pendingClient, statePath));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal(ExitCodes.OperationFailed, error.ExitCode);
        Assert.Equal("op-pending", error.Context["operationId"]);
        Assert.Equal(statePath, error.Context["stateFile"]);
        Assert.Equal(before, File.ReadAllBytes(statePath));
        Assert.Empty(pendingClient.SlotReads);
        Assert.Equal(0, pendingClient.SessionReads);
    }

    [Fact]
    public async Task ExistingDirectCreationStampRequiresItsStateInsteadOfAdoptingTheSlot()
    {
        var client = InitialClient();
        var component = CreationOwnershipFixture.StampComponent("existing-stamp", CreationOwnershipFixture.Stamp.Serialize());
        client.AddComponent(component);
        var child = CreationOwnershipFixture.Slot("existing-root", CreationOwnershipFixture.ParentId,
            [CreationOwnershipFixture.Summary(component)]);
        client.AddSlot(child);
        var parent = client.Slots[CreationOwnershipFixture.ParentId];
        client.Slots[CreationOwnershipFixture.ParentId] = parent with
        { Slot = parent.Slot with { Children = [child] } };

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).PlanAsync(
            CreationDocument.Parse(Envelope), new CreationPlanOptions(Path.Combine(root, "absent.json"),
                Parent: CreationOwnershipFixture.ParentId, RequestedSessionId: CreationTestClient.SessionId)));

        Assert.Equal("CREATION_STATE_REQUIRED", error.Code);
        Assert.Equal(ExitCodes.NotFound, error.ExitCode);
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(0, client.GuardWriteCalls);
    }

    [Theory]
    [InlineData("S-other", "CREATION_SESSION_UNPROVEN")]
    [InlineData("S-requested", "CREATION_SESSION_UNPROVEN")]
    public async Task RequiresMatchedRequestedAndRecordedSessionIdentity(string requested, string code)
    {
        var client = InitialClient();
        var document = CreationDocument.Parse(Envelope);
        var requestedError = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).PlanAsync(document,
                new CreationPlanOptions(Path.Combine(root, "requested.json"), RequestedSessionId: requested)));
        Assert.Equal(code, requestedError.Code);

        var state = CreationOwnershipFixture.State() with
        { Session = CreationOwnershipFixture.State().Session! with { NormalizedUrl = "ws://other:47610/" } };
        var statePath = Path.Combine(root, "wrong-session.json");
        CreationStateStore.Save(statePath, state with { ActiveRoot = null });
        var recordedError = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(InitialClient()).PlanAsync(document,
                new CreationPlanOptions(statePath, RequestedSessionId: CreationTestClient.SessionId)));
        Assert.Equal(code, recordedError.Code);
    }

    [Fact]
    public async Task ExplicitParentMustEqualRecordedExactParentForReplace()
    {
        var client = CreationOwnershipFixture.Client();
        var other = CreationOwnershipFixture.Slot("other-parent", "Root");
        client.AddSlot(other);
        var statePath = Path.Combine(root, "replace.json");
        CreationStateStore.Save(statePath, CreationOwnershipFixture.State());

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope),
                new CreationPlanOptions(statePath, Parent: "other-parent", RequestedSessionId: CreationTestClient.SessionId)));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal(0, client.MutationCalls);
    }

    private Task<CreationPlanResult> Plan(CreationTestClient client, string statePath, bool requireState = false) =>
        new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope),
            new CreationPlanOptions(statePath, requireState, RequestedSessionId: CreationTestClient.SessionId));

    private static CreationTestClient InitialClient()
    {
        var client = new CreationTestClient();
        client.AddSlot(CreationOwnershipFixture.Slot("Root", null, [],
            [CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "Root")]));
        client.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "Root"));
        return client;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

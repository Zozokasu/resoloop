using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationPlanObservationTests
{
    [Fact]
    public async Task SharedContextDoesNotResetSlotLimitAcrossInitialAndReplacementVerification()
    {
        var client = new CreationTestClient();
        var marker = CreationOwnershipFixture.StampComponent("stamp-component", CreationOwnershipFixture.Stamp.Serialize());
        client.AddComponent(marker);
        var root = CreationOwnershipFixture.Slot(CreationOwnershipFixture.RootId, CreationOwnershipFixture.ParentId,
            [CreationOwnershipFixture.Summary(marker)]);
        client.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "world"));
        client.AddSlot(root);
        var verifier = new CreationOwnershipVerifier(client, new CreationOwnershipOptions(MaxSlots: 2));
        var context = verifier.CreateContext();

        var initial = await verifier.ObserveInitialParentAsync(CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId, context);
        Assert.Equal(CreationEvidenceCompleteness.Complete, initial.Completeness);
        var parentObservation = client.Slots[CreationOwnershipFixture.ParentId];
        client.Slots[CreationOwnershipFixture.ParentId] = parentObservation with
        { Slot = parentObservation.Slot with { Children = [root] } };
        var error = await Assert.ThrowsAsync<RLoopException>(() => verifier.VerifyAsync(CreationOwnershipFixture.State(), context));

        Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal(new[] { CreationOwnershipFixture.ParentId, CreationOwnershipFixture.ParentId }, client.SlotReads);
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(0, client.GuardWriteCalls);
    }

    [Fact]
    public async Task ParentPathRejectsFullChildWhoseParentDiffersEvenWhenItsNameDoesNotMatch()
    {
        var client = new CreationTestClient();
        var summary = CreationOwnershipFixture.Slot("child", "Root") with { Name = "not-requested" };
        client.AddSlot(CreationOwnershipFixture.Slot("Root", null, [], [summary]));
        client.AddSlot(CreationOwnershipFixture.Slot("child", "different-parent") with { Name = "not-requested" });

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope()),
                new CreationPlanOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                    Parent: "Root/wanted", RequestedSessionId: CreationTestClient.SessionId)));

        Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal(new[] { "Root", "child" }, client.SlotReads);
    }

    [Fact]
    public async Task ParentPathRequiresOneExactNameMatchAtEachObservedLevel()
    {
        var client = new CreationTestClient();
        var first = CreationOwnershipFixture.Slot("branch-a", "Root") with { Name = "branch" };
        var second = CreationOwnershipFixture.Slot("branch-b", "Root") with { Name = "branch" };
        client.AddSlot(CreationOwnershipFixture.Slot("Root", null, [], [first, second]));
        client.AddSlot(first);
        client.AddSlot(second);

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope()),
                new CreationPlanOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                    Parent: "Root/branch", RequestedSessionId: CreationTestClient.SessionId)));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task ParentAncestryRejectsAStampedCreationBoundary()
    {
        var client = new CreationTestClient();
        var otherStamp = new CreationStamp(1, "other-creation", CreationOwnershipFixture.OwnerNonce,
            CreationOwnershipFixture.GenerationId).Serialize();
        var component = CreationOwnershipFixture.StampComponent("ancestor-stamp", otherStamp);
        client.AddComponent(component);
        var parent = CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "Root",
            [CreationOwnershipFixture.Summary(component)]);
        client.AddSlot(CreationOwnershipFixture.Slot("Root", null, [], [parent]));
        client.AddSlot(parent);

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope()),
                new CreationPlanOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                    Parent: CreationOwnershipFixture.ParentId, RequestedSessionId: CreationTestClient.SessionId)));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Contains("creation stamp", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task ParentAncestryBeyondDepthLimitIsIncompleteRatherThanUnproven()
    {
        var client = new CreationTestClient();
        client.AddSlot(CreationOwnershipFixture.Slot("Root", null));
        for (var index = 64; index >= 0; index--)
        {
            var parentId = index == 64 ? "Root" : "ancestor-" + (index + 1);
            client.AddSlot(CreationOwnershipFixture.Slot("ancestor-" + index, parentId));
        }

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope()),
                new CreationPlanOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                    Parent: "ancestor-0", RequestedSessionId: CreationTestClient.SessionId)));

        Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Contains("ancestor-64", client.SlotReads);
        Assert.DoesNotContain("Root", client.SlotReads);
    }

    [Fact]
    public async Task ParentAncestryCycleIsOwnerUnproven()
    {
        var client = new CreationTestClient();
        client.AddSlot(CreationOwnershipFixture.Slot("parent-a", "parent-b"));
        client.AddSlot(CreationOwnershipFixture.Slot("parent-b", "parent-a"));

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope()),
                new CreationPlanOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                    Parent: "parent-a", RequestedSessionId: CreationTestClient.SessionId)));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal(new[] { "parent-a", "parent-a", "parent-a", "parent-b" }, client.SlotReads);
    }

    private const string Definition = "\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]}";
    private static string Envelope() => "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\"," + Definition + ",\"diagnostics\":[]}";
}

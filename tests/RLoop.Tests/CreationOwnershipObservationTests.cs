using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationOwnershipObservationTests
{
    [Fact]
    public async Task InitialParentReturnsCompleteNoMatchOrRequiresExistingState()
    {
        var empty = new CreationTestClient();
        empty.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "world"));
        var evidence = await new CreationOwnershipVerifier(empty).ObserveInitialParentAsync(
            CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId);
        Assert.Equal(CreationEvidenceCompleteness.Complete, evidence.Completeness);
        Assert.Equal("complete-no-match", evidence.StampStatus);
        Assert.Equal(0, empty.MutationCalls);

        var existing = CreationOwnershipFixture.Client();
        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationOwnershipVerifier(existing)
            .ObserveInitialParentAsync(CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId));
        Assert.Equal("CREATION_STATE_REQUIRED", error.Code);
        Assert.Equal(ExitCodes.NotFound, error.ExitCode);
        Assert.Equal(0, existing.MutationCalls);
    }

    [Fact]
    public async Task RejectsMatchingSiblingGenerationAndAnyOwnedDescendant()
    {
        var sibling = CreationOwnershipFixture.Client();
        AddSibling(sibling, "other-root", new CreationStamp(1, CreationOwnershipFixture.CreationId,
            new string('d', 64), CreationOwnershipFixture.GenerationId));
        await AssertCode("CREATION_OWNER_AMBIGUOUS", () => new CreationOwnershipVerifier(sibling)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var anotherCreation = CreationOwnershipFixture.Client();
        AddDescendant(anotherCreation, new CreationStamp(1, "another-room", new string('d', 64), new string('e', 64)));
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(anotherCreation)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var sameGeneration = CreationOwnershipFixture.Client();
        AddDescendant(sameGeneration, new CreationStamp(1, CreationOwnershipFixture.CreationId,
            new string('d', 64), CreationOwnershipFixture.GenerationId));
        await AssertCode("CREATION_OWNER_AMBIGUOUS", () => new CreationOwnershipVerifier(sameGeneration)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var laterGeneration = CreationOwnershipFixture.Client();
        AddDescendant(laterGeneration, new CreationStamp(1, CreationOwnershipFixture.CreationId,
            new string('d', 64), new string('e', 64)));
        var laterEvidence = await new CreationOwnershipVerifier(laterGeneration).VerifyAsync(CreationOwnershipFixture.State());
        Assert.Equal(CreationEvidenceCompleteness.Complete, laterEvidence.Completeness);

        var handAdded = CreationOwnershipFixture.Client();
        AddDescendant(handAdded, null);
        var handAddedEvidence = await new CreationOwnershipVerifier(handAdded).VerifyAsync(CreationOwnershipFixture.State());
        Assert.Equal(CreationEvidenceCompleteness.Complete, handAddedEvidence.Completeness);
        Assert.Equal(0, sibling.MutationCalls + anotherCreation.MutationCalls + sameGeneration.MutationCalls);
        Assert.Equal(0, laterGeneration.MutationCalls + handAdded.MutationCalls);
    }

    [Fact]
    public async Task ReflectionMetadataMustNameExactTypeAndBothMembers()
    {
        var wrongType = CreationOwnershipFixture.Client();
        wrongType.StampMetadata = wrongType.StampMetadata with { FullTypeName = "DynamicValueVariable<string>" };
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(wrongType)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Empty(wrongType.SlotReads);

        var missingVariableName = CreationOwnershipFixture.Client();
        missingVariableName.StampMetadata = missingVariableName.StampMetadata with
        { Members = missingVariableName.StampMetadata.Members.Where(member => member.Name != "VariableName").ToArray() };
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(missingVariableName)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var missingValue = CreationOwnershipFixture.Client();
        missingValue.StampMetadata = missingValue.StampMetadata with
        { Members = missingValue.StampMetadata.Members.Where(member => member.Name != "Value").ToArray() };
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(missingValue)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.All(new[] { wrongType, missingVariableName, missingValue }, client => Assert.Equal(0, client.MutationCalls));
    }

    [Fact]
    public async Task PartialCoverageUnknownObserverAndUnreadableValueStayIncomplete()
    {
        var partialChildren = CreationOwnershipFixture.Client();
        partialChildren.Slots[CreationOwnershipFixture.ParentId] = partialChildren.Slots[CreationOwnershipFixture.ParentId] with
        { ChildrenObserved = false };
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(partialChildren)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var partialComponents = CreationOwnershipFixture.Client();
        partialComponents.Slots[CreationOwnershipFixture.RootId] = partialComponents.Slots[CreationOwnershipFixture.RootId] with
        { ComponentsObserved = false };
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(partialComponents)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var referenceOnlyChild = CreationOwnershipFixture.Client();
        var parent = referenceOnlyChild.Slots[CreationOwnershipFixture.ParentId];
        referenceOnlyChild.Slots[CreationOwnershipFixture.ParentId] = parent with
        { Slot = parent.Slot with { Children = [parent.Slot.Children[0] with { IsReferenceOnly = true }] } };
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(referenceOnlyChild)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var inner = CreationOwnershipFixture.Client();
        var noObserver = new CreationClientWithoutDeletionObservation(inner);
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(noObserver)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Equal(0, inner.GuardWriteCalls);

        var unreadable = CreationOwnershipFixture.Client();
        var unknownValue = CreationOwnershipFixture.StampComponent("stamp-component", null, value: null);
        ReplaceRoot(unreadable, [unknownValue]);
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(unreadable)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var unreadableMember = CreationOwnershipFixture.Client();
        var missingValue = CreationOwnershipFixture.StampComponent("stamp-component", null, omitValueMember: true);
        ReplaceRoot(unreadableMember, [missingValue]);
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(unreadableMember)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Contains("stamp-component", unreadable.ComponentReads);
        Assert.Contains("stamp-component", unreadableMember.ComponentReads);
        Assert.Equal(0, partialChildren.MutationCalls + partialComponents.MutationCalls + referenceOnlyChild.MutationCalls +
            inner.MutationCalls + unreadable.MutationCalls + unreadableMember.MutationCalls);
    }

    [Fact]
    public async Task SessionMustBeProvenAtStartAndEndAndConnectionGenerationMustHold()
    {
        var unknown = CreationOwnershipFixture.Client();
        unknown.IdentityRead = _ => new(ApplySessionObservation.NormalizeUrl(CreationTestClient.Url), CreationTestClient.SessionId, "unknown");
        await AssertCode("CREATION_SESSION_UNPROVEN", () => new CreationOwnershipVerifier(unknown).VerifyAsync(CreationOwnershipFixture.State()));

        var mismatchedId = CreationOwnershipFixture.Client();
        mismatchedId.IdentityRead = _ => new(ApplySessionObservation.NormalizeUrl(CreationTestClient.Url), "S-other", "matched");
        await AssertCode("CREATION_SESSION_UNPROVEN", () => new CreationOwnershipVerifier(mismatchedId).VerifyAsync(CreationOwnershipFixture.State()));

        var explicitUrl = CreationOwnershipFixture.Client();
        var otherUrlState = CreationOwnershipFixture.State() with
        { Session = CreationOwnershipFixture.State().Session with { NormalizedUrl = "ws://other.invalid/" } };
        await AssertCode("CREATION_SESSION_UNPROVEN", () => new CreationOwnershipVerifier(explicitUrl).VerifyAsync(otherUrlState));

        var ended = CreationOwnershipFixture.Client();
        ended.SessionRead = read => read == 1
            ? new SessionInfo(CreationTestClient.Url, true, null, null, null)
            : new SessionInfo(CreationTestClient.Url, false, null, null, null);
        await AssertCode("CREATION_SESSION_UNPROVEN", () => new CreationOwnershipVerifier(ended).VerifyAsync(CreationOwnershipFixture.State()));

        var identityChanged = CreationOwnershipFixture.Client();
        identityChanged.IdentityRead = read => read == 1
            ? new(ApplySessionObservation.NormalizeUrl(CreationTestClient.Url), CreationTestClient.SessionId, "matched")
            : new(ApplySessionObservation.NormalizeUrl(CreationTestClient.Url), CreationTestClient.SessionId, "unknown");
        await AssertCode("CREATION_SESSION_UNPROVEN", () => new CreationOwnershipVerifier(identityChanged).VerifyAsync(CreationOwnershipFixture.State()));

        var generationChanged = CreationOwnershipFixture.Client();
        generationChanged.GuardRead = read => new(true, read == 1 ? "connection-a" : "connection-b");
        await AssertCode("CREATION_SESSION_UNPROVEN", () => new CreationOwnershipVerifier(generationChanged).VerifyAsync(CreationOwnershipFixture.State()));
        Assert.All(new[] { unknown, mismatchedId, explicitUrl, ended, identityChanged, generationChanged }, client => Assert.Equal(0, client.MutationCalls));
    }

    [Fact]
    public async Task SmallBudgetsBoundParentRootComponentsDepthQueueAndTime()
    {
        var slots = CreationOwnershipFixture.Client();
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(slots,
            new CreationOwnershipOptions(MaxSlots: 1)).VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Single(slots.SlotReads);

        var sharedSlots = CreationOwnershipFixture.Client();
        AddSibling(sharedSlots, "unmarked-sibling", new CreationStamp(1, "other-room", new string('d', 64), new string('e', 64)));
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(sharedSlots,
            new CreationOwnershipOptions(MaxSlots: 2)).VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Single(sharedSlots.SlotReads);

        var components = CreationOwnershipFixture.Client();
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(components,
            new CreationOwnershipOptions(MaxComponents: 0)).VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Empty(components.ComponentReads);

        var depth = CreationOwnershipFixture.Client();
        AddDescendant(depth, null);
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(depth,
            new CreationOwnershipOptions(MaxDepth: 0)).VerifyAsync(CreationOwnershipFixture.State()));

        var queue = CreationOwnershipFixture.Client();
        AddTwoDescendants(queue);
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(queue,
            new CreationOwnershipOptions(MaxQueuedSlots: 1)).VerifyAsync(CreationOwnershipFixture.State()));
        Assert.DoesNotContain("descendant-one", queue.SlotReads);

        var deadline = CreationOwnershipFixture.Client();
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(deadline,
            new CreationOwnershipOptions(MaxDuration: TimeSpan.Zero)).VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Equal(0, slots.MutationCalls + sharedSlots.MutationCalls + components.MutationCalls + depth.MutationCalls + queue.MutationCalls + deadline.MutationCalls);
    }

    [Fact]
    public async Task CancellationAndAdapterTimeoutPropagateUnchanged()
    {
        var canceled = CreationOwnershipFixture.Client();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CreationOwnershipVerifier(canceled)
            .VerifyAsync(CreationOwnershipFixture.State(), cancellation.Token));

        var timeout = CreationOwnershipFixture.Client();
        timeout.SlotRead = (_, _) => Task.FromException<ApplyDeletionObservation>(
            new RLoopException("REQUEST_TIMEOUT", "read timed out", ExitCodes.Timeout));
        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationOwnershipVerifier(timeout)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Equal("REQUEST_TIMEOUT", error.Code);
        Assert.Equal(ExitCodes.Timeout, error.ExitCode);
    }

    private static void AddSibling(CreationTestClient client, string id, CreationStamp stamp)
    {
        var component = CreationOwnershipFixture.StampComponent(id + "-stamp", stamp.Serialize());
        client.AddComponent(component);
        var slot = CreationOwnershipFixture.Slot(id, CreationOwnershipFixture.ParentId, [CreationOwnershipFixture.Summary(component)]);
        client.AddSlot(slot);
        var parent = client.Slots[CreationOwnershipFixture.ParentId];
        client.Slots[CreationOwnershipFixture.ParentId] = parent with { Slot = parent.Slot with { Children = parent.Slot.Children.Append(slot).ToArray() } };
    }

    private static void AddDescendant(CreationTestClient client, CreationStamp? stamp)
    {
        IReadOnlyList<ComponentSummary> summaries = [];
        if (stamp is not null)
        {
            var component = CreationOwnershipFixture.StampComponent("descendant-stamp", stamp.Serialize());
            client.AddComponent(component);
            summaries = [CreationOwnershipFixture.Summary(component)];
        }
        var descendant = CreationOwnershipFixture.Slot("descendant-one", CreationOwnershipFixture.RootId, summaries);
        var root = client.Slots[CreationOwnershipFixture.RootId];
        client.Slots[CreationOwnershipFixture.RootId] = root with { Slot = root.Slot with { Children = [descendant] } };
        client.AddSlot(descendant);
    }

    private static void AddTwoDescendants(CreationTestClient client)
    {
        var first = CreationOwnershipFixture.Slot("descendant-one", CreationOwnershipFixture.RootId);
        var second = CreationOwnershipFixture.Slot("descendant-two", CreationOwnershipFixture.RootId);
        var root = client.Slots[CreationOwnershipFixture.RootId];
        client.Slots[CreationOwnershipFixture.RootId] = root with { Slot = root.Slot with { Children = [first, second] } };
        client.AddSlot(first);
        client.AddSlot(second);
    }

    private static void ReplaceRoot(CreationTestClient client, IReadOnlyList<ComponentInfo> components)
    {
        foreach (var component in components) client.ReplaceComponent(component);
        var root = client.Slots[CreationOwnershipFixture.RootId];
        client.Slots[CreationOwnershipFixture.RootId] = root with
        { Slot = root.Slot with { Components = components.Select(CreationOwnershipFixture.Summary).ToArray() } };
    }

    private static async Task AssertCode(string code, Func<Task<CreationOwnershipEvidence>> action)
    {
        var error = await Assert.ThrowsAsync<RLoopException>(action);
        Assert.Equal(code, error.Code);
        Assert.Equal(code == "CREATION_STATE_REQUIRED" ? ExitCodes.NotFound : ExitCodes.ValidationFailed, error.ExitCode);
    }
}

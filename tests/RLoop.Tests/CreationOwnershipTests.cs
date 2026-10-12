using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationOwnershipTests
{
    [Fact]
    public async Task VerifiesExactStampAndUsesDiscoveredSessionAndRuntimeReflection()
    {
        var client = CreationOwnershipFixture.Client();

        var evidence = await new CreationOwnershipVerifier(client).VerifyAsync(CreationOwnershipFixture.State());

        Assert.Equal(CreationEvidenceCompleteness.Complete, evidence.Completeness);
        Assert.Equal("verified", evidence.SessionStatus);
        Assert.Equal("exact", evidence.ExactIdStatus);
        Assert.Equal("verified", evidence.ParentStatus);
        Assert.Equal("verified", evidence.StampStatus);
        Assert.Equal("unique", evidence.SiblingUniquenessStatus);
        Assert.Equal("bounded-and-closed", evidence.DescendantBoundaryStatus);
        Assert.Equal(new[] { CreationOwnershipFixture.ParentId, CreationOwnershipFixture.RootId }, client.SlotReads);
        Assert.Equal(new[] { CreationOwnershipVerifier.VariableComponentType }, client.TypeReads);
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(0, client.GuardWriteCalls);
        Assert.Equal(0, client.ConnectCalls);
    }

    [Fact]
    public async Task FakeKeepsAddStrictAndCountsOptionalMutationBoundaries()
    {
        var client = CreationOwnershipFixture.Client();
        Assert.Throws<ArgumentException>(() => client.AddComponent(client.Components["stamp-component"]));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await client.CallComponentMethodAsync("component", "method"); });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await client.ImportAssetAsync(null!, "source"); });
        Assert.Throws<InvalidOperationException>(() => client.GuardApplyWrites(null));
        Assert.Equal(2, client.MutationCalls);
        Assert.Equal(1, client.GuardWriteCalls);
    }

    [Theory]
    [InlineData("{\"version\":1,\"creationId\":\"room\",\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"} ")]
    [InlineData("{\"version\":1,\"creationId\":\"room\",\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"generationId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbc\"}")]
    public async Task RequiresObservedStampToMatchStateAsAnExactOrdinalString(string rawStamp)
    {
        var client = CreationOwnershipFixture.Client();
        ReplaceRootMarker(client, [CreationOwnershipFixture.StampComponent("stamp-component", rawStamp)]);

        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(client)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Contains("stamp-component", client.ComponentReads);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task RejectsDuplicateAndWrongTypeReservedStampComponents()
    {
        var duplicateClient = CreationOwnershipFixture.Client();
        var first = CreationOwnershipFixture.StampComponent("stamp-component", CreationOwnershipFixture.Stamp.Serialize());
        var second = CreationOwnershipFixture.StampComponent("stamp-component-2", CreationOwnershipFixture.Stamp.Serialize());
        ReplaceRootMarker(duplicateClient, [first, second]);
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(duplicateClient)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var wrongTypeClient = CreationOwnershipFixture.Client();
        var wrongType = CreationOwnershipFixture.StampComponent("wrong-type", CreationOwnershipFixture.Stamp.Serialize(),
            type: "[FrooxEngine]FrooxEngine.DynamicValueVariable<float>");
        ReplaceRootMarker(wrongTypeClient, [wrongType]);
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(wrongTypeClient)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Equal(0, duplicateClient.MutationCalls + wrongTypeClient.MutationCalls);
    }

    [Fact]
    public async Task RequiresExactParentExistingRootAndNonRootId()
    {
        var parentClient = CreationOwnershipFixture.Client();
        var emptyParent = CreationOwnershipFixture.Slot("other-parent", "world");
        parentClient.AddSlot(emptyParent);
        var wrongParentState = CreationOwnershipFixture.State() with
        {
            ActiveRoot = CreationOwnershipFixture.State().ActiveRoot! with { ParentId = "other-parent" }
        };
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(parentClient).VerifyAsync(wrongParentState));

        var missingRootClient = CreationOwnershipFixture.Client();
        var parent = missingRootClient.Slots[CreationOwnershipFixture.ParentId].Slot;
        var missingRoot = CreationOwnershipFixture.Slot("missing-root", CreationOwnershipFixture.ParentId);
        missingRootClient.Slots[CreationOwnershipFixture.ParentId] = new(parent with { Children = [missingRoot] }, true, true);
        var missingState = CreationOwnershipFixture.State() with
        {
            ActiveRoot = CreationOwnershipFixture.State().ActiveRoot! with { Id = "missing-root" }
        };
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(missingRootClient).VerifyAsync(missingState));

        var rootState = CreationOwnershipFixture.State() with
        {
            ActiveRoot = CreationOwnershipFixture.State().ActiveRoot! with { Id = "rOoT" }
        };
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(CreationOwnershipFixture.Client()).VerifyAsync(rootState));
    }

    [Fact]
    public async Task RejectsUnreadableValueAndInvalidStateStampWithoutMutation()
    {
        var client = CreationOwnershipFixture.Client();
        ReplaceRootMarker(client, [CreationOwnershipFixture.StampComponent("stamp-component", null, value: null)]);
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(client)
            .VerifyAsync(CreationOwnershipFixture.State()));

        var mismatched = new CreationStamp(1, "other", CreationOwnershipFixture.OwnerNonce, CreationOwnershipFixture.GenerationId);
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(CreationOwnershipFixture.Client())
            .VerifyAsync(CreationOwnershipFixture.State(mismatched)));
        Assert.Equal(0, client.MutationCalls);
    }

    [Theory]
    [InlineData(null, "matched", "CREATION_SESSION_UNPROVEN")]
    [InlineData("not-a-session", "matched", "CREATION_SESSION_UNPROVEN")]
    [InlineData("S-valid", "unknown", "CREATION_SESSION_UNPROVEN")]
    public async Task InvalidStateSessionIsClassifiedSeparately(string? sessionId, string status, string expectedCode)
    {
        var state = CreationOwnershipFixture.State();
        state = state with
        {
            Session = state.Session! with { IdentityStatus = status, DiscoverSessionId = sessionId }
        };
        var client = CreationOwnershipFixture.Client();
        await AssertCode(expectedCode, () => new CreationOwnershipVerifier(client).VerifyAsync(state));
        Assert.Empty(client.TypeReads);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task InvalidStateOwnerStampRemainsOwnerUnprovenAfterValidSession()
    {
        var state = CreationOwnershipFixture.State() with { OwnerNonce = "invalid" };
        var client = CreationOwnershipFixture.Client();
        await AssertCode("CREATION_OWNER_UNPROVEN", () => new CreationOwnershipVerifier(client).VerifyAsync(state));
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task MissingStateSessionIsSessionUnproven()
    {
        var state = CreationOwnershipFixture.State() with { Session = null! };
        var client = CreationOwnershipFixture.Client();
        await AssertCode("CREATION_SESSION_UNPROVEN", () => new CreationOwnershipVerifier(client).VerifyAsync(state));
        Assert.Empty(client.TypeReads);
    }

    [Fact]
    public async Task UnknownMemberSetIsIncompleteAtInitialChildSiblingAndDescendantWhileEmptySetIsKnownAbsence()
    {
        var initial = new CreationTestClient();
        var childComponent = new ComponentInfo("child-unknown", "OtherType", null!);
        initial.AddComponent(childComponent);
        var child = CreationOwnershipFixture.Slot("initial-child", CreationOwnershipFixture.ParentId,
            [CreationOwnershipFixture.Summary(childComponent)]);
        initial.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "world", [], [child]));
        initial.AddSlot(child);
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(initial)
            .ObserveInitialParentAsync(CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId));
        Assert.Contains("child-unknown", initial.ComponentReads);

        var sibling = CreationOwnershipFixture.Client();
        AddUnknownMemberChild(sibling, CreationOwnershipFixture.ParentId, "sibling-unknown");
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(sibling)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Contains("sibling-unknown-component", sibling.ComponentReads);

        var descendant = CreationOwnershipFixture.Client();
        AddUnknownMemberChild(descendant, CreationOwnershipFixture.RootId, "descendant-unknown");
        await AssertCode("CREATION_OBSERVATION_INCOMPLETE", () => new CreationOwnershipVerifier(descendant)
            .VerifyAsync(CreationOwnershipFixture.State()));
        Assert.Contains("descendant-unknown-component", descendant.ComponentReads);

        var knownAbsence = CreationOwnershipFixture.Client();
        var emptyMembers = new ComponentInfo("known-empty", "OtherType", new Dictionary<string, MemberValue>());
        knownAbsence.AddComponent(emptyMembers);
        var root = knownAbsence.Slots[CreationOwnershipFixture.RootId];
        knownAbsence.Slots[CreationOwnershipFixture.RootId] = root with
        {
            Slot = root.Slot with { Components = root.Slot.Components.Append(CreationOwnershipFixture.Summary(emptyMembers)).ToArray() }
        };
        var evidence = await new CreationOwnershipVerifier(knownAbsence).VerifyAsync(CreationOwnershipFixture.State());
        Assert.Equal(CreationEvidenceCompleteness.Complete, evidence.Completeness);
        Assert.Equal(0, initial.MutationCalls + sibling.MutationCalls + descendant.MutationCalls + knownAbsence.MutationCalls);
    }

    private static void AddUnknownMemberChild(CreationTestClient client, string parentId, string childId)
    {
        var component = new ComponentInfo(childId + "-component", "OtherType", null!);
        client.AddComponent(component);
        var child = CreationOwnershipFixture.Slot(childId, parentId, [CreationOwnershipFixture.Summary(component)]);
        client.AddSlot(child);
        var parent = client.Slots[parentId];
        client.Slots[parentId] = parent with { Slot = parent.Slot with { Children = parent.Slot.Children.Append(child).ToArray() } };
    }

    private static void ReplaceRootMarker(CreationTestClient client, IReadOnlyList<ComponentInfo> components)
    {
        foreach (var component in components) client.ReplaceComponent(component);
        var prior = client.Slots[CreationOwnershipFixture.RootId];
        var summaries = components.Select(CreationOwnershipFixture.Summary).ToArray();
        client.Slots[CreationOwnershipFixture.RootId] = prior with
        {
            Slot = prior.Slot with { Components = summaries }
        };
    }

    private static async Task AssertCode(string code, Func<Task<CreationOwnershipEvidence>> action)
    {
        var error = await Assert.ThrowsAsync<RLoopException>(action);
        Assert.Equal(code, error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
    }
}

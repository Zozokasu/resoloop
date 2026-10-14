using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyTwiceTests : IDisposable
{
    private const string FixtureSessionId = "S-test";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-twice-" + Guid.NewGuid().ToString("N"));

    public CreationApplyTwiceTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task SameDefinitionTwiceLeavesOneOwnedRootWithDefinedValues()
    {
        var client = NewClient();
        var statePath = Path.Combine(root, "same.json");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-same"));
        var document = Document("fixed-value");

        await Apply(client, statePath, document);
        await Apply(client, statePath, document);

        var final = CreationStateStore.Load(statePath, "room", true)!;
        var owned = OwnedRoots(client, final);
        Assert.Single(owned);
        Assert.Equal(final.ActiveRoot!.Id, owned.Single().Id);
        Assert.Equal("fixed-value", DefinedValue(client, owned.Single()));
    }

    [Fact]
    public async Task ChangedDefinitionSecondTimeLeavesOneOwnedRootWithNewValues()
    {
        var client = NewClient();
        var statePath = Path.Combine(root, "changed.json");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-changed"));
        var firstDocument = Document("first-value");
        await Apply(client, statePath, firstDocument);
        var firstRootId = CreationStateStore.Load(statePath, "room", true)!.ActiveRoot!.Id;

        await Apply(client, statePath, Document("second-value"));

        var final = CreationStateStore.Load(statePath, "room", true)!;
        var owned = OwnedRoots(client, final);
        Assert.Single(owned);
        Assert.Equal(final.ActiveRoot!.Id, owned.Single().Id);
        Assert.Equal("second-value", DefinedValue(client, owned.Single()));
        Assert.DoesNotContain(firstRootId, client.Slots.Keys);
    }

    [Fact]
    public async Task UnownedSameNamedSiblingSurvivesTwoApplies()
    {
        var client = NewClient();
        var sibling = new SlotInfo("unowned-room", "Room", "Root", null, null, null,
            true, true, null, false, [], []);
        client.AddChild("Root", sibling);
        var statePath = Path.Combine(root, "unowned.json");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-unowned"));
        var document = Document("value");

        await Apply(client, statePath, document);
        await Apply(client, statePath, document);

        Assert.Contains("unowned-room", client.Slots.Keys);
        Assert.Equal("Root", client.Slots["unowned-room"].ParentId);
        var final = CreationStateStore.Load(statePath, "room", true)!;
        var owned = OwnedRoots(client, final);
        Assert.Single(owned);
        Assert.Equal(final.ActiveRoot!.Id, owned.Single().Id);
    }

    [Fact]
    public async Task SameNamedSiblingsTwiceLeaveOneOwnedRoot()
    {
        var client = NewClient();
        var statePath = Path.Combine(root, "same-named-children.json");
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-same-named"));
        var document = Document("root-value", """
            {"slot":{"key":"first","name":"Lamp"},"components":[],"children":[]},
            {"slot":{"key":"second","name":"Lamp"},"components":[],"children":[]},
            {"slot":{"key":"third","name":"Lamp"},"components":[],"children":[]}
            """);

        await Apply(client, statePath, document);
        await Apply(client, statePath, document);

        var final = CreationStateStore.Load(statePath, "room", true)!;
        var owned = OwnedRoots(client, final);
        Assert.Single(owned);
        Assert.Equal(final.ActiveRoot!.Id, owned.Single().Id);
        Assert.Equal(3, owned.Single().Children.Count);
        Assert.All(owned.Single().Children, child => Assert.Equal("Lamp", child.Name));
    }

    private static CreationMutationTestClient NewClient()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("Probe", "Value:System.String");
        return client;
    }

    private static CreationDocument Document(string value, string children = "") => CreationDocument.Parse(
        "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[{\"type\":\"Probe\",\"key\":\"probe\",\"fields\":{\"Value\":\"" + value + "\"}}],\"children\":[" + children + "]},\"diagnostics\":[]}");

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string statePath,
        CreationDocument document) =>
        new CreationApplyService(client).ApplyAsync(document,
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: FixtureSessionId,
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    private static SlotInfo[] OwnedRoots(CreationMutationTestClient client, CreationState state) =>
        client.Slots[state.ActiveRoot!.ParentId].Children
        .Select(child => client.Slots.GetValueOrDefault(child.Id))
        .Where(slot => slot is not null && slot.Components.Any(summary =>
            summary.Type == CreationOwnershipVerifier.VariableComponentType &&
            client.Components.TryGetValue(summary.Id, out var component) &&
            component.Members.TryGetValue("VariableName", out var name) &&
            name.Value?.GetValue<string>() == CreationOwnershipVerifier.VariableName))
        .Select(slot => slot!)
        .ToArray();

    private static string? DefinedValue(CreationMutationTestClient client, SlotInfo rootSlot)
    {
        var componentSummary = Assert.Single(rootSlot.Components, summary =>
            client.Components.TryGetValue(summary.Id, out var component) && component.Type == "Probe");
        return client.Components[componentSummary.Id].Members["Value"].Value?.GetValue<string>();
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

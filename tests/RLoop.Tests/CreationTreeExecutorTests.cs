using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationTreeExecutorTests
{
    [Fact]
    public async Task CreatesEverySlotThenComponentsThenResolvesForwardComponentReference()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("A", "Value:System.String", "Common:System.String", "Initial:System.String", "Target:System.String");
        client.RegisterReference("A", "Target");
        client.RegisterType("B", "Value:System.String");
        var document = Document(
            [new ApplyComponentSpec("A", Fields(("Value", "scalar"), ("Common", "managed"), ("Target", "$component:B")), "A",
                InitialFields: Fields(("Initial", "only-initial")))],
            [new ApplyNodeSpec(new ApplySlotSpec("Child", null, null, null, null, "child"),
                [new ApplyComponentSpec("B", Fields(("Value", "later")), "B")])]);
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(document, CancellationToken.None);

        await executor.ExecuteAsync(tree, "root-created", Send(client), CancellationToken.None);

        Assert.Equal(new[] { "create-slot" }, client.SendLog.Take(1));
        Assert.Equal(new[] { "add-component", "add-component", "set-members" }, client.SendLog.Skip(1));
        Assert.Equal("add:A:Initial,Value,Common", client.FieldLog[0]);
        Assert.Equal("add:B:Value", client.FieldLog[1]);
        Assert.Equal("set:A:Target", client.FieldLog[2]);
        var a = client.Components.Values.Single(component => component.Type == "A");
        var b = client.Components.Values.Single(component => component.Type == "B");
        Assert.Equal("component-2", a.Id);
        Assert.Equal("component-3", b.Id);
        Assert.Equal("scalar", a.Members["Value"].Value!.GetValue<string>());
        Assert.Equal("managed", a.Members["Common"].Value!.GetValue<string>());
        Assert.Equal("only-initial", a.Members["Initial"].Value!.GetValue<string>());
        Assert.Equal("component-3", a.Members["Target"].TargetId);
        Assert.Equal("root-created", client.AddRequests[0].SlotId);
        Assert.Equal("slot-1", client.AddRequests[1].SlotId);
        Assert.Equal("scalar", client.AddRequests[0].Fields["Value"]);
        Assert.Equal("managed", client.AddRequests[0].Fields["Common"]);
        Assert.Equal("component-3", client.SetRequests[0].Fields["Target"]);
    }

    [Fact]
    public async Task RuntimeAndDriverOwnedDeclarationsAreNotValidatedOrWritten()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("Lamp", "Enabled:System.Boolean");
        var component = new ApplyComponentSpec("Lamp", Fields(("Enabled", "$component:missing")), "lamp",
            PropertyModes: new Dictionary<string, string> { ["Enabled"] = "runtime" });
        var executor = new CreationTreeExecutor(client);

        var prepared = await executor.PrepareAsync(Document([component]), CancellationToken.None);
        await executor.ExecuteAsync(prepared, "root-created", Send(client), CancellationToken.None);

        Assert.Single(client.AddRequests);
        Assert.Empty(client.AddRequests[0].Fields);
        Assert.Empty(client.SetRequests);
        Assert.Equal(0, client.ValidationCalls);
        Assert.True(client.Components.Single().Value.Members.ContainsKey("Enabled"));
        Assert.False(client.Components.Single().Value.Members["Enabled"].Value!.GetValue<bool>());
    }

    [Fact]
    public async Task StopsImmediatelyWhenARequiredMutationFails()
    {
        var client = new CreationMutationTestClient { ThrowOnMutation = 2 };
        client.RegisterType("A", "Value:System.String");
        client.RegisterType("B", "Value:System.String");
        var document = Document([new ApplyComponentSpec("A", Fields(("Value", "1")), "A"),
            new ApplyComponentSpec("B", Fields(("Value", "2")), "B")]);
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(document, CancellationToken.None);

        await Assert.ThrowsAsync<RLoopException>(() => executor.ExecuteAsync(tree, "root-created", Send(client), CancellationToken.None));

        Assert.Equal(new[] { "add-component" }, client.SendLog);
        Assert.Single(client.Components);
    }

    [Fact]
    public async Task PrepareRejectsAssetsAndNestedAssetSelectors()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("A", "Value:System.String");
        var executor = new CreationTreeExecutor(client);
        var withAsset = Document([]) with
        {
            Assets = new Dictionary<string, ApplyAssetSpec>
            {
                ["image"] = new ApplyAssetSpec("texture", "image.png")
            }
        };
        var withSelector = Document([new ApplyComponentSpec("A", Fields(("Value", new[] { "$asset:image" })), "A")]);

        var assetError = await Assert.ThrowsAsync<RLoopException>(() => executor.PrepareAsync(withAsset, CancellationToken.None));
        var selectorError = await Assert.ThrowsAsync<RLoopException>(() => executor.PrepareAsync(withSelector, CancellationToken.None));

        Assert.Equal("CREATION_DOCUMENT_INVALID", assetError.Code);
        Assert.Equal(ExitCodes.ValidationFailed, assetError.ExitCode);
        Assert.Equal("CREATION_DOCUMENT_INVALID", selectorError.Code);
        Assert.Equal(ExitCodes.ValidationFailed, selectorError.ExitCode);
    }

    [Fact]
    public async Task ChildWithoutStableKeyGetsAnOperationLocalKeyForEffectiveComponentKeys()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("B", "Value:System.String");
        var document = Document([], [new ApplyNodeSpec(new ApplySlotSpec("Child", null, null, null, null),
            [new ApplyComponentSpec("B", Fields(("Value", "child")))])]);
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(document, CancellationToken.None);

        await executor.ExecuteAsync(tree, "root-created", Send(client), CancellationToken.None);

        Assert.Equal("__creation_slot_000000", tree.Slots[1].Key);
        Assert.Equal(StableEffectiveKeys.Component("__creation_slot_000000", "B", 0), tree.Components[0].ReferenceKey);
        Assert.Equal("slot-1", client.AddRequests[0].SlotId);
    }

    [Fact]
    public async Task ComponentMustAppearExactlyOnceUnderTheReturnedOwnerSlot()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("A", "Value:System.String");
        client.RegisterType("B", "Value:System.String");
        client.SlotReadOverride = (slot, _) => slot.Id == "root-created" ? slot with { Components = [] } : slot;
        var tree = await new CreationTreeExecutor(client).PrepareAsync(Document([
            new ApplyComponentSpec("A", Fields(("Value", "first")), "A"),
            new ApplyComponentSpec("B", Fields(("Value", "later")), "B")]), CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationTreeExecutor(client)
            .ExecuteAsync(tree, "root-created", Send(client), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Single(client.SendLog);
        Assert.Equal("add-component", client.SendLog[0]);
    }

    [Fact]
    public async Task AddComponentTypeMismatchStopsFollowingSends()
    {
        var client = new CreationMutationTestClient { AddResultTypeOverride = "Wrong.Type" };
        client.RegisterType("A", "Value:System.String");
        client.RegisterType("B", "Value:System.String");
        var tree = await new CreationTreeExecutor(client).PrepareAsync(Document([
            new ApplyComponentSpec("A", Fields(("Value", "first")), "A"),
            new ApplyComponentSpec("B", Fields(("Value", "later")), "B")]), CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationTreeExecutor(client)
            .ExecuteAsync(tree, "root-created", Send(client), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Single(client.SendLog);
        Assert.Single(client.AddRequests);
    }

    [Fact]
    public async Task FullComponentReadbackTypeMismatchStopsFollowingSends()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("A", "Value:System.String");
        client.RegisterType("B", "Value:System.String");
        client.ComponentReadOverride = (component, _) => component.Id == "component-1" ? component with { Type = "Wrong.Type" } : component;
        var tree = await new CreationTreeExecutor(client).PrepareAsync(Document([
            new ApplyComponentSpec("A", Fields(("Value", "first")), "A"),
            new ApplyComponentSpec("B", Fields(("Value", "later")), "B")]), CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationTreeExecutor(client)
            .ExecuteAsync(tree, "root-created", Send(client), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Single(client.SendLog);
        Assert.Single(client.AddRequests);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("rotation")]
    [InlineData("scale")]
    [InlineData("tag")]
    public async Task SlotReadbackChecksTransformAndTagValues(string field)
    {
        var client = new CreationMutationTestClient();
        client.SlotReadOverride = (slot, _) => slot.Id != "slot-1" ? slot : field switch
        {
            "position" => slot with { Position = new Vector3Value(9, 2, 3) },
            "rotation" => slot with { Rotation = new QuaternionValue(0, 1, 0, 1) },
            "scale" => slot with { Scale = new Vector3Value(1, 2, 1) },
            _ => slot with { Tag = "wrong" }
        };
        var child = new ApplySlotSpec("Child", null, [1, 2, 3], [0, 0, 0, 1], [1, 1, 1], "child", Tag: "expected");
        var tree = await new CreationTreeExecutor(client).PrepareAsync(Document([], [new ApplyNodeSpec(child, [])]), CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationTreeExecutor(client)
            .ExecuteAsync(tree, "root-created", Send(client), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("slot-1", client.SlotReadIds.Last());
        Assert.Empty(client.AddRequests);
    }

    internal static ApplyDocument Document(IReadOnlyList<ApplyComponentSpec> components,
        IReadOnlyList<ApplyNodeSpec>? children = null) => new("1", new ApplyOwnershipSpec("tree"),
        new ApplySlotSpec("Root", null, null, null, null, "root"), components, children);

    internal static IReadOnlyDictionary<string, JsonElement> Fields(params (string Name, object Value)[] fields) =>
        fields.ToDictionary(item => item.Name, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal);

    internal static Func<Func<Task>, Task> Send(CreationMutationTestClient client) => async mutation =>
    {
        client.BeginApplySend();
        await mutation();
    };
}

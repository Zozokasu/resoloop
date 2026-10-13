using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationTreeReferenceTests
{
    [Fact]
    public async Task ResolvesEveryStableReferenceAfterAllTargetsExist()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("A", "ToSlot:System.String", "ToComponent:System.String", "ToRef:System.String",
            "ToMember:System.String", "ToSlotMember:System.String", "Nested:System.String");
        client.RegisterType("B", "Value:System.String");
        foreach (var member in new[] { "ToSlot", "ToComponent", "ToRef", "ToMember", "ToSlotMember" }) client.RegisterReference("A", member);
        var aFields = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["ToSlot"] = Json("$slot:child"),
            ["ToComponent"] = Json("$component:B"),
            ["ToRef"] = Json("$ref:B"),
            ["ToMember"] = Json("$member:B.Value"),
            ["ToSlotMember"] = Json("$slot-member:child.Name"),
            ["Nested"] = Json(new { values = new[] { "$component:B", "$member:B.Value" } })
        };
        var document = CreationTreeExecutorTests.Document([new ApplyComponentSpec("A", aFields, "A")],
            [new ApplyNodeSpec(new ApplySlotSpec("Child", null, null, null, null, "child"),
                [new ApplyComponentSpec("B", CreationTreeExecutorTests.Fields(("Value", "value")), "B")])]);
        var executor = new CreationTreeExecutor(client);
        var prepared = await executor.PrepareAsync(document, CancellationToken.None);

        await executor.ExecuteAsync(prepared, "root-created", CreationTreeExecutorTests.Send(client), CancellationToken.None);

        var a = client.Components.Values.Single(component => component.Type == "A");
        var b = client.Components.Values.Single(component => component.Type == "B");
        var child = client.Slots.Values.Single(slot => slot.Id.StartsWith("slot-", StringComparison.Ordinal));
        Assert.Equal("slot-1", child.Id);
        Assert.Equal("component-2", a.Id);
        Assert.Equal("component-3", b.Id);
        Assert.Equal("slot-1", a.Members["ToSlot"].TargetId);
        Assert.Equal("component-3", a.Members["ToComponent"].TargetId);
        Assert.Equal("component-3", a.Members["ToRef"].TargetId);
        Assert.Equal("component-3:Value", a.Members["ToMember"].TargetId);
        Assert.Equal("slot-1:Name", a.Members["ToSlotMember"].TargetId);
        Assert.Equal("{\"values\":[\"component-3\",\"component-3:Value\"]}", a.Members["Nested"].Value!.GetValue<string>());
        Assert.Equal("root-created", client.AddRequests.Single(request => request.Type == "A").SlotId);
        Assert.Equal("slot-1", client.AddRequests.Single(request => request.Type == "B").SlotId);
        Assert.Equal("component-3:Value", client.SetRequests.Single().Fields["ToMember"]);
    }

    [Fact]
    public async Task StructuralVerificationRejectsWrongCounts()
    {
        var client = new CreationMutationTestClient();
        await Assert.ThrowsAsync<RLoopException>(() => CreationTreeVerification.VerifyAsync(client,
            "root-created", new CreationPlanCounts(2, 1), CancellationToken.None));
    }

    [Fact]
    public async Task StructuralVerificationRejectsPartialObservation()
    {
        var client = new CreationMutationTestClient { IncompleteSlotId = "root-created" };
        await Assert.ThrowsAsync<RLoopException>(() => CreationTreeVerification.VerifyAsync(client,
            "root-created", new CreationPlanCounts(1, 0), CancellationToken.None));
    }

    [Fact]
    public async Task MemberReferenceRejectsComponentReadbackWithDifferentId()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("A", "Target:System.String");
        client.RegisterReference("A", "Target");
        client.RegisterType("B", "Value:System.String");
        client.ComponentReadOverride = (component, read) => read == 3 ? component with { Id = "wrong-component" } : component;
        var definition = CreationTreeExecutorTests.Document([new ApplyComponentSpec("A",
            CreationTreeExecutorTests.Fields(("Target", "$member:B.Value")), "A")],
            [new ApplyNodeSpec(new ApplySlotSpec("Child", null, null, null, null, "child"),
                [new ApplyComponentSpec("B", CreationTreeExecutorTests.Fields(("Value", "value")), "B")])]);
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(definition, CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => executor.ExecuteAsync(tree,
            "root-created", CreationTreeExecutorTests.Send(client), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal(ExitCodes.OperationFailed, error.ExitCode);
        Assert.Empty(client.SetRequests);
        Assert.Equal(new[] { "create-slot", "add-component", "add-component" }, client.SendLog);
    }

    [Fact]
    public async Task SlotMemberReferenceRejectsSlotReadbackWithDifferentId()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("A", "Target:System.String");
        client.RegisterReference("A", "Target");
        client.SlotReadOverride = (slot, read) => read == 3 ? slot with { Id = "wrong-slot" } : slot;
        var definition = CreationTreeExecutorTests.Document([new ApplyComponentSpec("A",
            CreationTreeExecutorTests.Fields(("Target", "$slot-member:child.Name")), "A")],
            [new ApplyNodeSpec(new ApplySlotSpec("Child", null, null, null, null, "child"), [])]);
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(definition, CancellationToken.None);

        var error = await Assert.ThrowsAsync<RLoopException>(() => executor.ExecuteAsync(tree,
            "root-created", CreationTreeExecutorTests.Send(client), CancellationToken.None));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal(ExitCodes.OperationFailed, error.ExitCode);
        Assert.Empty(client.SetRequests);
        Assert.Equal(new[] { "create-slot", "add-component" }, client.SendLog);
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}

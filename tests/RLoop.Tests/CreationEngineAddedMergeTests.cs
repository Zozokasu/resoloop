using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationEngineAddedMergeTests
{
    [Fact]
    public async Task DeclaredComponentWrittenAfterTheTriggerIsMergedIntoTheEngineAddedOne()
    {
        var client = Client();
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(Definition("X", "B", "A"), CancellationToken.None);

        var diagnostics = await executor.ExecuteAsync(tree, "root-created", CreationTreeExecutorTests.Send(client), CancellationToken.None);

        var engineB = client.Components.Values.Single(component => component.Type == "B");
        var a = client.Components.Values.Single(component => component.Type == "A");
        Assert.StartsWith("engine-auto-", engineB.Id);
        Assert.Equal("v", engineB.Members["Value"].Value!.GetValue<string>());
        Assert.Equal(engineB.Id, a.Members["ToComponent"].TargetId);
        Assert.Equal(engineB.Id + ":Value", a.Members["ToMember"].TargetId);
        var diagnostic = JsonSerializer.SerializeToElement(Assert.Single(diagnostics));
        Assert.Equal("CREATION_COMPONENT_MERGED_WITH_ENGINE_ADDED", diagnostic.GetProperty("code").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal(engineB.Id, diagnostic.GetProperty("componentId").GetString());
        Assert.Equal("B", diagnostic.GetProperty("type").GetString());
    }

    [Fact]
    public async Task DeclaredComponentWrittenBeforeTheTriggerNeedsNoMerge()
    {
        var client = Client();
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(Definition("B", "X", "A"), CancellationToken.None);

        var diagnostics = await executor.ExecuteAsync(tree, "root-created", CreationTreeExecutorTests.Send(client), CancellationToken.None);

        Assert.Empty(diagnostics);
        Assert.Single(client.Components.Values, component => component.Type == "B");
    }

    [Fact]
    public async Task NotFoundReadbackWithNoSameTypeCandidateStillFails()
    {
        var client = Client();
        client.ComponentReadOverride = (component, _) => component.Type == "B"
            ? throw new RLoopException("COMPONENT_NOT_FOUND", "gone", ExitCodes.NotFound) : component;
        var error = await Fail(client, "B");

        Assert.Equal("COMPONENT_NOT_FOUND", error.Code);
        Assert.Empty(client.SetRequests);
    }

    [Fact]
    public async Task NotFoundReadbackWithTwoSameTypeCandidatesStillFails()
    {
        var client = Client();
        client.EngineAutoAdds.Clear();
        client.Slots["root-created"] = client.Slots["root-created"] with
        {
            Components = [new ComponentSummary("pre-1", "B"), new ComponentSummary("pre-2", "B")]
        };
        client.ComponentReadOverride = (component, _) => component.Type == "B"
            ? throw new RLoopException("COMPONENT_NOT_FOUND", "gone", ExitCodes.NotFound) : component;
        var error = await Fail(client, "B");

        Assert.Equal("COMPONENT_NOT_FOUND", error.Code);
        Assert.Empty(client.SetRequests);
    }

    [Fact]
    public async Task TimeoutReadbackIsNeverTreatedAsAMerge()
    {
        var client = Client();
        client.EngineAutoAdds.Clear();
        client.Slots["root-created"] = client.Slots["root-created"] with { Components = [new ComponentSummary("pre-1", "B")] };
        client.ComponentReadOverride = (component, _) => component.Type == "B" ? throw new TimeoutException("slow") : component;
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(Definition("B"), CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(tree, "root-created",
            CreationTreeExecutorTests.Send(client), CancellationToken.None));
        Assert.Empty(client.SetRequests);
    }

    private static async Task<RLoopException> Fail(CreationMutationTestClient client, params string[] types)
    {
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(Definition(types), CancellationToken.None);
        return await Assert.ThrowsAsync<RLoopException>(() => executor.ExecuteAsync(tree, "root-created",
            CreationTreeExecutorTests.Send(client), CancellationToken.None));
    }

    private static CreationMutationTestClient Client()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("X");
        client.RegisterType("B", "Value:System.String");
        client.RegisterType("A", "ToComponent:System.String", "ToMember:System.String");
        client.RegisterReference("A", "ToComponent");
        client.RegisterReference("A", "ToMember");
        client.EngineAutoAdds["X"] = "B";
        return client;
    }

    private static ApplyDocument Definition(params string[] types) => CreationTreeExecutorTests.Document(types.Select(type => type switch
    {
        "B" => new ApplyComponentSpec("B", CreationTreeExecutorTests.Fields(("Value", "v")), "B"),
        "A" => new ApplyComponentSpec("A", CreationTreeExecutorTests.Fields(("ToComponent", "$component:B"), ("ToMember", "$member:B.Value")), "A"),
        _ => new ApplyComponentSpec(type, new Dictionary<string, JsonElement>(), type)
    }).ToArray());
}

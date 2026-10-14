using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private const string BindingChangedComponents = """
        [{"key":"first","type":"Test.Target","fields":{"Enabled":false}},
         {"key":"second","type":"Test.Target","fields":{"Enabled":true}}]
        """;

    [Fact]
    public async Task CompletedApplyWritesBindingAndInterruptedApplyDoesNot()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-twin" };
        var service = new WorldService(client);
        var statePath = Path.Combine(_root, "binding-completed.state.json");

        await service.ApplyAsync(ComponentsDocument("binding-completed", TwinComponents), new ApplyOptions(statePath));
        var saved = JsonNode.Parse(File.ReadAllText(statePath))!;
        var binding = saved["sessionBinding"]!.GetValue<string>();
        Assert.StartsWith("sha256:", binding);
        Assert.Equal(71, binding.Length);

        client.RejectKind = "setMembers";
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(
            ComponentsDocument("binding-completed", BindingChangedComponents), new ApplyOptions(statePath)));

        var interrupted = JsonNode.Parse(File.ReadAllText(statePath))!;
        Assert.Null(interrupted["session"]);
        Assert.Null(interrupted["sessionBinding"]);
    }

    [Fact]
    public async Task SwappedRecordedIdsStopAmbiguous()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-twin" };
        var service = new WorldService(client);
        var statePath = Path.Combine(_root, "binding-swapped.state.json");
        await service.ApplyAsync(ComponentsDocument("binding-swapped", TwinComponents), new ApplyOptions(statePath));

        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        var firstId = state["components"]!["first"]!["id"]!.GetValue<string>();
        var secondId = state["components"]!["second"]!["id"]!.GetValue<string>();
        state["components"]!["first"]!["id"] = secondId;
        state["components"]!["second"]!["id"] = firstId;
        File.WriteAllText(statePath, state.ToJsonString());
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(
            ComponentsDocument("binding-swapped", BindingChangedComponents), new ApplyOptions(statePath)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal("notRecorded", error.Context["sessionProof"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task StateWithoutBindingStopsAmbiguous()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-twin" };
        var service = new WorldService(client);
        var statePath = Path.Combine(_root, "binding-missing.state.json");
        await service.ApplyAsync(ComponentsDocument("binding-missing", TwinComponents), new ApplyOptions(statePath));

        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state.Remove("sessionBinding");
        File.WriteAllText(statePath, state.ToJsonString());
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(
            ComponentsDocument("binding-missing", BindingChangedComponents), new ApplyOptions(statePath)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal("notRecorded", error.Context["sessionProof"]);
        Assert.Equal(0, client.Writes);
    }
}

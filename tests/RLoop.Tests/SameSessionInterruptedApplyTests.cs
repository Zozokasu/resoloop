using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task RootUpdateFailureClearsSessionAndRetryStopsAmbiguous(string failure)
    {
        var client = new FakeResoniteClient { DiscoverId = "S-interrupted" };
        var service = new WorldService(client);
        var initial = ComponentsDocument("interrupted-root", TwinComponents);
        var statePath = Path.Combine(_root, "interrupted-root.state.json");
        await service.ApplyAsync(initial, new ApplyOptions(statePath));
        client.ResetWriteCounts();

        var changedPath = Path.Combine(_root, "interrupted-root-changed.json");
        File.WriteAllText(changedPath, """
            {
              "schemaVersion":"1",
              "ownership":{"key":"interrupted-root"},
              "slot":{"key":"root","name":"Managed","parent":"Root","position":[1,2,3]},
              "components":[
                {"key":"first","type":"Test.Target","fields":{"Enabled":false}},
                {"key":"second","type":"Test.Target","fields":{"Enabled":true}}
              ]
            }
            """);
        var changed = ApplyDocument.Load(changedPath);
        using var cancellation = new CancellationTokenSource();
        if (failure == "rejected") client.RejectKind = "updateSlot";
        else
        {
            client.Cancellation = cancellation;
            client.CancelAfterWrites = 1;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyAsync(changed, new ApplyOptions(statePath), cancellation.Token));

        Assert.Null(JsonNode.Parse(File.ReadAllText(statePath))!["session"]);
        client.RejectKind = null;
        client.Cancellation = null;
        client.CancelAfterWrites = null;
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(changed, new ApplyOptions(statePath)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal("notRecorded", error.Context["sessionProof"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task PruneNeverSelectsByRecordedId()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-prune" };
        var service = new WorldService(client);
        var initial = ComponentsDocument("prune-twin", TwinComponents);
        var statePath = Path.Combine(_root, "prune-twin.state.json");
        await service.ApplyAsync(initial, new ApplyOptions(statePath));
        var rootId = JsonNode.Parse(File.ReadAllText(statePath))!["slots"]!["root"]!["id"]!.GetValue<string>();
        var expectedIds = JsonNode.Parse(File.ReadAllText(statePath))!["components"]!.AsObject()
            .Select(pair => pair.Value!["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var retained = ComponentsDocument("prune-twin", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":true}}]
            """);
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(retained,
            new ApplyOptions(statePath, Prune: true, ConfirmDeletes: true)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(0, client.Writes);
        var root = await client.GetSlotAsync(rootId, 1, true);
        Assert.Equal(expectedIds, root.Components.Select(component => component.Id).ToHashSet(StringComparer.Ordinal));
    }
}

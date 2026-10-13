using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private const string TwinComponents = """
        [{"key":"first","type":"Test.Target","fields":{"Enabled":true}},
         {"key":"second","type":"Test.Target","fields":{"Enabled":false}}]
        """;

    [Fact]
    public async Task RecordedIdsContinueMatchingTwinsOnlyInTheSameSessionAndPlanDoesNotWriteState()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-twin" };
        var service = new WorldService(client);
        var first = ComponentsDocument("same-session-twins", TwinComponents);
        var statePath = Path.Combine(_root, "same-session-twins.state.json");
        await service.ApplyAsync(first, new ApplyOptions(statePath));
        var saved = JsonNode.Parse(File.ReadAllText(statePath))!;
        Assert.Equal("S-twin", saved["session"]!["discoverSessionId"]!.GetValue<string>());
        var firstId = saved["components"]!["first"]!["id"]!.GetValue<string>();
        var secondId = saved["components"]!["second"]!["id"]!.GetValue<string>();
        var changed = ComponentsDocument("same-session-twins", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":false}},
             {"key":"second","type":"Test.Target","fields":{"Enabled":true}}]
            """);
        var originalState = File.ReadAllText(statePath);

        var plan = await service.PlanApplyAsync(changed, new ApplyOptions(statePath));
        Assert.True(plan.Valid);
        Assert.Equal(originalState, File.ReadAllText(statePath));
        client.ResetWriteCounts();
        await service.ApplyAsync(changed, new ApplyOptions(statePath));

        var live = await client.GetSlotAsync(saved["slots"]!["root"]!["id"]!.GetValue<string>(), 1, true);
        Assert.Equal("false", Assert.Single(live.Components, c => c.Id == firstId).Members!["Enabled"].Value!.ToJsonString());
        Assert.Equal("true", Assert.Single(live.Components, c => c.Id == secondId).Members!["Enabled"].Value!.ToJsonString());
        Assert.Equal(2, client.Writes);
    }

    [Theory]
    [InlineData("ws://fake", "S-other", "changed")]
    [InlineData("ws://other", "S-twin", "changed")]
    [InlineData("ws://fake", null, "unknown")]
    public async Task UnprovenOrChangedSessionKeepsAmbiguousStop(string url, string? id, string proof)
    {
        var client = new FakeResoniteClient { DiscoverId = "S-twin" };
        var document = ComponentsDocument("unproven-twins", TwinComponents);
        var statePath = Path.Combine(_root, "unproven-twins.state.json");
        await new WorldService(client).ApplyAsync(document, new ApplyOptions(statePath));
        client.SessionUrl = url;
        client.DiscoverId = id;
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document, new ApplyOptions(statePath)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(proof, error.Context["sessionProof"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task OldStateWithoutSessionAndMissingOrDuplicateRecordedIdsStillStop()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-twin" };
        var document = ComponentsDocument("old-twins", TwinComponents);
        var statePath = Path.Combine(_root, "old-twins.state.json");
        await new WorldService(client).ApplyAsync(document, new ApplyOptions(statePath));
        var oldState = JsonNode.Parse(File.ReadAllText(statePath))!;
        oldState.AsObject().Remove("session");
        File.WriteAllText(statePath, oldState.ToJsonString());
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document, new ApplyOptions(statePath)));
        Assert.Equal("notRecorded", error.Context["sessionProof"]);
        Assert.Equal(0, client.Writes);

        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        state["components"]!["second"]!["id"] = state["components"]!["first"]!["id"]!.GetValue<string>();
        File.WriteAllText(statePath, state.ToJsonString());
        error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document, new ApplyOptions(statePath)));
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task MissingStoredIdAmongAmbiguousCandidatesStops()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-twin" };
        var document = ComponentsDocument("missing-id-twins", TwinComponents);
        var statePath = Path.Combine(_root, "missing-id-twins.state.json");
        await new WorldService(client).ApplyAsync(document, new ApplyOptions(statePath));
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        var slotId = state["slots"]!["root"]!["id"]!.GetValue<string>();
        var oldId = state["components"]!["first"]!["id"]!.GetValue<string>();
        await client.RemoveComponentAsync(oldId);
        await client.AddComponentAsync(slotId, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "true" });
        await client.AddComponentAsync(slotId, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document, new ApplyOptions(statePath)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(false, error.Context["storedIdAmongCandidates"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task IdentityFieldsReestablishSessionProofAndFailedApplyDoesNotPersistIt()
    {
        var first = ComponentsDocument("identity-twins", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":true},"identityFields":["Enabled"]},
             {"key":"second","type":"Test.Target","fields":{"Enabled":false},"identityFields":["Enabled"]}]
            """);
        var statePath = Path.Combine(_root, "identity-twins.state.json");
        var oldClient = new FakeResoniteClient { DiscoverId = "S-old" };
        await new WorldService(oldClient).ApplyAsync(first, new ApplyOptions(statePath));

        var newClient = new FakeResoniteClient { DiscoverId = "S-new" };
        var managed = await newClient.CreateSlotAsync(new SlotCreateRequest("Root", "Managed"));
        await newClient.AddComponentAsync(managed, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "true" });
        await newClient.AddComponentAsync(managed, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        newClient.ResetWriteCounts();
        await new WorldService(newClient).ApplyAsync(first, new ApplyOptions(statePath));
        var refreshed = JsonNode.Parse(File.ReadAllText(statePath))!;
        Assert.Equal("S-new", refreshed["session"]!["discoverSessionId"]!.GetValue<string>());

        var changed = ComponentsDocument("identity-twins", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":false},"identityFields":["Enabled"]},
             {"key":"second","type":"Test.Target","fields":{"Enabled":false},"identityFields":["Enabled"]}]
            """);
        newClient.DiscoverId = "S-next";
        newClient.FailOnWrite = 1;
        await Assert.ThrowsAnyAsync<Exception>(() => new WorldService(newClient).ApplyAsync(changed, new ApplyOptions(statePath)));
        Assert.Null(JsonNode.Parse(File.ReadAllText(statePath))!["session"]);
    }

    [Fact]
    public async Task FailedWriteCheckpointDoesNotKeepSessionAndNextApplyStopsAmbiguous()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-retry" };
        var statePath = Path.Combine(_root, "same-session-retry.state.json");
        var initial = ComponentsDocument("same-session-retry", TwinComponents);
        await new WorldService(client).ApplyAsync(initial, new ApplyOptions(statePath));
        var changed = ComponentsDocument("same-session-retry", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":false}},
             {"key":"second","type":"Test.Target","fields":{"Enabled":true}}]
            """);
        client.ResetWriteCounts();
        client.RejectKind = "setMembers";

        await Assert.ThrowsAnyAsync<Exception>(() => new WorldService(client).ApplyAsync(changed, new ApplyOptions(statePath)));

        var checkpoint = JsonNode.Parse(File.ReadAllText(statePath))!;
        Assert.Null(checkpoint["session"]);
        client.RejectKind = null;
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(changed, new ApplyOptions(statePath)));
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal("notRecorded", error.Context["sessionProof"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ChangedSessionDoesNotPersistProofWhenStateHasUnverifiedComponentRecord()
    {
        var client = new FakeResoniteClient { DiscoverId = "S-before" };
        var statePath = Path.Combine(_root, "unverified-record.state.json");
        var initial = ComponentsDocument("unverified-record", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":true},"identityFields":["Enabled"]},
             {"key":"second","type":"Test.Target","fields":{"Enabled":false},"identityFields":["Enabled"]}]
            """);
        await new WorldService(client).ApplyAsync(initial, new ApplyOptions(statePath));
        client.DiscoverId = "S-after";
        var reduced = ComponentsDocument("unverified-record", """
            [{"key":"first","type":"Test.Target","fields":{"Enabled":true},"identityFields":["Enabled"]}]
            """);

        await new WorldService(client).ApplyAsync(reduced, new ApplyOptions(statePath));

        var saved = JsonNode.Parse(File.ReadAllText(statePath))!;
        Assert.Null(saved["session"]);
        Assert.NotNull(saved["components"]!["second"]);
    }
}

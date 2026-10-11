using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

[Collection("Live world writes")]
public sealed class ComponentRelocationReferenceIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReferencedLightRelocatesAndButtonToggleTracksItsNewEnabledField()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N");
        var name = "ResoLoop_Test_Relocate_" + suffix;
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-live-relocate-" + suffix);
        Directory.CreateDirectory(directory);
        var initialPath = Path.Combine(directory, "initial.json");
        var desiredPath = Path.Combine(directory, "desired.json");
        var statePath = Path.Combine(directory, "state.json");
        var ownershipKey = "live-relocate-" + suffix;
        File.WriteAllText(initialPath, $$$"""
            { "schemaVersion":"1", "ownership":{"key":"{{{ownershipKey}}}"},
              "slot":{"key":"root","name":"{{{name}}}","parent":"Root"},
              "children":[
                {"slot":{"key":"destination","name":"Destination"}},
                {"slot":{"key":"source","name":"Source"},"components":[
                  {"key":"light","type":"[FrooxEngine]FrooxEngine.Light","fields":{"Enabled":true}},
                  {"key":"button-toggle","type":"[FrooxEngine]FrooxEngine.ButtonToggle","fields":{"TargetValue":"$member:light.Enabled"}}
                ]}
              ] }
            """);
        File.WriteAllText(desiredPath, $$$"""
            { "schemaVersion":"1", "ownership":{"key":"{{{ownershipKey}}}"},
              "slot":{"key":"root","name":"{{{name}}}","parent":"Root"},
              "children":[
                {"slot":{"key":"destination","name":"Destination"},"components":[
                  {"key":"light","type":"[FrooxEngine]FrooxEngine.Light","fields":{"Enabled":true}}
                ]},
                {"slot":{"key":"source","name":"Source"},"components":[
                  {"key":"button-toggle","type":"[FrooxEngine]FrooxEngine.ButtonToggle","fields":{"TargetValue":"$member:light.Enabled"}}
                ]}
              ] }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var world = new WorldService(client);
        string? rootId = null;
        string? oldLightId = null;
        try
        {
            var initial = await world.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(statePath));
            rootId = initial.SlotId;
            var state = ApplyStateStore.Load(statePath, ownershipKey);
            var sourceId = state.Slots["source"].Id;
            oldLightId = Assert.Single((await client.GetSlotAsync(sourceId, 0, false)).Components,
                component => component.Type == "[FrooxEngine]FrooxEngine.Light").Id;

            var moved = await world.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(statePath));
            Assert.Equal(1, moved.ComponentsDeleted);
            Assert.Equal(1, moved.ComponentsUpdated);
            Assert.Equal(1, moved.ComponentsAdded);
            var after = ApplyStateStore.Load(statePath, ownershipKey);
            var lightId = after.Components["light"].Id;
            var toggleId = after.Components["button-toggle"].Id;
            var light = await client.GetComponentAsync(lightId);
            var toggle = await client.GetComponentAsync(toggleId);
            Assert.Equal(light.Members["Enabled"].Id, toggle.Members["TargetValue"].TargetId);
            Assert.NotEqual(oldLightId, lightId);
            Assert.Equal("COMPONENT_NOT_FOUND", (await Assert.ThrowsAsync<RLoopException>(
                () => client.GetComponentAsync(oldLightId))).Code);

            var diff = await world.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(statePath));
            Assert.DoesNotContain(diff.Operations, operation => operation.Action is "create" or "update" or "relocate" or "delete");
            var second = await world.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(statePath));
            Assert.Equal(0, second.ComponentsAdded);
            Assert.Equal(0, second.ComponentsUpdated);
            Assert.Equal(0, second.ComponentsDeleted);
        }
        finally
        {
            if (rootId is null && File.Exists(statePath))
            {
                var state = ApplyStateStore.Load(statePath, ownershipKey);
                rootId = state.Slots.GetValueOrDefault("root")?.Id ?? state.Pending.FirstOrDefault(p =>
                    p.Kind == "createSlot" && p.Key == "root" && p.ParentId == "Root" &&
                    p.SlotValues?.Name == name && p.Id is not null)?.Id;
            }
            if (rootId is not null)
            {
                var observed = await client.GetSlotAsync(rootId, 0, false);
                Assert.Equal(rootId, observed.Id);
                Assert.Equal(name, observed.Name);
                Assert.Equal("Root", observed.ParentId);
                await client.DeleteSlotAsync(rootId);
                Assert.Equal("SLOT_NOT_FOUND", (await Assert.ThrowsAsync<RLoopException>(
                    () => client.GetSlotAsync(rootId, 0, false))).Code);
            }
            foreach (var path in new[] { initialPath, desiredPath, statePath, statePath + ".lock" })
                if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, false);
        }
    }
}

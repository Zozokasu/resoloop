using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

[Collection("Live world writes")]
public sealed class UixDriverMigrationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReplacesSliderDrivesPreservesExactNamesAndReconnects()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        await RunMigrationAsync(oneShot: false);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OneShotDriverReplacementStopsAtMismatchRetainsPendingAndOldSlider()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        await RunMigrationAsync(oneShot: true);
    }

    private static async Task RunMigrationAsync(bool oneShot)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var url = new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!);
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(url, TimeSpan.FromSeconds(20), token);
        var name = "ResoLoop_Test_DriverSwap_" + (oneShot ? "Mismatch_" : "TwoStage_") + Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS") ?? Path.GetTempPath(), name));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "driver-swap.state.json");
        string? root = null;
        try
        {
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-45, 2, -45)), token);
            var json = JsonNode.Parse("""
                {"schemaVersion":"1","ownership":{"key":"driver-swap"},"slot":{"key":"canvas","name":"Canvas"},
                 "components":[{"key":"canvas-rect","type":"FrooxEngine.UIX.RectTransform"},
                    {"key":"canvas-component","type":"FrooxEngine.UIX.Canvas","fields":{"Size":[200,100]}},
                    {"key":"old-slider","type":"[FrooxEngine]FrooxEngine.UIX.Slider<float>","fields":{
                      "HandleAnchorMinDrive":"$member:knob-rect.AnchorMin","HandleAnchorMaxDrive":"$member:knob-rect.AnchorMax"},"initialFields":{"Value":0.25}}],
                 "children":[{"slot":{"key":"group","name":"A/B"},"components":[{"key":"group-rect","type":"FrooxEngine.UIX.RectTransform"}],
                    "children":[{"slot":{"key":"knob","name":" Knob "},"components":[{"key":"knob-rect","type":"FrooxEngine.UIX.RectTransform"}]}]},
                    {"slot":{"key":"old-background","name":"Background"}}]}
                """)!;
            json["slot"]!["parent"] = root;
            var path = Path.Combine(directory, "driver-swap.json");
            await File.WriteAllTextAsync(path, json.ToJsonString(), token);
            var world = new WorldService(client);
            var options = new ApplyOptions(statePath);
            await world.ApplyAsync(ApplyDocument.Load(path), options, token);
            var oldSlider = await world.ResolveComponentSelectorAsync("$component:old-slider", options.StateFile, token);
            var knob = await world.ResolveComponentSelectorAsync("$component:knob-rect", options.StateFile, token);
            var initial = await client.GetComponentAsync(knob, token);
            var target = initial.Members["AnchorMin"].Id!;
            var definition = await client.DescribeComponentTypeAsync("[FrooxEngine]FrooxEngine.BooleanValueDriver<float2>", token);
            foreach (var field in new[] { "TargetField", "State", "FalseValue", "TrueValue" }) Assert.Contains(definition.Members, member => member.Name == field);
            var control = await client.AddComponentAsync(root, definition.FullTypeName, new Dictionary<string, string>
                { ["TargetField"] = target, ["FalseValue"] = "[0.2,0.5]", ["TrueValue"] = "[0.8,0.5]" }, token);
            var blocked = await client.GetComponentAsync(control.Id, token);
            Assert.NotEqual(target, blocked.Members["TargetField"].TargetId);
            // Remove only the control just created in this exact test root.
            await client.RemoveComponentAsync(control.Id, token);
            var components = (JsonArray)json["components"]!;
            components.RemoveAt(2);
            json["children"]![1]!["slot"]!["key"] = "new-background";
            ApplyResult? pruned = null;
            if (!oneShot)
            {
                // Prune the declared old driver with no replacement drivers in the document.
                await File.WriteAllTextAsync(path, json.ToJsonString(), token);
                pruned = await world.ApplyAsync(ApplyDocument.Load(path), options with { Prune = true, ConfirmDeletes = true }, token);
                Assert.Equal(1, pruned.ComponentsDeleted);
                Assert.Equal(1, pruned.SlotsDeleted);
                var missing = await Assert.ThrowsAsync<RLoopException>(() => client.GetComponentAsync(oldSlider, token));
                Assert.Equal("COMPONENT_NOT_FOUND", missing.Code);
            }
            foreach (var axis in new[] { "Min", "Max" })
            {
                components.Add(new JsonObject
                {
                    ["key"] = "drive-" + axis, ["type"] = definition.FullTypeName,
                    ["fields"] = new JsonObject { ["TargetField"] = "$member:knob-rect.Anchor" + axis,
                        ["FalseValue"] = JsonNode.Parse("[0.2,0.5]"), ["TrueValue"] = JsonNode.Parse("[0.8,0.5]") },
                    ["initialFields"] = new JsonObject { ["State"] = false }
                });
            }
            await File.WriteAllTextAsync(path, json.ToJsonString(), token);
            if (oneShot)
            {
                var error = await Assert.ThrowsAsync<RLoopException>(() => world.ApplyAsync(ApplyDocument.Load(path),
                    options with { Prune = true, ConfirmDeletes = true }, token));
                Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
                Assert.Equal("readbackMismatch", error.Context["reason"]);
                var pending = Assert.Single(ApplyStateStore.Load(statePath, "driver-swap").Pending);
                Assert.Equal("drive-Min", pending.Key);
                Assert.NotNull(pending.Id);
                var replacement = await client.GetComponentAsync(pending.Id, token);
                Assert.NotEqual(target, replacement.Members["TargetField"].TargetId);
                var retained = await client.GetComponentAsync(oldSlider, token);
                Assert.Equal(oldSlider, retained.Id);
                // Capture the journal before cleanup; no identity-based resume or replay is attempted.
                await File.WriteAllTextAsync(Path.Combine(directory, "driver-swap-pending.state.json"),
                    await File.ReadAllTextAsync(statePath, token), token);
                await File.WriteAllTextAsync(Path.Combine(directory, "driver-swap-observations.json"), JsonSerializer.Serialize(new
                { session = await client.GetSessionInfoAsync(token), blocked, oldSlider, retained, replacement, pending,
                    error = new { error.Code, error.Context } }, new JsonSerializerOptions { WriteIndented = true }), token);
                return;
            }
            var applied = await world.ApplyAsync(ApplyDocument.Load(path), options, token);
            Assert.Equal(0, applied.ComponentsDeleted);
            Assert.Equal(0, applied.SlotsDeleted);
            foreach (var axis in new[] { "Min", "Max" })
            {
                var driver = await world.ResolveComponentSelectorAsync("$component:drive-" + axis, options.StateFile, token);
                Assert.Equal(initial.Members["Anchor" + axis].Id, (await client.GetComponentAsync(driver, token)).Members["TargetField"].TargetId);
                await client.SetComponentMemberAsync(driver, "State", "true", token);
            }
            await Task.Delay(200, token);
            var driven = await client.GetComponentAsync(knob, token);
            Assert.Equal(.8f, driven.Members["AnchorMin"].Value!["x"]!.GetValue<float>(), .0001f);
            Assert.Equal(.8f, driven.Members["AnchorMax"].Value!["x"]!.GetValue<float>(), .0001f);
            await using var second = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
            await second.ConnectAsync(url, TimeSpan.FromSeconds(20), token);
            var otherWorld = new WorldService(second);
            var group = await otherWorld.ResolveSlotSelectorAsync("$slot:group", options.StateFile, token);
            var knobSlot = await otherWorld.ResolveSlotSelectorAsync("$slot:knob", options.StateFile, token);
            Assert.Equal("A/B", (await second.GetSlotAsync(group, 0, false, token)).Name);
            Assert.Equal(" Knob ", (await second.GetSlotAsync(knobSlot, 0, false, token)).Name);
            Assert.Equal(knob, await otherWorld.ResolveComponentSelectorAsync("$component:knob-rect", options.StateFile, token));
            var again = await otherWorld.ApplyAsync(ApplyDocument.Load(path), options, token);
            Assert.Equal(0, again.ComponentsAdded);
            Assert.Equal(0, again.ComponentsUpdated);
            Assert.Equal(0, again.SlotsCreated);
            Assert.Equal(0, again.SlotsUpdated);
            Assert.NotEmpty(await otherWorld.ResolveSlotSelectorAsync("$slot:new-background", options.StateFile, token));
            await File.WriteAllTextAsync(Path.Combine(directory, "driver-swap-observations.json"), JsonSerializer.Serialize(new
            { session = await second.GetSessionInfoAsync(token), blocked, oldSlider, oldSliderAbsent = true, pruned, applied, driven, again }, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally
        {
            if (oneShot && File.Exists(statePath))
                await File.WriteAllTextAsync(Path.Combine(directory, "driver-swap-pending.state.json"),
                    await File.ReadAllTextAsync(statePath));
            if (root is not null)
            {
                var observed = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(name, observed.Name);
                Assert.Equal("Root", observed.ParentId);
                Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root);
                var missing = await Assert.ThrowsAsync<RLoopException>(() => client.GetSlotAsync(root, 0, false));
                Assert.Equal("SLOT_NOT_FOUND", missing.Code);
                // The exact sandbox is gone. Leave the captured journal as evidence, but remove
                // this test's active state so its pending pointer cannot block later URL tests.
                if (oneShot && File.Exists(statePath)) File.Delete(statePath);
                await File.WriteAllTextAsync(Path.Combine(directory, "driver-swap-cleanup.json"), JsonSerializer.Serialize(new
                { root, name, deleted = true, absent = true, activeStateRemoved = oneShot && !File.Exists(statePath) }));
            }
        }
    }
}

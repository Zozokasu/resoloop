using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed partial class UixRecipeIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task StructuralRecipesDriveCallerValuesAndPreserveRuntimeState()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(20), ct);
        var name = "ResoLoop_Test_Recipes_" + Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), name));
        Directory.CreateDirectory(directory);
        var world = new WorldService(client);
        string? root = null;
        try
        {
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-40, 2, -40)), ct);
            foreach (var recipe in UixRecipes.Catalog)
                await File.WriteAllTextAsync(Path.Combine(directory, recipe.Name + ".json"), UixRecipes.Read(recipe.Name), ct);
            var node = JsonNode.Parse("""
            {
              "include":["button.json","boolean-state.json","scroll-content.json"],
              "schemaVersion":"1","ownership":{"key":"recipe-test"},"slot":{"key":"canvas","name":"Canvas"},
              "components":[{"key":"canvas-rect","type":"FrooxEngine.UIX.RectTransform"},
                {"key":"canvas-ui","type":"FrooxEngine.UIX.Canvas","fields":{"Size":[200,100]}}],
              "children":[
                {"$prototype":"uix.button","$with":{"key":"control","rect":{}},
                 "components":[{"key":"hit","type":"FrooxEngine.UIX.Image","fields":{"InteractionTarget":true,"Tint":[0,0,0,0]}}],
                 "children":[{"slot":{"key":"face","name":"Caller designed face"},"components":[
                   {"key":"face-rect","type":"FrooxEngine.UIX.RectTransform"},
                   {"key":"face-image","type":"FrooxEngine.UIX.Image","fields":{"InteractionTarget":false}}]}]},
                {"$prototype":"uix.boolean-state","$with":{"key":"motion","valueType":"float2",
                  "source":"$member:control-button.IsPressed","target":"$member:face-rect.OffsetMin","off":[0,0],"on":[3,-2]}},
                {"$prototype":"uix.boolean-state","$with":{"key":"color","valueType":"colorX",
                  "source":"$member:control-button.IsPressed","target":"$member:face-image.Tint","off":[0,0,0,1],"on":[1,0,0,1]}},
                {"$prototype":"uix.scroll-content","$with":{"key":"content","rect":{},"viewport":"$component:canvas-rect"}}
              ],
              "tests":[
                {"name":"Pressed feedback","probe":{"kind":"set-member","safe":true,"restore":true,"target":"$member:control-button.IsPressed","value":true},
                 "assertions":[{"target":"$member:face-rect.OffsetMin","expected":[3,-2],"phase":"after"},
                   {"target":"$member:face-image.Tint","expected":[1,0,0,1],"phase":"after"}]},
                {"name":"Released feedback","assertions":[{"target":"$member:control-button.IsPressed","expected":false},
                  {"target":"$member:face-rect.OffsetMin","expected":[0,0]},
                  {"target":"$member:face-image.Tint","expected":[0,0,0,1]}]}
              ]
            }
            """)!;
            node["slot"]!["parent"] = root;
            var path = Path.Combine(directory, "recipes.apply.json");
            await File.WriteAllTextAsync(path, node.ToJsonString(), ct);
            var doc = ApplyDocument.Load(path);
            var options = new ApplyOptions(Path.Combine(directory, "state.json"));
            var plan = await world.PlanApplyAsync(doc, options, ct);
            Assert.True(plan.Valid);
            var applied = await world.ApplyAsync(doc, options, ct);
            var report = await world.TestAsync(doc, options, true, ct);
            Assert.True(report.Passed, JsonSerializer.Serialize(report));
            Assert.True(report.Tests[0].ProbeExecuted);
            var buttonId = await world.ResolveComponentSelectorAsync("$component:control-button", options.StateFile, ct);
            var button = await client.GetComponentAsync(buttonId, ct);
            Assert.Empty(button.Members["ColorDrivers"].Elements!);
            var scrollId = await world.ResolveComponentSelectorAsync("$component:content-scroll", options.StateFile, ct);
            var viewportId = await world.ResolveComponentSelectorAsync("$component:canvas-rect", options.StateFile, ct);
            Assert.Equal(viewportId, (await client.GetComponentAsync(scrollId, ct)).Members["ViewportOverride"].TargetId);
            await client.SetComponentMemberAsync(scrollId, "NormalizedPosition", "[0.25,0.75]", ct);
            var before = (await client.GetComponentAsync(scrollId, ct)).Members["NormalizedPosition"].Value!.ToJsonString();
            var again = await world.ApplyAsync(doc, options, ct);
            Assert.Equal(0, again.ComponentsUpdated + again.ComponentsAdded + again.SlotsCreated + again.SlotsUpdated);
            Assert.Equal(before, (await client.GetComponentAsync(scrollId, ct)).Members["NormalizedPosition"].Value!.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            { session = await client.GetSessionInfoAsync(ct), plan, applied, report, again, scrollPosition = before }), ct);
        }
        finally
        {
            if (root is not null)
            {
                var current = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(name, current.Name);
                Assert.Equal("Root", current.ParentId);
                await client.DeleteSlotAsync(root);
                await File.WriteAllTextAsync(Path.Combine(directory, "cleanup.json"), JsonSerializer.Serialize(new { id = root, name, deleted = true }));
            }
        }
    }
}

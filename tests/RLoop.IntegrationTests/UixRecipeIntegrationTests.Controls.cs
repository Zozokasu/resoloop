using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed partial class UixRecipeIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InputToggleChoiceSliderPreserveStateAndExposeCallerBindings()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(20), ct);
        var name = "ResoLoop_Test_ControlRecipes_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS") ?? Path.GetTempPath()), name);
        Directory.CreateDirectory(directory);
        var world = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(directory, "state.json"));
        string? root = null;
        try
        {
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-42, 2, -42)), ct);
            foreach (var recipe in UixRecipes.Catalog)
                await File.WriteAllTextAsync(Path.Combine(directory, recipe.Name + ".json"), UixRecipes.Read(recipe.Name), ct);
            var node = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures/uix-controls-recipes.json"), ct))!;
            node["include"] = new JsonArray(UixRecipes.Catalog.Select(recipe => JsonValue.Create(recipe.Name + ".json")).ToArray());
            node["slot"]!["parent"] = root;
            var path = Path.Combine(directory, "controls.json");
            await File.WriteAllTextAsync(path, node.ToJsonString(), ct);
            var doc = ApplyDocument.Load(path);
            var plan = await world.PlanApplyAsync(doc, options, ct);
            var applied = await world.ApplyAsync(doc, options, ct);
            var report = await world.TestAsync(doc, options, true, ct);
            Assert.True(report.Passed, JsonSerializer.Serialize(report));
            var unmatched = new ApplyTestSpec("Unmatched selection", [
                new("$member:option-a-selected.Value", JsonSerializer.SerializeToElement(false), Phase: "after"),
                new("$member:option-b-selected.Value", JsonSerializer.SerializeToElement(false), Phase: "after")],
                new(Target: "$member:selection-state.Value", Kind: "set-member", Safe: true, Value: JsonSerializer.SerializeToElement(999)));
            var unmatchedReport = await world.TestAsync(doc with { Tests = [unmatched] }, options, true, ct);
            Assert.True(unmatchedReport.Passed, JsonSerializer.Serialize(unmatchedReport));
            var text = await Id("editable-text");
            var editor = await Id("input-editor");
            var input = await client.GetComponentAsync(await Id("input-input"), ct);
            Assert.Equal(text, input.Members["__text"].TargetId);
            Assert.Equal(editor, input.Members["Editor"].TargetId);
            Assert.Equal(text, (await client.GetComponentAsync(editor, ct)).Members["Text"].TargetId);
            var boolState = await client.GetComponentAsync(await Id("enabled-state"), ct);
            Assert.Equal(boolState.Members["Value"].Id, (await client.GetComponentAsync(await Id("toggle-toggle"), ct)).Members["TargetValue"].TargetId);

            var slider = await Id("slider-slider");
            var definition = await client.DescribeComponentTypeAsync("[FrooxEngine]FrooxEngine.UIX.Slider<float>", ct);
            var direction = await client.DescribeTypeAsync(ReflectedMemberType.ValueType(definition, "SlideDirection"), ct);
            Assert.Contains("Horizontal", direction.EnumValues!.Keys);
            Assert.Contains("Vertical", direction.EnumValues.Keys);
            // Test endpoints with the same assertion/probe path used by agents.
            var endpointReports = new List<ApplyTestReport>();
            foreach (var value in new[] { 0f, 0.5f, 1f })
            {
                var probe = new ApplyTestSpec("Slider endpoint", [
                    new("$member:handle-rect.AnchorMin", JsonSerializer.SerializeToElement(new[] { value, 0.5f }), Phase: "after"),
                    new("$member:handle-rect.AnchorMax", JsonSerializer.SerializeToElement(new[] { value, 0.5f }), Phase: "after")],
                    new(Target: "$member:slider-slider.Value", Kind: "set-member", Safe: true, Value: JsonSerializer.SerializeToElement(value)));
                var endpoint = await world.TestAsync(doc with { Tests = [probe] }, options, true, ct);
                Assert.True(endpoint.Passed, JsonSerializer.Serialize(endpoint));
                endpointReports.Add(endpoint);
            }

            await client.SetComponentMemberAsync(slider, "SlideDirection", "Vertical", ct);
            await client.SetComponentMemberAsync(slider, "AnchorOffset", "[0.5,0]", ct);
            var verticalProbe = new ApplyTestSpec("Vertical slider", [
                new("$member:handle-rect.AnchorMin", JsonSerializer.SerializeToElement(new[] { 0.5f, 0.75f }), Phase: "after"),
                new("$member:handle-rect.AnchorMax", JsonSerializer.SerializeToElement(new[] { 0.5f, 0.75f }), Phase: "after")],
                new(Target: "$member:slider-slider.Value", Kind: "set-member", Safe: true, Value: JsonSerializer.SerializeToElement(0.75f)));
            var verticalReport = await world.TestAsync(doc with { Tests = [verticalProbe] }, options, true, ct);
            Assert.True(verticalReport.Passed, JsonSerializer.Serialize(verticalReport));
            await client.SetComponentMemberAsync(slider, "SlideDirection", "Horizontal", ct);
            await client.SetComponentMemberAsync(slider, "AnchorOffset", "[0,0.5]", ct);

            await client.SetComponentMemberAsync(text, "Content", "Keep my edit", ct);
            await client.SetComponentMemberAsync(await Id("enabled-state"), "Value", "true", ct);
            await client.SetComponentMemberAsync(await Id("selection-state"), "Value", "2", ct);
            await client.SetComponentMemberAsync(slider, "Value", "0.8", ct);
            var again = await world.ApplyAsync(doc, options, ct);
            Assert.Equal(0, again.ComponentsUpdated + again.ComponentsAdded + again.SlotsCreated + again.SlotsUpdated);
            Assert.Equal("Keep my edit", (await client.GetComponentAsync(text, ct)).Members["Content"].Value!.GetValue<string>());
            Assert.True((await client.GetComponentAsync(await Id("enabled-state"), ct)).Members["Value"].Value!.GetValue<bool>());
            Assert.Equal(2, (await client.GetComponentAsync(await Id("selection-state"), ct)).Members["Value"].Value!.GetValue<int>());
            Assert.Equal(0.8f, (await client.GetComponentAsync(slider, ct)).Members["Value"].Value!.GetValue<float>(), 4);
            var a = await world.ResolveSlotSelectorAsync("$slot:page-a", options.StateFile, ct);
            var b = await world.ResolveSlotSelectorAsync("$slot:page-b", options.StateFile, ct);
            Assert.False((await client.GetSlotAsync(a, 0, false, ct)).IsActive);
            Assert.True((await client.GetSlotAsync(b, 0, false, ct)).IsActive);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            { session = await client.GetSessionInfoAsync(ct), plan, applied, report, unmatchedReport, endpointReports, verticalReport, again, preserved = true, tabVisibilityVerified = true }), ct);

            Task<string> Id(string key) => world.ResolveComponentSelectorAsync("$component:" + key, options.StateFile, ct);
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

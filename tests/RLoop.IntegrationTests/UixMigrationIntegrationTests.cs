using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

[Collection("Screenshot exports")]
public sealed class UixMigrationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComparesParentPreparationBeforeAndAfterMovingExistingUix()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(15), token);
        var name = "ResoLoop_Test_UixMigration_" + Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), name));
        Directory.CreateDirectory(directory);
        string? root = null;
        var evidence = new List<object>();
        try
        {
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-20, 2, -20)), token);
            var boards = new List<(string Canvas, string Leaf, string Rect, string Probe, bool Prepared)>();
            foreach (var prepared in new[] { false, true })
            {
                var canvas = await client.CreateSlotAsync(new SlotCreateRequest(root, prepared ? "PreparedParent" : "LateParent",
                    new Vector3Value(prepared ? 2 : 0, 0, 0), Scale: new Vector3Value(.003f, .003f, .003f)), token);
                await Add(canvas, "Canvas", new() { ["Size"] = "[400,300]", ["UnitScale"] = "1", ["PixelScale"] = "1" });
                await Add(canvas, "RectTransform", new());
                var leaf = await client.CreateSlotAsync(new SlotCreateRequest(canvas, "ExistingGraphic"), token);
                var rect = await Add(leaf, "RectTransform", new() { ["AnchorMin"] = "[0,0]", ["AnchorMax"] = "[1,1]" });
                await Add(leaf, "LayoutElement", new() { ["MinWidth"] = "200", ["MinHeight"] = "80", ["PreferredHeight"] = "100" });
                await Add(leaf, "Image", new() { ["Tint"] = prepared ? "[0.1,0.8,0.3,1]" : "[0.9,0.2,0.1,1]" });
                var probe = await Add(leaf, "RectTransformComputedProperties", new() { ["Rect"] = rect });
                boards.Add((canvas, leaf, rect, probe, prepared));
            }
            await Task.Delay(500, token);
            await Capture("before");
            foreach (var board in boards)
            {
                var before = await client.GetComponentAsync(board.Probe, token);
                var parent = await client.CreateSlotAsync(new SlotCreateRequest(board.Canvas, "NewLayoutParent"), token);
                if (board.Prepared) await Prepare(parent);
                await client.UpdateSlotAsync(new SlotUpdateRequest(board.Leaf, ParentId: parent), token);
                if (!board.Prepared) await Prepare(parent);
                await Task.Delay(500, token);
                var moved = await client.GetSlotAsync(board.Leaf, 0, true, token);
                Assert.Equal(parent, moved.ParentId);
                Assert.Contains(moved.Components, component => component.Id == board.Rect);
                var after = await client.GetComponentAsync(board.Probe, token);
                evidence.Add(new { board.Prepared, before, after, leafIdPreserved = moved.Id == board.Leaf });
            }
            await Capture("after");
            // Exercise the actual apply pipeline as well as the primitive control experiment.
            var manifest = JsonNode.Parse("""
                {"schemaVersion":"1","ownership":{"key":"uix-apply-migration"},
                 "slot":{"key":"canvas","name":"ApplyPrepared","position":[0,-1,0],"scale":[0.003,0.003,0.003]},
                 "components":[{"key":"canvas-rect","type":"FrooxEngine.UIX.RectTransform"},
                   {"key":"canvas-component","type":"FrooxEngine.UIX.Canvas","fields":{"Size":[400,300],"UnitScale":1,"PixelScale":1}}],
                 "children":[{"slot":{"key":"leaf","name":"RetainedCyan"},"components":[
                   {"key":"leaf-rect","type":"FrooxEngine.UIX.RectTransform"},
                   {"key":"leaf-size","type":"FrooxEngine.UIX.LayoutElement","fields":{"MinHeight":80,"PreferredHeight":100}},
                   {"key":"leaf-image","type":"FrooxEngine.UIX.Image","fields":{"Tint":[0,0.8,0.9,1]}}]}]}
                """)!;
            manifest["slot"]!["parent"] = root;
            var applyPath = Path.Combine(directory, "apply.json");
            await File.WriteAllTextAsync(applyPath, manifest.ToJsonString(), token);
            var world = new WorldService(client);
            var options = new ApplyOptions(Path.Combine(directory, "state.json"));
            await world.ApplyAsync(ApplyDocument.Load(applyPath), options, token);
            var originalId = await world.ResolveSlotSelectorAsync("$slot:leaf", options.StateFile, token);
            var retainedLeaf = manifest["children"]![0]!.DeepClone();
            manifest["children"] = JsonNode.Parse("""
                [{"slot":{"key":"parent","name":"PreparedLayout"},"components":[
                  {"key":"parent-rect","type":"FrooxEngine.UIX.RectTransform"},
                  {"key":"parent-layout","type":"FrooxEngine.UIX.VerticalLayout","fields":{"ForceExpandWidth":true,"ForceExpandHeight":false}}],"children":[]}]
                """);
            ((JsonArray)manifest["children"]![0]!["children"]!).Add(retainedLeaf);
            await File.WriteAllTextAsync(applyPath, manifest.ToJsonString(), token);
            var migrated = await world.ApplyAsync(ApplyDocument.Load(applyPath), options, token);
            Assert.Equal(originalId, await world.ResolveSlotSelectorAsync("$slot:leaf", options.StateFile, token));
            await Task.Delay(500, token);
            await Capture("apply");
            var again = await world.ApplyAsync(ApplyDocument.Load(applyPath), options, token);
            Assert.Equal(0, again.SlotsUpdated);
            Assert.Equal(0, again.ComponentsUpdated);
            Assert.Equal(0, again.ComponentsAdded);
            evidence.Add(new { apply = migrated, reapply = again, leafIdPreserved = true });
            var audit = await UixAuditService.InspectAsync(client, root, 6, 32, token);
            Assert.False(audit.Truncated);
            Assert.True(audit.Valid);
            await File.WriteAllTextAsync(Path.Combine(directory, "observations.json"), JsonSerializer.Serialize(
                new { session = await client.GetSessionInfoAsync(token), evidence, audit }, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally
        {
            if (root is not null)
            {
                var observed = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(root, observed.Id);
                Assert.Equal(name, observed.Name);
                Assert.Equal("Root", observed.ParentId);
                Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root);
                await File.WriteAllTextAsync(Path.Combine(directory, "cleanup.json"), JsonSerializer.Serialize(new { root, name, deleted = true }));
            }
        }

        async Task<string> Add(string slot, string shortType, Dictionary<string, string> fields)
        {
            var type = "FrooxEngine.UIX." + shortType;
            var definition = await client.DescribeComponentTypeAsync(type, token);
            foreach (var member in fields.Keys) Assert.Contains(definition.Members, item => item.Name == member);
            return (await client.AddComponentAsync(slot, definition.FullTypeName, fields, token)).Id;
        }
        async Task Prepare(string parent)
        {
            await Add(parent, "RectTransform", new() { ["AnchorMin"] = "[0,0]", ["AnchorMax"] = "[1,1]" });
            await Add(parent, "VerticalLayout", new() { ["ForceExpandWidth"] = "true", ["ForceExpandHeight"] = "false" });
        }
        async Task Capture(string stage)
        {
            if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_CAPTURE_INTEGRATION") != "1") return;
            var path = Path.Combine(directory, "capture.json");
            await File.WriteAllTextAsync(path, """
                {"schemaVersion":"1","ownership":{"key":"uix-migration-camera"},"slot":{"key":"root","name":"ResoLoop_Test_UixCapture"},
                 "cameras":{"main":{"position":[-19,1.5,-23],"target":[-19,1.5,-20],"width":1200,"height":800,"fieldOfView":55}}}
                """, token);
            await new LiveCaptureService(client).CaptureAsync(ApplyDocument.Load(path), "main", Path.Combine(directory, stage + ".jpg"),
                Environment.GetEnvironmentVariable("RESOLOOP_SCREENSHOTS_DIR") ?? ScreenshotDirectoryResolver.ResolveDefault(), cancellationToken: token);
        }
    }
}

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class CanvasFramingIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveFramingCapturesTransformedCanvasAndPreservesItsPose()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(20), ct);
        var name = "ResoLoop_Test_CanvasFrame_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS") ?? Path.GetTempPath()), name);
        Directory.CreateDirectory(directory);
        string? root = null;
        try
        {
            var rotation = Quaternion.CreateFromYawPitchRoll(.35f, .1f, .3f);
            root = await client.CreateSlotAsync(new("Root", name, new(-42, 2, -42), new(rotation.X, rotation.Y, rotation.Z, rotation.W), new(1.2f, .8f, 1)), ct);
            var world = new WorldService(client);
            var node = JsonNode.Parse("""
            {"schemaVersion":"1","ownership":{"key":"framing"},
             "slot":{"key":"canvas","name":"Canvas","scale":[0.002,0.002,0.002]},
             "components":[{"key":"rect","type":"FrooxEngine.UIX.RectTransform"},
               {"key":"canvas-ui","type":"FrooxEngine.UIX.Canvas","fields":{"Size":[600,400]}}],
             "children":[
               {"slot":{"key":"background","name":"Background"},"components":[
                 {"key":"bg-rect","type":"FrooxEngine.UIX.RectTransform"},
                 {"key":"bg-image","type":"FrooxEngine.UIX.Image","fields":{"Tint":[0.02,0.08,0.15,1],"Material":"$component:front","InteractionTarget":false}}],
                "children":[{"slot":{"key":"rear","name":"Rear"},"components":[
                 {"key":"rear-rect","type":"FrooxEngine.UIX.RectTransform"},
                 {"key":"rear-image","type":"FrooxEngine.UIX.Image","fields":{"Tint":[0.02,0.08,0.15,1],"Material":"$component:back","InteractionTarget":false}}]}]},
               {"slot":{"key":"label","name":"Label"},"components":[
                 {"key":"label-rect","type":"FrooxEngine.UIX.RectTransform","fields":{"OffsetMin":[30,30],"OffsetMax":[-30,-30]}},
                 {"key":"label-text","type":"FrooxEngine.UIX.Text","fields":{"Size":34,"Content":"AUTO FRAME\nLive rotated Canvas\n600 x 400","Color":[0.8,1,1,1],"InteractionTarget":false}}]}
             ]}
            """)!;
            node["slot"]!["parent"] = root;
            foreach (var (key, side) in new[] { ("front", "Front"), ("back", "Back") })
            {
                var provider = AuthoringSchema.Provider(key, "FrooxEngine.UI_UnlitMaterial");
                var material = provider.Components![0] with { Fields = new Dictionary<string, JsonElement>
                {
                    ["Sidedness"] = JsonSerializer.SerializeToElement(side), ["ZWrite"] = JsonSerializer.SerializeToElement("On"),
                    ["ZTest"] = JsonSerializer.SerializeToElement("LessOrEqual"), ["OffsetFactor"] = JsonSerializer.SerializeToElement(1),
                    ["OffsetUnits"] = JsonSerializer.SerializeToElement(100)
                } };
                node["children"]!.AsArray().Add(JsonSerializer.SerializeToNode(provider with { Components = [material] }, AuthoringSchema.JsonOptions));
            }
            var path = Path.Combine(directory, "panel.json");
            await File.WriteAllTextAsync(path, node.ToJsonString(), ct);
            var document = ApplyDocument.Load(path);
            var options = new ApplyOptions(Path.Combine(directory, "state.json"));
            var plan = await world.PlanApplyAsync(document, options, ct);
            Assert.Empty(plan.Warnings);
            var applied = await world.ApplyAsync(document, options, ct);
            var canvas = await world.ResolveSlotSelectorAsync("$slot:canvas", options.StateFile, ct);
            var before = await client.GetSlotAsync(canvas, 0, false, ct);
            var captures = new List<CaptureArtifact>();
            foreach (var view in new[] { "front", "rear" })
            {
                var framing = await CanvasFraming.ObserveAsync(client, canvas, view, 900, 700, cancellationToken: ct);
                Assert.Equal(new[] { 600f, 400, 0 }, framing.LocalSize);
                var captureDocument = document with { Cameras = new Dictionary<string, ApplyCameraSpec> { [view] = framing.Camera } };
                var capture = await new LiveCaptureService(client).CaptureAsync(captureDocument, view, Path.Combine(directory, view + ".jpg"),
                    Environment.GetEnvironmentVariable("RESOLOOP_SCREENSHOTS_DIR") ?? ScreenshotDirectoryResolver.ResolveDefault(), cancellationToken: ct);
                Assert.True(capture.Ownership!.CleanupCompleted);
                captures.Add(capture with { Framing = framing });
            }
            var after = await client.GetSlotAsync(canvas, 0, false, ct);
            Assert.Equal(before.Position, after.Position);
            Assert.Equal(before.Rotation, after.Rotation);
            Assert.Equal(before.Scale, after.Scale);
            var again = await world.ApplyAsync(document, options, ct);
            Assert.Equal(0, again.ComponentsAdded + again.ComponentsUpdated + again.SlotsCreated + again.SlotsUpdated);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { applied, captures, again, posePreserved = true }, AuthoringSchema.JsonOptions), ct);
        }
        finally
        {
            if (root is not null)
            {
                var observed = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(name, observed.Name);
                Assert.Equal("Root", observed.ParentId);
                await client.DeleteSlotAsync(root);
                await File.WriteAllTextAsync(Path.Combine(directory, "cleanup.json"), JsonSerializer.Serialize(new { id = root, name, deleted = true }));
            }
        }
    }
}

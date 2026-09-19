using System.Security.Cryptography;
using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

[Collection("Screenshot exports")]
public sealed class TextureVariantIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ComparesTextureVariantsWithoutChangingExistingProviders()
    {
        var samples = Environment.GetEnvironmentVariable("RESOLOOP_TEXTURE_SAMPLES")?.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1" || samples is not { Length: > 0 }) return;
        Assert.InRange(samples.Length, 1, 3);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(20), token);
        var name = "ResoLoop_Test_TextureVariants_" + Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), name));
        Directory.CreateDirectory(directory);
        string? root = null;
        var records = new List<object>();
        var providers = new List<string>();
        try
        {
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-40, 3, -40)), token);
            var canvas = await client.CreateSlotAsync(new SlotCreateRequest(root, "Comparison", Scale: new Vector3Value(.003f, .003f, .003f)), token);
            await Add(canvas, "FrooxEngine.UIX.RectTransform", new());
            await Add(canvas, "FrooxEngine.UIX.Canvas", new() { ["Size"] = "[1200,650]", ["UnitScale"] = "1", ["PixelScale"] = "1" });
            for (var row = 0; row < samples.Length; row++)
            {
                var source = Path.GetFullPath(samples[row]);
                Assert.True(File.Exists(source));
                var url = await client.ImportAssetAsync(new ApplyAssetSpec("texture", source), source, token);
                var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source, token)));
                for (var column = 0; column < 4; column++)
                {
                    var uncompressed = column >= 2;
                    var crunch = column % 2 == 0;
                    var assetSlot = await client.CreateSlotAsync(new SlotCreateRequest(root, $"Asset {row} {column}"), token);
                    var texture = await Add(assetSlot, "FrooxEngine.StaticTexture2D", new()
                    {
                        ["URL"] = url, ["Uncompressed"] = uncompressed ? "true" : "false", ["CrunchCompressed"] = crunch ? "true" : "false"
                    });
                    providers.Add(texture);
                    var sprite = await Add(assetSlot, "FrooxEngine.SpriteProvider", new() { ["Texture"] = texture });
                    var tile = await client.CreateSlotAsync(new SlotCreateRequest(canvas, $"Sample {row}, Uncompressed={uncompressed}, Crunch={crunch}"), token);
                    var x = column * 300 + 5;
                    var y = row * 200 + 40;
                    await Add(tile, "FrooxEngine.UIX.RectTransform", new()
                    {
                        ["AnchorMin"] = "[0,1]", ["AnchorMax"] = "[0,1]", ["OffsetMin"] = $"[{x},{-y - 150}]", ["OffsetMax"] = $"[{x + 285},{-y}]"
                    });
                    await Add(tile, "FrooxEngine.UIX.Image", new() { ["Tint"] = "[0.85,0.9,0.95,1]", ["InteractionTarget"] = "false" });
                    var graphic = await client.CreateSlotAsync(new SlotCreateRequest(tile, "Texture"), token);
                    await Add(graphic, "FrooxEngine.UIX.RectTransform", new());
                    await Add(graphic, "FrooxEngine.UIX.Image", new() { ["Sprite"] = sprite, ["InteractionTarget"] = "false" });
                    records.Add(new { row, column, source, digest, url, texture, sprite, uncompressed, crunch });
                }
            }
            await Capture("texture-early");
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            await Capture("texture-settled");
            // Re-request the same default variant after other variants are ready. No cache deletion.
            for (var row = 0; row < samples.Length; row++)
            {
                await client.SetComponentMemberAsync(providers[row * 4], "Uncompressed", "true", token);
                await client.SetComponentMemberAsync(providers[row * 4], "Uncompressed", "false", token);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            await Capture("texture-default-retry");
            var observations = new List<ComponentInfo>();
            foreach (var provider in providers) observations.Add(await client.GetComponentAsync(provider, token));
            await File.WriteAllTextAsync(Path.Combine(directory, "texture-observations.json"), JsonSerializer.Serialize(new
            {
                session = await client.GetSessionInfoAsync(token), records, observations,
                verification = "Public fields and separate captures; decoded texture/load status is not exposed by this observation."
            }, new JsonSerializerOptions { WriteIndented = true }), token);
        }
        finally
        {
            if (root is not null)
            {
                var current = await client.GetSlotAsync(root, 0, false);
                Assert.Equal(name, current.Name);
                Assert.Equal("Root", current.ParentId);
                Assert.NotEqual("Root", root);
                await client.DeleteSlotAsync(root);
                await File.WriteAllTextAsync(Path.Combine(directory, "texture-cleanup.json"), JsonSerializer.Serialize(new { root, name, deleted = true }));
            }
        }

        async Task<string> Add(string slot, string type, Dictionary<string, string> fields)
        {
            var definition = await client.DescribeComponentTypeAsync(type, token);
            foreach (var member in fields.Keys) Assert.Contains(definition.Members, item => item.Name == member);
            return (await client.AddComponentAsync(slot, definition.FullTypeName, fields, token)).Id;
        }
        async Task Capture(string stage)
        {
            if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_CAPTURE_INTEGRATION") != "1") return;
            var path = Path.Combine(directory, "texture-camera.json");
            await File.WriteAllTextAsync(path, """
                {"schemaVersion":"1","ownership":{"key":"texture-camera"},"slot":{"key":"root","name":"ResoLoop_Test_TextureCamera"},
                "cameras":{"main":{"position":[-40,3,-44],"target":[-40,3,-40],"width":1600,"height":1000,"fieldOfView":38}}}
                """, token);
            await new LiveCaptureService(client).CaptureAsync(ApplyDocument.Load(path), "main", Path.Combine(directory, stage + ".jpg"),
                Environment.GetEnvironmentVariable("RESOLOOP_SCREENSHOTS_DIR") ?? ScreenshotDirectoryResolver.ResolveDefault(), cancellationToken: token);
        }
    }
}

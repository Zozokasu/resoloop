using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

[Collection("Screenshot exports")]
public sealed class UixResponsiveIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SharedAssetsAndResponsiveCasesRestoreAndConverge()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(new Uri(Environment.GetEnvironmentVariable("RESONITE_LINK_URL")!), TimeSpan.FromSeconds(20), token);
        var name = "ResoLoop_Test_UixResponsive_" + Guid.NewGuid().ToString("N");
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), name));
        Directory.CreateDirectory(directory);
        string? root = null;
        try
        {
            root = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(-25, 2, -25)), token);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "examples", "uix-responsive.json"), token))!;
            json["slot"]!["parent"] = root;
            json["slot"]!["position"] = JsonNode.Parse("[0,0,0]");
            json["cameras"]!["main"]!["position"] = JsonNode.Parse("[-25,2,-27]");
            json["cameras"]!["main"]!["target"] = JsonNode.Parse("[-25,2,-25]");
            var path = Path.Combine(directory, "responsive.json");
            await File.WriteAllTextAsync(path, json.ToJsonString(), token);
            var document = ApplyDocument.Load(path);
            var world = new WorldService(client);
            var options = new ApplyOptions(Path.Combine(directory, "responsive.state.json"));
            var applied = await world.ApplyAsync(document, options, token);
            var probe = await world.TestAsync(document, options, true, token);
            Assert.True(probe.Passed);
            var session = await client.GetSessionInfoAsync(token);
            var canvas = await world.ResolveStableReferenceAsync(options.StateFile!, "$component:canvas-component", session.UniqueSessionId, token);
            var label = await world.ResolveStableReferenceAsync(options.StateFile!, "$component:first-text", session.UniqueSessionId, token);
            var scroll = await world.ResolveStableReferenceAsync(options.StateFile!, "$component:content-scroll", session.UniqueSessionId, token);
            var font = await world.ResolveStableReferenceAsync(options.StateFile!, "$component:font", session.UniqueSessionId, token);
            var originalText = (await client.GetComponentAsync(label.Id, token)).Members["Content"].Value!.GetValue<string>();
            Assert.Equal(font.Id, (await client.GetComponentAsync(label.Id, token)).Members["Font"].TargetId);
            var second = await world.ResolveStableReferenceAsync(options.StateFile!, "$component:second-text", session.UniqueSessionId, token);
            Assert.Equal(font.Id, (await client.GetComponentAsync(second.Id, token)).Members["Font"].TargetId);
            try
            {
                await Capture("responsive-normal");
                await client.SetComponentMemberAsync(canvas.Id, "Size", "[320,240]", token);
                await Capture("responsive-narrow");
                await client.SetComponentMemberAsync(label.Id, "Content", string.Join(" ", Enumerable.Repeat("Long content must wrap inside its equal-width card.", 8)), token);
                await Capture("responsive-long");
                await client.SetComponentMemberAsync(scroll.Id, "NormalizedPosition", "[0,1]", token);
                await Capture("responsive-bottom");
                await client.SetComponentMemberAsync(canvas.Id, "Size", "[640,240]", token);
                await client.SetComponentMemberAsync(label.Id, "Content", originalText, token);
                await client.SetComponentMemberAsync(scroll.Id, "NormalizedPosition", "[0,0]", token);
                await Capture("responsive-wide");
            }
            finally
            {
                await client.SetComponentMemberAsync(canvas.Id, "Size", "[480,240]");
                await client.SetComponentMemberAsync(label.Id, "Content", originalText);
                await client.SetComponentMemberAsync(scroll.Id, "NormalizedPosition", "[0,0]");
            }
            Assert.Equal(originalText, (await client.GetComponentAsync(label.Id, token)).Members["Content"].Value!.GetValue<string>());
            var again = await world.ApplyAsync(document, options, token);
            Assert.Equal(0, again.SlotsUpdated);
            Assert.Equal(0, again.ComponentsUpdated);
            Assert.Equal(0, again.ComponentsAdded);
            var audit = await UixAuditService.InspectAsync(client, applied.SlotId, 8, 32, token);
            Assert.True(audit.Valid);
            Assert.False(audit.Truncated);
            await File.WriteAllTextAsync(Path.Combine(directory, "responsive-observations.json"), JsonSerializer.Serialize(new { session, probe, again, audit }, new JsonSerializerOptions { WriteIndented = true }), token);

            async Task Capture(string stage)
            {
                if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_CAPTURE_INTEGRATION") != "1") return;
                await Task.Delay(750, token);
                await new LiveCaptureService(client).CaptureAsync(document, "main", Path.Combine(directory, stage + ".jpg"),
                    Environment.GetEnvironmentVariable("RESOLOOP_SCREENSHOTS_DIR") ?? ScreenshotDirectoryResolver.ResolveDefault(), cancellationToken: token);
            }
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
                await File.WriteAllTextAsync(Path.Combine(directory, "responsive-cleanup.json"), JsonSerializer.Serialize(new { root, name, deleted = true }));
            }
        }
    }
}


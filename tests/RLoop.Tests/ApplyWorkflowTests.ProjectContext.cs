using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private static readonly Lazy<string> JsxPackage = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "resoloop-jsx", "package.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var package = Path.Combine(directory.FullName, "tools", "resoloop-jsx");
        RunNode(package, "node_modules/typescript/bin/tsc", "-p", "tsconfig.json");
        return package;
    });

    private static void RunNode(string cwd, params string[] arguments)
    {
        var start = new ProcessStartInfo("node") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(true); Assert.Fail("JSX build timed out"); }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task JsonToActualTsxBuildPreservesFlatLegacyKeysIdsAndStateAcrossOutputMoves(int version)
    {
        var package = JsxPackage.Value;
        var workspace = Path.Combine(package, ".s2-1b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var project = Path.Combine(workspace, "project");
            var content = Path.Combine(project, "content");
            var other = Path.Combine(workspace, "other-project");
            Directory.CreateDirectory(content);
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(project, ".resoloop.json"), "{}");
            File.WriteAllText(Path.Combine(other, ".resoloop.json"), "{}");
            var originalPath = Path.Combine(content, "old.json");
            File.WriteAllText(originalPath, """
                {"schemaVersion":"1","ownership":{"key":"house-world"},
                 "slot":{"key":"root","name":"House","parent":"Root"},
                 "components":[{"type":"Test.Target","fields":{"Enabled":true}}],
                 "children":[{"slot":{"name":"Child"},"components":[{"key":"flat-component","type":"Test.Target","fields":{"Enabled":true}}]}]}
                """);
            var original = ApplyDocument.Load(originalPath);
            var client = new FakeResoniteClient(original);
            var service = new WorldService(client);
            var first = await service.ApplyAsync(original);
            var statePath = first.StateFile!;
            Assert.Equal(Path.Combine(project, ".resoloop", "state", "house-world.json"), statePath);
            var checkpoint = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
            Assert.Contains("$path:Root/House/Child", checkpoint["slots"]!.AsObject().Select(x => x.Key));
            var componentKey = checkpoint["components"]!.AsObject().Single(x => x.Value!["slotKey"]!.GetValue<string>() == "root").Key;
            checkpoint["schemaVersion"] = version;
            if (version == 1)
                foreach (var slot in checkpoint["slots"]!.AsObject()) slot.Value!.AsObject().Remove("pathSegments");
            File.WriteAllText(statePath, checkpoint.ToJsonString());
            var ids = StateIds(checkpoint);
            var entry = Path.Combine(content, "main.tsx");
            File.WriteAllText(entry, $$"""
                import { Slot, Component } from "resoloop-jsx";
                export const ownership = { key: "house-world" };
                export default <Slot key="root" name="House" parent="Root">
                  <Component key={{JsonSerializer.Serialize(componentKey)}} type="Test.Target" fields={ { Enabled: true } } />
                  <Slot key="$path:Root/House/Child" name="Child">
                    <Component key="flat-component" type="Test.Target" fields={ { Enabled: true } } />
                  </Slot>
                </Slot>;
                """);
            var outputs = new[] { Path.Combine(project, "build", "main.json"), Path.Combine(other, "main.json"), Path.Combine(workspace, "outside.json") };
            foreach (var output in outputs)
            {
                RunNode(package, "dist/src/cli.js", "build", entry, "-o", output);
                await AssertConverged(output);
                var copy = Path.Combine(_root, "copied.json");
                File.Copy(output, copy, true);
                await AssertConverged(copy);
            }
            // Explicit build root wins even when another config is nearer to the source.
            File.WriteAllText(Path.Combine(content, ".resoloop.json"), "{}");
            RunNode(package, "dist/src/cli.js", "build", entry, "-o", outputs[0], "--project-root", project);
            await AssertConverged(outputs[0]);

            async Task AssertConverged(string output)
            {
                client.ResetWriteCounts();
                var document = ApplyDocument.Load(output);
                Assert.Equal(statePath, document.ResolveStatePath());
                var plan = await service.PlanApplyAsync(document, new(RequireState: true));
                Assert.Empty(plan.Changes);
                Assert.Equal(0, plan.Creates + plan.Updates + plan.Renames + plan.Deletes);
                var result = await service.ApplyAsync(document, new(RequireState: true));
                Assert.Equal(statePath, result.StateFile);
                Assert.Equal(0, result.SlotsCreated + result.SlotsUpdated + result.SlotsDeleted + result.ComponentsAdded + result.ComponentsUpdated + result.ComponentsDeleted);
                Assert.Equal(0, client.Writes);
                var saved = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
                Assert.Equal(2, saved["schemaVersion"]!.GetValue<int>());
                Assert.Equal(ids, StateIds(saved));
            }
        }
        finally { Directory.Delete(workspace, true); }
    }

    private static string[] StateIds(JsonObject state) => new[] { "slots", "components" }
        .SelectMany(kind => state[kind]!.AsObject().Select(x => kind + ":" + x.Key + "=" + x.Value!["id"]!.GetValue<string>()))
        .Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"invalid\"")]
    [InlineData("{}")]
    [InlineData("{\"projectRoot\":\"relative\",\"source\":\"main.tsx\"}")]
    [InlineData("{\"projectRoot\":42,\"source\":\"main.tsx\"}")]
    [InlineData("{\"projectRoot\":\"C:/project\",\"source\":\"\"}")]
    [InlineData("{\"projectRoot\":\"C:/project\",\"source\":\"C:/main.tsx\"}")]
    [InlineData("{\"projectRoot\":\"C:/project\",\"source\":\"main.tsx\",\"unknown\":true}")]
    [InlineData("{\"projectRoot\":\"C:/project\",\"source\":\"main.tsx\",\"ownershipSource\":null}")]
    public void MalformedAuthoringStopsWithoutFallbackEvenWithExplicitState(string authoring)
    {
        var path = Path.Combine(_root, "invalid-context.json");
        File.WriteAllText(path, "{\"schemaVersion\":\"1\",\"authoring\":" + authoring + "}");
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(path).ResolveStatePath("override.json"));
        Assert.Equal("APPLY_PROJECT_CONTEXT_INVALID", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
    }

    [Fact]
    public async Task RequireStateStopsPlanAndApplyWithoutCreatingCheckpointOrLock()
    {
        var document = Document("missing-checkpoint", "[]");
        var path = document.ResolveStatePath();
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        foreach (var apply in new[] { false, true })
        {
            var error = await Assert.ThrowsAsync<RLoopException>(async () =>
            {
                if (apply) await service.ApplyAsync(document, new(RequireState: true));
                else await service.PlanApplyAsync(document, new(RequireState: true));
            });
            Assert.Equal("APPLY_STATE_NOT_FOUND", error.Code);
            Assert.Equal(ExitCodes.NotFound, error.ExitCode);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".lock"));
            Assert.Equal(0, client.Writes);
        }
        Assert.Equal(1, (await service.ApplyAsync(document)).SlotsCreated);
    }

    [Fact]
    public async Task SanitizeCollisionStillRequiresExactOwnershipMatch()
    {
        var original = Document("same key", "[]");
        var other = original with { Ownership = new("same_key") };
        Assert.Equal(original.ResolveStatePath(), other.ResolveStatePath());
        var client = new FakeResoniteClient(original);
        var service = new WorldService(client);
        await service.ApplyAsync(original);
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(other, new(RequireState: true)));
        Assert.Equal("APPLY_STATE_OWNERSHIP_MISMATCH", error.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public void AuthoringContextAndHandwrittenSearchKeepExplicitStateCwdRelative()
    {
        File.WriteAllText(Path.Combine(_root, ".resoloop.json"), "{}");
        var original = Document("context", "[]");
        var generated = original with { Authoring = new(Path.Combine(_root, "author-project"), "content/main.tsx") };
        Assert.Equal(Path.Combine(_root, ".resoloop", "state", "context.json"), original.ResolveStatePath());
        Assert.Equal(Path.Combine(_root, "author-project", ".resoloop", "state", "context.json"), generated.ResolveStatePath());
        Assert.Equal(Path.GetFullPath("explicit.state.json"), generated.ResolveStatePath("explicit.state.json"));
        Assert.Equal(Path.GetFullPath("explicit.state.json"), original.ResolveStatePath("explicit.state.json"));
        Assert.Equal(Path.Combine(_root, "author-project", "content"), generated.ResourceDirectory);
        Assert.Equal(_root, original.ResourceDirectory);
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.False(JsonSerializer.SerializeToElement(original, jsonOptions).TryGetProperty("authoring", out _));
        var metadata = JsonSerializer.SerializeToElement(generated, jsonOptions).GetProperty("authoring");
        Assert.False(metadata.TryGetProperty("ownershipSource", out _));
        Assert.Equal("APPLY_PROJECT_CONTEXT_INVALID", Assert.Throws<RLoopException>(() =>
            (generated with { Authoring = new("relative", "main.tsx") }).ResolveStatePath("explicit.state.json")).Code);
        Assert.Contains(AuthoringSchema.Describe("document").Properties, x => x.Name == "authoring" && !x.Required);
        var args = ParsedArguments.Parse(["diff", "--require-state", "main.json"]);
        Assert.True(args.Has("require-state"));
        Assert.Equal(["diff", "main.json"], args.Positionals);
    }

    [Fact]
    public async Task AuthorSourceDirectoryIsSharedByAssetsMeshBoundsAndBookmarkOutputWhileIncludesStayJsonRelative()
    {
        var source = Path.Combine(_root, "content");
        var output = Path.Combine(_root, "generated");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(source, "shape.mesh.json"), """{"vertices":[{"position":{"x":-2,"y":0,"z":0}},{"position":{"x":4,"y":3,"z":1}}]}""");
        File.WriteAllText(Path.Combine(output, "include.json"), """
            {"children":[{"slot":{"key":"included","name":"Included"}}]}
            """);
        var path = Path.Combine(output, "main.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"resources"},"slot":{"key":"root","name":"Resources"},
             "authoring":{"projectRoot":"PROJECT_ROOT","source":"content/main.tsx"},
             "include":"include.json",
             "assets":{"mesh":{"kind":"mesh","source":"shape.mesh.json"}},
             "components":[{"key":"mesh","type":"FrooxEngine.StaticMesh","fields":{"URL":"$asset:mesh"}},
                           {"key":"renderer","type":"FrooxEngine.MeshRenderer","fields":{"Mesh":"$component:mesh"}}],
             "cameras":{"main":{"position":[0,0,-10],"target":[0,0,0],"output":"artifacts/bookmark.svg"}}}
            """.Replace("\"PROJECT_ROOT\"", JsonSerializer.Serialize(_root)));
        var document = ApplyDocument.Load(path);
        Assert.Equal("included", Assert.Single(document.Children!).Slot.Key);
        Assert.True((await ApplyDocumentValidator.ValidateAsync(document)).Valid);
        var summary = await SceneArtifactService.SummarizeAsync(document);
        Assert.Equal("geometry", summary.Bounds.Kind);
        Assert.Equal(-2, summary.Bounds.Min[0]);
        Assert.Equal(4, summary.Bounds.Max[0]);
        var client = new FakeResoniteClient(document);
        await new WorldService(client).ApplyAsync(document);
        Assert.Equal(1, client.AssetImports);
        Assert.Equal(ExitCodes.Success, await Program.Main(["capture", path, "--camera", "main", "--json"]));
        Assert.True(File.Exists(Path.Combine(source, "artifacts", "bookmark.svg")));
        Assert.False(File.Exists(Path.Combine(output, "artifacts", "bookmark.svg")));
        var explicitOutput = Path.Combine(_root, "explicit.svg");
        Assert.Equal(ExitCodes.Success, await Program.Main(["capture", path, "--camera", "main", "--output", Path.GetRelativePath(Environment.CurrentDirectory, explicitOutput), "--json"]));
        Assert.True(File.Exists(explicitOutput));
    }
}

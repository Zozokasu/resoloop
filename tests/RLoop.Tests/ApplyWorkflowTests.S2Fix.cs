using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public void Documentation_SourceMapDescribesKnownAndUnknownCompatibility()
    {
        var jsx = File.ReadAllText(Path.Combine(SourceRepo(), "tools/resoloop-jsx/README.md"));
        var details = File.ReadAllText(Path.Combine(SourceRepo(), "README-DETAILS.md"));
        Assert.DoesNotContain("All current locations are explicitly", jsx);
        Assert.Contains("known original AST ranges", jsx);
        Assert.Contains("maps containing only unknown locations remain accepted", jsx);
        Assert.DoesNotContain("source は現在すべて明示", details);
        Assert.Contains("known range", details);
        Assert.Contains("位置が unknown だけの旧 map も受け付けます", details);
    }

    [Fact]
    public void Skills_CatalogGuidanceFollowsSpecificWorkflow()
    {
        string Skill(string name) => File.ReadAllText(Path.Combine(SourceRepo(), $"skills/codex/resonite-{name}/SKILL.md"));
        foreach (var name in new[] { "build", "blender", "uix", "inspect", "debug" })
            Assert.DoesNotContain("For offline declaration diagnosis with an explicitly supplied identified catalog,", Skill(name));
        var build = Skill("build");
        var catalog = build.IndexOf("For ordinary JSON, if an identified catalog", StringComparison.Ordinal);
        Assert.True(catalog > build.IndexOf("For TSX handoffs,", StringComparison.Ordinal));
        Assert.True(catalog < build.IndexOf("7. Before mutation,", StringComparison.Ordinal));
        foreach (var (name, step) in new[] { ("blender", "1. Run `resoloop status"), ("uix", "Start new declarations with"), ("debug", "For reapply or migration") })
        {
            var skill = Skill(name);
            Assert.Contains("[resonite-build](../resonite-build/SKILL.md)", skill);
            Assert.True(skill.IndexOf("If an identified catalog", StringComparison.Ordinal) > skill.IndexOf("# Resonite", StringComparison.Ordinal));
            Assert.Contains(step, skill);
        }
        Assert.DoesNotContain("If an identified catalog", Skill("inspect"));
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("nested")]
    [InlineData("unchanged")]
    public async Task Diagnostics_ForwardedPropsMutationMakesPrimaryUnknown(string mode)
    {
        var package = Path.Combine(SourceRepo(), "tools", "resoloop-jsx");
        var work = Path.Combine(package, ".s2-fix-primary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var entry = Path.Combine(work, "main.tsx"); var output = Path.Combine(work, "R1", "bundle.json");
            var change = mode == "scalar" ? "props.target = ref.component(\"missing\");" : mode == "nested" ? "props.fields.Target = ref.component(\"missing\");" : "";
            var target = mode == "unchanged" ? "missing" : "base";
            var fields = mode == "nested" ? "{props.fields}" : "{{ Target: props.target }}";
            var text = $$$"""
                import { Slot, Component, ref } from "resoloop-jsx";
                function Pass(props: { target: string; fields: { Target: string } }) {
                  {{{change}}}
                  return <Component key="holder" type="Synthetic.Holder" fields={{{fields}}} />;
                }
                export default <Slot key="root" name="Forward">
                  <Component key="base" type="Synthetic.Base" />
                  <Pass
                    target={ref.component("{{{target}}}")}
                    fields={{ Target: ref.component("base") }}
                  />
                </Slot>;
                """;
            File.WriteAllText(entry, text);
            await BuildTsx(package, entry, Path.Combine(package, "test/fixtures/catalog-v11/catalog.synthetic.json"), "R1", output);
            var diagnostics = Path.Combine(_root, "props.diagnostics.json"); var connects = 0;
            Assert.Equal(6, await Program.RunAsync(["validate", output, "--build-id", "R1", "--diagnostics", diagnostics, "--json"],
                _ => { connects++; return Task.FromResult<IResoniteClient>(new FakeResoniteClient()); }));
            Assert.Equal(0, connects);
            var payload = JsonNode.Parse(File.ReadAllText(diagnostics))!;
            var primary = payload["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == "APPLY_REFERENCE_NOT_FOUND")!["source"]!;
            Assert.Equal(mode == "unchanged" ? "known" : "unknown", primary["status"]!.GetValue<string>());
            if (mode == "unchanged")
            {
                // Handwritten expression range; no diagnostic/map output generates it.
                Assert.Equal(entry, primary["file"]!.GetValue<string>());
                Assert.Equal(9, primary["range"]!["start"]!["line"]!.GetValue<int>());
                Assert.Equal(13, primary["range"]!["start"]!["column"]!.GetValue<int>());
                Assert.Equal(37, primary["range"]!["end"]!["column"]!.GetValue<int>());
            }
        }
        finally { Directory.Delete(work, true); }
    }

    [Fact]
    public async Task Catalog_StrictSyntheticMatchingVersionsRejectedBeforeConnection()
    {
        var bundle = NewBundle();
        var file = Path.Combine(_root, "ordinary.json");
        File.WriteAllText(file, bundle.Root["ir"]!["text"]!.GetValue<string>());
        var document = ApplyDocument.Load(file);
        Assert.True((await ApplyDocumentValidator.ValidateAsync(document, catalog: ApplyCatalog.Load(bundle.Catalog))).Valid);
        var client = new FakeResoniteClient(document) { EngineVersion = "SYNTHETIC-engine-1", LinkVersion = "SYNTHETIC-server-1" };
        var connects = 0;
        var report = Path.Combine(_root, "strict.report");
        Assert.Equal(6, await Program.RunAsync(["validate", file, "--catalog", bundle.Catalog, "--strict", "--url", "ws://localhost:1", "--json", "--report", report],
            _ => { connects++; return Task.FromResult<IResoniteClient>(client); }));
        Assert.Equal(0, connects);
        Assert.Equal(0, client.Writes);
        Assert.Contains("APPLY_CATALOG_UNAVAILABLE", File.ReadAllText(report));
    }

    [Fact]
    public async Task Catalog_StrictUsesPreconnectionSnapshot()
    {
        var bundle = NewBundle(false); // Simulated provenance; no runtime evidence.
        var file = Path.Combine(_root, "ordinary.json");
        File.WriteAllText(file, bundle.Root["ir"]!["text"]!.GetValue<string>());
        var document = ApplyDocument.Load(file);
        var client = new FakeResoniteClient(document) { EngineVersion = "SYNTHETIC-engine-1", LinkVersion = "SYNTHETIC-server-1" };
        var connects = 0;
        Assert.Equal(0, await Program.RunAsync(["validate", file, "--catalog", bundle.Catalog, "--strict", "--url", "ws://localhost:1", "--json"], _ =>
        {
            connects++;
            File.WriteAllText(bundle.Catalog, "corrupted after preflight");
            return Task.FromResult<IResoniteClient>(client);
        }));
        Assert.Equal(1, connects);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task Bundle_WindowsProducerConsumerAcceptsEntryAndProjectRootCaseDifferences()
    {
        if (!OperatingSystem.IsWindows()) return;
        var package = Path.Combine(SourceRepo(), "tools", "resoloop-jsx");
        var work = Path.Combine(package, ".s2-fix-case-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(work, "Project");
        Directory.CreateDirectory(project);
        try
        {
            var entry = Path.Combine(project, "Main.tsx");
            File.WriteAllText(entry, "import { Slot } from 'resoloop-jsx'; export default <Slot key='root' name='Case' />;");
            var output = Path.Combine(work, "R1", "bundle.json");
            // The real producer receives different casing for both entry and projectRoot.
            var start = new System.Diagnostics.ProcessStartInfo("node") { WorkingDirectory = package,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { Path.Combine(package, "dist/src/cli.js"), "build", entry, "--project-root", project.ToLowerInvariant(),
                "--bundle", "--catalog", Path.Combine(package, "test/fixtures/catalog-v11/catalog.synthetic.json"), "--build-id", "R1", "-o", output })
                start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            Assert.NotNull(ApplyDocument.Load(output, "R1"));
            Assert.Equal(0, await Program.RunAsync(["validate", output, "--build-id", "R1", "--json"]));
        }
        finally { Directory.Delete(work, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagnostics_MissingRequiredStatePathNeverCreated(bool explicitState)
    {
        var document = Document("diagnostic-state", "[]");
        var file = document.SourcePath!;
        var state = document.ResolveStatePath(explicitState ? Path.Combine(_root, "missing.json") : null);
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        var report = Path.Combine(_root, "state.report");
        var client = new FakeResoniteClient();
        var args = new List<string> { "apply", file, "--require-state", "--diagnostics", state, "--url", "ws://localhost:1", "--json", "--report", report };
        if (explicitState) args.AddRange(["--state", state]);
        Assert.Equal(ExitCodes.NotFound, await Program.RunAsync(args.ToArray(), _ => Task.FromResult<IResoniteClient>(client)));
        Assert.False(File.Exists(state));
        Assert.Equal(0, client.Writes);
        Assert.Contains("APPLY_STATE_NOT_FOUND", File.ReadAllText(report));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("bundle")]
    [InlineData("catalog")]
    public async Task Diagnostics_MissingSelectedInputPathsNeverCreated(string target)
    {
        var document = Document("diagnostic-input", "[]");
        var missing = Path.Combine(_root, "missing.json");
        var report = Path.Combine(_root, "input.report");
        var args = new List<string> { "validate", target == "catalog" ? document.SourcePath! : missing,
            "--diagnostics", missing, "--json", "--report", report };
        if (target == "catalog") args.AddRange(["--catalog", missing]);
        if (target == "bundle") args.AddRange(["--build-id", "R1"]);
        Assert.Equal(target == "input" ? ExitCodes.NotFound : 6, await Program.RunAsync(args.ToArray()));
        Assert.False(File.Exists(missing));
        Assert.Contains(target == "input" ? "APPLY_FILE_NOT_FOUND" : target == "bundle" ? "APPLY_BUILD_BUNDLE_INVALID" : "APPLY_CATALOG_UNAVAILABLE", File.ReadAllText(report));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bundle_ExclusiveReadFailurePreservesRequestedAndLegacyClassification(bool requested)
    {
        var bundle = NewBundle(); var file = bundle.Save();
        if (!requested)
        {
            file = Path.Combine(_root, "ordinary.json");
            File.WriteAllText(file, bundle.Root["ir"]!["text"]!.GetValue<string>());
        }
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = Path.Combine(_root, "exclusive.report"); var connects = 0;
        var args = new List<string> { "validate", file, "--strict", "--url", "ws://localhost:1", "--json", "--report", report };
        if (requested) args.AddRange(["--build-id", "R1"]);
        Assert.Equal(requested ? 6 : ExitCodes.OperationFailed, await Program.RunAsync(args.ToArray(), _ =>
        { connects++; return Task.FromResult<IResoniteClient>(new FakeResoniteClient()); }));
        Assert.Equal(0, connects);
        Assert.Contains(requested ? "APPLY_BUILD_BUNDLE_INVALID" : "UNEXPECTED_ERROR", File.ReadAllText(report));
        if (requested) Assert.Contains("uncommitted", File.ReadAllText(report));
    }
}

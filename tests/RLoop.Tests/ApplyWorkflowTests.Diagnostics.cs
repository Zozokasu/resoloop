using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private static string SourceRepo()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "ResoLoop.slnx"))) repo = repo.Parent;
        return repo!.FullName;
    }

    private static async Task BuildTsx(string package, string entry, string catalog, string id, string output)
    {
        var start = new ProcessStartInfo("node") { WorkingDirectory = package, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { Path.Combine(package, "dist/src/cli.js"), "build", entry, "--bundle", "--catalog", catalog, "--build-id", id, "-o", output }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }

    [Fact]
    public async Task SyntheticLamp_R1_R2_R3_R4_R5_PreservesIdsAndWritesOnlyChangedMember()
    {
        var package = Path.Combine(SourceRepo(), "tools", "resoloop-jsx");
        var compiler = new ProcessStartInfo("node") { WorkingDirectory = package, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        compiler.ArgumentList.Add(Path.Combine(package, "node_modules/typescript/lib/tsc.js"));
        compiler.ArgumentList.Add("-p"); compiler.ArgumentList.Add("tsconfig.json");
        using (var process = Process.Start(compiler)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        var fixture = Path.Combine(package, "test/fixtures/lamp-s2-5");
        var work = Path.Combine(package, ".s2-5-lamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var entry = Path.Combine(work, "main.tsx"); var config = Path.Combine(work, "config.ts");
            var catalog = Path.Combine(work, "catalog.json");
            File.Copy(Path.Combine(fixture, "main.tsx"), entry); File.Copy(Path.Combine(fixture, "config.ts"), config);
            File.Copy(Path.Combine(fixture, "catalog.fake-session.json"), catalog);
            var originalTsx = File.ReadAllText(entry);
            var state = Path.Combine(_root, "lamp.state.json");
            async Task<string> Build(string id)
            {
                var output = Path.Combine(work, id, "bundle.json");
                await BuildTsx(package, entry, catalog, id, output); return output;
            }
            var r1 = ApplyDocument.Load(await Build("R1"), "R1");
            // Fixed definitions from R1, never reconstructed from an invalid R4 document.
            var client = new FakeResoniteClient(r1) { EngineVersion = "OFFLINE-FIXTURE-engine-1", LinkVersion = "OFFLINE-FIXTURE-server-1" };
            var world = new WorldService(client);
            var first = await world.ApplyAsync(r1, new ApplyOptions(state));
            Assert.Equal(1, first.SlotsCreated); Assert.Equal(2, first.ComponentsAdded); Assert.True(client.Writes > 0);
            var saved = JsonNode.Parse(File.ReadAllText(state))!;
            var holderId = saved["components"]!["holder"]!["id"]!.GetValue<string>();
            var baseId = saved["components"]!["base"]!["id"]!.GetValue<string>();
            var slotId = saved["slots"]!["lamp"]!["id"]!.GetValue<string>();
            client.ResetWriteCounts(); client.Mutations.Clear();
            var second = await world.ApplyAsync(ApplyDocument.Load(await Build("R2"), "R2"), new ApplyOptions(state, RequireState: true));
            Assert.Equal(0, second.ComponentsUpdated); Assert.Equal(0, client.Writes); Assert.Empty(client.Mutations);
            File.WriteAllText(config, "export const amount = 2;\n");
            client.ResetWriteCounts(); client.Mutations.Clear();
            var third = await world.ApplyAsync(ApplyDocument.Load(await Build("R3"), "R3"), new ApplyOptions(state, RequireState: true));
            Assert.Equal(1, third.ComponentsUpdated); Assert.Equal(1, client.Writes);
            Assert.Equal("set-members:" + holderId + ":Amount", Assert.Single(client.Mutations));
            Assert.Equal("2", client.Root.Children.Single().Components.Single(c => c.Id == holderId).Members["Amount"].Value!.ToJsonString());
            Assert.Equal(baseId, client.Root.Children.Single().Components.Single(c => c.Id == holderId).Members["Target"].TargetId);
            File.WriteAllText(entry, originalTsx.Replace("Amount: amount", "Typooo: amount").Replace("ref.component(\"base\")", "ref.component(\"missing\")"));
            client.ResetWriteCounts(); client.Mutations.Clear(); var connections = 0;
            var diagnostics = Path.Combine(_root, "R4.diagnostics.json");
            var r4 = await Build("R4");
            Assert.Equal(6, await Program.RunAsync(["apply", r4, "--build-id", "R4", "--state", state, "--require-state", "--diagnostics", diagnostics, "--json"],
                _ => { connections++; return Task.FromResult<IResoniteClient>(client); }));
            Assert.Equal(0, connections); Assert.Equal(0, client.Writes); Assert.Empty(client.Mutations);
            var report = JsonNode.Parse(File.ReadAllText(diagnostics))!;
            Assert.Equal("1", report["diagnosticVersion"]!.GetValue<string>());
            var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture, "locations.handwritten.json")))!.AsArray();
            foreach (var row in oracle)
            {
                var diagnostic = report["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == row!["code"]!.GetValue<string>());
                Assert.Equal(row!["key"]!.GetValue<string>(), diagnostic!["key"]!.GetValue<string>());
                Assert.Equal(row["member"]!.GetValue<string>(), diagnostic["member"]!.GetValue<string>());
                Assert.Equal(row["path"]!.GetValue<string>(), diagnostic["jsonPath"]!.GetValue<string>());
                Assert.Equal("apply", diagnostic["phase"]!.GetValue<string>()); Assert.Equal("R4", diagnostic["buildId"]!.GetValue<string>());
                Assert.Equal("known", diagnostic["source"]!["status"]!.GetValue<string>());
                Assert.Equal(entry, diagnostic["source"]!["file"]!.GetValue<string>());
                Assert.Equal(row["start"]![0]!.GetValue<int>(), diagnostic["source"]!["range"]!["start"]!["line"]!.GetValue<int>());
                Assert.Equal(row["start"]![1]!.GetValue<int>(), diagnostic["source"]!["range"]!["start"]!["column"]!.GetValue<int>());
                Assert.Equal(row["end"]![1]!.GetValue<int>(), diagnostic["source"]!["range"]!["end"]!["column"]!.GetValue<int>());
                var lines = File.ReadAllText(entry).Split('\n');
                int Offset(string part) => lines.Take(row[part]![0]!.GetValue<int>() - 1).Sum(line => line.Length + 1) + row[part]![1]!.GetValue<int>() - 1;
                Assert.Equal(Offset("start"), diagnostic["source"]!["range"]!["start"]!["offset"]!.GetValue<int>());
                Assert.Equal(Offset("end"), diagnostic["source"]!["range"]!["end"]!["offset"]!.GetValue<int>());
                Assert.Equal("unknown", diagnostic["completeness"]!["runtime"]!.GetValue<string>());
            }
            File.WriteAllText(entry, originalTsx);
            var fifth = await world.ApplyAsync(ApplyDocument.Load(await Build("R5"), "R5"), new ApplyOptions(state, RequireState: true));
            Assert.Equal(0, fifth.ComponentsUpdated); Assert.Equal(0, client.Writes);
            var final = JsonNode.Parse(File.ReadAllText(state))!;
            Assert.Equal(holderId, final["components"]!["holder"]!["id"]!.GetValue<string>());
            Assert.Equal(baseId, final["components"]!["base"]!["id"]!.GetValue<string>());
            Assert.Equal(slotId, final["slots"]!["lamp"]!["id"]!.GetValue<string>());
            Assert.Equal("synthetic-lamp-owner", final["ownershipKey"]!.GetValue<string>());
        }
        finally { Directory.Delete(work, true); }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("hash")]
    [InlineData("offset")]
    [InlineData("line")]
    [InlineData("column")]
    [InlineData("reversed")]
    [InlineData("missing-range")]
    [InlineData("related")]
    [InlineData("value")]
    public void Bundle_KnownLocationChecksFileHashAndRange(string fault)
    {
        var bundle = NewBundle();
        var source = new JsonObject { ["status"] = "known", ["file"] = bundle.Entry, ["sha256"] = BundleHash(File.ReadAllText(bundle.Entry)),
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["offset"] = 3, ["line"] = 1, ["column"] = 4 },
                ["end"] = new JsonObject { ["offset"] = 8, ["line"] = 1, ["column"] = 9 } } };
        var map = JsonNode.Parse(bundle.Root["map"]!["text"]!.GetValue<string>())!;
        var entry = map["entries"]![0]!;
        entry["source"] = source;
        if (fault == "file") source["file"] = bundle.Catalog;
        if (fault == "hash") source["sha256"] = new string('0', 64);
        if (fault == "offset") source["range"]!["end"]!["offset"] = 100;
        if (fault == "line") source["range"]!["start"]!["line"] = 2;
        if (fault == "column") source["range"]!["start"]!["column"] = 5;
        if (fault == "reversed") source["range"]!["end"] = new JsonObject { ["offset"] = 2, ["line"] = 1, ["column"] = 3 };
        if (fault == "missing-range") source["range"] = null;
        if (fault is "related" or "value")
        {
            entry["source"] = new JsonObject { ["status"] = "unknown" }; source["sha256"] = new string('0', 64);
            if (fault == "related") entry["related"] = new JsonArray(source); else entry["valueSource"] = source;
        }
        bundle.Root["map"] = BundlePayload(map.ToJsonString());
        AssertBundleError(Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1")), "mixed");
    }

    [Fact]
    public void DetailedValuesDistinguishKnownNullFromUnknown()
    {
        var json = JsonSerializer.SerializeToElement(new { known = ApplyDiagnosticValue.Known(null), unknown = ApplyDiagnosticValue.Unknown });
        Assert.Equal("known", json.GetProperty("known").GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("known").GetProperty("Value").ValueKind);
        Assert.Equal("unknown", json.GetProperty("unknown").GetProperty("Status").GetString());
    }

    [Fact]
    public async Task CatalogDiagnosticCarriesKnownNullWithoutParsingMessage()
    {
        var catalog = ApplyCatalog.Load(Path.Combine(SourceRepo(), "tools/resoloop-jsx/test/fixtures/catalog-v11/catalog.synthetic.json"));
        var document = Document("null-evidence", """[{"key":"holder","type":"Synthetic.Holder","fields":{"Amount":null}}]""");
        var result = await ApplyDocumentValidator.ValidateAsync(document, catalog: catalog);
        var diagnostic = Assert.Single(ApplyDiagnostics.ForResult(result).Diagnostics);
        Assert.Equal("VALUE_CONVERSION_FAILED", diagnostic.Code); Assert.Equal("known", diagnostic.Expected.Status);
        Assert.Equal("known", diagnostic.Observed.Status);
        Assert.Equal(JsonValueKind.Null, ((JsonElement)diagnostic.Observed.Value!).ValueKind);
        Assert.Equal("unknown", diagnostic.Source.Status); Assert.Equal("unknown", diagnostic.Completeness["runtime"]);
    }

    [Fact]
    public void AmbiguousMapAndLegacyPathsAreUnknown()
    {
        var bundle = NewBundle();
        var ir = JsonNode.Parse(bundle.Root["ir"]!["text"]!.GetValue<string>())!;
        ir["components"] = JsonNode.Parse("""[{"key":"holder","type":"Synthetic.Holder","fields":{"Bad":1}}]""");
        var irText = ir.ToJsonString(); bundle.Root["ir"] = BundlePayload(irText); bundle.Root["usedTypes"] = new JsonArray("Synthetic.Holder");
        var map = JsonNode.Parse(bundle.Root["map"]!["text"]!.GetValue<string>())!; map["irSha256"] = BundleHash(irText);
        var entry = new JsonObject { ["jsonPath"] = "$.components[0].fields[\"Bad\"]",
            ["pathSegments"] = new JsonArray("components", 0, "fields", "Bad"), ["entityKind"] = "component", ["key"] = "holder", ["member"] = "Bad",
            ["source"] = new JsonObject { ["status"] = "known", ["file"] = bundle.Entry, ["sha256"] = BundleHash(File.ReadAllText(bundle.Entry)),
                ["range"] = new JsonObject { ["start"] = new JsonObject { ["offset"] = 3, ["line"] = 1, ["column"] = 4 },
                    ["end"] = new JsonObject { ["offset"] = 8, ["line"] = 1, ["column"] = 9 } } } };
        map["entries"] = new JsonArray(entry, entry.DeepClone()); bundle.Root["map"] = BundlePayload(map.ToJsonString());
        var failure = Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1"));
        var diagnostic = Assert.Single(ApplyDiagnostics.ForException(failure, "validate").Diagnostics);
        Assert.Equal("COMPONENT_MEMBER_NOT_FOUND", diagnostic.Code); Assert.Equal("unknown", diagnostic.Source.Status);
        var legacy = new ApplyValidationResult(false, "1", 1, 1, 1, false,
            [new("APPLY_REFERENCE_NOT_FOUND", "unstructured", "$.components[0].fields.A.B"),
             new("APPLY_REFERENCE_NOT_FOUND", "unstructured", "$.components[0].fields.A.B")]);
        Assert.All(ApplyDiagnostics.ForResult(legacy).Diagnostics, d => { Assert.Equal("unknown", d.Source.Status); Assert.Null(d.PathSegments); });
    }

    [Fact]
    public void FieldOriginUnknownMemberReportsLocatedCatalogDiagnostic()
    {
        // A TSX <Field name="Wrong" /> is bundled as an ordinary `fields` member; its name range is the entry source.
        var bundle = NewBundle();
        var ir = JsonNode.Parse(bundle.Root["ir"]!["text"]!.GetValue<string>())!;
        ir["components"] = JsonNode.Parse("""[{"key":"holder","type":"Synthetic.Holder","fields":{"Wrong":1},"fieldAliases":{"wrong":"Wrong"}}]""");
        var irText = ir.ToJsonString(); bundle.Root["ir"] = BundlePayload(irText); bundle.Root["usedTypes"] = new JsonArray("Synthetic.Holder");
        var map = JsonNode.Parse(bundle.Root["map"]!["text"]!.GetValue<string>())!; map["irSha256"] = BundleHash(irText);
        map["entries"] = new JsonArray(new JsonObject { ["jsonPath"] = "$.components[0].fields[\"Wrong\"]",
            ["pathSegments"] = new JsonArray("components", 0, "fields", "Wrong"), ["entityKind"] = "component", ["key"] = "holder", ["member"] = "Wrong",
            ["source"] = new JsonObject { ["status"] = "known", ["file"] = bundle.Entry, ["sha256"] = BundleHash(File.ReadAllText(bundle.Entry)),
                ["range"] = new JsonObject { ["start"] = new JsonObject { ["offset"] = 3, ["line"] = 1, ["column"] = 4 },
                    ["end"] = new JsonObject { ["offset"] = 8, ["line"] = 1, ["column"] = 9 } } } });
        bundle.Root["map"] = BundlePayload(map.ToJsonString());
        var failure = Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1"));
        var diagnostic = Assert.Single(ApplyDiagnostics.ForException(failure, "validate").Diagnostics);
        Assert.Equal("COMPONENT_MEMBER_NOT_FOUND", diagnostic.Code);
        Assert.Equal("holder", diagnostic.Key); Assert.Equal("Wrong", diagnostic.Member);
        Assert.Equal("$.components[0].fields[\"Wrong\"]", diagnostic.JsonPath);
        Assert.Equal("known", diagnostic.Source.Status); Assert.Equal(bundle.Entry, diagnostic.Source.File);
        Assert.Equal(3, diagnostic.Source.Range!.Start.Offset); Assert.Equal(4, diagnostic.Source.Range.Start.Column);
        Assert.Equal(8, diagnostic.Source.Range.End.Offset); Assert.Equal(9, diagnostic.Source.Range.End.Column);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("diff")]
    [InlineData("plan")]
    [InlineData("apply")]
    public async Task HandJson_DiagnosticsDoesNotChangeLegacyIssuesOrExitCode(string command)
    {
        var document = Document("diagnostic-legacy", """[{"key":"holder","type":"Test.Source","initialFields":{"Target":"$component:absent"}}]""");
        var file = Path.Combine(_root, "legacy.json"); File.WriteAllText(file, JsonSerializer.Serialize(document, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var result = await ApplyDocumentValidator.ValidateAsync(document);
        var detailed = ApplyDiagnostics.ForResult(result);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("$.components[0].fields.Target", issue.Path);
        var diagnostic = Assert.Single(detailed.Diagnostics);
        Assert.Equal("$.components[0].initialFields[\"Target\"]", diagnostic.JsonPath);
        Assert.Equal("unknown", diagnostic.Source.Status); Assert.Equal("known", diagnostic.Observed.Status);
        var report = Path.Combine(_root, command + ".report"); var diagnostics = Path.Combine(_root, command + ".diagnostics");
        var client = new FakeResoniteClient();
        Assert.Equal(6, await Program.RunAsync([command, file, "--url", "ws://localhost:1", "--diagnostics", diagnostics, "--report", report, "--json"], _ => Task.FromResult<IResoniteClient>(client)));
        Assert.Equal(0, client.Writes);
        using var payload = JsonDocument.Parse(File.ReadAllText(report));
        var issues = payload.RootElement.GetProperty("error").GetProperty("context").GetProperty("issues");
        Assert.Equal(JsonSerializer.SerializeToElement(result.Issues, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetRawText(), issues.GetRawText());
        Assert.Contains("\"diagnosticVersion\": \"1\"", File.ReadAllText(diagnostics));
    }

    [Fact]
    public async Task DiagnosticsWriteFailurePreservesValidationJudgementAndExistingFile()
    {
        var bundle = NewBundle(); var file = bundle.Save(); var original = File.ReadAllText(file);
        Assert.Equal(0, await Program.RunAsync(["validate", file, "--build-id", "R1", "--diagnostics", file, "--json"]));
        Assert.Equal(original, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("status", "out.json", "INVALID_OPTION")]
    [InlineData("validate", "", "OPTION_REQUIRED")]
    public async Task DiagnosticsArgumentScopeAndMissingValue(string command, string value, string code)
    {
        var report = Path.Combine(_root, "option.report");
        Assert.Equal(2, await Program.RunAsync([command, "--diagnostics=" + value, "--json", "--report", report]));
        Assert.Contains(code, File.ReadAllText(report));
    }
}

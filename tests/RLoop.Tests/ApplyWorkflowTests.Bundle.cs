using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    // Handwritten transport/IR expectations. Hashes only seal these test inputs;
    // neither the Node bundle producer nor diagnostic output generates this oracle.
    private BundleFixture NewBundle(bool synthetic = true)
    {
        var entry = Path.Combine(_root, "main.tsx");
        var imported = Path.Combine(_root, "config.ts");
        File.WriteAllText(entry, "// fixed entry\n");
        File.WriteAllText(imported, "export const amount = 1;\n");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "ResoLoop.slnx"))) repo = repo.Parent;
        var catalogText = File.ReadAllText(Path.Combine(repo!.FullName, "tools", "resoloop-jsx", "test", "fixtures", "catalog-v11", "catalog.synthetic.json"));
        if (!synthetic)
        {
            // Simulated provenance exclusively for FakeResoniteClient boundary tests.
            var catalog = JsonNode.Parse(catalogText)!;
            catalog["synthetic"] = false;
            catalogText = catalog.ToJsonString();
        }
        var catalogPath = Path.Combine(_root, "catalog.json");
        File.WriteAllText(catalogPath, catalogText);
        var ir = JsonNode.Parse("""
            {"schemaVersion":"1","ownership":{"key":"bundle-test"},
             "slot":{"key":"root","name":"ResoLoop_Test_Bundle","parent":"Root"},
             "components":[{"key":"base","type":"Synthetic.Base"}],"children":[]}
            """)!;
        ir["authoring"] = new JsonObject { ["projectRoot"] = _root, ["source"] = "main.tsx", ["ownershipSource"] = "entry-export" };
        var irText = ir.ToJsonString();
        JsonObject Source(string path) => new() { ["path"] = path, ["sha256"] = BundleHash(File.ReadAllText(path)) };
        var source1 = Source(entry); var source2 = Source(imported);
        var map = new JsonObject
        {
            ["version"] = "1", ["buildId"] = "R1", ["irSha256"] = BundleHash(irText),
            ["sources"] = new JsonArray(source1.DeepClone(), source2.DeepClone()),
            ["entries"] = new JsonArray(new JsonObject { ["jsonPath"] = "$.components[0]", ["source"] = new JsonObject { ["status"] = "unknown" } })
        };
        source1["role"] = "source"; source2["role"] = "source";
        var root = new JsonObject
        {
            ["kind"] = "resoloop-build-bundle", ["bundleVersion"] = "1", ["buildId"] = "R1", ["completion"] = "committed",
            ["buildStages"] = new JsonObject { ["typecheck"] = "passed", ["emit"] = "passed", ["evaluate"] = "passed", ["inputs"] = "passed" },
            ["inputs"] = new JsonObject { ["status"] = "complete", ["files"] = new JsonArray(source1, source2,
                new JsonObject { ["path"] = catalogPath, ["sha256"] = BundleHash(catalogText), ["role"] = "catalog" }) },
            ["ir"] = BundlePayload(irText), ["map"] = BundlePayload(map.ToJsonString()),
            ["usedTypes"] = new JsonArray("Synthetic.Base"), ["catalog"] = BundlePayload(catalogText)
        };
        return new BundleFixture(Path.Combine(_root, "bundle.data"), root, entry, imported, catalogPath);
    }
    private sealed record BundleFixture(string File, JsonObject Root, string Entry, string Imported, string Catalog)
    {
        internal string Save() { System.IO.File.WriteAllText(File, Root.ToJsonString()); return File; }
    }
    private static string BundleHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static JsonObject BundlePayload(string text) => new() { ["text"] = text, ["sha256"] = BundleHash(text) };
    private static void AssertBundleError(RLoopException error, string reason)
    {
        Assert.Equal("APPLY_BUILD_BUNDLE_INVALID", error.Code);
        Assert.Equal(6, error.ExitCode);
        Assert.Equal(reason, error.Context["reason"]);
    }

    [Theory]
    [InlineData("completion", "uncommitted")]
    [InlineData("stage", "uncommitted")]
    [InlineData("ir-missing", "uncommitted")]
    [InlineData("map-missing", "uncommitted")]
    [InlineData("catalog-missing", "uncommitted")]
    [InlineData("version", "uncommitted")]
    [InlineData("kind", "uncommitted")]
    [InlineData("map-swap", "mixed")]
    [InlineData("map-ir", "mixed")]
    [InlineData("map-source", "mixed")]
    [InlineData("catalog-swap", "mixed")]
    [InlineData("ir-hash", "mixed")]
    [InlineData("used-types", "mixed")]
    [InlineData("inputs-unknown", "inputUnknown")]
    [InlineData("source-missing", "inputUnknown")]
    public void Bundle_RejectsUncommittedAndMixedPayloads(string fault, string reason)
    {
        var bundle = NewBundle();
        switch (fault)
        {
            case "completion": bundle.Root.Remove("completion"); break;
            case "stage": bundle.Root["buildStages"]!.AsObject().Remove("emit"); break;
            case "ir-missing": bundle.Root.Remove("ir"); break;
            case "map-missing": bundle.Root.Remove("map"); break;
            case "catalog-missing": bundle.Root.Remove("catalog"); break;
            case "version": bundle.Root["bundleVersion"] = "future"; break;
            case "kind": bundle.Root["kind"] = "wrong"; break;
            case "ir-hash": bundle.Root["ir"]!["text"] = "{}"; break;
            case "used-types": bundle.Root["usedTypes"] = new JsonArray("Synthetic.Holder"); break;
            case "inputs-unknown": bundle.Root["inputs"]!["status"] = "unknown"; break;
            case "source-missing": bundle.Root["inputs"]!["files"] = new JsonArray(bundle.Root["inputs"]!["files"]![2]!.DeepClone()); break;
            case "catalog-swap":
                var catalog = JsonNode.Parse(bundle.Root["catalog"]!["text"]!.GetValue<string>())!;
                catalog["identity"]!["retrievedAt"] = "2026-10-02T00:00:00+00:00";
                bundle.Root["catalog"] = BundlePayload(catalog.ToJsonString()); break;
            default:
                var map = JsonNode.Parse(bundle.Root["map"]!["text"]!.GetValue<string>())!;
                if (fault == "map-swap") map["buildId"] = "R2";
                if (fault == "map-ir") map["irSha256"] = new string('0', 64);
                if (fault == "map-source") map["sources"]![0]!["sha256"] = new string('0', 64);
                bundle.Root["map"] = BundlePayload(map.ToJsonString()); break;
        }
        AssertBundleError(Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1")), reason);
    }

    [Fact]
    public void Bundle_RequestMismatchEvenWithIdenticalInputsAndMtime()
    {
        var bundle = NewBundle(); var time = File.GetLastWriteTimeUtc(bundle.Entry);
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R2"));
        AssertBundleError(error, "requestMismatch"); Assert.Equal(time, File.GetLastWriteTimeUtc(bundle.Entry));
        File.WriteAllText(bundle.Entry, "changed"); File.WriteAllText(bundle.Entry, "// fixed entry\n");
        File.SetLastWriteTimeUtc(bundle.Entry, time);
        AssertBundleError(Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.File, "R3")), "requestMismatch");
    }

    [Theory]
    [InlineData("entry", "change")]
    [InlineData("import", "change")]
    [InlineData("catalog", "change")]
    [InlineData("entry", "delete")]
    [InlineData("import", "delete")]
    [InlineData("catalog", "delete")]
    [InlineData("entry", "unreadable")]
    [InlineData("import", "unreadable")]
    [InlineData("catalog", "unreadable")]
    public void Bundle_InputChangesAreRejected(string target, string fault)
    {
        var bundle = NewBundle(); bundle.Save();
        var file = target == "entry" ? bundle.Entry : target == "import" ? bundle.Imported : bundle.Catalog;
        using var unreadable = fault == "unreadable" ? new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (fault == "delete") File.Delete(file);
        if (fault == "change") File.AppendAllText(file, "changed");
        AssertBundleError(Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.File, "R1")), "inputChanged");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bundle_TruncatedJsonNeverFallsBack(bool requested)
    {
        var bundle = NewBundle(); File.WriteAllText(bundle.File, "{\"kind\":\"resoloop-build-bundle\",\"ir\":");
        AssertBundleError(Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.File, requested ? "R1" : null)), "uncommitted");
    }

    [Fact]
    public async Task Bundle_PreservesProjectAndStateContextAndLegacyValidationJson()
    {
        var bundle = NewBundle(); var document = ApplyDocument.Load(bundle.Save(), "R1");
        var ordinary = Path.Combine(_root, "ordinary.json"); File.WriteAllText(ordinary, bundle.Root["ir"]!["text"]!.GetValue<string>());
        var legacy = ApplyDocument.Load(ordinary);
        Assert.Equal(legacy.ResolveStatePath(), document.ResolveStatePath());
        Assert.Equal(Path.Combine(_root, ".resoloop", "state", "bundle-test.json"), document.ResolveStatePath());
        Assert.Equal(legacy.ResolveStatePath("explicit.json"), document.ResolveStatePath("explicit.json"));
        var a = await ApplyDocumentValidator.ValidateAsync(legacy, catalog: document.GetBundleCatalog());
        var b = await ApplyDocumentValidator.ValidateAsync(document, catalog: document.GetBundleCatalog());
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
        Assert.True(a.Valid); Assert.False(a.Strict);
        Assert.DoesNotContain("\"BuildBundle\"", JsonSerializer.Serialize(document));
        Assert.DoesNotContain("\"BundleCatalog\"", JsonSerializer.Serialize(document));
        Assert.Equal("OPTION_REQUIRED", Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.File)).Code);
        Assert.Equal("INVALID_OPTION", Assert.Throws<RLoopException>(() => ApplyDocument.Load(ordinary, "R1")).Code);
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("validate")]
    public async Task Bundle_PreflightRunsBeforeConnection(string command)
    {
        var bundle = NewBundle(); var connects = 0;
        var report = Path.Combine(_root, "report.ndjson");
        var args = new List<string> { command, bundle.Save(), "--build-id", "R2", "--url", "ws://localhost:1", "--json", "--report", report };
        if (command == "validate") args.Add("--strict");
        var exit = await Program.RunAsync(args.ToArray(), _ => { connects++; return Task.FromResult<IResoniteClient>(new FakeResoniteClient()); });
        Assert.Equal(6, exit); Assert.Equal(0, connects); Assert.Contains("requestMismatch", File.ReadAllText(report));
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("validate")]
    public async Task Bundle_SyntheticConnectedCommandsRejectedBeforeConnection(string command)
    {
        var bundle = NewBundle(); var connects = 0;
        var report = Path.Combine(_root, "report.ndjson");
        var args = new List<string> { command, bundle.Save(), "--build-id", "R1", "--url", "ws://localhost:1", "--json", "--report", report };
        if (command == "validate") args.Add("--strict");
        Assert.Equal(6, await Program.RunAsync(args.ToArray(), _ => { connects++; return Task.FromResult<IResoniteClient>(new FakeResoniteClient()); }));
        Assert.Equal(0, connects); Assert.Contains("APPLY_CATALOG_UNAVAILABLE", File.ReadAllText(report));
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("import")]
    [InlineData("catalog")]
    public async Task Bundle_InputChangedDuringPreparation_WritesZero(string target)
    {
        var bundle = NewBundle(false); var document = ApplyDocument.Load(bundle.Save(), "R1");
        var client = new FakeResoniteClient(document) { EngineVersion = "SYNTHETIC-engine-1", LinkVersion = "SYNTHETIC-server-1" };
        client.OnDescribe = () => File.AppendAllText(target == "entry" ? bundle.Entry : target == "import" ? bundle.Imported : bundle.Catalog, "changed");
        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document));
        AssertBundleError(error, "inputChanged"); Assert.Equal(0, client.Writes); Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Bundle_InputChangedImmediatelyBeforeFirstWrite_WritesZero()
    {
        var bundle = NewBundle(false); var document = ApplyDocument.Load(bundle.Save(), "R1");
        var raw = JsonNode.Parse(bundle.Root["ir"]!["text"]!.GetValue<string>())!; raw["components"] = new JsonArray();
        var file = Path.Combine(_root, "seed.json"); File.WriteAllText(file, raw.ToJsonString());
        var client = new FakeResoniteClient(document) { EngineVersion = "SYNTHETIC-engine-1", LinkVersion = "SYNTHETIC-server-1" };
        var service = new WorldService(client); await service.ApplyAsync(ApplyDocument.Load(file));
        client.ResetWriteCounts(); client.Mutations.Clear();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(Progress: progress =>
        { if (progress.Stage == "slots") File.AppendAllText(bundle.Imported, "changed"); })));
        AssertBundleError(error, "inputChanged"); Assert.Equal(0, client.Writes); Assert.Empty(client.Mutations);
    }

    [Fact]
    public async Task Bundle_SharedCatalogValidationIsMandatory()
    {
        var bundle = NewBundle();
        var ir = JsonNode.Parse(bundle.Root["ir"]!["text"]!.GetValue<string>())!;
        ir["components"]![0]!["fields"] = new JsonObject { ["NoSuchMember"] = 1 };
        var text = ir.ToJsonString(); bundle.Root["ir"] = BundlePayload(text);
        var map = JsonNode.Parse(bundle.Root["map"]!["text"]!.GetValue<string>())!; map["irSha256"] = BundleHash(text);
        bundle.Root["map"] = BundlePayload(map.ToJsonString());
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1"));
        Assert.Equal("APPLY_VALIDATION_FAILED", error.Code);
        Assert.Contains("MEMBER_NOT_FOUND", JsonSerializer.Serialize(error.Context));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("link")]
    [InlineData("client")]
    public async Task Bundle_ConnectedCatalogIdentityMismatch_WritesZero(string fault)
    {
        var bundle = NewBundle(false);
        if (fault == "client")
        {
            var catalog = JsonNode.Parse(File.ReadAllText(bundle.Catalog))!;
            catalog["identity"]!["clientPackageVersion"] = "wrong";
            catalog["evidenceIdentity"]!["clientPackageVersion"] = "wrong";
            var text = catalog.ToJsonString(); File.WriteAllText(bundle.Catalog, text);
            bundle.Root["catalog"] = BundlePayload(text); bundle.Root["inputs"]!["files"]![2]!["sha256"] = BundleHash(text);
        }
        var report = Path.Combine(_root, "report.ndjson"); var connects = 0;
        var client = new FakeResoniteClient { EngineVersion = fault == "engine" ? "wrong" : "SYNTHETIC-engine-1",
            LinkVersion = fault == "link" ? "wrong" : "SYNTHETIC-server-1" };
        Assert.Equal(6, await Program.RunAsync(["apply", bundle.Save(), "--build-id", "R1", "--url", "ws://localhost:1", "--json", "--report", report],
            _ => { connects++; return Task.FromResult<IResoniteClient>(client); }));
        Assert.Equal(1, connects); Assert.Equal(0, client.Writes); Assert.Empty(client.Mutations);
        Assert.Contains("APPLY_CATALOG_UNAVAILABLE", File.ReadAllText(report));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("diff")]
    [InlineData("plan")]
    [InlineData("apply")]
    public async Task Bundle_ConnectedCommandsUsePreparedInput(string command)
    {
        var bundle = NewBundle(false); var document = ApplyDocument.Load(bundle.Save(), "R1");
        var report = Path.Combine(_root, "report.ndjson"); var connects = 0;
        var client = new FakeResoniteClient(document) { EngineVersion = "SYNTHETIC-engine-1", LinkVersion = "SYNTHETIC-server-1" };
        var args = new List<string> { command, bundle.File, "--build-id", "R1", "--url", "ws://localhost:1", "--json", "--report", report };
        if (command == "validate") args.Add("--strict");
        Assert.Equal(0, await Program.RunAsync(args.ToArray(), _ =>
        {
            connects++;
            // Bundle bytes are held after preflight, not loaded again after connection.
            File.WriteAllText(bundle.File, "corrupted after preflight");
            return Task.FromResult<IResoniteClient>(client);
        }));
        Assert.Equal(1, connects);
        if (command != "apply") { Assert.Equal(0, client.Writes); Assert.Empty(client.Mutations); }
        else Assert.True(client.Writes > 0);
    }

    [Fact]
    public async Task Bundle_WorkbenchIsUnsupportedWithoutConnection()
    {
        var bundle = NewBundle(); var connects = 0; var report = Path.Combine(_root, "report.ndjson");
        Assert.Equal(ExitCodes.OperationFailed, await Program.RunAsync(["validate", bundle.Save(), "--build-id", "R1", "--backend", "workbench", "--json", "--report", report],
            _ => { connects++; return Task.FromResult<IResoniteClient>(new FakeResoniteClient()); }));
        Assert.Equal(0, connects); Assert.Contains("BACKEND_UNSUPPORTED", File.ReadAllText(report));
    }

    [Fact]
    public void Bundle_UnverifiedCatalogRetainsExistingUnavailableCode()
    {
        var bundle = NewBundle(); var catalog = JsonNode.Parse(File.ReadAllText(bundle.Catalog))!;
        catalog["source"] = "unverified";
        var text = catalog.ToJsonString(); File.WriteAllText(bundle.Catalog, text);
        bundle.Root["catalog"] = BundlePayload(text); bundle.Root["inputs"]!["files"]![2]!["sha256"] = BundleHash(text);
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1"));
        Assert.Equal("APPLY_VALIDATION_FAILED", error.Code);
        Assert.Contains("APPLY_CATALOG_UNAVAILABLE", JsonSerializer.Serialize(error.Context));
    }

    [Fact]
    public async Task Bundle_OfflineCliKeepsLegacyOutputAndRejectsExternalCatalog()
    {
        var bundle = NewBundle(); var report = Path.Combine(_root, "report.ndjson"); var connects = 0;
        Task<IResoniteClient> Connect(CancellationToken _) { connects++; return Task.FromResult<IResoniteClient>(new FakeResoniteClient()); }
        Assert.Equal(0, await Program.RunAsync(["validate", bundle.Save(), "--build-id", "R1", "--url", "not-a-url", "--json", "--report", report], Connect));
        Assert.Contains("\"strict\":false", File.ReadAllText(report)); Assert.Equal(0, connects);
        Assert.Equal(ExitCodes.InvalidArguments, await Program.RunAsync(["validate", bundle.File, "--build-id", "R1", "--catalog", bundle.Catalog, "--json"], Connect));
        Assert.Equal(ExitCodes.InvalidArguments, await Program.RunAsync(["validate", bundle.File, "--build-id", "--json"], Connect));
        Assert.Equal(0, connects);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("doctor")]
    [InlineData("scene")]
    public async Task Bundle_RequestOptionIsLimitedToFourCommands(string command)
    {
        var connects = 0;
        Assert.Equal(2, await Program.RunAsync([command, "--build-id", "R1", "--json"], _ =>
        { connects++; return Task.FromResult<IResoniteClient>(new FakeResoniteClient()); }));
        Assert.Equal(0, connects);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

[Collection("Catalog capture console")]
public sealed class CatalogObjectOutputTests : IDisposable
{
    private readonly string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "catalog-object-output-" + Guid.NewGuid().ToString("N"))).FullName;
    private const string Owner = "Fixture.Owner";
    private const string Boolean = "[System.Private.CoreLib]System.Boolean";
    private const string Int32 = "[System.Private.CoreLib]System.Int32";
    private const string String = "[System.Private.CoreLib]System.String";
    private const string Single = "[System.Private.CoreLib]System.Single";
    private const string IntField = "[FrooxEngine]FrooxEngine.IField<[System.Private.CoreLib]System.Int32>";

    private static string Fixture(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ResoLoop.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "tests", "RLoop.Tests", "fixtures", "catalog-objects", name);
    }

    private string Input(string name = "catalog.json")
    {
        var path = Path.Combine(directory, name);
        Catalog().Save(path);
        return path;
    }

    private static ApplyCatalog Catalog(bool synthetic = false)
    {
        var types = new List<CatalogType>
        {
            new(Boolean, true, true, null, [], Representation: "boolean"),
            new(Int32, true, true, null, [], Representation: "int32"),
            new(String, true, true, null, [], Representation: "string"),
            new(Single, true, true, null, [], Representation: "single"),
            new(IntField, true, true, null, [], IsGeneric: true),
            new(Owner, true, true, null, [], MembersComplete: true, Members: new Dictionary<string, CatalogMember>
            {
                ["Bool"] = new("field", ValueType: Boolean),
                ["Count"] = new("field", ValueType: Int32),
                ["Name"] = new("field", ValueType: String),
                ["Weight"] = new("field", ValueType: Single),
                ["Target"] = new("reference", TargetType: IntField),
            }),
        };
        var content = new CatalogContent(types, new Dictionary<string, string>());
        var identity = new CatalogIdentity("fixture", "fixture", "fixture", ApplyCatalog.CurrentMapperVersion,
            new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
        return new(ApplyCatalog.CurrentFormatVersion, identity, identity, "live", synthetic, ApplyCatalog.Hash(content), content);
    }

    private async Task<(int Exit, string Out, string Error, JsonDocument? Json)> Run(string input, string? destination = null, string[]? extra = null)
    {
        var args = new List<string> { "catalog", "types", input };
        if (destination is not null) args.AddRange(["--output", destination]);
        args.Add("--json");
        if (extra is not null) args.AddRange(extra);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var oldDirectory = Environment.CurrentDirectory;
        int exit;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Environment.CurrentDirectory = directory;
            exit = await Program.RunAsync(args.ToArray(), _ => throw new InvalidOperationException("catalog types must stay offline"));
        }
        finally
        {
            Environment.CurrentDirectory = oldDirectory;
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
        JsonDocument? json = null;
        if (!string.IsNullOrWhiteSpace(stdout.ToString())) json = JsonDocument.Parse(stdout.ToString());
        return (exit, stdout.ToString(), stderr.ToString(), json);
    }

    [Fact]
    public async Task DefaultAndCustomDeclarationPublishMatchingRuntimeTable()
    {
        var input = Input();
        var run = await Run(input);
        Assert.Equal(0, run.Exit);
        var declaration = Path.Combine(directory, ".resoloop", "catalog-types.d.ts");
        var runtime = Path.Combine(directory, ".resoloop", "catalog-fields.ts");
        AssertPublished(run, declaration, runtime, "catalog-fields.ts");

        var custom = Path.Combine(directory, "X.d.ts");
        run = await Run(input, custom);
        Assert.Equal(0, run.Exit);
        AssertPublished(run, custom, Path.Combine(directory, "X.fields.ts"), "X.fields.ts");

        if (OperatingSystem.IsWindows())
        {
            custom = Path.Combine(directory, "Mixed.D.TS");
            run = await Run(input, custom);
            Assert.Equal(0, run.Exit);
            AssertPublished(run, custom, Path.Combine(directory, "Mixed.fields.ts"), "Mixed.fields.ts");
        }
    }

    [Fact]
    public async Task NonDeclarationOutputPublishesOneFileAndReportsExplicitNullAndWarning()
    {
        var input = Input();
        var declaration = Path.Combine(directory, "catalog.custom");
        var report = Path.Combine(directory, "report.json");
        var run = await Run(input, declaration, ["--report", report]);
        Assert.Equal(0, run.Exit);
        Assert.True(File.Exists(declaration));
        Assert.False(File.Exists(Path.Combine(directory, "catalog.custom.fields.ts")));
        Assert.Contains("Runtime catalog table was not written: --output does not end in .d.ts; componentFields requires a companion table.", run.Error);
        AssertHeaderOmitsRuntimeFields(File.ReadAllText(declaration));
        var data = run.Json!.RootElement.GetProperty("data");
        Assert.Equal(Path.GetFullPath(declaration), data.GetProperty("output").GetString());
        Assert.Equal("13290e8b7558a80e35940b524e70d833ee24fbc842756e52793051b056e0ea4f", data.GetProperty("contentHash").GetString());
        Assert.Equal("3", data.GetProperty("generatorVersion").GetString());
        Assert.True(data.TryGetProperty("fallbacks", out _));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("runtimeOutput").ValueKind);
        using var saved = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("data").GetProperty("runtimeOutput").ValueKind);
    }

    [Fact]
    public async Task LongPreviouslyValidOutputLeafStillPublishesWithBoundedTemporaryName()
    {
        var input = Input();
        var leaf = new string('a', 214) + ".txt";
        Assert.Equal(218, leaf.Length);
        var declaration = Path.Combine(directory, leaf);
        var run = await Run(input, declaration);
        Assert.Equal(0, run.Exit);
        Assert.True(File.Exists(declaration));
        AssertHeaderOmitsRuntimeFields(File.ReadAllText(declaration));
        Assert.Equal(JsonValueKind.Null, run.Json!.RootElement.GetProperty("data").GetProperty("runtimeOutput").ValueKind);
        Assert.Contains("Runtime catalog table was not written: --output does not end in .d.ts; componentFields requires a companion table.", run.Error);
    }

    [Fact]
    public async Task InputAndOutputCollisionsAreRejectedBeforeAnyPublication()
    {
        var input = Input();
        var before = File.ReadAllBytes(input);
        var output = Path.Combine(directory, "other.d.ts");
        var runtime = Path.Combine(directory, "other.fields.ts");
        File.Copy(input, runtime);
        var runtimeBefore = File.ReadAllBytes(runtime);
        var collision = await Run(runtime, output);
        Assert.Equal(2, collision.Exit);
        Assert.Empty(collision.Out);
        Assert.Equal(runtimeBefore, File.ReadAllBytes(runtime));
        Assert.False(File.Exists(output));

        if (OperatingSystem.IsWindows())
        {
            collision = await Run(input, input.ToUpperInvariant());
            Assert.Equal(2, collision.Exit);
            Assert.Equal(before, File.ReadAllBytes(input));
        }
    }

    [Fact]
    public async Task UnavailableCatalogAndRuntimeDestinationFailureDoNotReportSuccessOrLeakTemps()
    {
        var input = Input();
        foreach (var kind in new[] { "synthetic", "hash" })
        {
            var catalog = Catalog();
            catalog = kind == "synthetic" ? catalog with { Synthetic = true } : catalog with { ContentHash = new string('0', 64) };
            catalog.Save(input);
            var unavailableOutput = Path.Combine(directory, $"unavailable-{kind}.d.ts");
            var unavailable = await Run(input, unavailableOutput);
            Assert.Equal(6, unavailable.Exit);
            Assert.Empty(unavailable.Out);
            Assert.False(File.Exists(unavailableOutput));
            Assert.False(File.Exists(Path.Combine(directory, $"unavailable-{kind}.fields.ts")));
        }

        input = Input("valid.json");
        var declaration = Path.Combine(directory, "blocked.d.ts");
        var runtimeDirectory = Path.Combine(directory, "blocked.fields.ts");
        Directory.CreateDirectory(runtimeDirectory);
        var unrelated = Path.Combine(runtimeDirectory, "keep.txt");
        File.WriteAllText(unrelated, "preserve");
        var failed = await Run(input, declaration);
        Assert.NotEqual(0, failed.Exit);
        Assert.Empty(failed.Out);
        Assert.False(File.Exists(declaration));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        Assert.Empty(Directory.EnumerateFiles(directory, ".*.tmp", SearchOption.AllDirectories));
    }

    private static void AssertPublished((int Exit, string Out, string Error, JsonDocument? Json) run,
        string declaration, string runtime, string expectedLeaf)
    {
        Assert.True(File.Exists(declaration));
        Assert.True(File.Exists(runtime));
        var declarationText = File.ReadAllText(declaration, Encoding.UTF8);
        var runtimeBytes = File.ReadAllBytes(runtime);
        Assert.Equal(File.ReadAllText(Fixture("runtime.handwritten.ts"), Encoding.UTF8), Encoding.UTF8.GetString(runtimeBytes));
        Assert.DoesNotContain('\uFEFF', Encoding.UTF8.GetString(runtimeBytes));
        Assert.DoesNotContain('\r', Encoding.UTF8.GetString(runtimeBytes));
        using var header = JsonDocument.Parse(declarationText.Split('\n', 2)[0]["// resoloop-catalog-types: ".Length..]);
        var metadata = header.RootElement;
        Assert.Equal(expectedLeaf, metadata.GetProperty("runtimeFile").GetString());
        var hash = Convert.ToHexStringLower(SHA256.HashData(runtimeBytes));
        Assert.Equal("687a0a1f9fdfa4dfc97f0c15a3c1fc446ea040fce717dd05feb68e5fb4d34f50", hash);
        Assert.Equal(hash, metadata.GetProperty("runtimeSha256").GetString());
        var data = run.Json!.RootElement.GetProperty("data");
        Assert.Equal(Path.GetFullPath(declaration), data.GetProperty("output").GetString());
        Assert.Equal(Path.GetFullPath(runtime), data.GetProperty("runtimeOutput").GetString());
        Assert.Equal("13290e8b7558a80e35940b524e70d833ee24fbc842756e52793051b056e0ea4f", data.GetProperty("contentHash").GetString());
        Assert.Equal("3", data.GetProperty("generatorVersion").GetString());
        Assert.True(data.TryGetProperty("fallbacks", out _));
        Assert.Empty(run.Error);
    }

    private static void AssertHeaderOmitsRuntimeFields(string text)
    {
        using var header = JsonDocument.Parse(text.Split('\n', 2)[0]["// resoloop-catalog-types: ".Length..]);
        var metadata = header.RootElement;
        Assert.Equal("1", metadata.GetProperty("formatVersion").GetString());
        Assert.Equal("13290e8b7558a80e35940b524e70d833ee24fbc842756e52793051b056e0ea4f", metadata.GetProperty("contentHash").GetString());
        Assert.Equal("3", metadata.GetProperty("generatorVersion").GetString());
        Assert.False(metadata.TryGetProperty("runtimeFile", out _));
        Assert.False(metadata.TryGetProperty("runtimeSha256", out _));
    }

    public void Dispose() => Directory.Delete(directory, true);
}

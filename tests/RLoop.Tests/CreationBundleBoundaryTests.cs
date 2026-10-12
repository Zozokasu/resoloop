using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationBundleBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-bundle-" + Guid.NewGuid().ToString("N"));
    private const string Envelope = """{"schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"room","definition":{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[]}}""";

    public CreationBundleBoundaryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ApplyDocumentLoaderRejectsCreationEnvelopeAtOldEntryPoint()
    {
        var path = Save("direct.json", Envelope);
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(path));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
        error = Assert.Throws<RLoopException>(() => ApplyDocument.LoadBundleIfPresent(path));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
    }

    [Fact]
    public void ExclusivelyOpenedRequestedBundleKeepsLegacyUncommittedClassification()
    {
        var path = Save("locked.json", "{}");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(path, "R1"));
        Assert.Equal("APPLY_BUILD_BUNDLE_INVALID", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
    }

    [Fact]
    public void ApplyDocumentLoaderRejectsCreationBundleKind()
    {
        var bundle = """{"kind":"resoloop-creation-build-bundle","bundleVersion":"1","creationId":"room"}""";
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(Save("creation-bundle.json", bundle)));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
    }

    [Fact]
    public void OldBundleWithCreationEnvelopeIrIsRejectedAfterValidBundleVerification()
    {
        var sourcePath = Save("source.tsx", "// fixed source\n");
        var catalogPath = FindCatalog();
        var catalogText = File.ReadAllText(catalogPath);
        var sourceHash = Hash(File.ReadAllText(sourcePath));
        var catalogHash = Hash(catalogText);
        var irText = Envelope;
        var mapText = new JsonObject
        {
            ["version"] = "1",
            ["buildId"] = "R1",
            ["irSha256"] = Hash(irText),
            ["sources"] = new JsonArray(new JsonObject { ["path"] = sourcePath, ["sha256"] = sourceHash }),
            ["entries"] = new JsonArray()
        }.ToJsonString();
        var bundle = new JsonObject
        {
            ["kind"] = "resoloop-build-bundle",
            ["bundleVersion"] = "1",
            ["buildId"] = "R1",
            ["completion"] = "committed",
            ["buildStages"] = new JsonObject { ["typecheck"] = "passed", ["emit"] = "passed", ["evaluate"] = "passed", ["inputs"] = "passed" },
            ["inputs"] = new JsonObject
            {
                ["status"] = "complete",
                ["files"] = new JsonArray(
                    new JsonObject { ["path"] = sourcePath, ["sha256"] = sourceHash, ["role"] = "source" },
                    new JsonObject { ["path"] = catalogPath, ["sha256"] = catalogHash, ["role"] = "catalog" })
            },
            ["ir"] = Payload(irText),
            ["map"] = Payload(mapText),
            ["usedTypes"] = new JsonArray(),
            ["catalog"] = Payload(catalogText)
        };
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.LoadBundleIfPresent(Save("legacy-bundle.data", bundle.ToJsonString()), "R1"));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
    }

    [Fact]
    public void BundleIrCompilationRejectsCreationEnvelope()
    {
        var error = Assert.Throws<RLoopException>(() => ApplyDocumentCompiler.CompileBundleIr(Envelope));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
    }

    [Fact]
    public void IncludeCompilerRejectsCreationEnvelopeBeforeLegacyCompilation()
    {
        var included = Save("included.json", Envelope);
        var source = Save("parent.json", "{\"include\":\"" + Path.GetFileName(included) + "\"}");
        var error = Assert.Throws<RLoopException>(() => ApplyDocumentCompiler.Compile(source));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
    }

    [Fact]
    public void MergedCompilerRootCannotBecomeAnEnvelope()
    {
        var source = Save("merged.json", "{\"definition\":{},\"schemaVersion\":\"1\"}");
        var error = Assert.Throws<RLoopException>(() => ApplyDocumentCompiler.Compile(source));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
    }

    private string Save(string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string FindCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ResoLoop.slnx"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "tools", "resoloop-jsx", "test", "fixtures", "catalog-v11", "catalog.synthetic.json");
    }

    private static JsonObject Payload(string text) => new() { ["text"] = text, ["sha256"] = Hash(text) };
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public void Dispose() => Directory.Delete(root, recursive: true);
}

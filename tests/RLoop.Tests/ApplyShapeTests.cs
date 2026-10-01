using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RLoop.ContractGen;
using RLoop.Core;
using Xunit;

namespace RLoop.Tests;

public sealed class ApplyShapeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResoLoop.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Shape_ExcludesIgnoredAndSourceOnlyProperties()
    {
        var document = ApplyShape.All.Single(s => s.Type == typeof(ApplyDocument));
        Assert.Equal(new[] { "SourcePath", "Compilation" }, document.IgnoredProperties);
        Assert.DoesNotContain(document.Properties, p => p.Name is "sourcePath" or "compilation" or "$scope" or "$draftKeys");
        Assert.Equal(new[] { "$draftKeys", "$scope" }, document.SourceProperties.Select(p => p.Name));
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, ApplyShape.Property<ApplyDocument>(nameof(ApplyDocument.Authoring)).IgnoreCondition);
        Assert.Equal(11, ApplyShape.All.Count);
    }

    [Fact]
    public void Shape_PreservesOptionalNullAndDefaults()
    {
        var version = ApplyShape.Property<ApplyDocument>(nameof(ApplyDocument.SchemaVersion));
        Assert.True(version.Nullable);
        Assert.True(version.ConstructorRequired);
        Assert.True(version.Rules.JsonRequired);
        var components = ApplyShape.Property<ApplyDocument>(nameof(ApplyDocument.Components));
        Assert.True(components.ConstructorRequired);
        Assert.False(components.Rules.JsonRequired);
        Assert.True(components.Rules.OutputRequired);
        var key = ApplyShape.Property<ApplySlotSpec>(nameof(ApplySlotSpec.Key));
        Assert.True(key.Nullable);
        Assert.True(key.HasDefault);
        Assert.Null(key.Default);
        Assert.False(key.Rules.JsonRequired);
        Assert.True(key.Rules.JsxRequired);
        var relocation = ApplyShape.Property<ApplySlotSpec>(nameof(ApplySlotSpec.RelocationTransform));
        Assert.False(relocation.Nullable);
        Assert.Equal("local", relocation.Default);
        Assert.False(relocation.Rules.JsonRequired);
        var preserve = ApplyShape.Property<ApplySlotSpec>(nameof(ApplySlotSpec.PreserveWorldTransform));
        Assert.Equal(false, preserve.Default);
        Assert.False(preserve.ConstructorRequired);
    }

    [Fact]
    public void Shape_PreservesLegacySuggestionVocabulary()
    {
        // Human-maintained compatibility oracle, independent of reflection.
        var expected = "schemaVersion authoring projectRoot source ownershipSource ownership key slot parent name position rotation scale managedFields preserveWorldTransform runtimeRelocatable relocationTransform migrateFrom components children type fields initialFields identityFields assets cameras tests assertions probe arguments method kind target value values restore safe expected exists phase componentType count delta timeoutMs pollMs";
        // Additions must not require another candidate list to be transcribed into this oracle.
        foreach (var candidate in expected.Split(' ')) Assert.Contains(candidate, ApplyShape.KnownProperties);
        foreach (var excluded in new[] { "options", "fieldOfView", "width", "height", "output", "representative" })
            Assert.DoesNotContain(excluded, ApplyShape.KnownProperties);
    }

    internal static void AssertCurrent(string expected, string actual) =>
        Assert.Equal(expected.Replace("\r\n", "\n"), actual.Replace("\r\n", "\n"));

    [Fact]
    public void GeneratedArtifacts_AreCurrent()
    {
        var generated = Path.Combine(RepoRoot(), "tools", "resoloop-jsx", "src", "generated");
        var artifacts = ContractGenerator.Generate();
        Assert.Equal(artifacts.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(generated).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var artifact in artifacts)
            AssertCurrent(artifact.Value, File.ReadAllText(Path.Combine(generated, artifact.Key)));
    }

    [Fact]
    public void GeneratedArtifacts_RejectStaleButAcceptCheckoutCrLf()
    {
        foreach (var artifact in ContractGenerator.Generate().Values)
        {
            AssertCurrent(artifact, artifact.Replace("\n", "\r\n"));
            Assert.ThrowsAny<Exception>(() => AssertCurrent(artifact, artifact + "stale"));
        }
    }

    [Fact]
    public void Generator_IsDeterministic()
    {
        var first = ContractGenerator.Generate();
        var second = ContractGenerator.Generate();
        Assert.Equal(first.Keys, second.Keys);
        foreach (var name in first.Keys)
        {
            Assert.Equal(Encoding.UTF8.GetBytes(first[name]), Encoding.UTF8.GetBytes(second[name]));
            Assert.DoesNotContain('\r', first[name]);
            Assert.DoesNotContain(RepoRoot(), first[name]);
        }
    }

    [Theory]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(object))]
    [InlineData(typeof(Dictionary<string, JsonElement>))]
    [InlineData(typeof(float[,]))]
    public void Generator_RejectsUnknownClrShapes(Type type) =>
        Assert.Throws<InvalidOperationException>(() => ContractGenerator.TypeScript(type));
}

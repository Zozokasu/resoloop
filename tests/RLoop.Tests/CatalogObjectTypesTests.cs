using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CatalogObjectTypesTests
{
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

    [Fact]
    public void GeneratesIndependentRuntimeOracleAndMatchingDeclaration()
    {
        var catalog = Catalog();
        Assert.Equal("13290e8b7558a80e35940b524e70d833ee24fbc842756e52793051b056e0ea4f", catalog.ContentHash);
        var generated = CatalogTypesGenerator.Generate(catalog);
        var expectedRuntime = File.ReadAllText(Fixture("runtime.handwritten.ts"), Encoding.UTF8);
        Assert.Equal(expectedRuntime, generated.RuntimeText);
        Assert.Equal("3", generated.GeneratorVersion);
        Assert.Equal(catalog.ContentHash, generated.ContentHash);
        var again = CatalogTypesGenerator.Generate(catalog);
        Assert.Equal(generated.Text, again.Text);
        Assert.Equal(generated.RuntimeText, again.RuntimeText);
        Assert.Equal(generated.Fallbacks.ToArray(), again.Fallbacks.ToArray());
        Assert.DoesNotContain('\r', generated.RuntimeText!);
        Assert.DoesNotContain('\uFEFF', generated.RuntimeText!);
        Assert.True(generated.RuntimeText!.EndsWith("\n", StringComparison.Ordinal));

        var headerJson = generated.Text.Split('\n', 2)[0]["// resoloop-catalog-types: ".Length..];
        using var header = JsonDocument.Parse(headerJson);
        var runtimeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(generated.RuntimeText!)));
        Assert.Equal(runtimeHash, header.RootElement.GetProperty("runtimeSha256").GetString());
        Assert.Contains("FieldRef<number, \"int32\"> | UncheckedReference | null", generated.Text);
        Assert.Contains("memberMetadata: {\"Bool\": CM0;", generated.Text);
        Assert.Contains("type CM0 = {", generated.Text);
        Assert.DoesNotContain("CatalogMemberMetadata", generated.Text);
    }

    [Fact]
    public void RuntimeFilenameMustBeALeafAndUnavailableCatalogsKeepExistingError()
    {
        foreach (var invalid in new[] { "", " ", ".", "..", "sub/file.ts", "sub\\file.ts", "C:\\file.ts", "/file.ts" })
            Assert.Equal("INVALID_ARGUMENT", Assert.Throws<RLoopException>(() => CatalogTypesGenerator.Generate(Catalog(), invalid)).Code);
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Throws<RLoopException>(() =>
            CatalogTypesGenerator.Generate(Catalog(synthetic: true))).Code);
    }

    [Fact]
    public void PositionalRecordConstructorRemainsCompatible()
    {
        var declaration = new CatalogTypesDeclaration("text", "hash", "3", []);
        Assert.Null(declaration.RuntimeText);
    }

    [Fact]
    public void RuntimeLiteralUsesComputedProtoKeysWithoutChangingStringValues()
    {
        const string marker = "value \"__proto__\": unchanged";
        var original = Catalog();
        var protoType = new CatalogType("__proto__", true, true, null, [], MembersComplete: true,
            Members: new Dictionary<string, CatalogMember>
            { ["__proto__"] = new("other", MemberType: marker) });
        var content = original.Content with
        {
            Types = [.. original.Content.Types, protoType],
            Aliases = new Dictionary<string, string> { ["ProtoAlias"] = "__proto__" },
        };
        var generated = CatalogTypesGenerator.Generate(original with { Content = content, ContentHash = ApplyCatalog.Hash(content) });
        var runtime = generated.RuntimeText!;

        Assert.Equal(3, runtime.Split("[\"__proto__\"]", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("\"__proto__\":", runtime);
        Assert.Contains(JsonSerializer.Serialize(marker), runtime);
        Assert.Contains("\"ProtoAlias\":", runtime);

        var owner = new CatalogType("Fixture.AliasOwner", true, true, null, [], MembersComplete: true,
            Members: new Dictionary<string, CatalogMember> { ["__proto__"] = new("other", MemberType: marker) });
        var aliasContent = new CatalogContent([owner], new Dictionary<string, string> { ["__proto__"] = owner.FullName });
        var aliasCatalog = original with { Content = aliasContent, ContentHash = ApplyCatalog.Hash(aliasContent) };
        var aliasRuntime = CatalogTypesGenerator.Generate(aliasCatalog).RuntimeText!;
        Assert.Equal(3, aliasRuntime.Split("[\"__proto__\"]", StringSplitOptions.None).Length - 1);
        Assert.Contains("    \"__proto__\": {", CatalogTypesGenerator.Generate(aliasCatalog).Text);
        Assert.DoesNotContain("\"__proto__\":", aliasRuntime);
    }

    [Fact]
    public void HostileNamesAreJsonQuotedAndMissingClosureStaysFallback()
    {
        var source = Catalog();
        var hostileOwner = new CatalogType("Fixture.Type\n// comment", true, false, null, [], MembersComplete: false,
            Members: new Dictionary<string, CatalogMember> { ["bad\n/*name*/"] = new("field", ValueType: "Fixture.Unknown") });
        var content = new CatalogContent([hostileOwner, .. source.Content.Types.Where(t => t.FullName == Int32)],
            new Dictionary<string, string>());
        var catalog = source with { Content = content, ContentHash = ApplyCatalog.Hash(content) };
        var generated = CatalogTypesGenerator.Generate(catalog);
        Assert.Contains("Fixture.Type\\n// comment", generated.Text);
        Assert.Contains("bad\\n/*name*/", generated.Text);
        Assert.Contains(generated.Fallbacks, f => f.Type == hostileOwner.FullName && f.Member is null);
        Assert.Contains(generated.Fallbacks, f => f.Type == hostileOwner.FullName && f.Member == "bad\n/*name*/");
    }
}

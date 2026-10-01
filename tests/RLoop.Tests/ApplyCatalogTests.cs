using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class ApplyCatalogTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResoLoop.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tools", "resoloop-jsx", "test", "fixtures", "catalog-v11", name);
    }
    private static ApplyCatalog Catalog() => ApplyCatalog.Load(Fixture("catalog.synthetic.json"));
    private static ApplyDocument Document(JsonElement value) => JsonSerializer.Deserialize<ApplyDocument>(value.GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    public static IEnumerable<object[]> OracleCases()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Fixture("oracle.handwritten.json")));
        foreach (var item in oracle.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.Clone()];
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public async Task V11_IndependentOracle(string name, JsonElement test)
    {
        Assert.Equal(name, test.GetProperty("name").GetString());
        var result = await ApplyDocumentValidator.ValidateAsync(Document(test.GetProperty("document")), catalog: Catalog());
        var expected = test.GetProperty("issues").EnumerateArray().Select(i => (i.GetProperty("code").GetString(), i.GetProperty("path").GetString())).ToArray();
        Assert.Equal(expected.Length == 0, result.Valid);
        Assert.False(result.Strict);
        Assert.Equal(expected, result.Issues.Select(i => ((string?)i.Code, (string?)i.Path)).ToArray());
    }

    [Fact]
    public void V11_FixedOriginalIdentityHashAndMemberTypes()
    {
        var catalog = Catalog();
        using var oracle = JsonDocument.Parse(File.ReadAllText(Fixture("oracle.handwritten.json")));
        Assert.True(catalog.Synthetic);
        Assert.Equal("60eca209e09e4eb9707232b7966d0812d4c9c079133bdb11ffcfa2e8cb7b6446", catalog.ContentHash);
        Assert.Equal(oracle.RootElement.GetProperty("contentHash").GetString(), catalog.ContentHash);
        Assert.Equal(catalog.ContentHash, ApplyCatalog.Hash(catalog.Content));
        Assert.Equal(JsonSerializer.Deserialize<CatalogIdentity>(oracle.RootElement.GetProperty("identity"), ApplyCatalog.Json), catalog.Identity);
        Assert.Null(catalog.UnavailableReason());
        foreach (var row in oracle.RootElement.GetProperty("members").EnumerateArray())
        {
            var member = catalog.Find(row.GetProperty("type").GetString())!.Members![row.GetProperty("member").GetString()!];
            if (row.TryGetProperty("valueType", out var value)) Assert.Equal(value.GetString(), member.ValueType);
            if (row.TryGetProperty("targetType", out var target)) Assert.Equal(target.GetString(), member.TargetType);
        }
    }

    [Theory]
    [InlineData("missing-identity")]
    [InlineData("mismatch")]
    [InlineData("unverified")]
    [InlineData("hash")]
    [InlineData("mapper")]
    public void CatalogMismatch_FailsClosed(string mutation)
    {
        var catalog = Catalog();
        catalog = mutation switch
        {
            "missing-identity" => catalog with { Identity = null },
            "mismatch" => catalog with { EvidenceIdentity = catalog.Identity! with { ResoniteVersion = "other" } },
            "unverified" => catalog with { Source = "unverified" },
            "hash" => catalog with { ContentHash = new string('0', 64) },
            _ => catalog with { Identity = catalog.Identity! with { MapperVersion = "1" }, EvidenceIdentity = catalog.Identity! with { MapperVersion = "1" } }
        };
        var doc = new ApplyDocument("1", new("test"), new("Root", null, null, null, null, "root"), [new("Synthetic.Holder", new Dictionary<string, JsonElement> { ["Amount"] = JsonSerializer.SerializeToElement(1) }, "holder")]);
        var issues = ApplyCatalogValidator.Validate(doc, catalog);
        Assert.All(issues, issue => Assert.Equal("APPLY_CATALOG_UNAVAILABLE", issue.Code));
        Assert.Contains(issues, issue => issue.Path == "$.components[0].fields.Amount" && issue.Message.Contains("Synthetic.Holder") && issue.Message.Contains("Amount"));
    }

    [Fact]
    public void Catalog_PreservesNestedDefinitions()
    {
        var scalar = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "float" } };
        var reference = new Link.ReferenceDefinition { TargetType = new Link.TypeReference { Type = "Synthetic.Base" } };
        var component = new Link.ComponentDefinition
        {
            Type = new Link.TypeDefinition { FullTypeName = "Synthetic.Holder", Interfaces = [] }, Methods = [],
            Members = new()
            {
                ["Values"] = new Link.ListDefinition { ElementDefinition = scalar },
                ["Array"] = new Link.ArrayDefinition { ValueType = new Link.TypeReference { Type = "float" } },
                ["Targets"] = new Link.DictionaryDefinition { KeyType = new Link.TypeReference { Type = "string" }, ElementDefinition = reference },
                ["Embedded"] = new Link.SyncObjectMemberDefinition { Type = new Link.TypeReference { Type = "Synthetic.Embedded" } },
                ["Optional"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "Nullable<>", GenericArguments = [new Link.TypeReference { Type = "float" }] } }
            }
        };
        var sync = new Link.SyncObjectDefinition { Type = new Link.TypeDefinition { FullTypeName = "Synthetic.Embedded", Interfaces = [] }, Members = new() { ["Amount"] = scalar }, Methods = [] };
        var content = new CatalogSnapshotContent([component], [], [sync], new Dictionary<string, string>());
        var identity = Catalog().Identity!;
        var snapshot = new CatalogSnapshot(identity, identity, "live", true, CatalogMapper.SnapshotHash(content), content);
        var directory = Path.Combine(Path.GetTempPath(), "catalog-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var original = Path.Combine(directory, "snapshot.json"); CatalogMapper.SaveSnapshot(snapshot, original);
            var mapped = CatalogMapper.Export(CatalogMapper.LoadSnapshot(original));
            var output = Path.Combine(directory, "catalog.json"); mapped.Save(output);
            mapped = ApplyCatalog.Load(output);
            var members = mapped.Find("Synthetic.Holder")!.Members!;
            // Independent, handwritten expectations over SDK metadata mapping.
            Assert.Equal("float", members["Values"].Element!.ValueType);
            Assert.Equal("float", members["Array"].Element!.ValueType);
            Assert.Equal("string", members["Targets"].KeyType);
            Assert.Equal("Synthetic.Base", members["Targets"].Element!.TargetType);
            Assert.Equal("Synthetic.Embedded", members["Embedded"].MemberType);
            Assert.Equal("float", mapped.Find("Synthetic.Embedded")!.Members!["Amount"].ValueType);
            Assert.Equal("nullable", mapped.Find(members["Optional"].ValueType)!.Representation);
            Assert.Equal("float", mapped.Find(members["Optional"].ValueType)!.ElementType);
            Assert.Null(mapped.UnavailableReason());
            Assert.Throws<RLoopException>(() => CatalogMapper.Export(snapshot with { Identity = null }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Catalog_DoesNotPromoteLegacyCache()
    {
        var file = Path.Combine(Path.GetTempPath(), "legacy-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, "{\"key\":\"old-cache-key\",\"observedAt\":\"2026-09-01T00:00:00Z\",\"value\":{}}");
            Assert.Throws<JsonException>(() => CatalogMapper.LoadSnapshot(file));
            Assert.Throws<JsonException>(() => ApplyCatalog.Load(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Catalog_MissingFileUsesExistingIssuesAndExit()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Fixture("oracle.handwritten.json")));
        var doc = Document(oracle.RootElement.GetProperty("cases")[0].GetProperty("document"));
        var result = await ApplyCatalogValidator.ValidateFileAsync(doc, Fixture("does-not-exist.json"));
        Assert.False(result.Valid); Assert.False(result.Strict);
        Assert.All(result.Issues, issue => Assert.Equal("APPLY_CATALOG_UNAVAILABLE", issue.Code));
        Assert.Contains(result.Issues, issue => issue.Path == "$.components[0].fields.Amount");
        var ex = Assert.Throws<RLoopException>(() => ApplyDocumentValidator.ThrowIfInvalid(result));
        Assert.Equal("APPLY_VALIDATION_FAILED", ex.Code); Assert.Equal(ExitCodes.ValidationFailed, ex.ExitCode);
    }

    [Fact]
    public async Task ValidateCatalog_IsOfflineAndDoesNotElevateStrict()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Fixture("oracle.handwritten.json")));
        var docFile = Path.Combine(Path.GetTempPath(), "offline-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        var report = docFile + ".ndjson";
        try
        {
            File.WriteAllText(docFile, oracle.RootElement.GetProperty("cases")[0].GetProperty("document").GetRawText());
            var exit = await RLoop.Cli.Program.Main(["validate", docFile, "--catalog", Fixture("catalog.synthetic.json"), "--url", "not-a-url", "--json", "--report", report]);
            Assert.Equal(0, exit); Assert.Contains("\"strict\":false", File.ReadAllText(report));
        }
        finally { File.Delete(docFile); File.Delete(report); }
    }

    [Theory]
    [InlineData("missing-member")]
    [InlineData("unconfirmed-member")]
    [InlineData("unknown-type")]
    [InlineData("short-name")]
    [InlineData("missing-element")]
    [InlineData("missing-syncobject")]
    public void Catalog_UnknownIsUnavailableNeverInvented(string mutation)
    {
        var catalog = Catalog();
        var holder = catalog.Find("Synthetic.Holder")!;
        var members = new Dictionary<string, CatalogMember>(holder.Members!);
        string name = "Amount";
        switch (mutation)
        {
            case "missing-member": members.Remove("Amount"); break;
            case "unconfirmed-member": members["Amount"] = members["Amount"] with { Confirmed = false }; break;
            case "unknown-type": members["Amount"] = members["Amount"] with { ValueType = "Unacquired.Single" }; break;
            case "missing-element": name = "Values"; members[name] = members[name] with { Element = null }; break;
            case "missing-syncobject": name = "Embedded"; members[name] = members[name] with { MemberType = "Unacquired.SyncObject" }; break;
        }
        var content = catalog.Content with { Types = catalog.Content.Types.Select(t => t.FullName == holder.FullName ? t with { Members = members, MembersComplete = false } : t).ToArray() };
        catalog = catalog with { Content = content, ContentHash = ApplyCatalog.Hash(content) };
        var value = JsonSerializer.SerializeToElement(mutation == "missing-element" ? (object)Array.Empty<int>() : mutation == "missing-syncobject" ? new { } : 1);
        var doc = new ApplyDocument("1", new("test"), new("Root", null, null, null, null, "root"),
            [new(mutation == "short-name" ? "Holder" : "Synthetic.Holder", new Dictionary<string, JsonElement> { [name] = value }, "holder")]);
        Assert.All(ApplyCatalogValidator.Validate(doc, catalog), i => Assert.Equal("APPLY_CATALOG_UNAVAILABLE", i.Code));
        Assert.Contains(ApplyCatalogValidator.Validate(doc, catalog), i => i.Path == "$.components[0].fields." + name);
    }

    [Fact]
    public void Reference_ConfirmedInterfaceAndMemberWrapperUseFullIdentity()
    {
        var catalog = Catalog();
        var source = catalog.Find("Synthetic.Other")! with
        { Interfaces = ["Synthetic.Base"], Members = new Dictionary<string, CatalogMember> { ["Wrapper"] = new("field", "Synthetic.Derived", "System.Single") } };
        var content = catalog.Content with { Types = catalog.Content.Types.Select(t => t.FullName == source.FullName ? source : t).ToArray() };
        catalog = catalog with { Content = content, ContentHash = ApplyCatalog.Hash(content) };
        var doc = new ApplyDocument("1", new("test"), new("Root", null, null, null, null, "root"),
            [new("Synthetic.Holder", new Dictionary<string, JsonElement> { ["Target"] = JsonSerializer.SerializeToElement("$component:source") }, "holder"), new("Synthetic.Other", null, "source")]);
        Assert.Empty(ApplyCatalogValidator.Validate(doc, catalog));
        doc = doc with { Components = [doc.Components![0] with { Fields = new Dictionary<string, JsonElement> { ["Target"] = JsonSerializer.SerializeToElement("$member:source.Wrapper") } }, doc.Components[1]] };
        Assert.Empty(ApplyCatalogValidator.Validate(doc, catalog));
        Assert.Equal(new[] { "Synthetic.Holder", "Synthetic.Other" }, ApplyCatalogValidator.UsedTypes(doc, catalog).OrderBy(t => t).ToArray());
    }

    [Fact]
    public void Catalog_AcquisitionConfirmedAliasesAndEnumsSurviveMapping()
    {
        var type = new Link.TypeDefinition { FullTypeName = "System.Single", Interfaces = [] };
        var enumeration = new Link.TypeDefinition { FullTypeName = "Synthetic.Mode", IsEnum = true, Interfaces = [] };
        var component = new Link.ComponentDefinition { Type = new Link.TypeDefinition { FullTypeName = "Synthetic.Holder", Interfaces = [] }, Methods = [],
            Members = new() { ["Amount"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "float" } } } };
        var content = new CatalogSnapshotContent([component], [type, enumeration], [], new Dictionary<string, string> { ["float"] = "System.Single" },
            new Dictionary<string, CatalogEnum> { ["Synthetic.Mode"] = new(new Dictionary<string, long> { ["A"] = 1 }, false) });
        var identity = Catalog().Identity!;
        var mapped = CatalogMapper.Export(new(identity, identity, "version-cache", true, CatalogMapper.SnapshotHash(content), content));
        Assert.Equal("single", mapped.Find("float")!.Representation);
        Assert.Equal("System.Single", mapped.Find("float")!.FullName);
        Assert.Equal(1, mapped.Find("Synthetic.Mode")!.EnumValues!["A"]);
        Assert.False(mapped.Find("Synthetic.Mode")!.IsFlags);
        Assert.Equal("version-cache", mapped.Source);
        Assert.Null(mapped.UnavailableReason());
    }

    [Fact]
    public async Task ValidateCatalog_StrictFailureIsRejectedBeforeConnection()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Fixture("oracle.handwritten.json")));
        var docFile = Path.Combine(Path.GetTempPath(), "strict-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        var report = docFile + ".ndjson";
        try
        {
            var rejected = oracle.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "reference-mismatch");
            File.WriteAllText(docFile, rejected.GetProperty("document").GetRawText());
            var exit = await RLoop.Cli.Program.Main(["validate", docFile, "--catalog", Fixture("catalog.synthetic.json"), "--strict", "--url", "not-a-url", "--json", "--report", report]);
            Assert.Equal(ExitCodes.ValidationFailed, exit);
            Assert.Contains("APPLY_REFERENCE_TYPE_MISMATCH", File.ReadAllText(report));
            Assert.DoesNotContain("INVALID_RESONITE_LINK_URL", File.ReadAllText(report));
        }
        finally { File.Delete(docFile); File.Delete(report); }
    }

    [Theory]
    [InlineData("Values", "[1,2]")]
    [InlineData("Values", "")]
    [InlineData("Array", "[1,2]")]
    [InlineData("Embedded", "{\"Amount\":0.25}")]
    [InlineData("Targets", "{\"one\":null}")]
    public void Catalog_ExistingStructuredStringSyntaxRemainsValid(string member, string raw)
    {
        var doc = new ApplyDocument("1", new("test"), new("Root", null, null, null, null, "root"),
            [new("Synthetic.Holder", new Dictionary<string, JsonElement> { [member] = JsonSerializer.SerializeToElement(raw) }, "holder")]);
        Assert.Empty(ApplyCatalogValidator.Validate(doc, Catalog()));
    }

    [Fact]
    public void Catalog_UnknownPrimitiveRepresentationIsNeverCertified()
    {
        var component = new Link.ComponentDefinition { Type = new Link.TypeDefinition { FullTypeName = "Synthetic.Holder", Interfaces = [] }, Methods = [],
            Members = new() { ["Amount"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "Synthetic.UnrecognizedPrimitive" } } } };
        var primitive = new Link.TypeDefinition { FullTypeName = "Synthetic.UnrecognizedPrimitive", Interfaces = [], IsValueType = true, IsEnginePrimitive = true };
        var content = new CatalogSnapshotContent([component], [primitive], [], new Dictionary<string, string>());
        var identity = Catalog().Identity!;
        var mapped = CatalogMapper.Export(new(identity, identity, "live", true, CatalogMapper.SnapshotHash(content), content));
        var doc = new ApplyDocument("1", new("test"), new("Root", null, null, null, null, "root"),
            [new("Synthetic.Holder", new Dictionary<string, JsonElement> { ["Amount"] = JsonSerializer.SerializeToElement(1) }, "holder")]);
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Single(ApplyCatalogValidator.Validate(doc, mapped)).Code);
    }
}

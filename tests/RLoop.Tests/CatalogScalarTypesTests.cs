using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class CatalogScalarTypesTests
{
    private const string Light = "[FrooxEngine]FrooxEngine.Light";
    private const string ValueField = "[FrooxEngine]FrooxEngine.ValueField<float>";
    private const string StringOwner = "[FrooxEngine]FrooxEngine.AudioDeviceIndexFinder";
    private static string Fixture(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ResoLoop.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "tests", "fixtures", "catalog-scalars-v3", file);
    }
    private static ApplyCatalog Catalog() => ApplyCatalog.Load(Fixture("catalog.handwritten.json"));
    private static ApplyDocument Document(string owner, string member, JsonElement value, bool initial = false) =>
        new("1", new("scalar-fixture"), new("Root", null, null, null, null, "root"),
            [new(owner, initial ? null : new Dictionary<string, JsonElement> { [member] = value }, "subject",
                InitialFields: initial ? new Dictionary<string, JsonElement> { [member] = value } : null)]);

    [Fact]
    public void LiveExtractionHasIndependentDeclarationAndKeepsIncompleteGenericClosure()
    {
        var catalog = Catalog();
        Assert.Null(catalog.UnavailableReason());
        Assert.True(catalog.Find(ValueField)!.IsGeneric);
        Assert.False(catalog.Find(ValueField)!.ClosureComplete);
        Assert.Equal(File.ReadAllText(Fixture("declaration.handwritten.d.ts")).Replace("\r\n", "\n"),
            CatalogTypesGenerator.Generate(catalog).Text);
    }

    public static IEnumerable<object[]> ScalarCases()
    {
        foreach (var initial in new[] { false, true })
        {
            foreach (var json in new[] { "true", "false", "\"true\"", "\" FALSE \"", "\"yes\"", "1", "null", "\"\"", "{}", "[]" })
                yield return [Light, "Enabled", "bool", json, initial];
            foreach (var json in new[] { "42", "-2147483648", "2147483647", "\"42\"", "\" +42 \"", "\"big\"", "1.5", "2147483648", "-2147483649", "1e2", "true", "null", "{}" })
                yield return [Light, "ShadowMapResolution", "int", json, initial];
            foreach (var json in new[] { "\"hello\"", "\"\"", "\"null\"", "null", "42", "true", "{\"nested\":1}", "[1,false]" })
                yield return [StringOwner, "DeviceName", "string", json, initial];
        }
    }

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public async Task ScalarCatalogValidationMatchesRuntimeRawConversion(string owner, string member, string wire, string json, bool initial)
    {
        using var parsed = JsonDocument.Parse(json);
        var value = parsed.RootElement;
        // ApplyWorkflow and WorldService pass strings unquoted and other JSON values as raw text.
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        var issues = ApplyCatalogValidator.Validate(Document(owner, member, value, initial), Catalog());
        Link.Member? runtime = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            runtime = await ValueCodec.ParseAsync(new Link.LinkInterface(),
                new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = wire } }, raw);
        });
        Assert.Equal(error is null, issues.Count == 0);
        if (error is null && wire == "string") Assert.Equal(raw, Assert.IsType<Link.Field_string>(runtime).Value);
        if (error is not null)
        {
            Assert.Equal("VALUE_CONVERSION_FAILED", Assert.IsType<RLoopException>(error).Code);
            var issue = Assert.Single(issues);
            Assert.Equal("VALUE_CONVERSION_FAILED", issue.Code);
            Assert.Equal($"$.components[0].{(initial ? "initialFields" : "fields")}.{member}", issue.Path);
            Assert.Contains(member, issue.Message);
        }
    }

    [Theory]
    [InlineData("bool", "boolean")]
    [InlineData("System.Boolean", "boolean")]
    [InlineData("[mscorlib]System.Boolean", "boolean")]
    [InlineData("[System.Private.CoreLib]System.Boolean", "boolean")]
    [InlineData("int", "int32")]
    [InlineData("System.Int32", "int32")]
    [InlineData("[mscorlib]System.Int32", "int32")]
    [InlineData("[System.Private.CoreLib]System.Int32", "int32")]
    [InlineData("string", "string")]
    [InlineData("System.String", "string")]
    [InlineData("[mscorlib]System.String", "string")]
    [InlineData("[System.Private.CoreLib]System.String", "string")]
    public void WireAndExactFullNamesMapFromAcquiredTypesAndFieldValueTypes(string name, string representation)
    {
        var identity = Catalog().Identity!;
        var acquired = new Link.TypeDefinition { FullTypeName = name, Interfaces = [], IsEnginePrimitive = true };
        var holder = new Link.TypeDefinition { FullTypeName = "Fixture.Holder", Interfaces = [] };
        var field = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = name } };
        var content = new CatalogSnapshotContent([new() { Type = holder, Members = new() { ["Value"] = field }, Methods = [] }],
            [acquired, holder], [], new Dictionary<string, string>());
        var mapped = CatalogMapper.Export(new(identity, identity, "live", false, CatalogMapper.SnapshotHash(content), content));
        Assert.Equal(representation, mapped.Find(name)!.Representation);
        Assert.Equal(name, mapped.Find(holder.FullTypeName)!.Members!["Value"].ValueType);
        // Also classify acquired types when no member can provide a wire representation.
        content = content with { Components = [] };
        mapped = CatalogMapper.Export(new(identity, identity, "live", false, CatalogMapper.SnapshotHash(content), content));
        Assert.Equal(representation, mapped.Find(name)!.Representation);
        // And the field-only path when no independent scalar definition was acquired.
        content = content with { Components = [new() { Type = holder, Members = new() { ["Value"] = field }, Methods = [] }], Types = [holder] };
        mapped = CatalogMapper.Export(new(identity, identity, "live", false, CatalogMapper.SnapshotHash(content), content));
        Assert.Equal(representation, mapped.Find(name)!.Representation);
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("double")]
    [InlineData("Uri")]
    [InlineData("Type")]
    public void OtherWireScalarsRetainFallback(string name)
    {
        var identity = Catalog().Identity!;
        var owner = new Link.TypeDefinition { FullTypeName = "Fixture.Other", Interfaces = [] };
        var content = new CatalogSnapshotContent([new() { Type = owner, Methods = [], Members = new()
            { ["Value"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = name } } } }], [], [], new Dictionary<string, string>());
        var mapped = CatalogMapper.Export(new(identity, identity, "live", false, CatalogMapper.SnapshotHash(content), content));
        Assert.Equal("other", mapped.Find(name)!.Representation);
        Assert.Contains("\"Value\": JsonValue;", CatalogTypesGenerator.Generate(mapped).Text);
    }

    [Theory]
    [InlineData("0.25", true)]
    [InlineData("\"0.25\"", true)]
    [InlineData("\"bad\"", false)]
    [InlineData("1e100", false)]
    public void GenericOwnerAlreadyValidatesItsConfirmedFloatValue(string json, bool valid)
    {
        using var value = JsonDocument.Parse(json);
        var issues = ApplyCatalogValidator.Validate(Document(ValueField, "Value", value.RootElement), Catalog());
        Assert.Equal(valid, issues.Count == 0);
        if (!valid) Assert.Equal("VALUE_CONVERSION_FAILED", Assert.Single(issues).Code);
    }

    [Fact]
    public void OldMapperIsRejectedEvenWhenContentHashMatchesAndSuggestsRecapture()
    {
        var catalog = Catalog(); var old = catalog.Identity! with { MapperVersion = "2" };
        catalog = catalog with { Identity = old, EvidenceIdentity = old };
        var issues = ApplyCatalogValidator.Validate(Document(Light, "Enabled", JsonSerializer.SerializeToElement(true)), catalog);
        Assert.All(issues, i => { Assert.Equal("APPLY_CATALOG_UNAVAILABLE", i.Code); Assert.Contains("Recapture", i.Message); });
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Throws<RLoopException>(() => CatalogTypesGenerator.Generate(catalog)).Code);
    }

    [Theory]
    [InlineData("true", "Enabled", JsonValueKind.True)]
    [InlineData("42", "ShadowMapResolution", JsonValueKind.Number)]
    public async Task RecipeInitialParameterExpandsBeforeCatalogValidation(string initial, string member, JsonValueKind expectedKind)
    {
        var file = Path.Combine(Path.GetTempPath(), "catalog-recipe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, """
                { "schemaVersion":"1", "ownership":{"key":"recipe"}, "slot":{"key":"root","name":"Recipe","parent":"Root"},
                  "prototypes":{"state":{"slot":{"key":"child","name":"State"},"components":[{"key":"light","type":"__TYPE__","initialFields":{"__MEMBER__":"${initial}"}}]}},
                  "children":[{"$prototype":"state","$with":{"initial":__INITIAL__}}] }
                """.Replace("__TYPE__", Light).Replace("__MEMBER__", member).Replace("__INITIAL__", initial));
            var document = ApplyDocument.Load(file);
            var value = document.Children![0].Components![0].InitialFields![member];
            Assert.Equal(expectedKind, value.ValueKind);
            Assert.True((await ApplyDocumentValidator.ValidateAsync(document, catalog: Catalog())).Valid);
        }
        finally { File.Delete(file); }
    }
}

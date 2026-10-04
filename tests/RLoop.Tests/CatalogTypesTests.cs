using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class CatalogTypesTests : IDisposable
{
    private readonly string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "resoloop-catalog-types-" + Guid.NewGuid().ToString("N"))).FullName;
    private static CatalogIdentity Identity => new("fixture-engine", "fixture-link", CatalogMapper.ClientPackageVersion,
        ApplyCatalog.CurrentMapperVersion, DateTimeOffset.Parse("2026-10-03T00:00:00+00:00"));
    private static CatalogType Scalar(string name, string representation = "single") => new(name, true, true, null, [], Representation: representation);
    private static ApplyCatalog Catalog(CatalogContent? content = null)
    {
        content ??= new([
            new("Fixture.Light", true, true, null, [], Members: new Dictionary<string, CatalogMember>
            {
                ["Amount"] = new("field", ValueType: "System.Single"),
                ["Opaque"] = new("field", ValueType: "Missing.Value"),
                ["Target"] = new("reference", TargetType: "Missing.Target")
            }, MembersComplete: true), Scalar("System.Single")], new Dictionary<string, string> { ["Alias.Light"] = "Fixture.Light" });
        return new("2", Identity, Identity, "live", false, ApplyCatalog.Hash(content), content);
    }

    private static string Fixture(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ResoLoop.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "tests", "RLoop.Tests", "fixtures", "catalog-types-v1", name);
    }
    private static string HandwrittenDeclaration() => File.ReadAllText(Fixture("declaration.handwritten.d.ts")).Replace("\r\n", "\n");

    [Fact]
    public void IndependentHandwrittenDeclarationFixesHeaderAliasesFallbacksAndMissingReferenceTarget()
    {
        var catalog = Catalog();
        Assert.Equal("91bd5b53e5f843d63c9afa86ac7b96371d88a82ca1fe76260ef9fe39c588e66b", catalog.ContentHash);
        var generated = CatalogTypesGenerator.Generate(catalog);
        Assert.Equal(HandwrittenDeclaration(), generated.Text);
        Assert.Equal("1", generated.GeneratorVersion);
        Assert.Equal(3, generated.Fallbacks.Count);
        Assert.Equal(generated.Text, CatalogTypesGenerator.Generate(catalog).Text);
        Assert.Equal(generated.Text, CatalogTypesGenerator.Generate(ApplyCatalog.Load(Fixture("catalog.handwritten.json"))).Text);
    }

    [Theory]
    [InlineData("synthetic")]
    [InlineData("hash")]
    [InlineData("source")]
    [InlineData("identity")]
    [InlineData("format")]
    public void GeneratorRefusesUnavailableOrSyntheticEvidenceWithoutChangingValidatorSemantics(string kind)
    {
        var catalog = Catalog();
        catalog = kind switch
        {
            "synthetic" => catalog with { Synthetic = true },
            "hash" => catalog with { ContentHash = new string('0', 64) },
            "source" => catalog with { Source = "unverified" },
            "identity" => catalog with { EvidenceIdentity = Identity with { ResoniteVersion = "changed" } },
            _ => catalog with { FormatVersion = "999" }
        };
        if (kind == "synthetic") Assert.Null(catalog.UnavailableReason());
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Throws<RLoopException>(() => CatalogTypesGenerator.Generate(catalog)).Code);
    }

    [Fact]
    public void OnlySupportedConfirmedValuesReceiveTypesAndIncompleteNamesStayOpen()
    {
        var members = new Dictionary<string, CatalogMember>
        {
            ["Optional"] = new("field", ValueType: "Fixture.Nullable"),
            ["Position"] = new("field", ValueType: "Fixture.Tuple"),
            ["Mode"] = new("field", ValueType: "Fixture.Enum"),
            ["Flags"] = new("field", ValueType: "Fixture.Flags"),
            ["Opaque"] = new("field", ValueType: "System.Boolean"),
            ["Unknown"] = new("field", ValueType: "System.Single", Confirmed: false),
            ["UnsafeGeneric"] = new("field", ValueType: "Fixture.Generic<float>"),
            ["UnsafeTarget"] = new("reference", TargetType: "Fixture.Generic<float>"),
            ["UnconfirmedGenericTarget"] = new("reference", TargetType: "Alias.UnconfirmedGeneric"),
            ["List"] = new("list", Element: new("field", ValueType: "System.Single"))
        };
        var owner = new CatalogType("Fixture.Incomplete", true, true, null, [], Members: members, MembersComplete: false);
        var catalog = Catalog(new([owner, owner with { FullName = "Fixture.Unconfirmed", Confirmed = false, MembersComplete = true },
            owner with { FullName = "Fixture.Owner<float>", IsGeneric = true, ClosureComplete = false, MembersComplete = true },
            Scalar("System.Single"), Scalar("System.Boolean", "other"), Scalar("Fixture.Nullable", "nullable") with { ElementType = "System.Single" },
            Scalar("Fixture.Tuple", "tuple") with { ElementType = "System.Single", TupleSize = 3 },
            Scalar("Fixture.Enum", "enum") with { EnumValues = new Dictionary<string, long> { ["On"] = 1, ["Off"] = 0 }, IsFlags = false },
            Scalar("Fixture.Flags", "enum") with { EnumValues = new Dictionary<string, long> { ["On"] = 1 }, IsFlags = true },
            Scalar("Fixture.Generic<float>") with { IsGeneric = true, ClosureComplete = false },
            Scalar("Fixture.UnconfirmedGeneric<float>") with { IsGeneric = true, Confirmed = false, ClosureComplete = true }],
            new Dictionary<string, string> { ["Alias.UnconfirmedGeneric"] = "Fixture.UnconfirmedGeneric<float>" }));
        var generated = CatalogTypesGenerator.Generate(catalog);
        Assert.Contains("\"Optional\": (number) | null;", generated.Text);
        Assert.Contains("\"Position\": [number, number, number] | { x: number; y: number; z: number; } | { r: number; g: number; b: number; };", generated.Text);
        Assert.Contains("\"Mode\": \"Off\" | \"On\" | number;", generated.Text);
        Assert.Contains("\"Flags\": string | number;", generated.Text);
        foreach (var name in new[] { "Opaque", "Unknown", "UnsafeGeneric", "UnsafeTarget", "UnconfirmedGenericTarget", "List" })
            Assert.Contains($"\"{name}\": JsonValue;", generated.Text);
        Assert.Contains(generated.Fallbacks, f => f.Type == "Fixture.Incomplete" && f.Member is null);
        Assert.Contains(generated.Fallbacks, f => f.Type == "Fixture.Unconfirmed" && f.Member == "Position");
        Assert.Contains(generated.Fallbacks, f => f.Type == "Fixture.Owner<float>" && f.Member == "Optional");
    }

    [Fact]
    public void ConfirmedClosedGenericValuesCanBeTypedAndRecursiveOrMalformedValuesFallback()
    {
        var owner = new CatalogType("Fixture.Owner<float>", true, true, null, [], IsGeneric: true, Members: new Dictionary<string, CatalogMember>
        {
            ["Safe"] = new("field", ValueType: "Fixture.Generic<float>"),
            ["Cycle"] = new("field", ValueType: "Fixture.Cycle"),
            ["BadTuple"] = new("field", ValueType: "Fixture.BadTuple")
        }, MembersComplete: true);
        var generated = CatalogTypesGenerator.Generate(Catalog(new([owner,
            Scalar("Fixture.Generic<float>") with { IsGeneric = true },
            Scalar("Fixture.Cycle", "nullable") with { ElementType = "Fixture.Cycle" },
            Scalar("Fixture.BadTuple", "tuple") with { TupleSize = 5, ElementType = "Fixture.Generic<float>" }], new Dictionary<string, string>())));
        Assert.Contains("\"Safe\": number;", generated.Text);
        Assert.Contains("\"Cycle\": JsonValue;", generated.Text);
        Assert.Contains("\"BadTuple\": JsonValue;", generated.Text);
    }

    [Fact]
    public void CatalogValidatorRemainsAuthorityForGoodUnknownBadSingleAndUnavailable()
    {
        ApplyDocument Document(string member, object value) => new("1", new("fixture"), new("Root", null, null, null, null, "root"),
            [new("Fixture.Light", new Dictionary<string, JsonElement> { [member] = JsonSerializer.SerializeToElement(value) }, "light")]);
        var catalog = Catalog();
        Assert.Empty(ApplyCatalogValidator.Validate(Document("Amount", 2.5), catalog));
        Assert.Equal("COMPONENT_MEMBER_NOT_FOUND", Assert.Single(ApplyCatalogValidator.Validate(Document("Wrong", 2.5), catalog)).Code);
        Assert.Equal("VALUE_CONVERSION_FAILED", Assert.Single(ApplyCatalogValidator.Validate(Document("Amount", "bad"), catalog)).Code);
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Single(ApplyCatalogValidator.Validate(Document("Opaque", 2.5), catalog)).Code);
        Assert.Contains(CatalogTypesGenerator.Generate(catalog).Fallbacks, f => f.Member == "Opaque");
    }

    [Fact]
    public void SharedAcceptanceCatalogProvidesIndependentCrossLanguageDiagnosticCases()
    {
        var catalog = ApplyCatalog.Load(Fixture("acceptance.handwritten.json"));
        Assert.Equal("608223ad9b382c68898852c246213b6bb37d03dd74de86b1110bb40e78bff76f", catalog.ContentHash);
        Assert.Null(catalog.UnavailableReason());
        ApplyDocument Document(string member, object value) => new("1", new("fixture"), new("Root", null, null, null, null, "root"),
            [new("Synthetic.Strict", new Dictionary<string, JsonElement> { [member] = JsonSerializer.SerializeToElement(value) }, "strict")]);
        Assert.Empty(ApplyCatalogValidator.Validate(Document("Amount", 2.5), catalog));
        Assert.Equal("COMPONENT_MEMBER_NOT_FOUND", Assert.Single(ApplyCatalogValidator.Validate(Document("Missing", 2.5), catalog)).Code);
        Assert.Equal("VALUE_CONVERSION_FAILED", Assert.Single(ApplyCatalogValidator.Validate(Document("Amount", "bad"), catalog)).Code);
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Single(ApplyCatalogValidator.Validate(Document("Unsupported", true), catalog)).Code);
        var generated = CatalogTypesGenerator.Generate(catalog);
        Assert.Contains("\"Enabled\": JsonValue;", generated.Text);
        Assert.Contains("\"Target\": string | null;", generated.Text);
        Assert.Contains(generated.Fallbacks, f => f.Type == "Synthetic.Open" && f.Member is null);
        Assert.DoesNotContain(generated.Fallbacks, f => f.Type == "Synthetic.Empty");
    }

    [Fact]
    public void HundredsOfTypesProduceBoundedDeterministicDeclarations()
    {
        var strict = Catalog().Content.Types[0];
        var types = Enumerable.Range(0, 400).Select(i => strict with { FullName = $"Fixture.Component{i:D3}" }).Append(Scalar("System.Single")).ToArray();
        var catalog = Catalog(new(types, new Dictionary<string, string>()));
        var declaration = CatalogTypesGenerator.Generate(catalog);
        Assert.Equal(400, declaration.Text.Split("\"Amount\": number;", StringSplitOptions.None).Length - 1);
        Assert.Equal(401, declaration.Fallbacks.Count);
        Assert.True(declaration.Text.Length < 200_000);
        Assert.Equal(declaration.Text, CatalogTypesGenerator.Generate(catalog).Text);
    }

    [Fact]
    public async Task TypesCliIsOfflineAndWritesDeclarationWithNormalReport()
    {
        var input = Path.Combine(directory, "catalog.json"); Catalog().Save(input);
        var destination = Path.Combine(directory, "nested", "catalog.d.ts");
        var report = Path.Combine(directory, "report.json");
        var exit = await Program.RunAsync(["catalog", "types", input, "--output", destination, "--backend", "workbench", "--url", "bad", "--report", report],
            _ => throw new InvalidOperationException("offline types must not connect"));
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(HandwrittenDeclaration(), File.ReadAllText(destination));
        Assert.Contains("\"generatorVersion\":\"1\"", File.ReadAllText(report));
        Assert.False(Program.IsDirectWrite(ParsedArguments.Parse(["catalog", "types", input])));
    }

    [Fact]
    public async Task TypesCliRejectsSyntheticAndInputOverwriteBeforeWriting()
    {
        var input = Path.Combine(directory, "catalog.json"); (Catalog() with { Synthetic = true }).Save(input);
        var before = File.ReadAllText(input);
        var destination = Path.Combine(directory, "types.d.ts");
        Assert.Equal(ExitCodes.ValidationFailed, await Program.RunAsync(["catalog", "types", input, "--output", destination]));
        Assert.False(File.Exists(destination));
        Assert.Equal(ExitCodes.InvalidArguments, await Program.RunAsync(["catalog", "types", input, "--output", input]));
        Assert.Equal(before, File.ReadAllText(input));
    }

    private static CatalogCapture.Reader Reader() => new(
        name => Task.FromResult(new Link.ComponentDefinitionData { Success = true, Definition = new() { Type = Type(name), Members = new(), Methods = [] } }),
        name => Task.FromResult(new Link.TypeDefinitionData { Success = true, Definition = Type(name) }),
        _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), () => true);
    private static Link.TypeDefinition Type(string name) => new() { FullTypeName = name, Interfaces = [], IsInterface = true };

    [Fact]
    public async Task CaptureCliAndDevelopmentExporterShareSnapshotMappingAndVersionChecks()
    {
        var names = Path.Combine(directory, "names.json"); File.WriteAllText(names, "[\"Fixture.Light\"]");
        var catalogFile = Path.Combine(directory, "catalog.json"); var snapshotFile = Path.Combine(directory, "snapshot.json");
        var reads = 0; CatalogSnapshot? captured = null;
        var exit = await Program.RunAsync(["catalog", "capture", "--types", names, "--output", catalogFile, "--snapshot", snapshotFile,
            "--backend", "link", "--url", "ws://localhost:65530"], _ => throw new InvalidOperationException("capture must use shared acquisition"),
            async (uri, types, ct) =>
            {
                Assert.Equal("ws://localhost:65530/", uri.AbsoluteUri);
                Assert.Equal(new[] { "Fixture.Light" }, types);
                captured = await CatalogCapture.ReadAsync(types, Reader(), () => { reads++; return Task.FromResult(Identity); }, ct);
                return captured;
            });
        Assert.Equal(ExitCodes.Success, exit); Assert.Equal(2, reads);
        // These are exactly the save/load/export methods used by the development exporter commands.
        var saved = CatalogMapper.LoadSnapshot(snapshotFile);
        var exported = CatalogMapper.Export(saved);
        var cli = ApplyCatalog.Load(catalogFile);
        Assert.Equal(captured!.ContentHash, saved.ContentHash);
        Assert.Equal(JsonSerializer.Serialize(exported, ApplyCatalog.Json), JsonSerializer.Serialize(cli, ApplyCatalog.Json));
        Assert.False(Program.IsDirectWrite(ParsedArguments.Parse(["catalog", "capture"])));
    }

    [Fact]
    public async Task CaptureVersionChangeFailsAndFinalDeadlineLeavesUnverifiedSnapshot()
    {
        var reads = 0;
        var error = await Assert.ThrowsAsync<RLoopException>(() => CatalogCapture.ReadAsync(["Fixture.Light"], Reader(),
            () => Task.FromResult(++reads == 1 ? Identity : Identity with { ResoniteVersion = "changed" })));
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", error.Code);
        using var deadline = new CancellationTokenSource(); reads = 0;
        var snapshot = await CatalogCapture.ReadAsync(["Fixture.Light"], Reader(), () =>
        {
            if (++reads == 1) return Task.FromResult(Identity);
            deadline.Cancel(); return new TaskCompletionSource<CatalogIdentity>().Task;
        }, deadline: deadline.Token);
        Assert.Equal("unverified", snapshot.Source);
        Assert.Contains(snapshot.Content.AcquisitionFailures!, f => f.Request == "session");
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Throws<RLoopException>(() => CatalogMapper.Export(snapshot)).Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[\" \" ]")]
    [InlineData("[\" Fixture.Light\"]")]
    public async Task CaptureBadExplicitNameArrayFailsBeforeAcquisition(string json)
    {
        var names = Path.Combine(directory, "names.json"); File.WriteAllText(names, json);
        Assert.Equal(ExitCodes.InvalidArguments, await Program.RunAsync(["catalog", "capture", "--types", names,
            "--output", Path.Combine(directory, "catalog.json"), "--url", "ws://localhost:65530", "--backend", "link"],
            captureCatalog: (_, _, _) => throw new InvalidOperationException("invalid names must not acquire")));
    }

    [Fact]
    public async Task CaptureBoundAndWorkbenchRefusalOccurBeforeAcquisition()
    {
        var names = Path.Combine(directory, "names.json"); File.WriteAllText(names, JsonSerializer.Serialize(Enumerable.Repeat("Fixture.Light", 513)));
        Assert.Equal("INVALID_ARGUMENT", Assert.Throws<RLoopException>(() => CatalogCommands.LoadNames(names)).Code);
        var exit = await Program.RunAsync(["catalog", "capture", "--types", names, "--output", Path.Combine(directory, "catalog.json"), "--backend", "workbench"],
            captureCatalog: (_, _, _) => throw new InvalidOperationException("workbench must refuse"));
        Assert.Equal(ExitCodes.OperationFailed, exit);
    }

    public void Dispose() => Directory.Delete(directory, true);
}

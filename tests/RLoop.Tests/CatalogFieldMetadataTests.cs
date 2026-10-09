using RLoop.Core;

namespace RLoop.Tests;

public sealed class CatalogFieldMetadataTests
{
    private static CatalogIdentity Identity => new("fixture-engine", "fixture-link", "fixture-client", "3",
        DateTimeOffset.Parse("2026-10-03T00:00:00+00:00"));

    private static CatalogType Type(string name, string representation = "other", bool confirmed = true,
        bool closed = true, string? baseType = null, string[]? interfaces = null, bool generic = false) =>
        new(name, confirmed, closed, baseType, interfaces ?? [], IsGeneric: generic, Representation: representation);

    private static CatalogType Field(string name, string? memberType, string? valueType, string? targetType = null,
        string kind = "field", bool confirmed = true) =>
        new(name, true, false, null, [], Members: new Dictionary<string, CatalogMember>
        {
            ["Value"] = new(kind, memberType, valueType, targetType, Confirmed: confirmed)
        });

    private static ApplyCatalog Catalog(IEnumerable<CatalogType> types, IReadOnlyDictionary<string, string>? aliases = null) =>
        new("2", Identity, Identity, "live", false,
            ApplyCatalog.Hash(new(types.ToArray(), aliases ?? new Dictionary<string, string>())),
            new(types.ToArray(), aliases ?? new Dictionary<string, string>()));

    private static CatalogMemberEvidence Describe(ApplyCatalog catalog, string owner = "Fixture.Owner") =>
        CatalogFieldMetadata.Describe(catalog, catalog.Content.Types.Single(t => t.FullName == owner),
            catalog.Content.Types.Single(t => t.FullName == owner).Members!["Value"]);

    [Theory]
    [InlineData("System.Boolean", "boolean")]
    [InlineData("System.Int32", "int32")]
    [InlineData("System.String", "string")]
    [InlineData("System.Single", "single")]
    public void SupportedDirectRepresentationsAreIndependentOfInterfaceEvidence(string name, string representation)
    {
        var catalog = Catalog([Field("Fixture.Owner", null, name), Type(name, representation)]);
        var evidence = Describe(catalog);
        Assert.Equal(representation, evidence.Scalar);
        Assert.Null(evidence.FieldInterface);
        Assert.Equal("field", evidence.Kind);
        Assert.Equal("field", evidence.CatalogKind);
    }

    [Fact]
    public void AliasesResolveToCanonicalScalarAndExactClosedFieldTarget()
    {
        const string canonicalValue = "System.Boolean";
        const string canonicalField = "[FrooxEngine]FrooxEngine.IField<System.Boolean>";
        var catalog = Catalog([
            Field("Fixture.Owner", "Alias.Sync", "Alias.Bool", "Alias.IField", "reference"),
            Type("Fixture.Sync", interfaces: ["Alias.IField"]),
            Type(canonicalField, generic: true), Type(canonicalValue, "boolean")],
            new Dictionary<string, string>
            {
                ["Alias.Sync"] = "Fixture.Sync", ["Alias.Bool"] = canonicalValue, ["Alias.IField"] = canonicalField
            });

        var evidence = Describe(catalog);
        Assert.Equal("reference", evidence.Kind);
        Assert.Equal("Alias.Sync", evidence.MemberType);
        Assert.Equal("Alias.Bool", evidence.ValueType);
        Assert.Equal("Alias.IField", evidence.TargetType);
        Assert.Equal(new CatalogFieldTargetEvidence("boolean", canonicalValue, canonicalField), evidence.FieldTarget);
        Assert.Null(evidence.Scalar);
    }

    [Fact]
    public void PositiveInheritedFieldPathSurvivesCycleAndMissingUnrelatedBranch()
    {
        const string canonicalField = "[FrooxEngine]FrooxEngine.IField<System.Single>";
        var catalog = Catalog([
            Field("Fixture.Owner", "Fixture.Sync", "System.Single"),
            Type("Fixture.Sync", closed: false, baseType: "Fixture.Missing", interfaces: ["Alias.IField"]),
            Type(canonicalField, generic: true), Type("System.Single", "single")],
            new Dictionary<string, string> { ["Alias.IField"] = canonicalField });

        var evidence = Describe(catalog);
        Assert.Equal("single", evidence.Scalar);
        Assert.Equal(canonicalField, evidence.FieldInterface);
    }

    [Fact]
    public void SourceTraversalContinuesPastWrongArgumentUntilCanonicalMatch()
    {
        const string wrong = "[FrooxEngine]FrooxEngine.IField<System.Int32>";
        const string expected = "[FrooxEngine]FrooxEngine.IField<System.Boolean>";
        var catalog = Catalog([Field("Fixture.Owner", "Fixture.Sync", "System.Boolean"),
            Type("Fixture.Sync", interfaces: [wrong, expected]), Type(wrong, generic: true), Type(expected, generic: true),
            Type("System.Boolean", "boolean"), Type("System.Int32", "int32")]);

        Assert.Equal(expected, Describe(catalog).FieldInterface);
    }

    [Fact]
    public void ReferenceRequiresDirectIFieldTargetAndAcceptsAValidIntegerTarget()
    {
        const string field = "[FrooxEngine]FrooxEngine.IField<System.Boolean>";
        var derivedCatalog = Catalog([Field("Fixture.Owner", null, "System.Boolean", "Fixture.Derived", "reference"),
            Type("Fixture.Derived", baseType: field), Type(field, generic: true), Type("System.Boolean", "boolean")]);
        Assert.Null(Describe(derivedCatalog).FieldTarget);

        const string integerField = "[FrooxEngine]FrooxEngine.IField<System.Int32>";
        var integerCatalog = Catalog([Field("Fixture.Owner", null, "System.Boolean", integerField, "reference"),
            Type(integerField, generic: true), Type("System.Boolean", "boolean"), Type("System.Int32", "int32")]);
        Assert.Equal(new CatalogFieldTargetEvidence("int32", "System.Int32", integerField), Describe(integerCatalog).FieldTarget);
    }

    [Fact]
    public void CyclesAndPathsPastDepthLimitCannotCreateFieldProof()
    {
        var types = new List<CatalogType> { Field("Fixture.Owner", "Fixture.Node0", "System.Boolean"),
            Type("System.Boolean", "boolean") };
        for (var i = 0; i < 65; i++)
            types.Add(Type($"Fixture.Node{i}", baseType: i == 64 ? "Fixture.Node0" : $"Fixture.Node{i + 1}"));
        types.Add(Type("[FrooxEngine]FrooxEngine.IField<System.Boolean>", generic: true));
        types[^2] = types[^2] with { Interfaces = ["[FrooxEngine]FrooxEngine.IField<System.Boolean>"] };
        var catalog = Catalog(types);
        Assert.Null(Describe(catalog).FieldInterface);
    }

    [Fact]
    public void FieldInterfaceArgumentMustMatchCanonicalValueTypeEvenForSameRepresentation()
    {
        const string integerField = "[FrooxEngine]FrooxEngine.IField<System.Int32>";
        var catalog = Catalog([Field("Fixture.Owner", "Fixture.Sync", "System.Boolean"),
            Type("Fixture.Sync", interfaces: [integerField]), Type(integerField, generic: true),
            Type("System.Boolean", "boolean"), Type("System.Int32", "boolean")]);
        var evidence = Describe(catalog);
        Assert.Equal("boolean", evidence.Scalar);
        Assert.Null(evidence.FieldInterface);
    }

    [Theory]
    [InlineData("[Other]FrooxEngine.IField<System.Boolean>")]
    [InlineData("[FrooxEngine]Other.IField<System.Boolean>")]
    [InlineData("[FrooxEngine]FrooxEngine.NotIField<System.Boolean>")]
    [InlineData("[FrooxEngine]FrooxEngine.IField<System.Boolean,System.Int32>")]
    [InlineData("[FrooxEngine]FrooxEngine.IField<Fixture.Box<System.Boolean>>")]
    [InlineData("[FrooxEngine]FrooxEngine.IField< System.Boolean>")]
    public void SimilarOrMalformedFieldNamesNeverProveStrictEvidence(string fieldName)
    {
        var catalog = Catalog([
            Field("Fixture.Owner", "Fixture.Sync", "System.Boolean", fieldName, "reference"),
            Type("Fixture.Sync", interfaces: [fieldName]), Type(fieldName, generic: true),
            Type("System.Boolean", "boolean")]);
        Assert.Null(Describe(catalog).FieldTarget);
        Assert.Null(Describe(catalog).FieldInterface);
    }

    [Fact]
    public void UnconfirmedOrUnclosedFieldAndUnconfirmedArgumentCannotProveEvidence()
    {
        const string field = "[FrooxEngine]FrooxEngine.IField<System.Boolean>";
        foreach (var variant in new[] { "unconfirmed-field", "open-field", "unconfirmed-argument", "missing-argument" })
        {
            var candidate = variant == "missing-argument" ? "[FrooxEngine]FrooxEngine.IField<System.Int32>" : field;
            var target = Type(candidate, generic: true, confirmed: variant != "unconfirmed-field", closed: variant != "open-field");
            var catalogTypes = new List<CatalogType>
            {
                Field("Fixture.Owner", "Fixture.Sync", "System.Boolean", candidate, "reference"),
                Type("Fixture.Sync", interfaces: [candidate]), target,
                Type("System.Boolean", "boolean", confirmed: variant != "unconfirmed-argument")
            };
            if (variant != "missing-argument") catalogTypes.Add(Type("System.Int32", "int32"));
            var catalog = Catalog(catalogTypes);
            Assert.Null(Describe(catalog).FieldTarget);
            Assert.Null(Describe(catalog).FieldInterface);
        }
    }

    [Fact]
    public void UnconfirmedOwnerAndMemberAndNonScalarWrappersCannotAssertScalarEvidence()
    {
        var owner = Field("Fixture.Owner", null, "System.Boolean") with { Confirmed = false };
        var boolean = Type("System.Boolean", "boolean");
        Assert.Null(Describe(Catalog([owner, boolean])).Scalar);

        var memberCatalog = Catalog([Field("Fixture.Owner", null, "System.Boolean", confirmed: false), boolean]);
        Assert.False(Describe(memberCatalog).Confirmed);
        Assert.Null(Describe(memberCatalog).Scalar);

        var wrapped = Catalog([Field("Fixture.Owner", null, "Fixture.Nullable"), boolean,
            Type("Fixture.Nullable", "nullable")]);
        Assert.Null(Describe(wrapped).Scalar);
    }

    [Fact]
    public void UnclosedGenericValueAndOwnerCannotProduceScalarOrReferenceEvidence()
    {
        var genericValue = Type("Fixture.Value<System.Boolean>", "boolean", generic: true, closed: false);
        var valueCatalog = Catalog([Field("Fixture.Owner", null, genericValue.FullName), genericValue]);
        Assert.Null(Describe(valueCatalog).Scalar);

        var owner = Field("Fixture.Owner<float>", "Fixture.Sync", null, "[FrooxEngine]FrooxEngine.IField<System.Boolean>", "reference")
            with { IsGeneric = true, ClosureComplete = false };
        var field = Type("[FrooxEngine]FrooxEngine.IField<System.Boolean>", generic: true);
        var catalog = Catalog([owner, Type("Fixture.Sync", interfaces: [field.FullName]), field, Type("System.Boolean", "boolean")]);
        Assert.Null(Describe(catalog, owner.FullName).FieldTarget);
    }
}

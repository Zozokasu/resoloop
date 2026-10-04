using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private ApplyDocument AliasDocument(string components, string extra = "")
    {
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"aliases\"},\"slot\":{\"key\":\"root\",\"name\":\"Aliases\",\"parent\":\"Root\"},\"components\":" + components + extra + "}");
        return ApplyDocument.Load(file);
    }

    [Fact]
    public void FieldAliasesResolveForwardNestedValuesAndProbeTargets()
    {
        var document = AliasDocument("""
            [{"key":"wire","type":"Test.Source","fields":{"Target":"$field:enabled","Nested":{"a":["$field:clock"]}}},
             {"key":"state","type":"Test.Target","fields":{"Enabled":false},"propertyModes":{"Clock":"runtime"},"fieldAliases":{"enabled":"Enabled","clock":"Clock"}}]
            """, """
            ,"tests":[{"name":"read","assertions":[{"target":"$field:enabled","expected":false}],"probe":{"kind":"set-members","safe":true,"values":{"$field:enabled":true}}}]
            """);
        var wire = document.Components![0];
        Assert.Equal("$member:state.Enabled", wire.Fields!["Target"].GetString());
        Assert.Equal("$member:state.Clock", wire.Fields["Nested"].GetProperty("a")[0].GetString());
        Assert.Equal("$member:state.Enabled", document.Tests![0].Assertions![0].Target);
        Assert.Contains("$member:state.Enabled", document.Tests[0].Probe!.Values!.Keys);
        Assert.DoesNotContain("$field:", JsonSerializer.Serialize(document));
        Assert.False(StableSelectorSyntax.TryParse("$field:enabled", out _));
    }

    [Fact]
    public void FieldAliasesJsonScopeAndQualifiedOutsideReference()
    {
        var document = AliasDocument("""[{"key":"wire","type":"Test.Source","fields":{"Target":"$field:left::enabled"}}]""", """
            ,"children":[{"$scope":"left","slot":{"key":"body","name":"Left"},"components":[
            {"key":"state","type":"Test.Target","initialFields":{"Enabled":false},"fieldAliases":{"enabled":"Enabled"}},
            {"key":"wire","type":"Test.Source","fields":{"Target":"$field:enabled"}}]},
            {"$scope":"right","slot":{"key":"body","name":"Right"},"components":[{"key":"state","type":"Test.Target","fields":{"Enabled":true},"fieldAliases":{"enabled":"Enabled"}}]}]
            """);
        Assert.Equal("$member:left::state.Enabled", document.Components![0].Fields!["Target"].GetString());
        Assert.Equal("$member:left::state.Enabled", document.Children![0].Components![1].Fields!["Target"].GetString());
        Assert.Equal("Enabled", document.Children[1].Components![0].FieldAliases!["right::enabled"]);
    }

    [Theory]
    [InlineData("""[{"key":"wire","type":"Test.Source","fields":{"Target":"$field:missing"}}]""", "APPLY_FIELD_ALIAS_NOT_FOUND", "$.components[0].fields[\"Target\"]")]
    [InlineData("""[{"key":"a","type":"Test.Target","fieldAliases":{"x":"Enabled"}}]""", "APPLY_FIELD_ALIAS_MEMBER_UNDECLARED", "$.components[0][\"fieldAliases\"][\"x\"]")]
    [InlineData("""[{"key":"a","type":"Test.Target","fields":{"Enabled":true},"fieldAliases":{"x":"Enabled"}},{"key":"b","type":"Test.Target","fields":{"Enabled":false},"fieldAliases":{"x":"Enabled"}}]""", "APPLY_FIELD_ALIAS_DUPLICATE", "$.components[1][\"fieldAliases\"][\"x\"]")]
    [InlineData("""[{"type":"Test.Target","fields":{"Enabled":true},"fieldAliases":{"x":"Enabled"}}]""", "APPLY_FIELD_ALIAS_INVALID", "$.components[0][\"fieldAliases\"][\"x\"]")]
    public void FieldAliasFailuresHaveExactJsonIssuePath(string components, string code, string path)
    {
        var error = Assert.Throws<RLoopException>(() => AliasDocument(components));
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.Context["jsonPath"]);
        var diagnostic = Assert.Single(ApplyDiagnostics.ForException(error, "validate").Diagnostics);
        Assert.Equal(path, diagnostic.JsonPath);
    }

    [Fact]
    public async Task FieldAliasAndExistingMemberSelectorsProduceSameOperationsAndStoredSelectors()
    {
        const string source = """[{"key":"wire","type":"Test.Source","fields":{"Target":"$field:enabled"}},{"key":"state","type":"Test.Target","fields":{"Enabled":false},"fieldAliases":{"enabled":"Enabled"}}]""";
        var alias = AliasDocument(source);
        var direct = AliasDocument(source.Replace("$field:enabled", "$member:state.Enabled").Replace(",\"fieldAliases\":{\"enabled\":\"Enabled\"}", ""));
        var client = new FakeResoniteClient(alias);
        var world = new WorldService(client);
        var state = Path.Combine(_root, "aliases.state.json");
        var aliasPlan = await world.PlanApplyAsync(alias, new(state));
        var directPlan = await world.PlanApplyAsync(direct, new(state));
        Assert.Equal(JsonSerializer.Serialize(directPlan.Changes), JsonSerializer.Serialize(aliasPlan.Changes));
        await world.ApplyAsync(alias, new(state));
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal("$member:state.Enabled", saved["components"]!["wire"]!["referenceSelectors"]!["Target"]!.GetValue<string>());
        client.ResetWriteCounts();
        Assert.Empty((await world.PlanApplyAsync(direct, new(state, RequireState: true))).Changes);
        await world.ApplyAsync(direct, new(state, RequireState: true));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldAliasBundleLoweringPreservesOnlyExactOriginalValueSource(bool parameter)
    {
        var bundle = NewBundle();
        var ir = JsonNode.Parse(bundle.Root["ir"]!["text"]!.GetValue<string>())!;
        ir["components"]![0]!["fields"] = new JsonObject { ["Missing"] = parameter ? "${selector}" : "$field:enabled", ["Amount"] = 1 };
        ir["components"]![0]!["fieldAliases"] = new JsonObject { ["enabled"] = "Amount" };
        if (parameter) ir["parameters"] = new JsonObject { ["selector"] = "$field:enabled" };
        var irText = ir.ToJsonString();
        bundle.Root["ir"] = BundlePayload(irText);
        var map = JsonNode.Parse(bundle.Root["map"]!["text"]!.GetValue<string>())!;
        map["irSha256"] = BundleHash(irText);
        var known = new JsonObject { ["status"] = "known", ["file"] = bundle.Entry, ["sha256"] = BundleHash(File.ReadAllText(bundle.Entry)),
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["offset"] = 0, ["line"] = 1, ["column"] = 1 },
            ["end"] = new JsonObject { ["offset"] = 2, ["line"] = 1, ["column"] = 3 } } };
        map["entries"] = new JsonArray(new JsonObject { ["jsonPath"] = "$.components[0].fields[\"Missing\"]",
            ["pathSegments"] = new JsonArray("components", 0, "fields", "Missing"), ["entityKind"] = "component", ["key"] = "base", ["member"] = "Missing",
            ["source"] = known.DeepClone(), ["valueSource"] = known.DeepClone() });
        bundle.Root["map"] = BundlePayload(map.ToJsonString());
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1"));
        var diagnostic = Assert.Single(ApplyDiagnostics.ForException(error, "validate").Diagnostics, d => d.Member == "Missing");
        Assert.Equal(parameter ? "unknown" : "known", diagnostic.Source.Status);
    }

    [Fact]
    public async Task FieldAliasesActualTsxAndHandwrittenJsonHaveEquivalentMemberPlans()
    {
        var package = JsxPackage.Value;
        var output = Path.Combine(_root, "actual-fields.json");
        RunNode(package, "dist/src/cli.js", "build", "test/fixtures/field-aliases.tsx", "-o", output);
        var tsx = ApplyDocument.Load(output);
        var json = ApplyDocument.Load(Path.Combine(package, "test/fixtures/field-aliases.handwritten.json"));
        Assert.Equal(JsonSerializer.Serialize(tsx.Components), JsonSerializer.Serialize(json.Components));
        Assert.Equal(JsonSerializer.Serialize(tsx.Children), JsonSerializer.Serialize(json.Children));
        var world = new WorldService(new FakeResoniteClient(tsx));
        var state = Path.Combine(_root, "tsx-aliases.state.json");
        Assert.Equal(JsonSerializer.Serialize((await world.PlanApplyAsync(tsx, new(state))).Changes),
            JsonSerializer.Serialize((await world.PlanApplyAsync(json, new(state))).Changes));
    }

    [Fact]
    public async Task FieldAliasSameKeysPreserveIdsAfterNameAndPlacementChanges()
    {
        const string source = """[{"key":"wire","type":"Test.Source","fields":{"Target":"$field:enabled"}},{"key":"state","type":"Test.Target","fields":{"Enabled":false},"fieldAliases":{"enabled":"Enabled"}}]""";
        var original = AliasDocument(source);
        var client = new FakeResoniteClient(original);
        var world = new WorldService(client);
        var state = Path.Combine(_root, "identity-aliases.state.json");
        await world.ApplyAsync(original, new(state));
        var before = StateIds(JsonNode.Parse(File.ReadAllText(state))!.AsObject());
        var changed = original with { Slot = original.Slot! with { Name = "Renamed", Position = [2, 3, 4] } };
        client.ResetWriteCounts();
        await world.ApplyAsync(changed, new(state, RequireState: true));
        Assert.Equal(before, StateIds(JsonNode.Parse(File.ReadAllText(state))!.AsObject()));
        client.ResetWriteCounts();
        Assert.Empty((await world.PlanApplyAsync(changed, new(state, RequireState: true))).Changes);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FieldAliasCompileErrorsUseExactBundleEntries(bool undeclared, bool parameter)
    {
        var bundle = NewBundle();
        var ir = JsonNode.Parse(bundle.Root["ir"]!["text"]!.GetValue<string>())!;
        if (undeclared) ir["components"]![0]!["fieldAliases"] = new JsonObject { ["bad"] = parameter ? "${member}" : "Missing" };
        else ir["components"]![0]!["fields"] = new JsonObject { ["Target"] = parameter ? "${selector}" : "$field:missing" };
        if (parameter) ir["parameters"] = new JsonObject { ["member"] = "Missing", ["selector"] = "$field:missing" };
        var irText = ir.ToJsonString();
        bundle.Root["ir"] = BundlePayload(irText);
        var map = JsonNode.Parse(bundle.Root["map"]!["text"]!.GetValue<string>())!;
        map["irSha256"] = BundleHash(irText);
        var known = new JsonObject { ["status"] = "known", ["file"] = bundle.Entry, ["sha256"] = BundleHash(File.ReadAllText(bundle.Entry)),
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["offset"] = 0, ["line"] = 1, ["column"] = 1 },
            ["end"] = new JsonObject { ["offset"] = 2, ["line"] = 1, ["column"] = 3 } } };
        var section = undeclared ? "fieldAliases" : "fields";
        var member = undeclared ? "bad" : "Target";
        map["entries"] = new JsonArray(new JsonObject { ["jsonPath"] = undeclared ? "$.components[0][\"fieldAliases\"][\"bad\"]" : "$.components[0].fields[\"Target\"]",
            ["pathSegments"] = new JsonArray("components", 0, section, member), ["entityKind"] = "component", ["key"] = "base", ["member"] = member,
            ["source"] = known.DeepClone(), ["valueSource"] = known.DeepClone() });
        bundle.Root["map"] = BundlePayload(map.ToJsonString());
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(bundle.Save(), "R1"));
        var diagnostic = Assert.Single(ApplyDiagnostics.ForException(error, "apply").Diagnostics);
        Assert.Equal(undeclared ? "APPLY_FIELD_ALIAS_MEMBER_UNDECLARED" : "APPLY_FIELD_ALIAS_NOT_FOUND", diagnostic.Code);
        Assert.Equal(parameter ? "unknown" : "known", diagnostic.Source.Status);
        Assert.Equal(parameter ? null : bundle.Entry, diagnostic.Source.File);
        Assert.Equal("R1", diagnostic.BuildId);
        Assert.Equal(member, diagnostic.Member);
        Assert.Equal(parameter ? "unknown" : "complete", diagnostic.Completeness["location"]);
        foreach (var evidence in new[] { "type", "member", "reference", "inputs", "runtime" })
            Assert.Equal("unknown", diagnostic.Completeness[evidence]);
    }
}

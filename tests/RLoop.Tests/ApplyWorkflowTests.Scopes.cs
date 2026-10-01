using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task ScopedActualTsxInstancesResolveLocallyAndPreserveIdsAfterSiblingInsertion()
    {
        var package = JsxPackage.Value;
        var path = Path.Combine(_root, "scoped.json");
        var insertedPath = Path.Combine(_root, "inserted.json");
        RunNode(package, "dist/src/cli.js", "build", "test/fixtures/scoped.tsx", "-o", path);
        RunNode(package, "dist/src/cli.js", "build", "test/fixtures/scoped-inserted.tsx", "-o", insertedPath);
        var document = ApplyDocument.Load(path);
        var inserted = ApplyDocument.Load(insertedPath);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "scoped.state.json");
        await service.ApplyAsync(document, new(state));
        var before = StateIds(JsonNode.Parse(File.ReadAllText(state))!.AsObject());
        foreach (var instance in new[] { "left", "right" })
        {
            var body = await service.ResolveStableReferenceAsync(state, "$slot:" + instance + "::body", client.SessionId);
            var target = await service.ResolveStableReferenceAsync(state, "$component:" + instance + "::state", client.SessionId);
            var member = await service.ResolveStableReferenceAsync(state, "$member:" + instance + "::state.Enabled", client.SessionId);
            var rotation = await service.ResolveStableReferenceAsync(state, "$slot-member:" + instance + "::body.Rotation", client.SessionId);
            var slot = client.Root.Children.Single().Children.Single(slot => slot.Id == body.Id);
            var wire = slot.Components.Single(component => component.Type == "Test.Source");
            Assert.Equal(target.Id, wire.Members["Target"].TargetId);
            Assert.Equal(member.Id, wire.Members["Member"].TargetId);
            Assert.Equal(body.Id, wire.Members["Slot"].TargetId);
            Assert.Equal(rotation.Id, wire.Members["Rotation"].TargetId);
        }
        client.ResetWriteCounts();
        Assert.Empty((await service.PlanApplyAsync(document, new(state, RequireState: true))).Changes);
        await service.ApplyAsync(document, new(state, RequireState: true));
        Assert.Equal(0, client.Writes);
        var changes = (await service.PlanApplyAsync(inserted, new(state, RequireState: true))).Changes;
        Assert.Single(changes);
        Assert.Equal("unrelated", changes[0].Key);
        await service.ApplyAsync(inserted, new(state, RequireState: true));
        var after = StateIds(JsonNode.Parse(File.ReadAllText(state))!.AsObject());
        Assert.All(before, id => Assert.Contains(id, after));
        client.ResetWriteCounts();
        Assert.Empty((await service.PlanApplyAsync(inserted, new(state, RequireState: true))).Changes);
        await service.ApplyAsync(inserted, new(state, RequireState: true));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task LegacyEffectiveKeysToNewActualTsxBuildHasZeroDiffZeroWritesAndAllIds()
    {
        var path = Path.Combine(_root, "legacy.json");
        // Old JSON: omitted child/component keys, plus literal delimiter/colon keys.
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"legacy"},"slot":{"key":"root","name":"Legacy","parent":"Root"},
             "components":[{"type":"Test.Target","fields":{"Enabled":true}},{"key":"literal::old:key","type":"Test.Source","fields":{"Target":"$slot:root"}}],
             "children":[{"slot":{"name":"Child"},"components":[{"type":"Test.Target","fields":{"Enabled":false}}]}]}
            """);
        var original = ApplyDocument.Load(path);
        var client = new FakeResoniteClient(original);
        var service = new WorldService(client);
        var state = Path.Combine(_root, "legacy.state.json");
        await service.ApplyAsync(original, new(state));
        var checkpoint = JsonNode.Parse(File.ReadAllText(state))!.AsObject();
        var ids = StateIds(checkpoint);
        var package = JsxPackage.Value;
        var workspace = Path.Combine(package, ".s2-2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var entry = Path.Combine(workspace, "main.tsx");
            File.WriteAllText(entry, """
                import { Slot, Component } from "resoloop-jsx";
                export const ownership = { key: "legacy" };
                export default <Slot key="root" name="Legacy" parent="Root">
                  <Component key="root/component:Test.Target:0" type="Test.Target" fields={{ Enabled: true }} />
                  <Component key="literal::old:key" type="Test.Source" fields={{ Target: "$slot:root" }} />
                  <Slot key="$path:Root/Legacy/Child" name="Child">
                    <Component key="$path:Root/Legacy/Child/component:Test.Target:0" type="Test.Target" fields={{ Enabled: false }} />
                  </Slot>
                </Slot>;
                """);
            var output = Path.Combine(_root, "new.json");
            RunNode(package, "dist/src/cli.js", "build", entry, "-o", output);
            var rebuilt = ApplyDocument.Load(output);
            client.ResetWriteCounts();
            Assert.Empty((await service.PlanApplyAsync(rebuilt, new(state, RequireState: true))).Changes);
            await service.ApplyAsync(rebuilt, new(state, RequireState: true));
            Assert.Equal(0, client.Writes);
            Assert.Equal(ids, StateIds(JsonNode.Parse(File.ReadAllText(state))!.AsObject()));
        }
        finally { Directory.Delete(workspace, true); }
    }

    private string ScopeSource(string children)
    {
        var path = Path.Combine(_root, "scope-source.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"scopes"},"slot":{"key":"root","name":"Root"},"children":
            """ + children + "}");
        return path;
    }

    [Fact]
    public async Task JsonScopesShareTsxRulesForNestedSelectorsInitialFieldsAndMigration()
    {
        var document = ApplyDocument.Load(ScopeSource("""
            [{"$scope":"a","slot":{"key":"body","name":"A","migrateFrom":"old"},"children":[
              {"$scope":"b","slot":{"key":"body","name":"B"},"components":[
                {"key":"state","type":"Test.Target","fields":{"Enabled":true}},
                {"key":"wire","migrateFrom":"old","type":"Test.Source","fields":{"Target":"$ref:state","Slot":"$slot:body","Member":"$member:state.Enabled"},
                 "initialFields":{"Rotation":"$slot-member:body.Rotation","Nested":[{"Target":"$component:a::b::state"}]}}]}]}]
            """));
        var node = document.Children![0].Children![0];
        Assert.Equal("a::b::body", node.Slot.Key);
        Assert.Equal("a::old", document.Children[0].Slot.MigrateFrom);
        Assert.Equal("a::b::old", node.Components![1].MigrateFrom);
        Assert.Equal("$ref:a::b::state", node.Components[1].Fields!["Target"].GetString());
        Assert.Equal("$member:a::b::state.Enabled", node.Components[1].Fields!["Member"].GetString());
        Assert.Equal("$slot-member:a::b::body.Rotation", node.Components[1].InitialFields!["Rotation"].GetString());
        Assert.True((await ApplyDocumentValidator.ValidateAsync(document)).Valid);
    }

    [Theory]
    [InlineData("a::b", "body")]
    [InlineData("a:", "body")]
    [InlineData(":a", "body")]
    [InlineData("", "body")]
    [InlineData("a", "x:y")]
    [InlineData("a", "x::y")]
    [InlineData("a", "")]
    public void ScopeSegmentsRejectSeparatorAmbiguity(string instance, string key)
    {
        var source = JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["$scope"] = instance, ["slot"] = new { key, name = "A" } } });
        Assert.Equal("APPLY_SCOPE_INVALID", Assert.Throws<RLoopException>(() => ApplyDocument.Load(ScopeSource(source))).Code);
    }

    [Theory]
    [InlineData("[{\"$scope\":\"a\",\"slot\":{\"key\":\"body\",\"name\":\"A\"}},{\"slot\":{\"key\":\"a::body\",\"name\":\"B\"}}]")]
    [InlineData("[{\"$scope\":\"a\",\"slot\":{\"key\":\"body\",\"name\":\"A\"}},{\"$scope\":\"a\",\"slot\":{\"key\":\"other\",\"name\":\"B\"}}]")]
    [InlineData("[{\"$scope\":\"a\",\"slot\":{\"key\":\"body\",\"name\":\"A\"},\"components\":[{\"key\":\"body\",\"type\":\"T\"}]}]")]
    public void ScopedAndLegacyFlattenedKeyCollisionsStopCompilation(string source)
    {
        Assert.Equal("APPLY_EXPANDED_KEY_CONFLICT", Assert.Throws<RLoopException>(() => ApplyDocument.Load(ScopeSource(source))).Code);
    }

    [Fact]
    public async Task ScopeLocalReferencesDoNotEscapeToGlobalsAndFailBeforeWrites()
    {
        var document = ApplyDocument.Load(ScopeSource("""
            [{"slot":{"key":"target","name":"Global"}},
             {"$scope":"a","slot":{"key":"body","name":"A"},"components":[{"key":"wire","type":"Test.Source","fields":{"Target":"$slot:target"}}]}]
            """));
        var validation = await ApplyDocumentValidator.ValidateAsync(document);
        Assert.False(validation.Valid);
        Assert.Contains(validation.Issues, issue => issue.Code == "APPLY_REFERENCE_NOT_FOUND" && issue.Message.Contains("a::target"));
        var client = new FakeResoniteClient(document);
        await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document, new(Path.Combine(_root, "invalid.state.json"))));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public void IndexDerivedDraftBuildCannotLoadForValidateOrApply()
    {
        var package = JsxPackage.Value;
        var output = Path.Combine(_root, "draft.json");
        RunNode(package, "dist/src/cli.js", "build", "test/fixtures/missing-key-draft.tsx", "--draft", "-o", output);
        Assert.Equal("APPLY_DRAFT_KEY_UNSTABLE", Assert.Throws<RLoopException>(() => ApplyDocument.Load(output)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitKeysCannotCollideWithLegacyDefaultEffectiveKeys(bool implicitFirst)
    {
        var implicitComponent = new ApplyComponentSpec("[Test]Test.Target", null);
        var explicitComponent = new ApplyComponentSpec("Test.Source", null, "root/component:Test.Target:0");
        var document = AuthoringSchema.Scaffold("root") with
        {
            Components = implicitFirst ? [implicitComponent, explicitComponent] : [explicitComponent, implicitComponent]
        };
        var validation = await ApplyDocumentValidator.ValidateAsync(document);
        Assert.False(validation.Valid);
        Assert.Contains(validation.Issues, issue => issue.Code == "APPLY_KEY_DUPLICATE" && issue.Message.Contains("root/component:Test.Target:0"));
    }
}

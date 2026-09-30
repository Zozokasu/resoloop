using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task ObservationProjectsValuesAndReferencesWithOneReadPerComponentAndNoWrites()
    {
        var document = Document("observe", """
            [{"key":"source","type":"Test.Source","fields":{"Target":"$ref:target"}},
             {"key":"target","type":"Test.Target","fields":{"Enabled":true}}]
            """);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var state = Path.Combine(_root, "observe-state.json");
        await service.ApplyAsync(document, new ApplyOptions(state));
        client.ResetMetrics(); client.ResetWriteCounts();
        var result = await service.ObserveAsync(["$member:source.Target", "$member:target.Enabled", "$member:target.Enabled", "$member:target.enabled"], state);
        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Components);
        // session + owner Slot check (Slot, parent one level deep) + owner Slot Component list + two components.
        // A stored Component ID is no longer trusted before its owner Slot has been verified in the live world.
        Assert.Equal(6, client.SnapshotMetrics().Requests);
        Assert.Equal(0, client.Writes);
        Assert.True(result.Values["$member:target.Enabled"].Member.Value!.GetValue<bool>());
        Assert.Equal(result.Values["$member:target.Enabled"].ComponentId, result.Values["$member:source.Target"].Member.TargetId);
        Assert.Equal("OBSERVE_MEMBER_NOT_FOUND", (await Assert.ThrowsAsync<RLoopException>(() => service.ObserveAsync(["$member:target.Missing"], state))).Code);
        Assert.Equal("OBSERVE_LIMIT", (await Assert.ThrowsAsync<RLoopException>(() => service.ObserveAsync(Enumerable.Repeat("$member:target.Enabled", 65).ToArray(), state))).Code);
        Assert.Equal("OBSERVE_SELECTOR_INVALID", (await Assert.ThrowsAsync<RLoopException>(() => service.ObserveAsync(["$slot:target"], state))).Code);
        client.SessionId = "session-after-reconnect";
        var again = await service.ObserveAsync(["$member:source.Target", "$member:target.Enabled"], state);
        Assert.Equal(result.Values["$member:target.Enabled"].ComponentId, again.Values["$member:target.Enabled"].ComponentId);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task BuiltInRecipesSeparateSharedNamesAndPreserveCallerVisuals()
    {
        var path = Path.Combine(_root, "recipes.json");
        File.WriteAllText(path, """
        {"schemaVersion":"1","ownership":{"key":"recipes"},"slot":{"key":"root","name":"RootChild"},
         "children":[
          {"$recipe":"value-state","$with":{"key":"enabled","valueType":"bool","initial":false}},
          {"$recipe":"toggle","$with":{"key":"enabled","rect":{"OffsetMin":[7,9]},"state":"$member:uix-value-state--enabled-state.Value"},
           "children":[{"slot":{"key":"visual","name":"Custom shape"},"components":[]}]}
         ]}
        """);
        var doc = ApplyDocument.Load(path);
        Assert.True((await ApplyDocumentValidator.ValidateAsync(doc)).Valid);
        Assert.Equal("uix-value-state--enabled", doc.Children![0].Slot.Key);
        Assert.Equal("uix-toggle--enabled", doc.Children[1].Slot.Key);
        Assert.Equal("Custom shape", Assert.Single(doc.Children[1].Children!).Slot.Name);
        Assert.Equal(7, doc.Children[1].Components![0].Fields!["OffsetMin"][0].GetInt32());
        Assert.Equal("$member:uix-value-state--enabled-state.Value", doc.Children[1].Components![2].Fields!["TargetValue"].GetString());
        var raw = JsonNode.Parse(File.ReadAllText(path))!;
        raw["children"]![0]!["$with"]!.AsObject().Remove("initial");
        raw["children"]![0]!["$with"]!["typo"] = 1;
        File.WriteAllText(path, raw.ToJsonString());
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(path));
        Assert.Equal("APPLY_RECIPE_PARAMETERS", error.Code);
        Assert.Contains("initial", error.Message); Assert.Contains("typo", error.Message);
    }

    [Fact]
    public void BuiltInRecipeRepeatAndNestedInstancesUseTheirOwnScopes()
    {
        var path = Path.Combine(_root, "nested.json");
        File.WriteAllText(path, """
        {"schemaVersion":"1","ownership":{"key":"nested"},"slot":{"key":"root","name":"RootChild"},
         "children":[{"$recipe":"button","$with":{"key":"parent","rect":{}},
           "children":[{"$recipe":"button","$repeat":{"count":2,"as":"i"},"$with":{"key":"child-${i}","rect":{}}}]}]}
        """);
        var doc = ApplyDocument.Load(path);
        Assert.Equal("uix-button--child-0", doc.Children![0].Children![0].Slot.Key);
        Assert.Equal("uix-button--child-1", doc.Children[0].Children![1].Slot.Key);
        Assert.Equal(3, doc.Compilation!.Instances);
    }
}

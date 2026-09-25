using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ControlRecipeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-controls-" + Guid.NewGuid().ToString("N"));

    private ApplyDocument Load(Action<JsonNode>? edit = null)
    {
        Directory.CreateDirectory(root);
        foreach (var recipe in UixRecipes.Catalog)
            File.WriteAllText(Path.Combine(root, recipe.Name + ".json"), UixRecipes.Read(recipe.Name));
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/uix-controls-recipes.json")))!;
        node["include"] = new JsonArray(UixRecipes.Catalog.Select(recipe => JsonValue.Create(recipe.Name + ".json")).ToArray());
        edit?.Invoke(node);
        var path = Path.Combine(root, "main.json");
        File.WriteAllText(path, node.ToJsonString());
        return ApplyDocument.Load(path);
    }

    [Fact]
    public async Task ExpandedControlsWireSharedStateAndKeepUserValuesOutOfManagedFields()
    {
        var doc = Load();
        var result = await ApplyDocumentValidator.ValidateAsync(doc);
        Assert.True(result.Valid, JsonSerializer.Serialize(result.Issues));
        var components = Flatten(doc.Children!).ToDictionary(component => component.Key!);
        Assert.Equal("$member:enabled-state.Value", components["toggle-toggle"].Fields!["TargetValue"].GetString());
        Assert.Equal("$member:selection-state.Value", components["option-a-choice"].Fields!["TargetValue"].GetString());
        Assert.Equal("$member:selection-state.Value", components["option-b-choice"].Fields!["TargetValue"].GetString());
        Assert.Equal("$member:option-a-selected.Value", components["option-a-choice"].Fields!["CheckVisual"].GetString());
        Assert.Equal("$component:editable-text", components["input-editor"].Fields!["Text"].GetString());
        Assert.Equal("$component:editable-text", components["input-input"].Fields!["__text"].GetString());
        Assert.False(components["input-button"].Fields!["ClearFocusOnPress"].GetBoolean());
        Assert.True(components["toggle-button"].Fields!["SendSlotEvents"].GetBoolean());
        foreach (var key in new[] { "enabled-state", "selection-state", "slider-slider" })
        {
            Assert.True(components[key].Fields?.ContainsKey("Value") != true);
            Assert.True(components[key].InitialFields!.ContainsKey("Value"));
        }
        Assert.Equal("$member:handle-rect.AnchorMin", components["slider-slider"].Fields!["HandleAnchorMinDrive"].GetString());
        Assert.False(components["handle-rect"].Fields!.ContainsKey("AnchorMin"));
        Assert.Equal("$slot-member:page-a.IsActive", components["page-a-visibility-driver"].Fields!["TargetField"].GetString());
    }

    [Theory]
    [InlineData(2, "text")]
    [InlineData(3, "state")]
    [InlineData(4, "option")]
    [InlineData(6, "handle")]
    [InlineData(6, "anchorOffset")]
    public void MissingConnectionParametersFailDuringExpansion(int child, string parameter)
    {
        var ex = Assert.Throws<RLoopException>(() => Load(node => node["children"]![child]!["$with"]!.AsObject().Remove(parameter)));
        Assert.Equal("APPLY_PARAMETER_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task BrokenExternalTextReferenceIsRejectedOffline()
    {
        var doc = Load(node => node["children"]![2]!["$with"]!["text"] = "$component:missing-text");
        var result = await ApplyDocumentValidator.ValidateAsync(doc);
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "APPLY_REFERENCE_NOT_FOUND");
    }

    private static IEnumerable<ApplyComponentSpec> Flatten(IReadOnlyList<ApplyNodeSpec> nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var component in node.Components ?? []) yield return component;
            foreach (var child in Flatten(node.Children ?? [])) yield return child;
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

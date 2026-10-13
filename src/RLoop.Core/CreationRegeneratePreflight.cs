using System.Text.Json;

namespace RLoop.Core;

internal static class CreationRegeneratePreflight
{
    private const string UnsupportedMessage = "This content is not supported by regenerate in this version.";
    private const string ReservedStampName = "_resoloop_creation_stamp";

    internal static IReadOnlyList<ApplyValidationIssue> Validate(CreationDocument document)
    {
        var issues = new List<ApplyValidationIssue>();
        void Unsupported(string path) => issues.Add(new(
            "CREATION_DOCUMENT_INVALID", UnsupportedMessage, path));

        CheckCollection(document.Objects, "objects");
        CheckCollection(document.Variables, "variables");
        CheckCollection(document.Requirements, "requirements");
        CheckCollection(document.Bindings, "bindings");
        CheckCollection(document.Scripts, "scripts");

        var definition = document.Definition;
        if (definition is null) return issues;
        foreach (var asset in definition.Assets ?? new Dictionary<string, ApplyAssetSpec>())
            Unsupported("$.definition.assets." + asset.Key);
        foreach (var camera in definition.Cameras ?? new Dictionary<string, ApplyCameraSpec>())
            Unsupported("$.definition.cameras." + camera.Key);
        for (var index = 0; index < (definition.Tests?.Count ?? 0); index++)
            Unsupported($"$.definition.tests[{index}]");

        VisitNode(definition.Slot, definition.Components, definition.Children, "$.definition", isRoot: true);
        return issues;

        void CheckCollection(IReadOnlyList<JsonElement>? values, string name)
        {
            for (var index = 0; index < (values?.Count ?? 0); index++)
                Unsupported($"$.{name}[{index}]");
        }

        void VisitNode(ApplySlotSpec? slot, IReadOnlyList<ApplyComponentSpec>? components,
            IReadOnlyList<ApplyNodeSpec>? children, string path, bool isRoot = false)
        {
            if (slot?.MigrateFrom is not null) Unsupported(path + ".slot.migrateFrom");
            if (!isRoot && slot?.Parent is not null) Unsupported(path + ".slot.parent");
            for (var index = 0; index < (components?.Count ?? 0); index++)
            {
                var component = components![index];
                if (component is null) continue;
                var componentPath = $"{path}.components[{index}]";
                if (component.MigrateFrom is not null) Unsupported(componentPath + ".migrateFrom");
                CheckAssetSelectors(component.Fields, componentPath + ".fields");
                CheckAssetSelectors(component.InitialFields, componentPath + ".initialFields");
            }
            for (var index = 0; index < (children?.Count ?? 0); index++)
            {
                var child = children![index];
                if (child is not null)
                    VisitNode(child.Slot, child.Components, child.Children, $"{path}.children[{index}]");
            }
        }

        void CheckAssetSelectors(IReadOnlyDictionary<string, JsonElement>? fields, string path)
        {
            foreach (var field in fields ?? new Dictionary<string, JsonElement>())
            {
                if (field.Key == "VariableName" && field.Value.ValueKind == JsonValueKind.String &&
                    field.Value.GetString() == ReservedStampName)
                    issues.Add(new("CREATION_DOCUMENT_INVALID", "The reserved creation stamp VariableName is owned by regenerate.", path + ".VariableName"));
                CheckAssetSelectorValue(field.Value, path + "." + field.Key);
            }
        }

        void CheckAssetSelectorValue(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.String &&
                value.GetString()?.StartsWith("$asset:", StringComparison.Ordinal) == true)
            {
                Unsupported(path);
                return;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    CheckAssetSelectorValue(item, $"{path}[{index++}]");
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                    CheckAssetSelectorValue(property.Value, path + "." + property.Name);
            }
        }
    }
}

using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationRegeneratePreflightTests
{
    private const string BaseDefinition = "\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"}";

    [Fact]
    public void ReportsEveryUnsupportedSchemaV1ItemAtItsJsonPathWithoutClientCalls()
    {
        var definition = "{" + BaseDefinition +
            ",\"assets\":{\"a\":{\"kind\":\"texture\",\"source\":\"a.png\"}}" +
            ",\"cameras\":{\"c\":{\"position\":[0,0,0],\"target\":[0,0,1]}}" +
            ",\"tests\":[{\"name\":\"smoke\",\"assertions\":[]}]" +
            ",\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\",\"migrateFrom\":\"old-root\"}" +
            ",\"components\":[{\"type\":\"Example.Component\",\"key\":\"c1\",\"migrateFrom\":\"old-c1\",\"fields\":{\"Asset\":\"$asset:a\"}}]," +
            "\"children\":[{\"slot\":{\"key\":\"child\",\"name\":\"Child\",\"parent\":\"Root\"},\"components\":[]}]}";
        var document = CreationDocument.Parse(Envelope(definition));
        var client = new CreationTestClient();

        var issues = CreationRegeneratePreflight.Validate(document);

        Assert.Equal(new[]
        {
            "$.definition.assets.a", "$.definition.cameras.c", "$.definition.tests[0]",
            "$.definition.slot.migrateFrom", "$.definition.components[0].migrateFrom",
            "$.definition.components[0].fields.Asset", "$.definition.children[0].slot.parent"
        }, issues.Select(issue => issue.Path));
        Assert.All(issues, issue =>
        {
            Assert.Equal("CREATION_DOCUMENT_INVALID", issue.Code);
            Assert.Equal("This content is not supported by regenerate in this version.", issue.Message);
        });
        Assert.Equal(0, client.ConnectCalls);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public void ReportsNonemptyEnvelopeCollectionsAndNestedAssetSelectors()
    {
        using var objectValue = JsonDocument.Parse("{}");
        using var nestedValue = JsonDocument.Parse("{\"items\":[\"$asset:image\"]}");
        var parsed = CreationDocument.Parse(Envelope("{" + BaseDefinition +
            ",\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"}," +
            "\"components\":[{\"type\":\"Example.Component\",\"fields\":{},\"initialFields\":{}}],\"children\":[]}"));
        var document = parsed with
        {
            Objects = [objectValue.RootElement.Clone()],
            Scripts = [objectValue.RootElement.Clone()],
            Definition = parsed.Definition with
            {
                Components = [parsed.Definition.Components![0] with { InitialFields = new Dictionary<string, JsonElement> { ["Nested"] = nestedValue.RootElement.Clone() } }]
            }
        };

        var issues = CreationRegeneratePreflight.Validate(document);

        Assert.Equal(new[] { "$.objects[0]", "$.scripts[0]", "$.definition.components[0].initialFields.Nested.items[0]" },
            issues.Select(issue => issue.Path));
        Assert.All(issues, issue => Assert.Equal("CREATION_DOCUMENT_INVALID", issue.Code));
    }

    private static string Envelope(string definition) =>
        "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":" + definition + "}";
}

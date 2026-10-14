using RLoop.Core;
using RLoop.Cli;
using System.Text.Json;

namespace RLoop.Tests;

public sealed class CreationDocumentTests
{
    private const string Definition = """{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[]}""";
    private static string Envelope(string definition = Definition) =>
        "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":" + definition + ",\"diagnostics\":[]}";

    [Fact]
    public async Task ParsesAndValidatesTheHandwrittenEnvelopeOffline()
    {
        var parsed = CreationDocument.Parse(Envelope());
        var result = await parsed.ValidateAsync();
        Assert.Equal("creation-1", parsed.SchemaVersion);
        Assert.Equal("resoloop-creation", parsed.Kind);
        Assert.Equal("1", parsed.CreationVersion);
        Assert.Equal("room", parsed.CreationId);
        Assert.Equal("room", parsed.Definition.Ownership!.Key);
        Assert.True(result.Valid);
        Assert.Equal("1", result.SchemaVersion);
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("schemaVersion", "creation-2")]
    [InlineData("kind", "resoloop-creation-v2")]
    [InlineData("creationVersion", "2")]
    public void RejectsUnsupportedEnvelopeVersions(string property, string value)
    {
        var json = Envelope().Replace("\"" + property + "\":\"" +
            (property == "schemaVersion" ? "creation-1" : property == "kind" ? "resoloop-creation" : "1") + "\"",
            "\"" + property + "\":\"" + value + "\"", StringComparison.Ordinal);
        AssertInvalid(json, "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public void RejectsUnknownAndDuplicateOuterPropertiesCaseSensitively()
    {
        AssertInvalid(Envelope().Replace("\"diagnostics\":[]", "\"extra\":true,\"diagnostics\":[]"), "CREATION_DOCUMENT_INVALID");
        AssertInvalid(Envelope().Replace("\"diagnostics\":[]", "\"kind\":\"resoloop-creation\",\"diagnostics\":[]"), "CREATION_DOCUMENT_INVALID");
        AssertInvalid(Envelope().Replace("\"kind\":", "\"Kind\":\"resoloop-creation\",\"kind\":"), "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public void RejectsCreationIdLengthAndControlCharacters()
    {
        AssertInvalid(Envelope().Replace("\"creationId\":\"room\"", "\"creationId\":\"\""), "CREATION_ID_INVALID");
        AssertInvalid(Envelope().Replace("\"creationId\":\"room\"", "\"creationId\":\"" + new string('x', 129) + "\""), "CREATION_ID_INVALID");
        AssertInvalid(Envelope().Replace("\"creationId\":\"room\"", "\"creationId\":\"r\\u0000oom\""), "CREATION_ID_INVALID");
        AssertInvalid(Envelope().Replace("\"creationId\":\"room\"", "\"creationId\":1"), "CREATION_ID_INVALID");
    }

    [Fact]
    public void RejectsOwnershipMismatchAndInvalidDefinitionShape()
    {
        AssertInvalid(Envelope(Definition.Replace("\"room\"", "\"other\"")), "CREATION_DOCUMENT_INVALID");
        AssertInvalid(Envelope(Definition.Replace("\"children\":[]", "\"children\":[{\"key\":\"sibling\"}]")), "CREATION_DOCUMENT_INVALID");
        AssertInvalid(Envelope(Definition.Replace("\"children\":[]", "\"children\":[],\"$draftKeys\":true")), "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public async Task InnerApplyValidationIssuesUseCreationErrorCode()
    {
        var innerVersionTwo = Definition.Replace("\"schemaVersion\":\"1\"", "\"schemaVersion\":\"2\"");
        var result = await CreationDocument.Parse(Envelope(innerVersionTwo)).ValidateAsync();
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, issue => issue.Code == "CREATION_DOCUMENT_INVALID");
        Assert.Contains(result.Issues, issue => issue.Message.Contains("APPLY_SCHEMA_VERSION_UNSUPPORTED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsyncChecksDirectlyConstructedAndWithUpdatedEnvelopeRecords()
    {
        var parsed = CreationDocument.Parse(Envelope());
        var direct = new CreationDocument("bad-schema", "resoloop-creation", "1", "room", parsed.Definition);
        var directResult = await direct.ValidateAsync();
        Assert.False(directResult.Valid);
        Assert.Contains(directResult.Issues, issue => issue.Code == "CREATION_DOCUMENT_INVALID");

        var idResult = await (parsed with { CreationId = "" }).ValidateAsync();
        Assert.Contains(idResult.Issues, issue => issue.Code == "CREATION_ID_INVALID");

        using var value = JsonDocument.Parse("true");
        var collectionResult = await (parsed with { Objects = [value.RootElement.Clone()] }).ValidateAsync();
        Assert.Contains(collectionResult.Issues, issue => issue.Code == "CREATION_DOCUMENT_INVALID");

        var mismatchResult = await (parsed with { CreationId = "other" }).ValidateAsync();
        Assert.Contains(mismatchResult.Issues, issue => issue.Code == "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public void RejectsDuplicateSiblingStableKeys()
    {
        var duplicateSiblings = """{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[{"slot":{"key":"same","name":"A"}},{"slot":{"key":"same","name":"B"}}]}""";
        AssertInvalid(Envelope(duplicateSiblings), "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public async Task NullChildSlotIsCreationValidationIssueForParsedDirectAndCliInputs()
    {
        const string invalidDefinition = """{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[{"slot":null,"components":[],"children":[]}] }""";
        var parsed = CreationDocument.Parse(Envelope(invalidDefinition));

        var parsedResult = await parsed.ValidateAsync();
        AssertInvalidCreationResult(parsedResult, "$.definition.children[0].slot");

        var direct = parsed with
        {
            Definition = parsed.Definition with { Children = [new ApplyNodeSpec(null!, [], [])] }
        };
        var directResult = await direct.ValidateAsync();
        AssertInvalidCreationResult(directResult, "$.definition.children[0].slot");

        await AssertCliValidationFailedAsync(invalidDefinition);
    }

    [Fact]
    public async Task DuplicateSiblingNamesWithDifferentKeysPassValidationAndCli()
    {
        const string duplicateNames = """{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[{"slot":{"key":"first","name":"Lamp"},"components":[],"children":[]},{"slot":{"key":"second","name":"Lamp"},"components":[],"children":[]}] }""";
        var result = await CreationDocument.Parse(Envelope(duplicateNames)).ValidateAsync();

        Assert.True(result.Valid);
        Assert.Empty(result.Issues);
        var root = Path.Combine(Path.GetTempPath(), "resoloop-creation-valid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inputPath = Path.Combine(root, "creation.json");
            var reportPath = Path.Combine(root, "report.json");
            await File.WriteAllTextAsync(inputPath, Envelope(duplicateNames));
            var exit = await Program.RunAsync(["validate", inputPath, "--json", "--report", reportPath]);
            Assert.Equal(ExitCodes.Success, exit);
            using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var data = parsed.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("valid").GetBoolean());
            Assert.Empty(data.GetProperty("issues").EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("objects")]
    [InlineData("variables")]
    [InlineData("requirements")]
    [InlineData("bindings")]
    [InlineData("scripts")]
    public void AcceptsOnlyOmittedOrEmptyUnsupportedCollections(string name)
    {
        var parsed = CreationDocument.Parse(Envelope().Replace("\"diagnostics\":[]", "\"" + name + "\":[],\"diagnostics\":[]"));
        Assert.Equal("room", parsed.CreationId);
        AssertInvalid(Envelope().Replace("\"diagnostics\":[]", "\"" + name + "\":[{}],\"diagnostics\":[]"), "CREATION_DOCUMENT_INVALID", "not supported in this version");
        AssertInvalid(Envelope().Replace("\"diagnostics\":[]", "\"" + name + "\":null,\"diagnostics\":[]"), "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public async Task DiagnosticsAreShapeCheckedAndNeverForwardedAsSuccessIssues()
    {
        var creation = CreationDocument.Parse(Envelope().Replace("\"diagnostics\":[]", "\"diagnostics\":[{\"code\":\"FAKE\",\"severity\":\"success\"}]"));
        var result = await creation.ValidateAsync();
        Assert.True(result.Valid);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "FAKE");
        AssertInvalid(Envelope().Replace("\"diagnostics\":[]", "\"diagnostics\":{}"), "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public void AppliesExistingAuthoringValidationRules()
    {
        AssertInvalid(Envelope().Replace("\"diagnostics\":[]", "\"authoring\":{\"projectRoot\":\"relative\",\"source\":\"main.tsx\"},\"diagnostics\":[]"), "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public async Task OuterAuthoringValidatesAndResolvesDefaultStatePathFromProjectRoot()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "resoloop-creation-project-" + Guid.NewGuid().ToString("N")));
        var authoring = "\"authoring\":{\"projectRoot\":" + JsonSerializer.Serialize(projectRoot) + ",\"source\":\"content/entry.tsx\"},";
        var creation = CreationDocument.Parse(Envelope().Replace("\"diagnostics\":[]", authoring + "\"diagnostics\":[]"));

        var result = await creation.ValidateAsync();

        Assert.True(result.Valid);
        Assert.Equal(projectRoot, creation.Definition.ProjectContext.ProjectRoot);
        Assert.Equal(Path.Combine(projectRoot, ".resoloop", "state", "room.json"), creation.Definition.ResolveStatePath());
    }

    [Fact]
    public void RejectsAuthoringDeclaredOnBothEnvelopeAndDefinition()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "resoloop-creation-project-" + Guid.NewGuid().ToString("N")));
        var authoring = "{\"projectRoot\":" + JsonSerializer.Serialize(projectRoot) + ",\"source\":\"content/entry.tsx\"}";
        var definition = Definition.Replace("\"children\":[]", "\"children\":[],\"authoring\":" + authoring);
        var envelope = Envelope(definition).Replace("\"diagnostics\":[]", "\"authoring\":" + authoring + ",\"diagnostics\":[]");

        AssertInvalid(envelope, "CREATION_DOCUMENT_INVALID");
    }

    private static void AssertInvalid(string json, string code, string? message = null)
    {
        var error = Assert.Throws<RLoopException>(() => CreationDocument.Parse(json));
        Assert.Equal(code, error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        if (message is not null) Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertInvalidCreationResult(ApplyValidationResult result, string expectedPath)
    {
        Assert.False(result.Valid);
        Assert.NotEmpty(result.Issues);
        Assert.Contains(result.Issues, issue => issue.Code == "CREATION_DOCUMENT_INVALID" && issue.Path == expectedPath);
    }

    private static async Task AssertCliValidationFailedAsync(string definition)
    {
        var root = Path.Combine(Path.GetTempPath(), "resoloop-creation-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inputPath = Path.Combine(root, "creation.json");
            var reportPath = Path.Combine(root, "report.json");
            await File.WriteAllTextAsync(inputPath, Envelope(definition));
            var connects = 0;
            var exit = await Program.RunAsync(["validate", inputPath, "--strict", "--json", "--report", reportPath], _ =>
            {
                connects++;
                throw new InvalidOperationException("Creation validation must stay offline.");
            });

            Assert.Equal(ExitCodes.ValidationFailed, exit);
            Assert.Equal(0, connects);
            using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var data = parsed.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("valid").GetBoolean());
            Assert.Contains(data.GetProperty("issues").EnumerateArray(), issue =>
                issue.GetProperty("code").GetString() == "CREATION_DOCUMENT_INVALID");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

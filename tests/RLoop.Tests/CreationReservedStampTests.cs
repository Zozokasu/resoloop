using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationReservedStampTests
{
    private const string Prefix = """{"schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"room","definition":{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[""";
    private const string Suffix = """],"children":[]},"diagnostics":[]}""";

    [Theory]
    [InlineData("\"fields\":{\"VariableName\":\"_resoloop_creation_stamp\"}", "$.definition.components[0].fields.VariableName")]
    [InlineData("\"initialFields\":{\"VariableName\":\"_resoloop_creation_stamp\"}", "$.definition.components[0].initialFields.VariableName")]
    public void ReservedStampNameIsRejectedBeforeRuntimeValidation(string fields, string expectedPath)
    {
        var json = Prefix + "{\"type\":\"Any.Component\"," + fields + "}" + Suffix;
        var document = CreationDocument.Parse(json);
        var issues = CreationRegeneratePreflight.Validate(document);
        var issue = Assert.Single(issues);
        Assert.Equal("CREATION_DOCUMENT_INVALID", issue.Code);
        Assert.Equal(expectedPath, issue.Path);
        Assert.Contains("reserved creation stamp", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtherVariableNamesRemainAccepted()
    {
        var document = CreationDocument.Parse(Prefix +
            """{"type":"Any.Component","fields":{"VariableName":"ordinary"}}""" + Suffix);
        Assert.Empty(CreationRegeneratePreflight.Validate(document));
    }
}

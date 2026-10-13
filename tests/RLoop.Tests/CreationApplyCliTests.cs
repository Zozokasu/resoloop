using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyCliTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-apply-cli-" + Guid.NewGuid().ToString("N"));

    public CreationApplyCliTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("apply")]
    [InlineData("plan")]
    [InlineData("diff")]
    public async Task RegenerateCommandsRequireEnvelopePathBeforeBundleDetection(string command)
    {
        var report = Path.Combine(root, command + "-missing-path-report.json");
        var connects = 0;

        var exit = await Program.RunAsync([command, "--mode", "regenerate", "--yes", "--json", "--report", report], _ =>
        {
            connects++;
            throw new InvalidOperationException("Missing creation input must fail before connection.");
        });

        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Equal(0, connects);
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("ARGUMENT_REQUIRED", parsed.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("objects")]
    [InlineData("variables")]
    [InlineData("requirements")]
    [InlineData("bindings")]
    [InlineData("scripts")]
    public async Task UnsupportedEnvelopeCollectionsFailBeforeConnectionWithExactPath(string collection)
    {
        var path = Save(collection + ".json",
            $$"""{"schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"room","definition":{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[]},"{{collection}}":[{"key":"unsupported"}]}""");
        var report = Path.Combine(root, collection + "-report.json");
        var connects = 0;
        var exit = await Program.RunAsync(["apply", path, "--mode", "regenerate", "--yes", "--url", "auto", "--session", "S-test", "--json", "--report", report], _ =>
        {
            connects++;
            throw new InvalidOperationException("Unsupported creation content must fail before connection.");
        });
        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(0, connects);
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        var issues = parsed.RootElement.GetProperty("error").GetProperty("context").GetProperty("issues");
        Assert.Equal("CREATION_DOCUMENT_INVALID", issues[0].GetProperty("code").GetString());
        Assert.Contains(collection, issues[0].GetProperty("path").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitRegenerateSendsMalformedJsonToCreationReader()
    {
        var path = Save("broken.json", "{\"kind\":\"resoloop-creation\"");
        var report = Path.Combine(root, "broken-report.json");
        var connects = 0;
        var exit = await Program.RunAsync(["apply", path, "--mode", "regenerate", "--yes", "--json", "--report", report], _ =>
        {
            connects++;
            throw new InvalidOperationException("Malformed JSON must be classified offline.");
        });
        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(0, connects);
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("CREATION_DOCUMENT_INVALID", parsed.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private string Save(string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

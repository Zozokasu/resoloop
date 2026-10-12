using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationModeBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-mode-" + Guid.NewGuid().ToString("N"));
    private const string Envelope = """{"schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"room","definition":{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[]},"diagnostics":[]}""";
    private const string Legacy = """{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[]}""";

    public CreationModeBoundaryTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ValidateEnvelopeIsOfflineEvenWithStrict()
    {
        var path = Save("creation.json", Envelope);
        var connects = 0;
        var exit = await Program.RunAsync(["validate", path, "--strict", "--json"], _ =>
        {
            connects++;
            throw new InvalidOperationException("Creation validation must stay offline.");
        });
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task CreationValidationReportsInnerErrorsAsDataAndExitSix()
    {
        var invalid = Envelope.Replace("\"schemaVersion\":\"1\",\"ownership\"", "\"schemaVersion\":\"2\",\"ownership\"");
        var path = Save("invalid-creation.json", invalid);
        var report = Path.Combine(root, "invalid-report.json");
        var exit = await Program.RunAsync(["validate", path, "--report", report, "--json"]);
        Assert.Equal(ExitCodes.ValidationFailed, exit);
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        var data = parsed.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("valid").GetBoolean());
        Assert.Contains(data.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "CREATION_DOCUMENT_INVALID");
    }

    [Fact]
    public async Task SchemaV1ValidateRetainsItsHandwrittenOutputContract()
    {
        var path = Save("legacy-validate.json", Legacy);
        var report = Path.Combine(root, "legacy-report.json");
        var exit = await Program.RunAsync(["validate", path, "--report", report, "--json"]);
        Assert.Equal(ExitCodes.Success, exit);
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        var data = parsed.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("valid").GetBoolean());
        Assert.Equal("1", data.GetProperty("schemaVersion").GetString());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("issues").ValueKind);
        Assert.Equal(0, data.GetProperty("issues").GetArrayLength());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("diff")]
    [InlineData("apply")]
    public async Task LegacyCommandsRejectCreationEnvelopeWithoutMode(string command)
    {
        var path = Save(command + ".json", Envelope);
        await AssertModeMismatch([command, path, "--json"]);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("diff")]
    public async Task RegenerateRejectsBareSchemaV1ForPlanAndDiff(string command)
    {
        var path = Save(command + "-legacy-regenerate.json", Legacy);
        await AssertModeMismatch([command, path, "--mode", "regenerate", "--json"]);
    }

    [Fact]
    public async Task RegenerateApplyIsUnavailableBeforeConnection()
    {
        var path = Save("apply-regenerate.json", Envelope);
        await AssertModeMismatch(["apply", path, "--mode", "regenerate", "--json"], "not available in this version");
    }

    [Theory]
    [InlineData("--adopt")]
    [InlineData("--prune")]
    [InlineData("--deletes-only")]
    [InlineData("--changes-only")]
    public async Task RegenerateRejectsLegacyMutationAndFilterOptions(string option)
    {
        var path = Save("legacy-options.json", Legacy);
        await AssertModeMismatch(["plan", path, "--mode", "regenerate", option, "--json"]);
    }

    [Fact]
    public async Task RejectsUnknownModeAndCoversDiscardPendingFastPath()
    {
        var path = Save("unknown-mode.json", Envelope);
        await AssertModeMismatch(["plan", path, "--mode", "future", "--json"]);
        await AssertModeMismatch(["apply", path, "--discard-pending", "op", "--yes", "--json"]);
    }

    private async Task AssertModeMismatch(string[] args, string? expectedMessage = null)
    {
        var report = Path.Combine(root, "error-" + Guid.NewGuid().ToString("N") + ".json");
        var connects = 0;
        var exit = await Program.RunAsync(args.Concat(new[] { "--report", report }).ToArray(), _ =>
        {
            connects++;
            throw new InvalidOperationException("Mode rejection must happen before connection.");
        });
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Equal(0, connects);
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        var error = parsed.RootElement.GetProperty("error");
        Assert.Equal("CREATION_MODE_MISMATCH", error.GetProperty("code").GetString());
        if (expectedMessage is not null)
            Assert.Contains(expectedMessage, error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private string Save(string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

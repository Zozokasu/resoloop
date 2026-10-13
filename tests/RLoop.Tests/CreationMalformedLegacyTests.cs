using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationMalformedLegacyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-malformed-legacy-" + Guid.NewGuid().ToString("N"));

    public CreationMalformedLegacyTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task BrokenJsonWithCreationLookingStringsKeepsLegacyReaderClassification()
    {
        var malformedCreationLooking = await RunMalformedValidate("creation-looking.json",
            "{\"schemaVersion\":\"1\",\"creationId\":\"room\",\"definition\":");
        var malformedLegacy = await RunMalformedValidate("legacy.json", "{\"schemaVersion\":\"1\",\"slot\":");

        Assert.Equal(malformedLegacy.Exit, malformedCreationLooking.Exit);
        Assert.Equal(ExitCodes.ValidationFailed, malformedCreationLooking.Exit);
        Assert.Equal(malformedLegacy.Code, malformedCreationLooking.Code);
        Assert.Equal("APPLY_DOCUMENT_INVALID", malformedCreationLooking.Code);
    }

    private async Task<(int Exit, string Code)> RunMalformedValidate(string name, string content)
    {
        var input = Path.Combine(root, name);
        var report = Path.Combine(root, name + ".report.json");
        await File.WriteAllTextAsync(input, content);
        var connects = 0;
        var exit = await Program.RunAsync(["validate", input, "--json", "--report", report], _ =>
        {
            connects++;
            throw new InvalidOperationException("Malformed input must fail before connection.");
        });

        Assert.Equal(0, connects);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var code = parsed.RootElement.GetProperty("error").GetProperty("code").GetString()!;
        return (exit, code);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

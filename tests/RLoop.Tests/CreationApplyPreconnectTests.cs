using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyPreconnectTests : IDisposable
{
    private const string ValidEnvelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-preconnect-" + Guid.NewGuid().ToString("N"));

    public CreationApplyPreconnectTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task MissingYesWithNoUrlOrSessionIsConfirmationRequiredOffline()
    {
        var input = Save("no-yes.json", ValidEnvelope);
        var report = Path.Combine(root, "no-yes-report.json");
        var connects = 0;

        var exit = await Program.RunAsync(["apply", input, "--mode", "regenerate", "--json", "--report", report], _ =>
        {
            connects++;
            throw new InvalidOperationException("No confirmation must stop before connection.");
        });

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(0, connects);
        Assert.Equal("CONFIRMATION_REQUIRED", Error(report).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("assets")]
    [InlineData("cameras")]
    [InlineData("tests")]
    [InlineData("root-migrate")]
    [InlineData("component-migrate")]
    [InlineData("nested-parent")]
    [InlineData("asset-selector")]
    [InlineData("reserved-name")]
    public async Task UnsupportedRegenerateDocumentFailsOfflineWithExactPath(string caseName)
    {
        var (definition, expectedPath, expectedMessage) = InvalidDefinition(caseName);
        var envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":" + definition + "}";
        var input = Save(caseName + ".json", envelope);
        var report = Path.Combine(root, caseName + "-report.json");
        var connects = 0;

        var exit = await Program.RunAsync(["apply", input, "--mode", "regenerate", "--yes", "--url", "auto", "--session", "S-test", "--json", "--report", report], _ =>
        {
            connects++;
            throw new InvalidOperationException("Unsupported regenerate content must fail before connection.");
        });

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(0, connects);
        var issue = Error(report).GetProperty("context").GetProperty("issues")[0];
        Assert.Equal("CREATION_DOCUMENT_INVALID", issue.GetProperty("code").GetString());
        Assert.Equal(expectedPath, issue.GetProperty("path").GetString());
        Assert.Equal(expectedMessage, issue.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    public async Task PendingCreationStateBlocksNextPlanAndApplyBeforeConnect(string command)
    {
        var input = Save("pending-input.json", ValidEnvelope);
        var statePath = Path.Combine(root, "pending-state.json");
        var report = Path.Combine(root, command + "-pending-report.json");
        var state = CreationOwnershipFixture.State();
        var old = state.ActiveRoot!;
        var pending = new CreationPending("manual-pending", "prepared",
            new CreationScope("creation", null, old.Id, null, old.ParentId), "delete-first", state.Session,
            "generation", null, old.InputHash, null, old, null, [], null);
        CreationStateStore.Save(statePath, state with { Pending = pending });
        var args = new List<string> { command, input, "--mode", "regenerate", "--state", statePath,
            "--json", "--report", report };
        if (command == "apply") args.Add("--yes");
        var connects = 0;

        var exit = await Program.RunAsync(args.ToArray(), _ =>
        {
            connects++;
            throw new InvalidOperationException("Pending creation state must stop before connection.");
        });

        Assert.Equal(ExitCodes.OperationFailed, exit);
        Assert.Equal(0, connects);
        Assert.Equal("APPLY_WRITE_UNVERIFIED", Error(report).GetProperty("code").GetString());
    }

    private static JsonElement Error(string report)
    {
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        return parsed.RootElement.GetProperty("error").Clone();
    }

    private string Save(string name, string value)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, value);
        return path;
    }

    private static (string Definition, string Path, string Message) InvalidDefinition(string name) => name switch
    {
        "assets" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"assets\":{\"a\":{}},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]}",
            "$.definition.assets.a", "This content is not supported by regenerate in this version."),
        "cameras" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"cameras\":{\"c\":{}},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]}",
            "$.definition.cameras.c", "This content is not supported by regenerate in this version."),
        "tests" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"tests\":[{}],\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]}",
            "$.definition.tests[0]", "This content is not supported by regenerate in this version."),
        "root-migrate" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\",\"migrateFrom\":\"old\"},\"components\":[],\"children\":[]}",
            "$.definition.slot.migrateFrom", "This content is not supported by regenerate in this version."),
        "component-migrate" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[{\"type\":\"A.B\",\"migrateFrom\":\"old\",\"fields\":{}}],\"children\":[]}",
            "$.definition.components[0].migrateFrom", "This content is not supported by regenerate in this version."),
        "nested-parent" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[{\"slot\":{\"key\":\"child\",\"name\":\"Child\",\"parent\":\"Root\"},\"components\":[]}]}",
            "$.definition.children[0].slot.parent", "This content is not supported by regenerate in this version."),
        "asset-selector" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[{\"type\":\"A.B\",\"fields\":{\"Asset\":\"$asset:a\"}}],\"children\":[]}",
            "$.definition.components[0].fields.Asset", "This content is not supported by regenerate in this version."),
        "reserved-name" => ("{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[{\"type\":\"A.B\",\"fields\":{\"VariableName\":\"_resoloop_creation_stamp\"}}],\"children\":[]}",
            "$.definition.components[0].fields.VariableName", "The reserved creation stamp VariableName is owned by regenerate."),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public void Dispose() => Directory.Delete(root, recursive: true);
}

using System.Reflection;
using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationPlanCompatibilityTests : IDisposable
{
    private const string Legacy = "{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-compat-" + Guid.NewGuid().ToString("N"));

    public CreationPlanCompatibilityTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task SchemaV1ValidatePlanDiffAndApplyKeepTheirLegacyCliBehavior()
    {
        var input = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(input, Legacy);
        var report = Path.Combine(root, "validate-report.json");
        var exit = await Program.RunAsync(["validate", input, "--json", "--report", report]);

        Assert.Equal(ExitCodes.Success, exit);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.Equal("{\"ok\":true,\"data\":{\"valid\":true,\"schemaVersion\":\"1\",\"slots\":1,\"components\":0," +
            "\"references\":0,\"strict\":false,\"issues\":[]}}", parsed.RootElement.GetRawText());

        var client = new CreationLegacyTestClient();
        var state = Path.Combine(root, "legacy-state.json");
        var planReport = await RunLegacyAsync(client, "plan", input, state);
        var diffReport = await RunLegacyAsync(client, "diff", input, state);
        Assert.True(planReport.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(diffReport.RootElement.GetProperty("ok").GetBoolean());
        var planData = planReport.RootElement.GetProperty("data");
        var diffData = diffReport.RootElement.GetProperty("data");
        Assert.Equal(planData.GetRawText(), diffData.GetRawText());
        Assert.True(planData.GetProperty("valid").GetBoolean());
        Assert.Equal("1", planData.GetProperty("schemaVersion").GetString());
        Assert.Equal("room", planData.GetProperty("ownershipKey").GetString());
        Assert.Equal(Path.GetFullPath(state), planData.GetProperty("stateFile").GetString());
        var operations = planData.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Equal(2, operations.Length);
        var slotOperation = Assert.Single(operations, operation => operation.GetProperty("kind").GetString() == "slot");
        Assert.Equal("create", slotOperation.GetProperty("action").GetString());
        Assert.Equal("Root/Room", slotOperation.GetProperty("path").GetString());
        Assert.Equal("root", slotOperation.GetProperty("key").GetString());
        var componentOperation = Assert.Single(operations, operation => operation.GetProperty("kind").GetString() == "component");
        Assert.Equal("create", componentOperation.GetProperty("action").GetString());
        Assert.Equal(GeneratedContentMetadata.ComponentType, componentOperation.GetProperty("type").GetString());
        Assert.Equal(GeneratedContentMetadata.SourceMember,
            Assert.Single(componentOperation.GetProperty("members").EnumerateArray()).GetString());
        Assert.Equal(2, planData.GetProperty("creates").GetInt32());
        Assert.Empty(client.Creates);
        Assert.Empty(client.AddedComponents);
        planReport.Dispose();
        diffReport.Dispose();

        var applyClient = new CreationLegacyTestClient();
        using var applyReport = await RunLegacyAsync(applyClient, "apply", input, Path.Combine(root, "apply-state.json"));
        Assert.True(applyReport.RootElement.GetProperty("ok").GetBoolean());
        var applyData = applyReport.RootElement.GetProperty("data");
        Assert.True(applyData.GetProperty("created").GetBoolean());
        Assert.Equal("slot-1", applyData.GetProperty("slotId").GetString());
        Assert.Equal("Room", Assert.Single(applyClient.Creates).Name);
        Assert.Equal("Root", applyClient.Creates[0].ParentId);
        var addedComponent = Assert.Single(applyClient.AddedComponents);
        Assert.Equal("slot-1", addedComponent.SlotId);
        Assert.Equal(GeneratedContentMetadata.ComponentType, addedComponent.Type);
        var componentReadback = await applyClient.GetComponentAsync(addedComponent.Id);
        Assert.Equal(ExpectedGeneratedSource(),
            componentReadback.Members[GeneratedContentMetadata.SourceMember].Value!.GetValue<string>());
    }

    private async Task<JsonDocument> RunLegacyAsync(CreationLegacyTestClient client, string command, string input, string state)
    {
        var report = Path.Combine(root, command + "-" + Guid.NewGuid().ToString("N") + ".json");
        var connects = 0;
        var exit = await Program.RunAsync([command, input, "--state", state, "--url", client.Url,
            "--json", "--report", report], _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(client);
        });
        var reportExists = File.Exists(report);
        var reportText = reportExists ? await File.ReadAllTextAsync(report) : "<report file missing>";
        var details = reportText;
        if (reportExists)
        {
            using var diagnostic = JsonDocument.Parse(reportText);
            if (diagnostic.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : "unknown";
                var context = error.TryGetProperty("context", out var contextValue) ? contextValue.GetRawText() : "null";
                details = $"error.code={code}; context={context}; report={reportText}";
            }
        }
        Assert.True(exit == ExitCodes.Success, $"Expected exit {ExitCodes.Success}, got {exit}; {details}");
        Assert.True(connects == 1, $"Expected one client connection, got {connects}; {details}");
        Assert.True(reportExists, $"Expected report file '{report}' to exist; {details}");
        return JsonDocument.Parse(reportText);
    }

    private static string ExpectedGeneratedSource()
    {
        var assembly = typeof(Program).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "unknown"
            : informational.Split('+')[0];
        return GeneratedContentMetadata.SourceForVersion(version);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

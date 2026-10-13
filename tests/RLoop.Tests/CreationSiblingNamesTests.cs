using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationSiblingNamesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-sibling-names-" + Guid.NewGuid().ToString("N"));

    public CreationSiblingNamesTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task CreationExecutorCreatesSameNamedSiblingsAndResolvesReferencesByKey()
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("Probe", "Value:System.String", "SlotTarget:System.String", "ComponentTarget:System.String", "MemberTarget:System.String");
        foreach (var member in new[] { "SlotTarget", "ComponentTarget", "MemberTarget" }) client.RegisterReference("Probe", member);
        var first = new ApplyComponentSpec("Probe", CreationTreeExecutorTests.Fields(
            ("Value", "first"), ("SlotTarget", "$slot:second"),
            ("ComponentTarget", "$component:second-component"), ("MemberTarget", "$member:second-component.Value")), "first-component");
        var second = new ApplyComponentSpec("Probe", CreationTreeExecutorTests.Fields(("Value", "second")), "second-component");
        var definition = CreationTreeExecutorTests.Document([], [
            new ApplyNodeSpec(new ApplySlotSpec("Lamp", null, null, null, null, "first"), [first]),
            new ApplyNodeSpec(new ApplySlotSpec("Lamp", null, null, null, null, "second"), [second])]);
        var executor = new CreationTreeExecutor(client);
        var tree = await executor.PrepareAsync(definition, CancellationToken.None);

        await executor.ExecuteAsync(tree, "root-created", CreationTreeExecutorTests.Send(client), CancellationToken.None);

        Assert.Equal(new[] { "create-slot", "create-slot", "add-component", "add-component", "set-members" }, client.SendLog);
        Assert.Equal(new[] { "slot-1", "slot-2" }, client.AddRequests.Select(request => request.SlotId));
        Assert.Equal(new[] { "component-3", "component-4" }, client.Components.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("first", client.AddRequests[0].Fields["Value"]);
        Assert.Equal("second", client.AddRequests[1].Fields["Value"]);
        Assert.Equal("component-3", client.SetRequests.Single().ComponentId);
        Assert.Equal("slot-2", client.SetRequests.Single().Fields["SlotTarget"]);
        Assert.Equal("component-4", client.SetRequests.Single().Fields["ComponentTarget"]);
        Assert.Equal("component-4:Value", client.SetRequests.Single().Fields["MemberTarget"]);
    }

    [Fact]
    public async Task CreationEnvelopeRejectsRepeatedStableSlotKeys()
    {
        var envelope = Envelope("""{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[{"slot":{"key":"same","name":"Lamp"},"components":[],"children":[]},{"slot":{"key":"same","name":"Lamp"},"components":[],"children":[]}]}""");
        var error = Assert.Throws<RLoopException>(() => CreationDocument.Parse(envelope));

        Assert.Equal("CREATION_DOCUMENT_INVALID", error.Code);
        Assert.Contains("Stable key 'same'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreationPlanAllowsDuplicateSiblingNamesAndPerformsNoSends()
    {
        var document = CreationDocument.Parse(Envelope("""{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[{"slot":{"key":"first","name":"Lamp"},"components":[],"children":[]},{"slot":{"key":"second","name":"Lamp"},"components":[],"children":[]}]}"""));
        var client = new CreationMutationTestClient();

        var result = await new CreationApplyService(client).PlanAsync(document,
            new CreationPlanOptions(Path.Combine(root, "plan-state.json")));

        Assert.Equal("create", result.Action);
        Assert.Equal(new CreationPlanCounts(3, 2), result.Counts);
        Assert.Empty(client.SendLog);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("plan")]
    [InlineData("apply")]
    public async Task OrdinaryApplyCliRejectsDuplicateSiblingNamesBeforeAnySend(string command)
    {
        var input = Save("ordinary-duplicates.json", """{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[{"slot":{"key":"first","name":"Lamp"}},{"slot":{"key":"second","name":"Lamp"}}]}""");
        var report = Path.Combine(root, command + "-report.json");
        var client = new CreationMutationTestClient();
        var connects = 0;
        var args = command == "validate"
            ? new[] { command, input, "--json", "--report", report }
            : command == "apply"
                ? new[] { command, input, "--yes", "--url", CreationTestClient.Url, "--json", "--report", report }
                : new[] { command, input, "--url", CreationTestClient.Url, "--json", "--report", report };

        var exit = await Program.RunAsync(args, _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(client);
        });

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Contains("APPLY_SIBLING_NAME_DUPLICATE", IssueCodes(report));
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task CreationValidateRejectsUnsupportedRegenerateModeBeforeConnecting()
    {
        var input = Save("creation-mode.json", Envelope("""{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},"components":[],"children":[{"slot":{"key":"first","name":"Lamp"},"components":[],"children":[]},{"slot":{"key":"second","name":"Lamp"},"components":[],"children":[]}]}"""));
        var report = Path.Combine(root, "creation-mode-report.json");
        var client = new CreationMutationTestClient();
        var connects = 0;

        var exit = await Program.RunAsync(["validate", input, "--mode", "regenerate", "--json", "--report", report], _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(client);
        });

        Assert.Equal(ExitCodes.InvalidArguments, exit);
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("CREATION_MODE_MISMATCH", parsed.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, connects);
        Assert.Empty(client.SendLog);
    }

    private static string Envelope(string definition) =>
        "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":" + definition + ",\"diagnostics\":[]}";

    private string Save(string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static IEnumerable<string?> IssueCodes(string report)
    {
        using var parsed = JsonDocument.Parse(File.ReadAllText(report));
        var root = parsed.RootElement;
        var issues = root.TryGetProperty("data", out var data)
            ? data.GetProperty("issues")
            : root.GetProperty("error").GetProperty("context").GetProperty("issues");
        return issues.EnumerateArray().Select(issue => issue.GetProperty("code").GetString()).ToArray();
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

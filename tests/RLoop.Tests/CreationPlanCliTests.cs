using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationPlanCliTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private const string InputHash = "8d44fcd6c980f46af1b961f08206e2272290ff32115d6a286174dad7fe93e1b0";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-cli-" + Guid.NewGuid().ToString("N"));

    public CreationPlanCliTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task PlanAndDiffEmitIdenticalResultAndUseOnlyTheFakeConnectHook()
    {
        var input = Save("creation.json", Envelope);
        var state = Path.Combine(root, "state.json");
        var planReport = Path.Combine(root, "plan-report.json");
        var diffReport = Path.Combine(root, "diff-report.json");
        var connects = 0;
        var planExit = await Program.RunAsync(Args("plan", input, state, planReport), _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(InitialClient());
        });
        var diffExit = await Program.RunAsync(Args("diff", input, state, diffReport), _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(InitialClient());
        });

        Assert.Equal(ExitCodes.Success, planExit);
        Assert.Equal(ExitCodes.Success, diffExit);
        Assert.Equal(2, connects);
        using var plan = JsonDocument.Parse(File.ReadAllText(planReport));
        using var diff = JsonDocument.Parse(File.ReadAllText(diffReport));
        Assert.Equal(plan.RootElement.GetProperty("data").GetRawText(), diff.RootElement.GetProperty("data").GetRawText());
        Assert.Equal("{\"resultVersion\":\"1\",\"mode\":\"regenerate\",\"creationId\":\"room\",\"stateFile\":" +
            JsonSerializer.Serialize(Path.GetFullPath(state)) + ",\"buildId\":null,\"inputHash\":\"" + InputHash +
            "\",\"action\":\"create\",\"previousRootId\":null,\"replaceOrder\":\"delete-first\",\"definitionsReset\":true," +
            "\"counts\":{\"slots\":1,\"components\":1},\"pending\":null,\"evidence\":{\"overall\":\"complete\"," +
            "\"session\":\"complete\",\"exactId\":\"unknown\",\"parent\":\"complete\",\"rootStamp\":\"unknown\"," +
            "\"siblingUniqueness\":\"complete\",\"descendantBoundary\":\"unknown\"},\"diagnostics\":[],\"atomic\":false}",
            plan.RootElement.GetProperty("data").GetRawText());
        Assert.Equal("create", plan.RootElement.GetProperty("data").GetProperty("action").GetString());
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task PlanAndDiffEmitHandwrittenSameHashReplaceDataWithoutChangingStateBytes()
    {
        var input = Save("replace.json", Envelope);
        var state = Path.Combine(root, "replace-state.json");
        var document = CreationDocument.Parse(Envelope);
        var active = CreationOwnershipFixture.State().ActiveRoot! with { InputHash = InputHash };
        CreationStateStore.Save(state, CreationOwnershipFixture.State() with { ActiveRoot = active });
        var before = File.ReadAllBytes(state);
        var planReport = Path.Combine(root, "replace-plan-report.json");
        var diffReport = Path.Combine(root, "replace-diff-report.json");
        var connects = 0;
        var planExit = await Program.RunAsync(Args("plan", input, state, planReport), _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(CreationOwnershipFixture.Client());
        });
        var diffExit = await Program.RunAsync(Args("diff", input, state, diffReport), _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(CreationOwnershipFixture.Client());
        });

        Assert.Equal(ExitCodes.Success, planExit);
        Assert.Equal(ExitCodes.Success, diffExit);
        Assert.Equal(2, connects);
        using var plan = JsonDocument.Parse(File.ReadAllText(planReport));
        using var diff = JsonDocument.Parse(File.ReadAllText(diffReport));
        const string expected = "{\"resultVersion\":\"1\",\"mode\":\"regenerate\",\"creationId\":\"room\",\"stateFile\":\"";
        var exact = expected + Path.GetFullPath(state).Replace("\\", "\\\\", StringComparison.Ordinal) +
            "\",\"buildId\":null,\"inputHash\":\"" + InputHash + "\",\"action\":\"replace\",\"previousRootId\":\"created-root\"," +
            "\"replaceOrder\":\"delete-first\",\"definitionsReset\":true,\"counts\":{\"slots\":1,\"components\":1},\"pending\":null," +
            "\"evidence\":{\"overall\":\"complete\",\"session\":\"complete\",\"exactId\":\"complete\",\"parent\":\"complete\"," +
            "\"rootStamp\":\"complete\",\"siblingUniqueness\":\"complete\",\"descendantBoundary\":\"complete\"},\"diagnostics\":[],\"atomic\":false}";
        Assert.Equal(exact, plan.RootElement.GetProperty("data").GetRawText());
        Assert.Equal(exact, diff.RootElement.GetProperty("data").GetRawText());
        Assert.Equal(before, File.ReadAllBytes(state));
    }

    [Theory]
    [InlineData("--creates-only")]
    [InlineData("--summary")]
    [InlineData("--discard-pending=op")]
    [InlineData("--replace-order=stage-inactive")]
    public async Task UnsupportedRegenerateOptionsFailBeforeConnect(string option)
    {
        var input = Save("unsupported-" + Guid.NewGuid().ToString("N") + ".json", Envelope);
        var report = Path.Combine(root, "unsupported-" + Guid.NewGuid().ToString("N") + ".json");
        var connects = 0;
        var exit = await Program.RunAsync(["plan", input, "--mode", "regenerate", option, "--json", "--report", report], _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(InitialClient());
        });
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Equal(0, connects);
        using var error = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("CREATION_MODE_MISMATCH", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        if (option == "--replace-order=stage-inactive")
            Assert.Contains("not available in this version", error.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ws://localhost:47610/", "S-creation-test")]
    [InlineData("auto", null)]
    public async Task RequiresExplicitAutoDiscoveryAndSessionIdentity(string url, string? session)
    {
        var input = Save("url-" + Guid.NewGuid().ToString("N") + ".json", Envelope);
        var report = Path.Combine(root, "url-report-" + Guid.NewGuid().ToString("N") + ".json");
        var args = new List<string> { "plan", input, "--mode", "regenerate", "--url", url, "--json" };
        args.Add("--report"); args.Add(report);
        if (session is not null) { args.Add("--session"); args.Add(session); }
        var connects = 0;
        var exit = await Program.RunAsync(args.ToArray(), _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(InitialClient());
        });
        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(0, connects);
        using var error = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("CREATION_SESSION_UNPROVEN", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task PendingAndRequiredStateErrorsPreserveStateAndFailBeforeConnect()
    {
        var input = Save("state-gates.json", Envelope);
        var state = Path.Combine(root, "pending-state.json");
        var current = CreationOwnershipFixture.State();
        var pending = new CreationPending("op-pending", "prepared", new("creation", null, null, null), "delete-first",
            current.Session, null, null, null, null, null, null, [], null);
        CreationStateStore.Save(state, current with { Pending = pending });
        var before = File.ReadAllBytes(state);
        var pendingReport = Path.Combine(root, "pending-report.json");
        var requiredReport = Path.Combine(root, "required-report.json");
        var connects = 0;
        var pendingExit = await Program.RunAsync(Args("plan", input, state, pendingReport), _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(InitialClient());
        });
        var missingState = Path.Combine(root, "missing-required-state.json");
        var requiredArgs = new List<string>(Args("plan", input, missingState, requiredReport)) { "--require-state" };
        var requiredExit = await Program.RunAsync(requiredArgs.ToArray(), _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(InitialClient());
        });

        Assert.Equal(ExitCodes.OperationFailed, pendingExit);
        Assert.Equal(ExitCodes.NotFound, requiredExit);
        Assert.Equal(0, connects);
        Assert.Equal(before, File.ReadAllBytes(state));
        using var pendingError = JsonDocument.Parse(File.ReadAllText(pendingReport));
        using var requiredError = JsonDocument.Parse(File.ReadAllText(requiredReport));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", pendingError.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("CREATION_STATE_REQUIRED", requiredError.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(missingState));
    }

    private string[] Args(string command, string input, string state, string report) =>
        [command, input, "--mode", "regenerate", "--url", "auto", "--session", CreationTestClient.SessionId,
            "--state", state, "--json", "--report", report];

    private CreationTestClient InitialClient()
    {
        var client = new CreationTestClient();
        client.AddSlot(CreationOwnershipFixture.Slot("Root", null, [],
            [CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "Root")]));
        client.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "Root"));
        return client;
    }

    private string Save(string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

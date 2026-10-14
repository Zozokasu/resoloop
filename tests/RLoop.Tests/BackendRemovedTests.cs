using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

[CollectionDefinition("Backend removed process state", DisableParallelization = true)]
public sealed class BackendRemovedProcessStateCollection { }

[Collection("Backend removed process state")]
public sealed class BackendRemovedTests
{
    private const string ApplyDefinition = "{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"backend-removed\"},\"slot\":{\"key\":\"root\",\"name\":\"Test\",\"parent\":\"Root\",\"position\":[0,1,2]},\"components\":[]}";
    private static readonly string[] RunResoniteNames =
    [
        "status", "ping", "hierarchy", "snapshot", "find", "observe", "inspect", "slot", "component",
        "type", "apply", "diff", "plan", "validate", "test", "uix", "item", "tool"
    ];

    public static IEnumerable<object[]> RemovedBackendCases()
    {
        foreach (var name in RunResoniteNames)
        foreach (var setting in new[] { "cli", "environment", "project" })
            yield return [name, setting];
    }

    public static IEnumerable<object[]> OfflineCommandCases()
    {
        var commands = new (string Name, string[] Args)[]
        {
            ("help", ["help"]),
            ("version", ["version"]),
            ("init", ["init"]),
            ("manifest scaffold", ["manifest", "scaffold", "--output", "manifest.json"]),
            ("skills sync --update", ["skills", "sync", "--update"]),
            ("creation validate", ["validate", "creation.json", "--json"]),
            ("apply discard pending", ["apply", "definition.json", "--state", "state.json", "--discard-pending", "review-op", "--yes", "--json"])
        };
        foreach (var (name, args) in commands)
        foreach (var setting in new[] { "environment", "project" })
            yield return [name, setting, args];
    }

    [Fact]
    public async Task BackendWorkbenchFromCliEnvironmentAndProjectFailsBeforeConnect()
    {
        await AssertRemovedAsync(["status", "--backend", "workbench"]);
        await AssertRemovedAsync(["status"], environment: ("RESOLOOP_BACKEND", "workbench"));
        await AssertRemovedAsync(["status"], projectConfig: "{\"backend\":\"workbench\"}");
        await AssertRemovedAsync(["wb", "status"]);
        await AssertRemovedAsync(["wb"]);
        await AssertRemovedAsync(["wb", "foo"]);
        await AssertRemovedAsync(["--workbench-pipe", "X", "status"]);
    }

    [Fact]
    public async Task ApplyWithProjectWorkbenchSettingFailsBeforeConnect()
    {
        await InTemporaryStateAsync(async () =>
        {
            File.WriteAllText(".resoloop.json", "{\"backend\":\"workbench\"}");
            File.WriteAllText("definition.json", ApplyDefinition);
            var outcome = await InvokeAsync(["apply", "definition.json", "--url", "ws://localhost:1", "--json"]);
            AssertRemoved(outcome);
            return true;
        });
    }

    [Fact]
    public async Task LegacyWorkbenchPipeEnvironmentIsIgnored()
    {
        var outcome = await InvokeInStateAsync(["status", "--json", "--url", "ws://localhost:1"], environment: ("RESOLOOP_WORKBENCH_PIPE", "X"));
        Assert.Equal(1, outcome.Connects);
        Assert.Equal(ExitCodes.Success, outcome.Exit);
        Assert.DoesNotContain("BACKEND_REMOVED", outcome.Text);
    }

    [Fact]
    public async Task LegacyWorkbenchPipeProjectKeyIsIgnored()
    {
        var outcome = await InvokeInStateAsync(["status", "--json", "--url", "ws://localhost:1"], projectConfig: "{\"workbenchPipe\":\"X\"}");
        Assert.Equal(1, outcome.Connects);
        Assert.Equal(ExitCodes.Success, outcome.Exit);
        Assert.DoesNotContain("BACKEND_REMOVED", outcome.Text);
    }

    [Fact]
    public async Task ExplicitAndDefaultLinkHaveSameJsonOutputAndExitCode()
    {
        var client = new CreationLegacyTestClient();
        var explicitLink = await InvokeInStateAsync(["--backend", "link", "status", "--json", "--url", "ws://localhost:1"], client: client);
        var defaultLink = await InvokeInStateAsync(["status", "--json", "--url", "ws://localhost:1"], client: client);
        Assert.Equal(1, explicitLink.Connects);
        Assert.Equal(1, defaultLink.Connects);
        Assert.Equal(explicitLink.Exit, defaultLink.Exit);
        Assert.Equal(NormalizeStatusJson(explicitLink.Text), NormalizeStatusJson(defaultLink.Text));
    }

    [Fact]
    public async Task UnknownBackendRemainsInvalidBeforeConnect()
    {
        var outcome = await InvokeInStateAsync(["--backend", "foo", "status", "--json"]);
        Assert.Equal(0, outcome.Connects);
        Assert.Equal(ExitCodes.InvalidArguments, outcome.Exit);
        Assert.Contains("INVALID_BACKEND", outcome.Text);
    }

    [Fact]
    public async Task HelpOmitsRemovedBackendAndCommandOptions()
    {
        var output = await CaptureAsync(() => Program.RunAsync(["help"]));
        Assert.DoesNotContain("workbench", output.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--workbench-pipe", output.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\nwb ", output.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\nresoloop wb", output.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(OfflineCommandCases))]
    public async Task OfflineCommandRejectsRemovedBackendFromEnvironmentAndProject(string command, string setting, string[] args)
    {
        await InTemporaryStateAsync(async () =>
        {
            if (setting == "environment") Environment.SetEnvironmentVariable("RESOLOOP_BACKEND", "workbench");
            else File.WriteAllText(".resoloop.json", "{\"backend\":\"workbench\"}");

            File.WriteAllText("creation.json", "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}");
            File.WriteAllText("definition.json", ApplyDefinition);
            if (command == "apply discard pending")
            {
                var state = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "RLoop.Tests", "fixtures", "ordinary-apply-surface", "state-v3.json")))!;
                state["pending"] = new JsonArray(new JsonObject
                {
                    ["operationId"] = "review-op",
                    ["kind"] = "addComponent",
                    ["key"] = "unconfirmed",
                    ["ownershipKey"] = state["ownershipKey"]!.GetValue<string>(),
                    ["id"] = "C-exact"
                });
                File.WriteAllText("state.json", state.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            }

            var before = SnapshotFiles(Environment.CurrentDirectory);
            var outcome = await InvokeAsync(args);
            AssertRemoved(outcome);
            Assert.Equal(before, SnapshotFiles(Environment.CurrentDirectory));
            return true;
        });
    }

    [Fact]
    public async Task EnvironmentLinkOverridesProjectWorkbench()
    {
        var outcome = await InvokeInStateAsync(["help"], environment: ("RESOLOOP_BACKEND", "link"), projectConfig: "{\"backend\":\"workbench\"}");
        Assert.Equal(ExitCodes.Success, outcome.Exit);
        Assert.Equal(0, outcome.Connects);
        Assert.DoesNotContain("BACKEND_REMOVED", outcome.Text);
    }

    [Theory]
    [MemberData(nameof(RemovedBackendCases))]
    public async Task EveryConnectedCommandRejectsRemovedBackend(string command, string setting)
    {
        var args = new List<string> { command };
        if (setting == "cli") args.AddRange(["--backend", "workbench"]);
        args.Add("--json");
        var outcome = setting switch
        {
            "environment" => await InvokeInStateAsync(args.ToArray(), environment: ("RESOLOOP_BACKEND", "workbench")),
            "project" => await InvokeInStateAsync(args.ToArray(), projectConfig: "{\"backend\":\"workbench\"}"),
            _ => await InvokeInStateAsync(args.ToArray())
        };
        AssertRemoved(outcome);
    }

    private static async Task AssertRemovedAsync(string[] args, (string Name, string Value)? environment = null,
        string? projectConfig = null)
    {
        var outcome = await InvokeInStateAsync(args, environment, projectConfig);
        AssertRemoved(outcome);
    }

    private static void AssertRemoved((int Exit, int Connects, string Text) outcome)
    {
        Assert.Equal(ExitCodes.InvalidArguments, outcome.Exit);
        Assert.Equal(0, outcome.Connects);
        Assert.Contains("BACKEND_REMOVED", outcome.Text);
    }

    private static async Task<(int Exit, int Connects, string Text)> InvokeInStateAsync(string[] args,
        (string Name, string Value)? environment = null, string? projectConfig = null, IResoniteClient? client = null) =>
        await InTemporaryStateAsync(async () =>
        {
            if (projectConfig is not null) File.WriteAllText(".resoloop.json", projectConfig);
            if (environment is { } setting) Environment.SetEnvironmentVariable(setting.Name, setting.Value);
            return await InvokeAsync(args, client);
        });

    private static async Task<(int Exit, int Connects, string Text)> InvokeAsync(string[] args, IResoniteClient? clientOverride = null)
    {
        var connects = 0;
        var client = clientOverride ?? new CreationLegacyTestClient();
        var captured = await CaptureAsync(() => Program.RunAsync(args, _ =>
        {
            connects++;
            return Task.FromResult<IResoniteClient>(client);
        }));
        return (captured.Exit, connects, captured.Text);
    }

    private static async Task<T> InTemporaryStateAsync<T>(Func<Task<T>> action)
    {
        var oldDirectory = Environment.CurrentDirectory;
        var oldBackend = Environment.GetEnvironmentVariable("RESOLOOP_BACKEND");
        var oldPipe = Environment.GetEnvironmentVariable("RESOLOOP_WORKBENCH_PIPE");
        var root = Path.Combine(Path.GetTempPath(), "resoloop-backend-removed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("RESOLOOP_BACKEND", null);
            Environment.SetEnvironmentVariable("RESOLOOP_WORKBENCH_PIPE", null);
            Environment.CurrentDirectory = root;
            return await action();
        }
        finally
        {
            Environment.CurrentDirectory = oldDirectory;
            Environment.SetEnvironmentVariable("RESOLOOP_BACKEND", oldBackend);
            Environment.SetEnvironmentVariable("RESOLOOP_WORKBENCH_PIPE", oldPipe);
            Directory.Delete(root, true);
        }
    }

    private static Dictionary<string, string> SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToBase64String(File.ReadAllBytes(path)), StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ResoLoop.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static async Task<(int Exit, string Text)> CaptureAsync(Func<Task<int>> action)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = await action();
            return (exit, output.ToString() + error.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    private static string NormalizeStatusJson(string text)
    {
        var jsonStart = text.IndexOf('{');
        var json = JsonNode.Parse(text[jsonStart..])!;
        json["data"]?.AsObject().Remove("latencyMs");
        return json.ToJsonString();
    }
}

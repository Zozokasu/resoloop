using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class BackendSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-tests-" + Guid.NewGuid().ToString("N"));

    public BackendSupportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void BackendUsesCliEnvironmentProjectUserPrecedence()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"backend\":\"link\"}");
        var project = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(project, "{\"backend\":\"workbench\"}");
        var cli = new Dictionary<string, string?> { ["backend"] = "workbench" };
        string? Env(string key) => key == "RESOLOOP_BACKEND" ? "link" : null;

        var cliResult = ConfigResolver.Resolve(_root, cli, Env, user);
        Assert.Equal("workbench", cliResult.Config.Backend);
        Assert.Equal("cli", cliResult.Sources["backend"]);

        cli.Clear();
        var envResult = ConfigResolver.Resolve(_root, cli, Env, user);
        Assert.Equal("link", envResult.Config.Backend);
        Assert.Equal("environment:RESOLOOP_BACKEND", envResult.Sources["backend"]);

        var projectResult = ConfigResolver.Resolve(_root, cli, _ => null, user);
        Assert.Equal("workbench", projectResult.Config.Backend);
        Assert.Equal(project, projectResult.Sources["backend"]);

        File.Delete(project);
        var userResult = ConfigResolver.Resolve(_root, cli, _ => null, user);
        Assert.Equal("link", userResult.Config.Backend);
        Assert.Equal(Path.Combine(user, ".resoloop", "config.json"), userResult.Sources["backend"]);
    }

    [Fact]
    public void WorkbenchPipeUsesCliEnvironmentProjectUserPrecedence()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"workbenchPipe\":\"user-pipe\"}");
        var project = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(project, "{\"workbenchPipe\":\"project-pipe\"}");
        var cli = new Dictionary<string, string?> { ["workbench-pipe"] = "cli-pipe" };
        string? Env(string key) => key == "RESOLOOP_WORKBENCH_PIPE" ? "env-pipe" : null;

        Assert.Equal("cli-pipe", ConfigResolver.Resolve(_root, cli, Env, user).Config.WorkbenchPipe);
        cli.Clear();
        Assert.Equal("env-pipe", ConfigResolver.Resolve(_root, cli, Env, user).Config.WorkbenchPipe);
        Assert.Equal("project-pipe", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.WorkbenchPipe);
        File.Delete(project);
        Assert.Equal("user-pipe", ConfigResolver.Resolve(_root, cli, _ => null, user).Config.WorkbenchPipe);
    }

    [Fact]
    public void BackendAndWorkbenchPipeHaveDefaults()
    {
        var result = ConfigResolver.Resolve(_root, new Dictionary<string, string?>(), _ => null, Path.Combine(_root, "none"));
        Assert.Equal("link", result.Config.Backend);
        Assert.Equal("ResoniteWorkbench.Rpc.v1", result.Config.WorkbenchPipe);
        Assert.False(result.Sources.ContainsKey("backend"));
        Assert.False(result.Sources.ContainsKey("workbenchPipe"));
    }

    [Theory]
    [InlineData("LINK", "link")]
    [InlineData("Workbench", "workbench")]
    public void BackendIsCaseInsensitiveAndNormalized(string value, string expected)
    {
        var result = ConfigResolver.Resolve(_root,
            new Dictionary<string, string?> { ["backend"] = value }, _ => null, Path.Combine(_root, "none"));
        Assert.Equal(expected, result.Config.Backend);
    }

    [Fact]
    public void BackendRejectsInvalidValues()
    {
        var ex = Assert.Throws<RLoopException>(() => ConfigResolver.Resolve(_root,
            new Dictionary<string, string?> { ["backend"] = "resonitelink" }, _ => null, Path.Combine(_root, "none")));
        Assert.Equal("INVALID_BACKEND", ex.Code);
        Assert.Equal(ExitCodes.InvalidArguments, ex.ExitCode);
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("slot")]
    [InlineData("component")]
    [InlineData("test")]
    [InlineData("flux")]
    [InlineData("diff")]
    [InlineData("plan")]
    [InlineData("uix")]
    [InlineData("item")]
    [InlineData("tool")]
    [InlineData("capture")]
    [InlineData("doctor")]
    [InlineData("scene")]
    [InlineData("blender")]
    [InlineData("logs")]
    public void ListedCommandsThrowOnWorkbench(string command)
    {
        var ex = Assert.Throws<RLoopException>(() => BackendSupport.RequireSupported(command, "workbench"));
        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.NotEmpty(ex.Suggestions);
        Assert.True(ex.Context.ContainsKey("reason"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("ping")]
    [InlineData("hierarchy")]
    [InlineData("find")]
    [InlineData("observe")]
    [InlineData("inspect")]
    [InlineData("type")]
    [InlineData("validate")]
    public void SupportedCommandsPassOnWorkbench(string command) =>
        BackendSupport.RequireSupported(command, "workbench");

    [Theory]
    [InlineData("hierarchy", "profile")]
    [InlineData("hierarchy", "query")]
    [InlineData("snapshot", "create")]
    public void ObservationSubcommandsAreUnsupportedOnWorkbench(string command, string subcommand)
    {
        Assert.Equal(BackendSupport.Unsupported, BackendSupport.WorkbenchSupport[$"{command} {subcommand}"]);
        var ex = Assert.Throws<RLoopException>(() => BackendSupport.RequireSupported(command, "workbench", subcommand));
        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal(BackendSupport.Unsupported, ex.Context["level"]);
        Assert.Contains($"{command} {subcommand}", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace((string?)ex.Context["reason"]));
        // The same subcommand is fine on the link backend and when no backend is chosen.
        BackendSupport.RequireSupported(command, "link", subcommand);
        BackendSupport.RequireSupported(command, null, subcommand);
    }

    [Fact]
    public void PlainHierarchyStaysSupportedAndSnapshotDiffStaysUnregisteredBecauseItNeverConnects()
    {
        BackendSupport.RequireSupported("hierarchy", "workbench");
        BackendSupport.RequireSupported("hierarchy", "workbench", "Root");
        Assert.DoesNotContain("snapshot diff", BackendSupport.WorkbenchSupport.Keys);
        Assert.DoesNotContain("snapshot", BackendSupport.WorkbenchSupport.Keys);
        BackendSupport.RequireSupported("snapshot", "workbench", "diff");
    }

    [Fact]
    public void LinkBackendAndUnlistedCommandsPassThrough()
    {
        BackendSupport.RequireSupported("status", "link");
        BackendSupport.RequireSupported("apply", null);
        BackendSupport.RequireSupported("discover", "workbench");
        BackendSupport.RequireSupported("help", "workbench");
        BackendSupport.RequireSupported("not-a-command", "workbench");
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("uix")]
    [InlineData("doctor")]
    public async Task WorkbenchBackendRejectsCommandBeforeConnecting(string command)
    {
        // No URL is configured: without the gate these commands would fail with
        // RESONITE_LINK_URL_MISSING or attempt a real connection. Exit code 7 and
        // BACKEND_UNSUPPORTED prove the gate fired before URL resolution.
        var (exit, report) = await RunAsync(command, "--backend", "workbench", "--json");
        Assert.Equal(ExitCodes.OperationFailed, exit);
        Assert.Contains("BACKEND_UNSUPPORTED", report);
    }

    [Fact]
    public async Task OfflineCommandStillRunsOnWorkbenchBackend()
    {
        var (exit, _) = await RunAsync("schema", "list", "--backend", "workbench", "--json");
        Assert.Equal(ExitCodes.Success, exit);
    }

    // --report records each outcome to a per-invocation file, so assertions stay deterministic
    // while parallel test classes share the process-wide Console streams.
    private static async Task<(int Exit, string Report)> RunAsync(params string[] args)
    {
        var reportPath = Path.Combine(Path.GetTempPath(), "resoloop-test-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            var exit = await Program.Main([.. args, "--report", reportPath]);
            return (exit, File.ReadAllText(reportPath));
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }
}

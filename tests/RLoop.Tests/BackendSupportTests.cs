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
    [InlineData("status")]
    [InlineData("hierarchy")]
    [InlineData("apply")]
    [InlineData("slot")]
    [InlineData("flux")]
    public void ListedCommandsThrowOnWorkbench(string command)
    {
        var ex = Assert.Throws<RLoopException>(() => BackendSupport.RequireSupported(command, "workbench"));
        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.NotEmpty(ex.Suggestions);
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
    [InlineData("status")]
    [InlineData("hierarchy")]
    [InlineData("apply")]
    public async Task WorkbenchBackendRejectsCommandBeforeConnecting(string command)
    {
        // No URL is configured: without the gate these commands would fail with
        // RESONITE_LINK_URL_MISSING or attempt a real connection. Exit code 7 and
        // BACKEND_UNSUPPORTED prove the gate fired before URL resolution.
        var (exit, stderr) = await RunAsync(command, "--backend", "workbench", "--json");
        Assert.Equal(ExitCodes.OperationFailed, exit);
        Assert.Contains("BACKEND_UNSUPPORTED", stderr);
    }

    [Fact]
    public async Task OfflineCommandStillRunsOnWorkbenchBackend()
    {
        var (exit, _) = await RunAsync("schema", "list", "--backend", "workbench", "--json");
        Assert.Equal(ExitCodes.Success, exit);
    }

    private static async Task<(int Exit, string StdErr)> RunAsync(params string[] args)
    {
        var stderr = new StringWriter();
        var originalError = Console.Error;
        var originalOut = Console.Out;
        try
        {
            Console.SetError(stderr);
            Console.SetOut(new StringWriter());
            return (await Program.Main(args), stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
        }
    }
}

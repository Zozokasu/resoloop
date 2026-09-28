using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class WorkbenchCliTests
{
    private static readonly string MissingPipe = "resoloop-test-missing-" + Guid.NewGuid().ToString("N");

    private static RLoopConfig WorkbenchConfig(string? url = null) =>
        new(ResoniteLinkUrl: url, TimeoutSeconds: 1, Backend: "workbench", WorkbenchPipe: MissingPipe);

    [Fact]
    public async Task WorkbenchBackendSkipsUrlResolution()
    {
        // An invalid ResoniteLink URL must not surface INVALID_RESONITE_LINK_URL: the workbench
        // backend never resolves it and goes straight to the pipe, where no server answers.
        var ex = await Assert.ThrowsAsync<RLoopException>(() => ResoniteClientFactory.ConnectAsync(
            ParsedArguments.Parse(["status"]), WorkbenchConfig("not a url"),
            new ReflectionCacheOptions("off"), CancellationToken.None));
        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task WorkbenchBackendConnectsWithoutConfiguredUrl()
    {
        // No URL configured: RESONITE_LINK_URL_MISSING must not fire on the workbench backend.
        var ex = await Assert.ThrowsAsync<RLoopException>(() => ResoniteClientFactory.ConnectAsync(
            ParsedArguments.Parse(["status"]), WorkbenchConfig(),
            new ReflectionCacheOptions("off"), CancellationToken.None));
        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task LinkBackendStillResolvesUrl()
    {
        var config = new RLoopConfig(TimeoutSeconds: 1, Backend: "link");
        var ex = await Assert.ThrowsAsync<RLoopException>(() => ResoniteClientFactory.ConnectAsync(
            ParsedArguments.Parse(["status"]), config, new ReflectionCacheOptions("off"), CancellationToken.None));
        Assert.Equal("RESONITE_LINK_URL_MISSING", ex.Code);
    }

    [Theory]
    [InlineData("link")]
    [InlineData("workbench")]
    public async Task WbStatusAlwaysConnectsToWorkbench(string backend)
    {
        // wb status ignores --backend: with no pipe server it must fail WORKBENCH_UNAVAILABLE
        // either way. A link attempt would instead report RESONITE_LINK_URL_MISSING.
        var (exit, report) = await RunAsync("wb", "status", "--backend", backend,
            "--workbench-pipe", MissingPipe, "--timeout", "1", "--json");
        Assert.Equal(ExitCodes.ConnectionFailed, exit);
        Assert.Contains("WORKBENCH_UNAVAILABLE", report);
    }

    [Theory]
    [InlineData("wb")]
    [InlineData("wb", "bogus")]
    [InlineData("wb", "status", "extra")]
    public async Task WbRejectsUnknownSubcommands(params string[] args)
    {
        var (exit, report) = await RunAsync(args);
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Contains("UNKNOWN_COMMAND", report);
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

using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class LinkConnectionErrorPrecedenceTests
{
    private static readonly string MissingPipe = "resoloop-test-missing-" + Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkBackendInvalidUrlBeatsInvalidCacheOption(bool explicitBackend)
    {
        // --url and --cache are both invalid. The default connect path must resolve the URL
        // before ReflectionCacheFrom validates --cache, so the URL error always wins.
        var args = new List<string> { "status", "--url", "not a url", "--cache", "bogus" };
        if (explicitBackend) args.AddRange(new[] { "--backend", "link" });
        var (exit, report) = await RunAsync(args.ToArray());
        Assert.Equal(ExitCodes.ConfigurationError, exit);
        Assert.Contains("INVALID_RESONITE_LINK_URL", report);
        Assert.DoesNotContain("REFLECTION_CACHE_INVALID", report);
    }

    [Fact]
    public async Task LinkBackendMissingUrlBeatsInvalidCacheOption()
    {
        // RESONITE_LINK_URL_MISSING is reachable only when no URL comes from environment,
        // project or user configuration (an empty --url still falls through to them). Probe the
        // ambient resolution; when it provides a URL the scenario cannot occur and only the
        // cache-error ordering is pinned. --timeout/--discovery-seconds bound any stray attempt.
        var ambient = ConfigResolver.Resolve(Environment.CurrentDirectory, new Dictionary<string, string?>());
        var (exit, report) = await RunAsync("status", "--cache", "bogus",
            "--timeout", "1", "--discovery-seconds", "1");
        Assert.DoesNotContain("REFLECTION_CACHE_INVALID", report);
        if (ambient.Config.ResoniteLinkUrl is null)
        {
            Assert.Equal(ExitCodes.ConfigurationError, exit);
            Assert.Contains("RESONITE_LINK_URL_MISSING", report);
        }
    }

    [Fact]
    public async Task WorkbenchBackendIgnoresInvalidUrlAndConnectsPipe()
    {
        // The workbench backend never resolves the link URL: an invalid --url is ignored and the
        // pipe connect is attempted instead. "bogus" is unlisted in BackendSupport.WorkbenchSupport,
        // so it reaches the shared connect path, where the missing pipe fails WORKBENCH_UNAVAILABLE.
        var (exit, report) = await RunAsync("bogus", "--backend", "workbench",
            "--url", "not a url", "--workbench-pipe", MissingPipe, "--timeout", "1");
        Assert.Equal(ExitCodes.ConnectionFailed, exit);
        Assert.Contains("WORKBENCH_UNAVAILABLE", report);
        Assert.DoesNotContain("INVALID_RESONITE_LINK_URL", report);
        Assert.DoesNotContain("UNKNOWN_COMMAND", report);
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

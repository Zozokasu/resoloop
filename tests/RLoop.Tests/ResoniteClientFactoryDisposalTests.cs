using System.Diagnostics;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

/// <summary>
/// Regression coverage for the P1 review fix in <see cref="ResoniteClientFactory.ConnectAsync"/>:
/// a client the factory created must be disposed when its inner ConnectAsync throws.
/// Neither client exposes an observable "disposed" state, so these tests do NOT observe disposal
/// directly; they pin the exception contract (type, Code, ExitCode) and timing so adding the
/// DisposeAsync call does not change what callers see, does not hang, and leaves a repeated
/// attempt on identical inputs failing the same way instead of tripping over a leaked resource.
/// </summary>
public sealed class ResoniteClientFactoryDisposalTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);

    private static string NewMissingPipe() => "resoloop-test-missing-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task WorkbenchBranch_ConnectFailure_ThrowsUnavailableTwiceWithoutHanging()
    {
        var config = new RLoopConfig(TimeoutSeconds: 1, Backend: "workbench", WorkbenchPipe: NewMissingPipe());
        var args = ParsedArguments.Parse(["status"]);
        using var guard = new CancellationTokenSource(GuardTimeout);

        var watch = Stopwatch.StartNew();
        var first = await Assert.ThrowsAsync<RLoopException>(() =>
            ResoniteClientFactory.ConnectAsync(args, config, new ReflectionCacheOptions("off"), guard.Token));
        // A second attempt on identical inputs must fail identically; a leaked half-open
        // connection would surface as a hang or a different failure mode here.
        var second = await Assert.ThrowsAsync<RLoopException>(() =>
            ResoniteClientFactory.ConnectAsync(args, config, new ReflectionCacheOptions("off"), guard.Token));
        watch.Stop();

        Assert.Equal("WORKBENCH_UNAVAILABLE", first.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, first.ExitCode);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.ExitCode, second.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"two failed workbench connects took {watch.Elapsed}");
    }

    [Fact]
    public async Task LinkBranch_ConnectFailure_ThrowsConnectionFailedWithoutHanging()
    {
        // 127.0.0.1:1 is a closed loopback port: the WebSocket connect is refused immediately and
        // ResoniteLinkClientAdapter.ConnectAsync maps it to CONNECTION_FAILED (not the timeout
        // path). No traffic leaves the machine.
        var config = new RLoopConfig(ResoniteLinkUrl: "ws://127.0.0.1:1/", TimeoutSeconds: 5, Backend: "link");
        var args = ParsedArguments.Parse(["status"]);
        using var guard = new CancellationTokenSource(GuardTimeout);

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RLoopException>(() =>
            ResoniteClientFactory.ConnectAsync(args, config, new ReflectionCacheOptions("off"), guard.Token));
        watch.Stop();

        Assert.Equal("CONNECTION_FAILED", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"failed link connect took {watch.Elapsed}");
    }
}

using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class WorkbenchCliTests
{
    [Fact]
    public async Task LinkBackendStillResolvesUrl()
    {
        var config = new RLoopConfig(TimeoutSeconds: 1, Backend: "link");
        var ex = await Assert.ThrowsAsync<RLoopException>(() => ResoniteClientFactory.ConnectAsync(
            ParsedArguments.Parse(["status"]), config, new ReflectionCacheOptions("off"), CancellationToken.None));
        Assert.Equal("RESONITE_LINK_URL_MISSING", ex.Code);
    }

}

using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

public sealed class SessionDiscoveryIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DiscoversAnnouncementAndReadsSelectedSessionWithoutWorldMutation()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var url = await SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), new ResoniteSessionDiscovery(),
            session: Environment.GetEnvironmentVariable("RESOLOOP_DISCOVERY_SESSION"), cancellationToken: timeout.Token);
        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(url, TimeSpan.FromSeconds(10), timeout.Token);
        var session = await client.GetSessionInfoAsync(timeout.Token);
        Assert.True(session.Connected);
        Assert.False(string.IsNullOrWhiteSpace(session.ResoniteVersion));
    }
}

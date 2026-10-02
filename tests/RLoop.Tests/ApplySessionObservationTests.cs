using RLoop.Core;
using RLoop.Cli;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

public sealed class ApplySessionObservationTests
{
    [Fact]
    public async Task FactoryReusesSameUriInstanceAndReplacesAnnouncementEvidence()
    {
        var uri = new Uri("ws://localhost:47610");
        ResoniteClientFactory.RememberAnnouncements(uri, [new("S-before", "before", uri.AbsoluteUri, DateTime.UtcNow)]);
        ResoniteClientFactory.RememberAnnouncements(uri, [new("S-after", "after", uri.AbsoluteUri, DateTime.UtcNow)]);
        var link = new ScriptedMetadataLink { Connected = false };
        await using var adapter = new ResoniteLinkClientAdapter(link, TimeSpan.FromSeconds(2), new ReflectionCacheOptions("off"));
        var connected = await ResoniteClientFactory.ConnectAsync(ParsedArguments.Parse(["status"]), new RLoopConfig(uri.AbsoluteUri),
            new ReflectionCacheOptions("off"), CancellationToken.None, uri, null, () => adapter);
        Assert.Same(adapter, connected);
        Assert.Equal("S-after", adapter.ObserveApplySession().DiscoverSessionId);
    }
    [Theory]
    [InlineData("WS://LOCALHOST:047610", "ws://localhost:47610/")]
    [InlineData("ws://127.0.0.1:47610/", "ws://localhost:47610/")]
    [InlineData("ws://127.0.0.2:47610/", "ws://localhost:47610/")]
    [InlineData("ws://[::1]:47610/", "ws://localhost:47610/")]
    [InlineData("ws://Example.COM:80/", "ws://example.com/")]
    public void SessionUrlNormalizesHostAndPort(string url, string expected) =>
        Assert.Equal(expected, ApplySessionObservation.NormalizeUrl(url));

    [Theory]
    [InlineData("WS://BÜCHER.example.:80/a?x=1", "ws://xn--bcher-kva.example/a?x=1")]
    [InlineData("ws://127.0.0.2:47610/path?q=one", "ws://[::1]:47610/path?q=one")]
    public void EndpointAliasesShareTheExistingNormalizedKey(string left, string right) =>
        Assert.Equal(ApplySessionObservation.NormalizeUrl(left), ApplySessionObservation.NormalizeUrl(right));

    [Theory]
    [InlineData("ws://localhost:47610/a", "ws://localhost:47611/a")]
    [InlineData("ws://localhost:47610/a", "ws://localhost:47610/b")]
    [InlineData("ws://localhost:47610/a?x=1", "ws://localhost:47610/a?x=2")]
    [InlineData("ws://localhost:47610/a", "wss://localhost:47610/a")]
    public void PortPathQueryAndSchemeRemainPartOfTheExistingKey(string left, string right) =>
        Assert.NotEqual(ApplySessionObservation.NormalizeUrl(left), ApplySessionObservation.NormalizeUrl(right));

    [Fact]
    public void SessionAnnouncementOnlyMatchesNormalizedUrlAndSId()
    {
        var announcement = new DiscoveredResoniteSession("S-observed", "session", "ws://127.0.0.1:47610/", DateTime.UtcNow);
        var matched = ApplySessionObservation.Observe("ws://LOCALHOST:47610", [announcement]);
        Assert.Equal("matched", matched.IdentityStatus);
        Assert.Equal("S-observed", matched.DiscoverSessionId);
        Assert.Equal("unknown", ApplySessionObservation.Observe("ws://localhost:47611", [announcement]).IdentityStatus);
        Assert.Null(ApplySessionObservation.Observe("ws://localhost:47610").DiscoverSessionId);
        Assert.Null(ApplySessionObservation.Observe("ws://localhost:47610", [announcement with { SessionId = "67" }]).DiscoverSessionId);
        Assert.Equal("unknown", ApplySessionObservation.Observe("ws://localhost:47610",
            [announcement, announcement with { SessionId = "S-other" }]).IdentityStatus);
    }

    [Fact]
    public async Task AdapterCounterAndGenerationAreNotSessionIdentity()
    {
        var link = new ScriptedMetadataLink { Connected = false };
        await using var adapter = new ResoniteLinkClientAdapter(link, TimeSpan.FromSeconds(2), new ReflectionCacheOptions("off"));
        var url = new Uri("ws://localhost:47610");
        await adapter.ConnectAsync(url, TimeSpan.FromSeconds(2));
        Assert.Null(adapter.ObserveApplySession().DiscoverSessionId);
        adapter.SetApplyAnnouncements([new("S-observed", "session", "ws://127.0.0.1:47610", DateTime.UtcNow)]);
        var first = adapter.ObserveApplySession();
        var generation = adapter.ObserveApplyConnection().Generation;
        link.Connected = false;
        await adapter.ConnectAsync(url, TimeSpan.FromSeconds(2));
        Assert.NotEqual(generation, adapter.ObserveApplyConnection().Generation);
        Assert.Equal("unknown", adapter.ObserveApplySession().IdentityStatus);
        adapter.SetApplyAnnouncements([new("S-observed", "session", "ws://127.0.0.1:47610", DateTime.UtcNow)]);
        Assert.Equal(first, adapter.ObserveApplySession());
        Assert.NotEqual((await adapter.GetSessionInfoAsync()).UniqueSessionId, first.DiscoverSessionId);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("disconnect")]
    [InlineData("cancel")]
    public async Task AdapterGuardsImmediatelyBeforeCreatingWriteTask(string fault)
    {
        var link = new ScriptedMetadataLink { Connected = false };
        await using var adapter = new ResoniteLinkClientAdapter(link, TimeSpan.FromSeconds(2), new ReflectionCacheOptions("off"));
        await adapter.ConnectAsync(new("ws://localhost:47610"), TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        using var guard = adapter.GuardApplyWrites(fault == "generation" ? "old-generation" : adapter.ObserveApplyConnection().Generation);
        adapter.BeginApplySend();
        adapter.BeforeApplyWriteBoundaryForTests = () =>
        {
            if (fault == "disconnect") link.Connected = false;
            if (fault == "cancel") cancellation.Cancel();
        };
        if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.UpdateSlotAsync(new("exact-target", Name: "Name"), cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => adapter.UpdateSlotAsync(new("exact-target", Name: "Name")));
            Assert.Equal(fault == "generation" ? "CONNECTION_GENERATION_CHANGED" : "APPLY_PRECONDITION_FAILED", error.Code);
        }
        // SDK LinkInterface has no connection in this seam. A send would reach its failure path.
        Assert.Empty(link.RequestedSlots);
        Assert.False(adapter.ApplySendStarted);
        Assert.False(adapter.ApplyResponseReceived);
    }
}

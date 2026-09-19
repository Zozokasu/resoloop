using System.Net;
using ResoniteLink;
using RLoop.Cli;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

public sealed class SessionDiscoveryTests
{
    private static readonly DateTime Now = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);
    private static DiscoveredResoniteSession Session(string id, string name, int port) => new(id, name, $"ws://127.0.0.1:{port}/", Now);

    [Fact]
    public async Task ExplicitUrlNeverDiscoversOrFallsBack()
    {
        var discovery = new FakeDiscovery();
        Assert.Equal("wss://example.org:1234/", (await SessionDiscovery.ResolveUrlAsync(new RLoopConfig("wss://example.org:1234"), discovery)).AbsoluteUri);
        var invalid = await Assert.ThrowsAsync<RLoopException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig("http://wrong"), discovery));
        Assert.Equal("INVALID_RESONITE_LINK_URL", invalid.Code);
        var missing = await Assert.ThrowsAsync<RLoopException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig(), discovery));
        Assert.Equal("RESONITE_LINK_URL_MISSING", missing.Code);
        Assert.Equal(0, discovery.Calls);
    }

    [Fact]
    public async Task AutoWaitsThenSelectsOneAndNamesAreExact()
    {
        var discovery = new FakeDiscovery(Session("S-one", "My World", 64696));
        Assert.Equal(64696, (await SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), discovery, session: "My World")).Port);
        Assert.Equal(TimeSpan.FromSeconds(12), discovery.Duration);
        var missing = await Assert.ThrowsAsync<RLoopException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), discovery, session: "World"));
        Assert.Equal("RESONITE_DISCOVERY_NOT_FOUND", missing.Code);
    }

    [Fact]
    public async Task EmptyOrAmbiguousDiscoveryDoesNotChooseFirstAndExposesCandidates()
    {
        var error = await Assert.ThrowsAsync<RLoopException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), new FakeDiscovery()));
        Assert.Equal("RESONITE_DISCOVERY_NOT_FOUND", error.Code);
        var discovery = new FakeDiscovery(Session("S-one", "Same", 1111), Session("S-two", "Same", 2222));
        foreach (var selector in new string?[] { null, "Same" })
        {
            error = await Assert.ThrowsAsync<RLoopException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), discovery, session: selector));
            Assert.Equal("RESONITE_DISCOVERY_AMBIGUOUS", error.Code);
            Assert.True(error.Context.ContainsKey("sessions"));
        }
        Assert.Equal(2222, (await SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), discovery, session: "S-two")).Port);
    }

    [Fact]
    public async Task InvalidOptionsAndCancellationDoNotStartDiscovery()
    {
        var discovery = new FakeDiscovery();
        foreach (var seconds in new[] { 0, 61 })
            Assert.Equal("INVALID_OPTION", (await Assert.ThrowsAsync<RLoopException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), discovery, seconds))).Code);
        Assert.Equal("DISCOVERY_SELECTOR_REQUIRES_AUTO", (await Assert.ThrowsAsync<RLoopException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig("ws://localhost:1"), discovery, session: "S-one"))).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionDiscovery.ResolveUrlAsync(new RLoopConfig("auto"), discovery, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ResoniteSessionDiscovery().DiscoverAsync(TimeSpan.FromSeconds(12), cancellation.Token));
        Assert.Equal(0, discovery.Calls);
    }

    [Theory]
    [InlineData("127.0.0.1", "ws://localhost:64696/")]
    [InlineData("192.0.2.5", "ws://192.0.2.5:64696/")]
    [InlineData("::1", "ws://localhost:64696/")]
    [InlineData("2001:db8::5", "ws://[2001:db8::5]:64696/")]
    public void MapsAnnouncedPortAndSenderAddress(string address, string expected)
    {
        var source = new ResoniteLinkSession { SessionId = "S-one", SessionName = "World", LinkPort = 64696,
            LinkEndPoint = new IPEndPoint(IPAddress.Parse(address), 5555), LastUpdateTimestamp = Now };
        Assert.Equal(expected, ResoniteSessionDiscovery.Map(source, Now)!.Url);
        Assert.Equal("S-one", ResoniteSessionDiscovery.Map(source, Now)!.SessionId);
        Assert.Equal("ws://localhost:64696/", ResoniteSessionDiscovery.Map(source, Now, new HashSet<IPAddress> { IPAddress.Parse(address) })!.Url);
        Assert.Null(ResoniteSessionDiscovery.Map(source, Now.AddSeconds(26)));
        foreach (var port in new[] { -1, 0, 65536 })
        {
            source.LinkPort = port;
            Assert.Null(ResoniteSessionDiscovery.Map(source, Now));
        }
        source.LinkPort = 64696;
        source.SessionId = "";
        Assert.Null(ResoniteSessionDiscovery.Map(source, Now));
    }

    [Fact]
    public void ParsesDiscoveryOptionsAndPreservesUrlConfigurationPrecedence()
    {
        var args = ParsedArguments.Parse(["status", "--url", "auto", "--session", "My World", "--discovery-seconds", "15", "--json"]);
        Assert.Equal("auto", args.Option("url"));
        Assert.Equal("My World", args.Option("session"));
        Assert.Equal(15, args.IntOption("discovery-seconds", 12, 1, 60));
        Assert.True(args.Has("json"));
        var root = Path.Combine(Path.GetTempPath(), "resoloop-discovery-tests-" + Guid.NewGuid().ToString("N"));
        var explicitConfig = ConfigResolver.Resolve(root, new Dictionary<string, string?> { ["url"] = "ws://localhost:1234" },
            key => key == "RESONITE_LINK_URL" ? "auto" : null, root);
        Assert.Equal("ws://localhost:1234", explicitConfig.Config.ResoniteLinkUrl);
        var autoConfig = ConfigResolver.Resolve(root, new Dictionary<string, string?> { ["url"] = "auto" },
            key => key == "RESONITE_LINK_URL" ? "ws://localhost:1234" : null, root);
        Assert.Equal("auto", autoConfig.Config.ResoniteLinkUrl);
    }

    private sealed class FakeDiscovery(params DiscoveredResoniteSession[] sessions) : IResoniteSessionDiscovery
    {
        public int Calls { get; private set; }
        public TimeSpan Duration { get; private set; }
        public Task<IReadOnlyList<DiscoveredResoniteSession>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Calls++;
            Duration = duration;
            return Task.FromResult<IReadOnlyList<DiscoveredResoniteSession>>(sessions);
        }
    }
}

using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

/// <summary>
/// Connection generation (S1-I2, U3). A metadata answer that arrives after the connection changed must not
/// reach the memory or disk cache. Expected values are written by hand.
/// </summary>
public sealed class ConnectionGenerationTests : IDisposable
{
    private const string Light = "[FrooxEngine]FrooxEngine.Light";
    private static readonly Uri A = new("ws://127.0.0.1:1111/");
    private static readonly Uri B = new("ws://127.0.0.1:2222/");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "resoloop-gen-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static ScriptedMetadataLink Link()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Light], Connected = false };
        link.Definitions[Light] = ScriptedMetadataLink.Definition(Light);
        return link;
    }

    private ResoniteLinkClientAdapter Adapter(ScriptedMetadataLink link, string mode = "off") =>
        new(link, Timeout, new ReflectionCacheOptions(mode, _dir));

    private static ScriptedMetadataLink Gated()
    {
        var link = Link();
        link.DefinitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.DefinitionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return link;
    }

    [Fact]
    public async Task EachConnectionGetsANewGenerationOnSessionInfo()
    {
        var link = Link();
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var first = (await client.GetSessionInfoAsync()).ConnectionGeneration;
        link.Connected = false; // the first connection is gone
        await client.ConnectAsync(B, Timeout);
        var second = (await client.GetSessionInfoAsync()).ConnectionGeneration;
        Assert.False(string.IsNullOrEmpty(first));
        Assert.False(string.IsNullOrEmpty(second));
        Assert.NotEqual(first, second);
        Assert.Equal(second, (await client.GetSessionInfoAsync()).ConnectionGeneration);
    }

    [Fact]
    public async Task ConnectingAnAlreadyConnectedAdapterIsRejectedWhileAReadIsInFlight()
    {
        var link = Gated();
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var pending = client.DescribeComponentTypeAsync(Light);
        await link.DefinitionStarted!.Task.WaitAsync(Timeout);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.ConnectAsync(B, Timeout));
        Assert.Equal("ALREADY_CONNECTED", ex.Code);
        Assert.Equal(1, link.ConnectCalls);

        // The rejected connect changed nothing: the in-flight read still completes and is cached.
        link.DefinitionGate!.SetResult();
        Assert.Equal(Light, (await pending).FullTypeName);
        await client.DescribeComponentTypeAsync(Light);
        Assert.Equal(1, link.DefinitionCalls);
    }

    [Fact]
    public async Task LateAnswerAfterAGenerationChangeIsNotCachedInMemory()
    {
        var link = Gated();
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var pending = client.DescribeComponentTypeAsync(Light);
        await link.DefinitionStarted!.Task.WaitAsync(Timeout);

        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        link.DefinitionGate!.SetResult();
        var ex = await Assert.ThrowsAsync<RLoopException>(() => pending);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
        Assert.NotEqual(ExitCodes.NotFound, ex.ExitCode);

        // Nothing from the old connection was remembered: the new connection reads live again.
        var before = link.DefinitionCalls;
        Assert.Equal(Light, (await client.DescribeComponentTypeAsync(Light)).FullTypeName);
        Assert.Equal(before + 1, link.DefinitionCalls);
    }

    [Fact]
    public async Task LateAnswerAfterAGenerationChangeIsNotWrittenToTheDiskCache()
    {
        var link = Gated();
        await using var client = Adapter(link, "auto");
        await client.ConnectAsync(A, Timeout);
        var pending = client.DescribeComponentTypeAsync(Light);
        await link.DefinitionStarted!.Task.WaitAsync(Timeout);

        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        link.DefinitionGate!.SetResult();
        var ex = await Assert.ThrowsAsync<RLoopException>(() => pending);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
        var files = Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*", SearchOption.AllDirectories) : [];
        Assert.Empty(files);
    }

    [Fact]
    public async Task NewConnectionWalksTheTypeListAgainInsteadOfReusingTheOldOne()
    {
        var link = new ScriptedMetadataLink { Connected = false };
        link.Categories[""] = ScriptedMetadataLink.Level(["A"], []);
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        Assert.Equal(["A"], await client.SearchComponentTypesAsync("A", 10));
        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        await client.SearchComponentTypesAsync("A", 10);
        Assert.Equal(2, link.GetAllCalls);
    }
}

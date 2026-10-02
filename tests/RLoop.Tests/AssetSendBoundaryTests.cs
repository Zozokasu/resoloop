using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

public sealed class AssetSendBoundaryTests
{
    [Fact]
    public async Task MalformedMeshDoesNotReachApplySendBoundary()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{");
            await using var adapter = await ConnectedAdapter();
            using var guard = adapter.GuardApplyWrites(adapter.ObserveApplyConnection().Generation);
            adapter.BeginApplySend();

            var error = await Assert.ThrowsAsync<RLoopException>(() =>
                adapter.ImportAssetAsync(new("mesh", path), path));

            Assert.Equal("MESH_JSON_INVALID", error.Code);
            Assert.False(adapter.ApplySendStarted);
            Assert.False(adapter.ApplyResponseReceived);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancellationWhileMeshReadIsInProgressDoesNotReachApplySendBoundary()
    {
        await using var adapter = await ConnectedAdapter();
        using var cancellation = new CancellationTokenSource();
        using var guard = adapter.GuardApplyWrites(adapter.ObserveApplyConnection().Generation);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.ReadMeshFileAsync = async (_, token) =>
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "";
        };
        adapter.BeginApplySend();

        var import = adapter.ImportAssetAsync(new("mesh", "pending.mesh.json"), "pending.mesh.json", cancellation.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        Assert.False(adapter.ApplySendStarted);
        Assert.False(adapter.ApplyResponseReceived);
    }

    [Fact]
    public async Task UnsupportedAssetKindDoesNotReachApplySendBoundary()
    {
        await using var adapter = await ConnectedAdapter();
        using var guard = adapter.GuardApplyWrites(adapter.ObserveApplyConnection().Generation);
        adapter.BeginApplySend();

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            adapter.ImportAssetAsync(new("material", "resdb:///material"), "resdb:///material"));

        Assert.Equal("ASSET_KIND_UNSUPPORTED", error.Code);
        Assert.False(adapter.ApplySendStarted);
    }

    private static async Task<ResoniteLinkClientAdapter> ConnectedAdapter()
    {
        var adapter = new ResoniteLinkClientAdapter(new ScriptedMetadataLink { Connected = false },
            TimeSpan.FromSeconds(2), new ReflectionCacheOptions("off"));
        await adapter.ConnectAsync(new("ws://localhost:47610"), TimeSpan.FromSeconds(2));
        return adapter;
    }
}

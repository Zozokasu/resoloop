using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task BrokenMeshPreparationClearsPendingAndAllowsOtherProjectAtSameEndpoint()
    {
        var mesh = Path.Combine(_root, "broken.mesh.json");
        await File.WriteAllTextAsync(mesh, "{");
        var (document, _) = MeshAssetDocument("broken-mesh", mesh);
        var first = await AdapterBackedAssetClient();
        await using var adapter = first.AssetAdapter!;
        var second = new FakeResoniteClient { SessionUrl = first.SessionUrl };
        ShareSessionLock(first, second);
        var state = Path.Combine(_root, "broken.state.json");

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new WorldService(first).ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("MESH_JSON_INVALID", error.Code);
        Assert.False(first.ApplySendStarted);
        Assert.Empty(ReadPending(state));
        var otherProject = Document("other-project", "[]");
        var otherResult = await new WorldService(second).ApplyAsync(otherProject,
            new ApplyOptions(Path.Combine(_root, "other-project.state.json")));
        Assert.Equal(1, otherResult.SlotsCreated);
        Assert.Equal(0, first.Writes);
    }

    [Fact]
    public async Task CancellationDuringMeshReadClearsPendingAndAllowsOtherProjectAtSameEndpoint()
    {
        var mesh = Path.Combine(_root, "slow.mesh.json");
        await File.WriteAllTextAsync(mesh, "valid placeholder");
        var (document, _) = MeshAssetDocument("cancel-mesh", mesh);
        var first = await AdapterBackedAssetClient();
        await using var adapter = first.AssetAdapter!;
        var second = new FakeResoniteClient { SessionUrl = first.SessionUrl };
        ShareSessionLock(first, second);
        var state = Path.Combine(_root, "cancel.state.json");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.AssetAdapter!.ReadMeshFileAsync = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "";
        };
        using var cancellation = new CancellationTokenSource();

        var applying = new WorldService(first).ApplyAsync(document, new ApplyOptions(state), cancellation.Token);
        var readStarted = true;
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { readStarted = false; }
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<RLoopException>(() => applying);

        Assert.True(readStarted, "The mesh read seam must start before cancellation.");
        Assert.Equal("APPLY_CANCELLED", error.Code);
        Assert.False(first.ApplySendStarted);
        Assert.Empty(ReadPending(state));
        var otherProject = Document("other-project-after-cancel", "[]");
        var result = await new WorldService(second).ApplyAsync(otherProject,
            new ApplyOptions(Path.Combine(_root, "other-project-after-cancel.state.json")));
        Assert.Equal(1, result.SlotsCreated);
        Assert.Equal(0, first.Writes);
    }

    private async Task<FakeResoniteClient> AdapterBackedAssetClient()
    {
        var link = new ScriptedMetadataLink { Connected = false };
        var adapter = new ResoniteLinkClientAdapter(link, TimeSpan.FromSeconds(2), new ReflectionCacheOptions("off"));
        await adapter.ConnectAsync(new Uri("ws://127.0.0.2:47610"), TimeSpan.FromSeconds(2));
        var client = new FakeResoniteClient { SessionUrl = "ws://127.0.0.2:47610" };
        client.AssetAdapter = adapter;
        client.AssetImportEvidence = adapter;
        client.AssetImporter = (asset, path, token) => adapter.ImportAssetAsync(asset, path, token);
        return client;
    }

    private void ShareSessionLock(FakeResoniteClient first, FakeResoniteClient second)
    {
        var directory = Path.Combine(_root, "shared-session-locks");
        SessionLockTestIsolation.Share(first, directory);
        SessionLockTestIsolation.Share(second, directory);
    }

    private (ApplyDocument Document, string Source) MeshAssetDocument(string key, string meshPath)
    {
        var source = Path.Combine(_root, key + ".json");
        var contents = "{ \"schemaVersion\":\"1\", \"ownership\":{\"key\":\"" + key + "\"}, " +
            "\"slot\":{\"key\":\"root\",\"name\":\"Managed\",\"parent\":\"Root\"}, " +
            "\"assets\":{\"mesh\":{\"kind\":\"mesh\",\"source\":\"" + Path.GetFileName(meshPath) + "\"}} }";
        File.WriteAllText(source, contents);
        return (ApplyDocument.Load(source), source);
    }

    private static JsonArray ReadPending(string statePath)
    {
        var state = JsonNode.Parse(File.ReadAllText(statePath));
        Assert.NotNull(state);
        return Assert.IsType<JsonArray>(state["pending"]);
    }
}

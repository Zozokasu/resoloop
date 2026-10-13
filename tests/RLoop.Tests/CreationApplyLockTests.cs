using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyLockTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-locks-" + Guid.NewGuid().ToString("N"));

    public CreationApplyLockTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task BusyUrlLeaseWinsLockOrderAndNeverGetsStolen()
    {
        var statePath = Path.Combine(root, "url-busy.json");
        var lockDirectory = Path.Combine(root, "url-locks");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, lockDirectory);
        using var held = SessionWriteLock.Acquire(ApplySessionObservation.NormalizeUrl(client.SessionUrl), lockDirectory);

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath));

        Assert.Equal("APPLY_SESSION_BUSY", error.Code);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.RequestHistory);
    }

    [Fact]
    public async Task BusyStateWriterFailsAfterUrlLeaseWithoutMutation()
    {
        var statePath = Path.Combine(root, "state-busy.json");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, Path.Combine(root, "state-locks"));
        using var held = CheckpointFiles.AcquireWriter(statePath);

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath));

        Assert.Equal("APPLY_STATE_BUSY", error.Code);
        Assert.True(client.SessionReadCount > 0);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.RequestHistory);
    }

    [Fact]
    public async Task UrlBusyTakesPrecedenceWhenBothUrlAndStateLocksAreHeld()
    {
        var statePath = Path.Combine(root, "both-busy.json");
        var lockDirectory = Path.Combine(root, "both-locks");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, lockDirectory);
        using var url = SessionWriteLock.Acquire(ApplySessionObservation.NormalizeUrl(client.SessionUrl), lockDirectory);
        using var state = CheckpointFiles.AcquireWriter(statePath);

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath));

        Assert.Equal("APPLY_SESSION_BUSY", error.Code);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.RequestHistory);
    }

    [Fact]
    public async Task SessionUrlChangeAfterLeaseAcquisitionIsRejectedBeforeAnySend()
    {
        var statePath = Path.Combine(root, "session-url-changed.json");
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, Path.Combine(root, "changed-url-locks"));
        client.BeforeSessionRead = read =>
        {
            if (read != 2) return;
            client.SessionUrl = "ws://localhost:47611/";
            client.ConnectionGeneration = "changed-generation";
        };

        var error = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, statePath));

        Assert.Equal("CREATION_SESSION_UNPROVEN", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal(2, client.SessionReadCount);
        Assert.Empty(client.SendLog);
        Assert.Empty(client.RequestHistory);
    }

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string statePath) =>
        new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test",
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    public void Dispose() => Directory.Delete(root, recursive: true);
}

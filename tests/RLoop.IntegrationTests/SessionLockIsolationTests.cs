using System.Reflection;
using RLoop.Core;

namespace RLoop.IntegrationTests;

public sealed class SessionLockIsolationTests
{
    [Fact]
    public async Task DifferentClientsAtNormalizedEndpointContendInAssemblyTemporaryDirectory()
    {
        var endpoint = Guid.NewGuid().ToString("N");
        var first = Client("ws://localhost:47610/" + endpoint);
        var second = Client("WS://127.0.0.1:47610/" + endpoint);
        Assert.Equal(SessionLockTestIsolation.LockDirectory, SessionWriteLock.DirectoryForTests!(first));
        Assert.Equal(SessionLockTestIsolation.LockDirectory, SessionWriteLock.DirectoryForTests!(second));
        Assert.NotEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ResoLoop", "write-locks"), SessionLockTestIsolation.LockDirectory);

        string lockPath;
        using (var held = await SessionWriteLock.AcquireAsync(first, null, CancellationToken.None))
        {
            Assert.NotNull(held);
            lockPath = held.Path;
            Assert.Equal(SessionLockTestIsolation.LockDirectory, Path.GetDirectoryName(lockPath));
            var busy = await Assert.ThrowsAsync<RLoopException>(() =>
                SessionWriteLock.AcquireAsync(second, null, CancellationToken.None));
            Assert.Equal("APPLY_SESSION_BUSY", busy.Code);
            Assert.Equal(lockPath, busy.Context["lockFile"]);
        }
        using var next = await SessionWriteLock.AcquireAsync(second, null, CancellationToken.None);
        Assert.NotNull(next);
        Assert.Equal(lockPath, next.Path);
    }

    [Fact]
    public async Task ForeignPendingStateBlocksDifferentClientWhileOriginalProjectCanRecover()
    {
        var endpoint = Guid.NewGuid().ToString("N");
        var first = Client("ws://localhost:47610/" + endpoint);
        var second = Client("ws://127.0.0.1:47610/" + endpoint);
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-integration-pending-" + endpoint);
        var statePath = Path.Combine(directory, "first.state.json");
        try
        {
            ApplyStateStore.Save(statePath, new ApplyState
            {
                OwnershipKey = "first",
                Pending = [new() { Kind = "createSlot", Key = "root", OwnershipKey = "first" }]
            });
            string lockPath;
            using (var held = await SessionWriteLock.AcquireAsync(first, statePath, CancellationToken.None))
            {
                Assert.NotNull(held);
                held.RecordState(statePath);
                lockPath = held.Path;
            }
            var blocked = await Assert.ThrowsAsync<RLoopException>(() => SessionWriteLock.AcquireAsync(second,
                Path.Combine(directory, "second.state.json"), CancellationToken.None));
            Assert.Equal("APPLY_WRITE_UNVERIFIED", blocked.Code);
            Assert.Equal("previousStatePending", blocked.Context["reason"]);
            Assert.Equal(statePath, blocked.Context["stateFile"]);
            Assert.Equal(lockPath, blocked.Context["lockFile"]);
            Assert.Single(ApplyStateStore.Load(statePath, "first").Pending);
            using var original = await SessionWriteLock.AcquireAsync(second, statePath, CancellationToken.None);
            Assert.NotNull(original);
            Assert.Equal(lockPath, original.Path);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
            if (Directory.Exists(directory)) Directory.Delete(directory); // Exact test-created directory, no recursion.
        }
    }

    private static IResoniteClient Client(string url)
    {
        var client = DispatchProxy.Create<IResoniteClient, SessionInfoOnlyClient>();
        ((SessionInfoOnlyClient)client).Url = url;
        return client;
    }

    // This offline client exposes session metadata only; any connection or world call fails.
    public class SessionInfoOnlyClient : DispatchProxy
    {
        public string Url { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IResoniteClient.GetSessionInfoAsync) => Task.FromResult(new SessionInfo(Url, true, null, null, null)),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new InvalidOperationException("Offline lock test must not connect or call the world.")
        };
    }
}

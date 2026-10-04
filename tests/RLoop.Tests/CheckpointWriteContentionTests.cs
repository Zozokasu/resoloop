using System.Diagnostics;
using System.Text.Json;
using RLoop.Core;
using Xunit;

namespace RLoop.Tests;

public sealed class CheckpointWriteContentionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-checkpoint-write-" + Guid.NewGuid().ToString("N"));

    public CheckpointWriteContentionTests() => Directory.CreateDirectory(root);

    [WindowsCheckpointFact]
    public async Task AtomicReplacementWaitsForDeleteSharingToBeReleased()
    {
        var path = Path.Combine(root, "released.json");
        const string previous = "{\"pending\":[\"intent-before-world-command\"]}";
        const string next = "{\"pending\":[],\"settled\":true}";
        CheckpointFiles.Write(path, previous);
        Task write;
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            write = Task.Run(() => CheckpointFiles.Write(path, next));
            try
            {
                var observedTemporary = await WaitForTemporaryOrCompletion(path, write);
                Assert.True(observedTemporary, "Writer must reach its real filesystem replacement while the destination denies delete sharing.");
                await Task.Delay(300);
                Assert.False(write.IsCompleted, "Transient contention must keep the atomic write pending until delete sharing is released.");
                Assert.Equal(previous, File.ReadAllText(path));
            }
            finally
            {
                blocker.Dispose();
                // Observe task failures even if an assertion above fails; never leave a background writer.
                await write.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        Assert.Equal(next, CheckpointFiles.Read(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [WindowsCheckpointFact]
    public async Task PersistentDeleteContentionRetainsJournalAndWriteFailureDetails()
    {
        var path = Path.Combine(root, "persistent.json");
        var previous = new ApplyState { OwnershipKey = "checkpoint-test", Pending = [new() { Kind = "createSlot", Key = "root", OwnershipKey = "checkpoint-test" }] };
        ApplyStateStore.Save(path, previous);
        var previousBytes = File.ReadAllBytes(path);
        using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var timer = Stopwatch.StartNew();
        var error = await Task.Run(() => Assert.Throws<RLoopException>(() =>
            ApplyStateStore.Save(path, new ApplyState { OwnershipKey = "checkpoint-test" }))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
        Assert.Equal(ExitCodes.OperationFailed, error.ExitCode);
        var cause = Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(32, cause.HResult & 0xffff);
        Assert.Contains(nameof(CheckpointFiles), cause.StackTrace!);
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(8));
        Assert.Equal(previousBytes, File.ReadAllBytes(path));
        Assert.Single(ApplyStateStore.Load(path, "checkpoint-test").Pending);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [WindowsCheckpointFact]
    public void PermissionFailureKeepsOldSnapshotAndCleansTemporary()
    {
        var path = Path.Combine(root, "readonly.json");
        CheckpointFiles.Write(path, "{\"old\":true}");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var error = Assert.Throws<UnauthorizedAccessException>(() => CheckpointFiles.Write(path, "{\"new\":true}"));
            Assert.Equal(5, error.HResult & 0xffff);
            Assert.Equal("{\"old\":true}", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void CompleteSnapshotReplacementRetainsOpenReadersAndWriterLease()
    {
        var path = Path.Combine(root, "snapshot.json");
        const string previous = "{\"sequence\":1,\"pending\":[\"intent\"]}";
        var next = JsonSerializer.Serialize(new { sequence = 2, pending = Array.Empty<string>(), payload = new string('x', 100000) });
        using var lease = CheckpointFiles.AcquireWriter(path);
        CheckpointFiles.Write(path, previous);
        using var oldSnapshot = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        CheckpointFiles.Write(path, next);
        using var reader = new StreamReader(oldSnapshot);
        Assert.Equal(previous, reader.ReadToEnd());
        Assert.Equal(next, CheckpointFiles.Read(path));
        Assert.Equal("APPLY_STATE_BUSY", Assert.Throws<RLoopException>(() => CheckpointFiles.AcquireWriter(path)).Code);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    private static async Task<bool> WaitForTemporaryOrCompletion(string path, Task write)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Length > 0) return true;
            if (write.IsCompleted) return false;
            await Task.Delay(10);
        }
        return false;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public sealed class WindowsCheckpointFactAttribute : FactAttribute
{
    public WindowsCheckpointFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows delete-sharing and native replacement error semantics are unverified on this platform.";
    }
}

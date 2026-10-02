using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RLoop.Core;

// Cooperation between this user's local CLI processes, not a world/session identity proof.
internal sealed class SessionWriteLock : IDisposable
{
    private readonly FileStream handle;
    internal string Path { get; }
    internal string NormalizedUrl { get; }
    internal static Func<IResoniteClient, string>? DirectoryForTests;
    internal static readonly AsyncLocal<Action<string>?> RecordFault = new();

    private SessionWriteLock(FileStream handle, string path, string url) { this.handle = handle; Path = path; NormalizedUrl = url; }

    internal static async Task<SessionWriteLock?> AcquireAsync(IResoniteClient client, string? currentState,
        CancellationToken ct)
    {
        var session = await client.GetSessionInfoAsync(ct);
        if (!new Uri(session.Url).Scheme.StartsWith("ws", StringComparison.OrdinalIgnoreCase)) return null;
        var observed = (client as IApplySessionObservation)?.ObserveApplySession() ?? ApplySessionObservation.Observe(session.Url);
        var lease = Acquire(observed.NormalizedUrl, DirectoryForTests?.Invoke(client));
        try { lease.CheckPreviousState(currentState); return lease; }
        catch { lease.Dispose(); throw; }
    }

    internal static SessionWriteLock Acquire(string url, string? directory = null)
    {
        var normalized = ApplySessionObservation.NormalizeUrl(url);
        directory ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResoLoop", "write-locks");
        var path = System.IO.Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))) + ".lock");
        try
        {
            Directory.CreateDirectory(directory);
            return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), path, normalized);
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        {
            throw new RLoopException("APPLY_SESSION_BUSY", "Another ResoLoop writer holds this URL's lock. Wait for it to finish.",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["normalizedUrl"] = normalized, ["lockFile"] = path }, innerException: e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw PersistenceFailure(path, e); }
    }

    internal void CheckPreviousState(string? currentState)
    {
        string? previous = null;
        try
        {
            handle.Position = 0;
            using var reader = new StreamReader(handle, Encoding.UTF8, true, 1024, leaveOpen: true);
            var content = reader.ReadToEnd();
            if (content.Length == 0) return; // Newly created lock, no writer has registered a state.
            previous = JsonSerializer.Deserialize<string>(content);
            if (string.IsNullOrWhiteSpace(previous) || !System.IO.Path.IsPathFullyQualified(previous)) throw new JsonException("Invalid state location.");
            if (currentState is not null && string.Equals(previous, System.IO.Path.GetFullPath(currentState), StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                using var json = JsonDocument.Parse(CheckpointFiles.Read(previous));
                var ownership = json.RootElement.GetProperty("ownershipKey").GetString();
                if (string.IsNullOrWhiteSpace(ownership)) throw new JsonException("Missing state ownership.");
                var state = ApplyStateStore.Load(previous, ownership, requireState: true);
                if (state.Pending.Count > 0 || ApplyPendingDiscard.LegacyPending(state).Any())
                    throw Blocked(previous, "previousStatePending");
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException ||
                e is RLoopException { Code: "APPLY_STATE_NOT_FOUND", InnerException: FileNotFoundException or DirectoryNotFoundException })
            {
                // Only a missing file/directory permits continuation. File.Exists also hides access errors.
                return;
            }
        }
        catch (RLoopException e) when (e.Code == "APPLY_WRITE_UNVERIFIED") { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or RLoopException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw Blocked(previous, "previousStateUnreadable", e); }
    }

    internal void RecordState(string statePath)
    {
        statePath = System.IO.Path.GetFullPath(statePath);
        try
        {
            RecordFault.Value?.Invoke(statePath);
            // Durably invalidate the old pointer before replacing it. A torn record stops the next holder.
            handle.Position = 0;
            handle.WriteByte((byte)'!');
            handle.Flush(flushToDisk: true);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(statePath));
            handle.Position = 0;
            handle.Write(bytes);
            handle.SetLength(bytes.Length);
            handle.Flush(flushToDisk: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw PersistenceFailure(statePath, e); }
    }

    private RLoopException Blocked(string? state, string reason, Exception? inner = null) => new(
        "APPLY_WRITE_UNVERIFIED", "The previous writer's state must be inspected before new writes to this URL.", ExitCodes.OperationFailed,
        new Dictionary<string, object?> { ["reason"] = reason, ["stateFile"] = state, ["lockFile"] = Path },
        ["Repair that state or resolve pending evidence in the original project; inspect the exact targets before --discard-pending OPERATION_ID --yes. If the state is permanently lost, verify that no ResoLoop writes are running and inspect the live world before deleting the lock file reported in lockFile. Never delete or steal a lock held by an active writer. Do not repair another project's state automatically."], inner);
    private static RLoopException PersistenceFailure(string path, Exception e) => new("APPLY_STATE_WRITE_FAILED",
        "Could not persist the session lock or its state location; no new write is allowed.", ExitCodes.OperationFailed,
        new Dictionary<string, object?> { ["path"] = path, ["reason"] = "sessionLockPersistenceFailed" }, innerException: e);
    public void Dispose() => handle.Dispose();
}

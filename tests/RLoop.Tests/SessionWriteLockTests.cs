using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RLoop.Core;
using RLoop.Cli;
using Xunit.Abstractions;

namespace RLoop.Tests;

public sealed class SessionWriteLockTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-session-lock-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ws://localhost", "WS://127.0.0.1:80/")]
    [InlineData("ws://LOCALHOST:047610", "ws://127.0.0.1:47610/")]
    [InlineData("wss://localhost", "wss://LOCALHOST:443/")]
    [InlineData("ws://localhost:47610", "ws://[::1]:47610/")]
    public void S3UrlVariantsShareExclusiveHandle(string first, string second)
    {
        using var holder = SessionWriteLock.Acquire(first, root);
        var e = Assert.Throws<RLoopException>(() => SessionWriteLock.Acquire(second, root));
        Assert.Equal("APPLY_SESSION_BUSY", e.Code);
        Assert.Equal(7, e.ExitCode);
        Assert.Equal(holder.Path, e.Context["lockFile"]);
    }

    [Fact]
    public void S3StateAndSessionLocksRemainIndependent()
    {
        var path = Path.Combine(root, "state.json");
        using var state = CheckpointFiles.AcquireWriter(path);
        using var session = SessionWriteLock.Acquire("ws://localhost", root);
        Assert.Equal("APPLY_STATE_BUSY", Assert.Throws<RLoopException>(() => CheckpointFiles.AcquireWriter(path)).Code);
        Assert.Equal("APPLY_SESSION_BUSY", Assert.Throws<RLoopException>(() => SessionWriteLock.Acquire("ws://localhost", root)).Code);
        using var anotherState = CheckpointFiles.AcquireWriter(Path.Combine(root, "other-state.json"));
        using var anotherSession = SessionWriteLock.Acquire("ws://localhost:12345", root);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("\"partial")]
    [InlineData("null")]
    [InlineData("\"relative-state.json\"")]
    public void S3TornLocationRecordBlocksNewWrites(string content)
    {
        string path;
        using (var first = SessionWriteLock.Acquire("ws://localhost", root)) path = first.Path;
        File.WriteAllText(path, content);
        using var next = SessionWriteLock.Acquire("ws://localhost", root);
        var e = Assert.Throws<RLoopException>(() => next.CheckPreviousState(null));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", e.Code);
        Assert.Equal("previousStateUnreadable", e.Context["reason"]);
        Assert.Equal(path, e.Context["lockFile"]);
        Assert.True(e.Context.ContainsKey("stateFile"));
        var suggestion = Assert.Single(e.Suggestions);
        Assert.Contains("Repair that state or resolve pending evidence in the original project", suggestion);
        Assert.Contains("permanently lost", suggestion);
        Assert.Contains("no ResoLoop writes are running", suggestion);
        Assert.Contains("inspect the live world", suggestion);
        Assert.Contains("lockFile", suggestion);
        Assert.Contains("active writer", suggestion);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("invalid")]
    [InlineData("missing")]
    public void S3OwnPreviousStateRemainsAvailableForProjectRecovery(string fault)
    {
        var path = Path.Combine(root, "own.state.json");
        using var lease = SessionWriteLock.Acquire("ws://localhost", root);
        ApplyStateStore.Save(path, new ApplyState { OwnershipKey = "own", Pending = [new() { Kind = "createSlot", Key = "root", OwnershipKey = "own" }] });
        lease.RecordState(path);
        if (fault == "invalid") File.WriteAllText(path, "{");
        if (fault == "missing") File.Delete(path);
        // The owning project retains responsibility for validating/reconciling its own state.
        lease.CheckPreviousState(path);
    }

    [Fact]
    public async Task S3RealProcessKillReleasesHandleButPreviousPendingStillBlocks()
    {
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, "holder-state.json");
        ApplyStateStore.Save(statePath, new ApplyState { OwnershipKey = "holder", Pending = [new() { Kind = "createSlot", Key = "root", OwnershipKey = "holder" }] });
        string lockPath;
        using (var lease = SessionWriteLock.Acquire("ws://localhost", root)) lockPath = lease.Path;
        var script = "$h = [System.IO.File]::Open('" + Quote(lockPath) + "', [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); " +
            "$b = [System.Text.Encoding]::UTF8.GetBytes('" + Quote(JsonSerializer.Serialize(statePath)) + "'); " +
            "$h.Write($b, 0, $b.Length); $h.SetLength($b.Length); $h.Flush($true); [Console]::WriteLine('held'); [Console]::ReadLine() | Out-Null; $h.Dispose()";
        using var holder = StartPowerShell(script);
        try
        {
            Assert.Equal("held", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            output.WriteLine($"Holder PID {holder.Id}; contender testhost PID {Environment.ProcessId}; lock {lockPath}");
            var e = Assert.Throws<RLoopException>(() => SessionWriteLock.Acquire("ws://127.0.0.1:80", root));
            Assert.Equal("APPLY_SESSION_BUSY", e.Code);
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var next = SessionWriteLock.Acquire("WS://LOCALHOST:80", root);
            var pending = Assert.Throws<RLoopException>(() => next.CheckPreviousState(Path.Combine(root, "other-state.json")));
            Assert.Equal("APPLY_WRITE_UNVERIFIED", pending.Code);
            Assert.Equal("previousStatePending", pending.Context["reason"]);
            Assert.Equal(statePath, pending.Context["stateFile"]);
            next.CheckPreviousState(statePath); // Original project is allowed to inspect its own journal.
            output.WriteLine("After kill: handle acquired; foreign pending blocked; original project may reconcile.");
        }
        finally { if (!holder.HasExited) { holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(); } }
    }

    // ROADMAP-9 unit 2: the pending rule is chosen by the kind of the state the lock points to.
    private static readonly FluxDeploySessionRecord FluxSession = new("ws://localhost/", null, "unknown");
    private static FluxDeployPending FluxPending() => FluxDeployPending.Begin("main", FluxSession, "Reso_Parent", "Main",
        null, [], "HASH", new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void R9FluxStateWithPendingBlocksOtherWritersLikeAnApplyPending()
    {
        var fluxState = Path.Combine(root, "flux", "flux.json");
        FluxDeployStateStore.AddPending(fluxState, FluxPending());
        using var lease = SessionWriteLock.Acquire("ws://localhost", root);
        lease.RecordState(fluxState);

        foreach (var other in new[] { Path.Combine(root, "other.state.json"), null })
        {
            var e = Assert.Throws<RLoopException>(() => lease.CheckPreviousState(other));
            Assert.Equal("APPLY_WRITE_UNVERIFIED", e.Code);
            Assert.Equal(7, e.ExitCode);
            Assert.Equal("previousFluxStatePending", e.Context["reason"]);
            Assert.Equal(Path.GetFullPath(fluxState), e.Context["stateFile"]);
            Assert.Equal(lease.Path, e.Context["lockFile"]);
            Assert.Contains("--discard-pending OPERATION_ID --yes", Assert.Single(e.Suggestions));
        }
        // The owner of the Flux state keeps responsibility for its own pending record.
        lease.CheckPreviousState(fluxState);
    }

    [Fact]
    public void R9FluxStateWithoutPendingLetsOtherWritersContinue()
    {
        var fluxState = Path.Combine(root, "flux", "flux.json");
        var pending = FluxPending();
        FluxDeployStateStore.AddPending(fluxState, pending);
        FluxDeployStateStore.ResolvePending(fluxState, pending.OperationId, new FluxDeployModuleRecord
            { ParentSlotId = "Reso_Parent", RootSlotId = "Reso_Root", ModuleName = "Main", InputHash = "HASH" });
        using var lease = SessionWriteLock.Acquire("ws://localhost", root);
        lease.RecordState(fluxState);

        lease.CheckPreviousState(Path.Combine(root, "other.state.json"));
        lease.CheckPreviousState(null);

        // Discarding the pending record of a blocked state unblocks it, without a client.
        FluxDeployStateStore.AddPending(fluxState, pending);
        Assert.Equal("previousFluxStatePending", Assert.Throws<RLoopException>(() => lease.CheckPreviousState(null)).Context["reason"]);
        FluxDeployPendingDiscard.Discard(fluxState, pending.OperationId, confirmed: true);
        lease.CheckPreviousState(null);
    }

    [Fact]
    public void R9MissingFluxStateLetsOtherWritersContinue()
    {
        var fluxState = Path.Combine(root, "flux", "flux.json");
        FluxDeployStateStore.AddPending(fluxState, FluxPending());
        using var lease = SessionWriteLock.Acquire("ws://localhost", root);
        lease.RecordState(fluxState);
        File.Delete(fluxState);

        lease.CheckPreviousState(null); // Same rule as a missing apply state.
    }

    [Theory]
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 3, "session": null, "modules": {}, "pending": [] }""")] // newer Flux state
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": {} }""")] // no pending list
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": {}, "pending": [], "future": true }""")]
    [InlineData("""{ "kind": "something-else", "schemaVersion": 2, "pending": [] }""")] // unknown kind
    [InlineData("""{ "kind": null, "ownershipKey": "own", "schemaVersion": 3 }""")]
    [InlineData("""{ "schemaVersion": 1, "parentSlotId": "P", "sessionId": "1", "modules": {} }""")] // Flux v1: no kind, no ownershipKey
    [InlineData("""{ "schemaVersion": 2, "session": null, "modules": {}, "pending": [] }""")]
    [InlineData("""{ "ownershipKey": "own", "schemaVersion": 4, "slots": {}, "components": {} }""")] // newer apply state
    [InlineData("{")]
    [InlineData("[]")]
    public void R9StateOfUnknownKindOrVersionBlocksAsUnreadable(string content)
    {
        var state = Path.Combine(root, "previous.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(state, content);
        using var lease = SessionWriteLock.Acquire("ws://localhost", root);
        lease.RecordState(state);

        var e = Assert.Throws<RLoopException>(() => lease.CheckPreviousState(Path.Combine(root, "other.state.json")));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", e.Code);
        Assert.Equal("previousStateUnreadable", e.Context["reason"]);
        Assert.Equal(state, e.Context["stateFile"]);
        lease.CheckPreviousState(state); // The owner may still repair its own state.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R9ApplyStateKeepsItsPendingRule(bool pending)
    {
        var state = Path.Combine(root, "apply.state.json");
        ApplyStateStore.Save(state, new ApplyState { OwnershipKey = "own",
            Pending = pending ? [new() { Kind = "createSlot", Key = "root", OwnershipKey = "own" }] : [] });
        using var lease = SessionWriteLock.Acquire("ws://localhost", root);
        lease.RecordState(state);

        if (!pending) { lease.CheckPreviousState(null); return; }
        var e = Assert.Throws<RLoopException>(() => lease.CheckPreviousState(null));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", e.Code);
        Assert.Equal("previousStatePending", e.Context["reason"]);
    }

    private static string Quote(string value) => value.Replace("'", "''");
    private static Process StartPowerShell(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    [Theory]
    [InlineData("slot create", true)]
    [InlineData("slot set", true)]
    [InlineData("slot delete", true)]
    [InlineData("component add", true)]
    [InlineData("component set", true)]
    [InlineData("component remove", true)]
    [InlineData("component list", false)]
    [InlineData("component inspect", false)]
    [InlineData("type describe", false)]
    [InlineData("snapshot create", false)]
    public void S3DirectEntryClassification(string command, bool write) =>
        Assert.Equal(write, Program.IsDirectWrite(ParsedArguments.Parse(command.Split(' '))));

    // ROADMAP-9 unit 4a (replaces S3's "flux deploy is not a write"): ProtoGraph deployments write to the world and
    // take the URL lock, but the deploy guard takes it once per deployment. The CLI's generic path does not take it as
    // well, which would make the guard's own acquisition APPLY_SESSION_BUSY. Commands that do not reach the world
    // (build, check, watch of a .pg source, discarding a pending record) take no lock.
    [Theory]
    [InlineData("slot create", "Cli")]
    [InlineData("component remove", "Cli")]
    [InlineData("flux deploy --project p --module m --parent P", "FluxGuard")]
    [InlineData("flux deploy-manifest modules.json", "FluxGuard")]
    [InlineData("flux watch modules.json", "FluxGuard")]
    [InlineData("flux watch Main.pg", "None")]
    [InlineData("flux build Main.pg", "None")]
    [InlineData("flux check Main.pg", "None")]
    [InlineData("flux deploy --project p --module m --discard-pending OP --yes", "None")]
    [InlineData("flux deploy-manifest modules.json --discard-pending OP --yes", "None")]
    [InlineData("type describe", "None")]
    public void R9SessionLockEntryClassification(string command, string expected)
    {
        var entry = Enum.Parse<Program.SessionLockEntry>(expected);
        var args = ParsedArguments.Parse(command.Split(' '));
        Assert.Equal(entry, Program.SessionLockEntryOf(args));
        Assert.Equal(entry == Program.SessionLockEntry.Cli, Program.IsDirectWrite(args));
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        // Delete only the exact, independently created test directory.
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(full, recursive: true);
    }
}

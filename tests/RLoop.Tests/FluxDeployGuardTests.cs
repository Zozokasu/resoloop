using System.Diagnostics;
using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

// ROADMAP-9 unit 3a: the deploy guard. Expected values come from the decisions (P1-b, P2, P3, P6-P8, P10) and the
// task contract, not from the guard's output. The world and the deployer are fakes; nothing connects anywhere.
public sealed partial class FluxDeployGuardTests : IDisposable
{
    private const string Parent = "Reso_Parent";
    private const string Unrelated = "Reso_Other";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-flux-guard-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWorld _world = new();
    private readonly FakeDeployer _deployer;
    private string StatePath => Path.Combine(_root, ".resoloop", "flux-state", "flux.json");
    private string LockDirectory => SessionWriteLock.DirectoryForTests!(_world);

    public FluxDeployGuardTests()
    {
        Directory.CreateDirectory(_root);
        _deployer = new FakeDeployer(_world);
    }

    public void Dispose()
    {
        FluxDeployStateStore.SaveFault.Value = null;
        if (!Directory.Exists(_root)) return;
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(full, recursive: true);
    }

    private FluxDeployGuard Guard() => new(_world, _deployer) { DeadlineGrace = TimeSpan.FromMilliseconds(50) };

    private FluxDeployGuardRequest Request(string parent = Parent, string? expectedName = null, string? url = null,
        string hash = "HASH-1", TimeSpan? deadline = null) => new(
        _root, "Main.pg", "main", parent, new Uri(url ?? _world.Url), null, null,
        new Dictionary<string, string> { ["Speed"] = "Reso_Field1" }, new Dictionary<string, string> { ["Angle"] = "Reso_Field2" },
        hash, null, StatePath, "1.9.0", deadline ?? TimeSpan.FromSeconds(30), expectedName);

    private Task<FluxDeployGuardResult> Deploy(FluxDeployGuardRequest? request = null) => Guard().DeployModuleAsync(request ?? Request());
    private async Task<RLoopException> Fails(FluxDeployGuardRequest? request = null) =>
        await Assert.ThrowsAsync<RLoopException>(() => Deploy(request));

    private string? StateText() => File.Exists(StatePath) ? File.ReadAllText(StatePath) : null;
    private FluxDeployState State() => FluxDeployStateStore.Load(StatePath);

    /// <summary>The stop wrote nothing: the deployer's execute step never ran, the world and the state file are as before.</summary>
    private async Task<RLoopException> StopsWithoutWriting(string code, int exitCode, FluxDeployGuardRequest? request = null)
    {
        var world = _world.Snapshot();
        var state = StateText();
        var executions = _deployer.Executions.Count;
        var e = await Fails(request);
        Assert.Equal(code, e.Code);
        Assert.Equal(exitCode, e.ExitCode);
        Assert.Equal(executions, _deployer.Executions.Count);
        Assert.Equal(world, _world.Snapshot());
        Assert.Equal(0, _world.WriteCalls);
        Assert.Equal(state, StateText());
        return e;
    }

    /// <summary>The deployment is left pending: recorded in the state, reported with its ID and how to discard it, and never settled.</summary>
    private FluxDeployPending AssertPendingKept(RLoopException e, string code, FluxDeployStage stage, FluxDeploySendStatus sendStatus,
        params string[] candidates)
    {
        Assert.Equal(code, e.Code);
        Assert.Equal(7, e.ExitCode);
        Assert.Equal("kept", e.Context["pending"]);
        Assert.Equal("possible", e.Context["worldWrites"]);
        var pending = Assert.Single(State().Pending);
        Assert.Equal(pending.OperationId, e.Context["operationId"]);
        Assert.Equal(Path.GetFullPath(StatePath), e.Context["stateFile"]);
        Assert.Equal($"--discard-pending {pending.OperationId} --yes", e.Context["discardPending"]);
        Assert.Contains($"--discard-pending {pending.OperationId} --yes", Assert.Single(e.Suggestions));
        Assert.Equal(stage, pending.Stage);
        Assert.Equal(sendStatus, pending.SendStatus);
        Assert.Equal(stage, e.Context["stage"]);
        Assert.Equal(candidates, pending.CandidateRootIds);
        Assert.Equal(candidates, Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateRootIds"]));
        Assert.Equal("main", pending.Module);
        Assert.Equal("Main", pending.ModuleName);
        Assert.Equal(Parent, pending.ParentSlotId);
        return pending;
    }

    /// <summary>A second run stops on the pending record without calling the deployer again; other writers on the URL stop too.</summary>
    private async Task AssertRerunBlocked()
    {
        var executions = _deployer.Executions.Count;
        var world = _world.Snapshot();
        var again = await Fails();
        Assert.Equal("FLUX_DEPLOY_PENDING", again.Code);
        Assert.Equal(executions, _deployer.Executions.Count);
        Assert.Equal(world, _world.Snapshot());
        // The guard released the URL lock, and the lock points at the Flux state whose pending record blocks others (F8).
        using var lease = SessionWriteLock.Acquire(_world.Url, LockDirectory);
        var blocked = Assert.Throws<RLoopException>(() => lease.CheckPreviousState(null));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", blocked.Code);
        Assert.Equal("previousFluxStatePending", blocked.Context["reason"]);
    }

    private void Discard(FluxDeployPending pending) =>
        FluxDeployPendingDiscard.Discard(StatePath, pending.OperationId, confirmed: true);

    private async Task<string> DeployedOnce()
    {
        var first = await Deploy();
        Assert.Equal("Flux_1", first.NewRootSlotId);
        return first.NewRootSlotId;
    }

    // ---- Normal paths --------------------------------------------------------------------------------------

    [Fact]
    public async Task NewDeploymentSettlesTheStateAndReleasesTheLock()
    {
        var result = await Deploy();

        Assert.Equal("Flux_1", result.NewRootSlotId);
        Assert.Equal("Main", result.ModuleName);
        Assert.Equal(FluxDeployRecordBasis.None, result.Preconditions.RecordBasis);
        Assert.Null(result.Preconditions.PreviousRootSlotId);
        Assert.Empty(result.Preconditions.SameNameChildIds);
        Assert.Equal([Unrelated], result.Preconditions.ObservedChildIds);
        Assert.Equal("matched", result.Preconditions.IdentityStatus);
        // Unit 3b: the request drives one output and binds two ports. The parent and its children were read in
        // full without another writer, and both bindings were read back from the new module.
        Assert.Equal("observedRangeClear", result.Preconditions.WriterCheck);
        Assert.Equal("verified", result.BindingReadback);

        var sent = Assert.Single(_deployer.Executions);
        Assert.Equal("Main", sent.ExpectedModuleName); // declared name from the preparation, not the module path
        Assert.Null(sent.PreviousRootSlotId);
        Assert.Equal(Parent, sent.ParentSlotId);
        Assert.Equal(TimeSpan.FromSeconds(30), sent.Deadline);

        var state = State();
        Assert.Empty(state.Pending);
        var record = state.Modules["main"];
        Assert.Equal(("Flux_1", Parent, "Main", "HASH-1", "1.9.0", "deployed"),
            (record.RootSlotId, record.ParentSlotId, record.ModuleName, record.InputHash, record.SdkVersion, record.Origin));
        Assert.Equal([new FluxDeployBindingRecord("Speed", "source", "Reso_Field1"), new FluxDeployBindingRecord("Angle", "drive", "Reso_Field2")],
            record.Bindings);
        Assert.Equal(new FluxDeploySessionRecord("ws://localhost:47610/", "S-world-1", "matched"), state.Session);
        Assert.Equal(0, _world.WriteCalls); // the guard's own connection only reads

        // The lock is free again, still points at the Flux state (P10), and does not block other writers.
        string lockFile;
        using (var lease = SessionWriteLock.Acquire(_world.Url, LockDirectory))
        {
            lockFile = lease.Path;
            lease.CheckPreviousState(null);
        }
        Assert.Equal(Path.GetFullPath(StatePath), JsonSerializer.Deserialize<string>(File.ReadAllText(lockFile, System.Text.Encoding.UTF8)));
    }

    [Fact]
    public async Task RecordedModuleIsReplacedByItsExactIdWithoutConfirmation()
    {
        var old = await DeployedOnce();

        var result = await Deploy(Request(hash: "HASH-2"));

        Assert.Equal("Flux_2", result.NewRootSlotId);
        Assert.Equal(FluxDeployRecordBasis.Deployed, result.Preconditions.RecordBasis);
        Assert.Equal(old, result.Preconditions.PreviousRootSlotId);
        Assert.Equal(FluxPreviousRootRemoval.Removed, result.PreviousRootRemoval);
        Assert.Equal(old, _deployer.Executions[1].PreviousRootSlotId);
        Assert.Equal("Flux_2", State().Modules["main"].RootSlotId);
        Assert.Equal("HASH-2", State().Modules["main"].InputHash);
        Assert.Empty(State().Pending);
        Assert.Equal(["Flux_2"], _world.ChildrenNamed(Parent, "Main"));
        Assert.Contains(Unrelated, _world.ChildIds(Parent));
    }

    [Fact]
    public async Task RecordedRootThatNoLongerExistsIsTreatedAsNoRecord()
    {
        var old = await DeployedOnce();
        _world.Remove(old);

        var result = await Deploy();

        Assert.Equal(FluxDeployRecordBasis.RecordedRootGone, result.Preconditions.RecordBasis);
        Assert.Null(_deployer.Executions[1].PreviousRootSlotId);
        Assert.Equal("Flux_2", State().Modules["main"].RootSlotId);
    }

    [Fact]
    public async Task WarningsDoNotStopTheDeployment()
    {
        _deployer.Preparation = FakeDeployer.Prepared("Main", Diagnostic("warning", "unused value"));
        Assert.Equal("Flux_1", (await Deploy()).NewRootSlotId);
    }

    [Fact]
    public async Task LoopbackAliasOfTheLockedUrlIsTheSameTarget()
    {
        Assert.Equal("Flux_1", (await Deploy(Request(url: "ws://127.0.0.1:47610"))).NewRootSlotId);
    }

    [Fact]
    public async Task UnmatchedSessionIdIsRecordedAsUnknownAndDoesNotStop()
    {
        _world.SessionId = null;

        var result = await Deploy();

        Assert.Equal("unknown", result.Preconditions.IdentityStatus);
        Assert.Equal(new FluxDeploySessionRecord("ws://localhost:47610/", null, "unknown"), State().Session);
    }

    // ---- Stops that write nothing --------------------------------------------------------------------------

    [Fact]
    public async Task ErrorDiagnosticStopsBeforeTheLock()
    {
        _deployer.Preparation = new FluxDeployPreparation(false, null, [Diagnostic("error", "Unknown name 'x'"), Diagnostic("warning", "w")],
            null, "Unknown name 'x'", "out", "");

        var e = await StopsWithoutWriting("FLUX_COMPILE_FAILED", 9);

        Assert.Equal(_deployer.Preparation.Diagnostics, e.Context["diagnostics"]);
        Assert.Equal(1, e.Context["errorCount"]);
        Assert.Equal("none", e.Context["worldWrites"]);
        Assert.Null(StateText());
        Assert.Empty(Directory.Exists(LockDirectory) ? Directory.GetFiles(LockDirectory) : []);
    }

    [Fact]
    public async Task ErrorDiagnosticStopsEvenWhenThePreparationClaimsSuccess()
    {
        _deployer.Preparation = FakeDeployer.Prepared("Main", Diagnostic("error", "type mismatch"));
        await StopsWithoutWriting("FLUX_COMPILE_FAILED", 9);
    }

    [Fact]
    public async Task DeclaredNameOtherThanTheExpectedNameStops()
    {
        _deployer.Preparation = FakeDeployer.Prepared("Bar");

        var e = await StopsWithoutWriting("FLUX_MODULE_NAME_MISMATCH", 6, Request(expectedName: "Foo"));

        Assert.Equal("Foo", e.Context["expectedModuleName"]);
        Assert.Equal("Bar", e.Context["declaredModuleName"]);
    }

    [Fact]
    public async Task ExpectedNameIsOnlyCheckedWhenGiven()
    {
        _deployer.Preparation = FakeDeployer.Prepared("Bar");
        Assert.Equal("Bar", (await Deploy()).ModuleName);
        Assert.Equal("Bar", _deployer.Executions[0].ExpectedModuleName);
    }

    [Theory]
    [InlineData("Root", "parentIsRoot")]
    [InlineData("root", "parentIsRoot")]
    [InlineData("", "parentMissing")]
    [InlineData("  ", "parentMissing")]
    public async Task RootOrMissingParentIsRefusedBeforeAnything(string parent, string reason)
    {
        var e = await StopsWithoutWriting("FLUX_PARENT_ROOT_REFUSED", 6, Request(parent: parent));
        Assert.Equal(reason, e.Context["reason"]);
        Assert.Equal(0, _deployer.PrepareCalls);
        Assert.Equal(0, _world.SessionReads);
    }

    [Fact]
    public async Task ParentWithoutAParentIsTheRootUnderAnotherId()
    {
        _world.Add("Reso_WorldRoot", "Root", null);
        await StopsWithoutWriting("FLUX_PARENT_ROOT_REFUSED", 6, Request(parent: "Reso_WorldRoot"));
    }

    [Fact]
    public async Task MissingParentStops()
    {
        var e = await StopsWithoutWriting("FLUX_PARENT_UNREADABLE", 6, Request(parent: "Reso_Absent"));
        Assert.Equal("parentReadFailed", e.Context["reason"]);
    }

    [Fact]
    public async Task ReferenceOnlyChildrenAreAnIncompleteObservation()
    {
        _world.ReferenceOnlyChildren = true;
        var e = await StopsWithoutWriting("FLUX_PARENT_UNREADABLE", 6);
        Assert.Equal("childrenIncomplete", e.Context["reason"]);
    }

    [Fact]
    public async Task UnrecordedSameNameChildStopsAndIsReportedAsACandidate()
    {
        _world.Add("Reso_Foreign", "Main", Parent);

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);

        Assert.Equal(["Reso_Foreign"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        Assert.Null(e.Context["recordedRootSlotId"]);
    }

    [Fact]
    public async Task SameNameChildBesideTheRecordedRootStops()
    {
        var old = await DeployedOnce();
        _world.Add("Reso_Foreign", "Main", Parent);

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);

        Assert.Equal(["Reso_Foreign"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        Assert.Equal(old, e.Context["recordedRootSlotId"]);
        Assert.Contains(old, _world.ChildIds(Parent));
    }

    [Fact]
    public async Task RecordedRootGoneWithAForeignSameNameChildStops()
    {
        var old = await DeployedOnce();
        _world.Remove(old);
        _world.Add("Reso_Foreign", "Main", Parent);

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);

        Assert.Equal(["Reso_Foreign"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
    }

    [Fact]
    public async Task RenamedRecordedRootIsStale()
    {
        var old = await DeployedOnce();
        _world.Rename(old, "Main (old)");

        var e = await StopsWithoutWriting("FLUX_MODULE_RECORD_STALE", 6);

        Assert.Equal("nameMismatch", e.Context["reason"]);
        Assert.Equal(old, e.Context["recordedRootSlotId"]);
        Assert.Equal("Main (old)", e.Context["currentName"]);
    }

    [Fact]
    public async Task MovedRecordedRootIsStale()
    {
        var old = await DeployedOnce();
        _world.Move(old, "Reso_Elsewhere");

        var e = await StopsWithoutWriting("FLUX_MODULE_RECORD_STALE", 6);

        Assert.Equal("parentMismatch", e.Context["reason"]);
        Assert.Equal("Reso_Elsewhere", e.Context["currentParentSlotId"]);
    }

    [Fact]
    public async Task RecordFromAnotherSessionIsNotUsedToRemoveAnything()
    {
        var old = await DeployedOnce();
        _world.SessionId = "S-world-2"; // another world on the same URL; the recorded ID proves nothing there

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);

        Assert.Equal([old], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        Assert.Equal(FluxDeployRecordBasis.RecordFromOtherSession, e.Context["recordBasis"]);

        _world.Remove(old);
        var result = await Deploy();
        Assert.Equal(FluxDeployRecordBasis.RecordFromOtherSession, result.Preconditions.RecordBasis);
        Assert.Null(_deployer.Executions[^1].PreviousRootSlotId);
        Assert.Equal("S-world-2", State().Session!.DiscoverSessionId);
    }

    [Fact]
    public async Task PendingDeploymentInTheStateStops()
    {
        var pending = FluxDeployPending.Begin("other", new("ws://localhost:47610/", "S-world-1", "matched"), Parent, "Other", null, [],
            "H", DateTimeOffset.UtcNow);
        FluxDeployStateStore.AddPending(StatePath, pending);

        var e = await StopsWithoutWriting("FLUX_DEPLOY_PENDING", 7);

        Assert.Equal(Path.GetFullPath(StatePath), e.Context["stateFile"]);
    }

    [Fact]
    public async Task AnotherWriterHoldingTheUrlLockStops()
    {
        using var holder = SessionWriteLock.Acquire(_world.Url, LockDirectory);
        await StopsWithoutWriting("APPLY_SESSION_BUSY", 7);
    }

    [Fact]
    public async Task AnotherProjectsApplyPendingStops()
    {
        var applyState = Path.Combine(_root, "other-project", "apply.state.json");
        ApplyStateStore.Save(applyState, new ApplyState { OwnershipKey = "other", Pending = [new() { Kind = "createSlot", Key = "root", OwnershipKey = "other" }] });
        using (var lease = SessionWriteLock.Acquire(_world.Url, LockDirectory)) lease.RecordState(applyState);

        var e = await StopsWithoutWriting("APPLY_WRITE_UNVERIFIED", 7);

        Assert.Equal("previousStatePending", e.Context["reason"]);
        Assert.Equal(applyState, e.Context["stateFile"]);
    }

    [Fact]
    public async Task DeployerUrlOtherThanTheLockedUrlStops()
    {
        var e = await StopsWithoutWriting("FLUX_SESSION_CHANGED", 6, Request(url: "ws://localhost:9999"));

        Assert.Equal("deployerUrlMismatch", e.Context["reason"]);
        Assert.Equal("ws://localhost:47610/", e.Context["lockedUrl"]);
        Assert.Equal("ws://localhost:9999/", e.Context["deployerUrl"]);
    }

    [Fact]
    public async Task SameNameChildAppearingJustBeforeTheDeployerStopsAndReleasesThePending()
    {
        _world.BeforeParentRead = read => { if (read == 2) _world.Add("Reso_Late", "Main", Parent); };

        var e = await Fails();

        Assert.Equal("FLUX_CHILDREN_CHANGED", e.Code);
        Assert.Equal(6, e.ExitCode);
        Assert.Equal("none", e.Context["worldWrites"]);
        Assert.Equal("released", e.Context["pending"]);
        Assert.Equal(FluxDeployStage.NotSent, e.Context["stage"]);
        Assert.Equal(FluxDeploySendStatus.NotSentProven, e.Context["sendStatus"]);
        Assert.Equal(["Reso_Late"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["currentSameNameChildIds"]));
        Assert.Empty(_deployer.Executions);
        Assert.Empty(State().Pending);
        Assert.Empty(State().Modules);
        Assert.Equal(0, _world.WriteCalls);
    }

    [Fact]
    public async Task RecordedRootVanishingJustBeforeTheDeployerStops()
    {
        var old = await DeployedOnce();
        var state = StateText();
        _world.BeforeParentRead = read => { if (read == 5) _world.Remove(old); }; // reads 1-3 were the first deployment

        var e = await Fails();

        Assert.Equal("FLUX_CHILDREN_CHANGED", e.Code);
        Assert.Single(_deployer.Executions);
        Assert.Equal(state, StateText()); // pending added and released; the record is unchanged
    }

    [Fact]
    public async Task SessionChangingJustBeforeTheDeployerStopsAndReleasesThePending()
    {
        // Session reads: 1 lock, 2 identity, 3 last look before the deployer.
        _world.BeforeSessionRead = read => { if (read == 3) _world.SessionId = "S-world-2"; };

        var e = await Fails();

        Assert.Equal("FLUX_SESSION_CHANGED", e.Code);
        Assert.Equal(6, e.ExitCode);
        Assert.Equal("sessionIdChanged", e.Context["reason"]);
        Assert.Equal("released", e.Context["pending"]);
        Assert.Empty(_deployer.Executions);
        Assert.Empty(State().Pending);
    }

    [Fact]
    public async Task PendingThatCannotBeSavedDeploysNothing()
    {
        FluxDeployStateStore.SaveFault.Value = (_, _) => throw new IOException("disk full");

        var e = await Fails();

        Assert.Equal("FLUX_STATE_WRITE_FAILED", e.Code);
        Assert.Equal("none", e.Context["worldWrites"]);
        Assert.Equal(FluxDeploySendStatus.NotSentProven, e.Context["sendStatus"]);
        Assert.Empty(_deployer.Executions);
        Assert.Null(StateText());
    }

    // ---- Fault injection: where the deployer stops ---------------------------------------------------------

    [Theory]
    [InlineData(FluxDeployOutcome.NotSent, "FLUX_DEPLOY_NOT_SENT", 7)]
    [InlineData(FluxDeployOutcome.CompileFailed, "FLUX_COMPILE_FAILED", 9)]
    [InlineData(FluxDeployOutcome.ModuleNameMismatch, "FLUX_MODULE_NAME_MISMATCH", 6)]
    public async Task StopBeforeTheRemovalReleasesThePendingAndCanBeRetried(FluxDeployOutcome outcome, string code, int exitCode)
    {
        var old = await DeployedOnce();
        var state = StateText();
        var world = _world.Snapshot();
        _deployer.OnExecute = (_, _) => Task.FromResult(FakeDeployer.Execution(outcome, FluxDeployStage.NotSent,
            FluxDeploySendStatus.NotSentProven, FluxPreviousRootRemoval.NotAttempted, error: "stopped before sending."));

        var e = await Fails();

        Assert.Equal(code, e.Code);
        Assert.Equal(exitCode, e.ExitCode);
        Assert.Equal("none", e.Context["worldWrites"]);
        Assert.Equal("released", e.Context["pending"]);
        Assert.Equal(world, _world.Snapshot());
        Assert.Equal(state, StateText());

        _deployer.OnExecute = null;
        var retry = await Deploy();
        Assert.Equal(old, retry.Preconditions.PreviousRootSlotId);
        Assert.Equal(3, _deployer.Executions.Count);
    }

    [Fact]
    public async Task NotSentWithoutProofIsUnknown()
    {
        _deployer.OnExecute = (_, _) => Task.FromResult(FakeDeployer.Execution(FluxDeployOutcome.NotSent, FluxDeployStage.NotSent,
            FluxDeploySendStatus.Unknown, FluxPreviousRootRemoval.NotAttempted, error: "cancelled"));

        var e = await Fails();

        AssertPendingKept(e, "FLUX_DEPLOY_UNVERIFIED", FluxDeployStage.NotSent, FluxDeploySendStatus.Unknown);
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task RemovalAnsweredWithAFailureIsLeftPending()
    {
        var old = await DeployedOnce();
        _deployer.OnExecute = (_, _) => Task.FromResult(FakeDeployer.Execution(FluxDeployOutcome.RemoveFailed, FluxDeployStage.Removing,
            FluxDeploySendStatus.Sent, FluxPreviousRootRemoval.Failed, error: "RemoveSlot failed: denied"));

        var e = await Fails();

        var pending = AssertPendingKept(e, "FLUX_DEPLOY_PARTIAL", FluxDeployStage.Removing, FluxDeploySendStatus.Sent);
        Assert.Equal(old, pending.PreviousRootSlotId);
        Assert.Equal(old, State().Modules["main"].RootSlotId);
        await AssertRerunBlocked();

        Discard(pending);
        _deployer.OnExecute = null;
        Assert.Equal(old, (await Deploy()).Preconditions.PreviousRootSlotId); // the old root is still there and still recorded
    }

    [Fact]
    public async Task StopAfterTheRemovalIsLeftPendingAndRedeploysAfterDiscard()
    {
        var old = await DeployedOnce();
        _deployer.OnExecute = (request, _) =>
        {
            _world.Remove(request.PreviousRootSlotId!);
            return Task.FromResult(FakeDeployer.Execution(FluxDeployOutcome.RemovedNotCreated, FluxDeployStage.Removed,
                FluxDeploySendStatus.Sent, FluxPreviousRootRemoval.Removed, removed: request.PreviousRootSlotId, error: "stopped after the removal"));
        };

        var e = await Fails();

        var pending = AssertPendingKept(e, "FLUX_DEPLOY_PARTIAL", FluxDeployStage.Removed, FluxDeploySendStatus.Sent);
        Assert.Equal(FluxPreviousRootRemoval.Removed, e.Context["previousRootRemoval"]);
        Assert.Equal(old, State().Modules["main"].RootSlotId); // not settled: the record is untouched
        await AssertRerunBlocked();

        Discard(pending);
        _deployer.OnExecute = null;
        var retry = await Deploy();
        Assert.Equal(FluxDeployRecordBasis.RecordedRootGone, retry.Preconditions.RecordBasis);
        Assert.Equal(["Flux_2"], _world.ChildrenNamed(Parent, "Main"));
        Assert.Equal("Flux_2", State().Modules["main"].RootSlotId);
    }

    [Fact]
    public async Task PartialCreationIsLeftPendingAndItsRootIsNeverAdoptedByName()
    {
        var old = await DeployedOnce();
        _deployer.OnExecute = (request, _) =>
        {
            _world.Remove(request.PreviousRootSlotId!);
            _world.Add("Flux_Partial", request.ExpectedModuleName, request.ParentSlotId);
            return Task.FromResult(FakeDeployer.Execution(FluxDeployOutcome.PartialCreated, FluxDeployStage.Creating,
                FluxDeploySendStatus.Sent, FluxPreviousRootRemoval.Removed, removed: request.PreviousRootSlotId,
                requested: "Flux_Partial", newRoot: "Flux_Partial", error: "1 inner response failed",
                failures: ["#3 Response: Slot with ID 'x' not found."]));
        };

        var e = await Fails();

        var pending = AssertPendingKept(e, "FLUX_DEPLOY_PARTIAL", FluxDeployStage.Creating, FluxDeploySendStatus.Sent, "Flux_Partial");
        Assert.Equal("Flux_Partial", pending.RequestedRootId);
        Assert.Equal(["#3 Response: Slot with ID 'x' not found."], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["batchFailures"]));
        Assert.Equal(old, State().Modules["main"].RootSlotId);
        await AssertRerunBlocked();

        // Discarding adopts nothing: the partial module is now an unrecorded same-name child and stops the next deploy.
        Discard(pending);
        _deployer.OnExecute = null;
        var stopped = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);
        Assert.Equal(["Flux_Partial"], Assert.IsAssignableFrom<IEnumerable<string>>(stopped.Context["candidateIds"]));

        _world.Remove("Flux_Partial"); // the user deleted it by exact ID
        Assert.Equal("Flux_2", (await Deploy()).NewRootSlotId);
        Assert.Equal(3, _deployer.Executions.Count);
        Assert.Null(_deployer.Executions[2].PreviousRootSlotId); // the recorded root is gone; nothing is removed by name
        Assert.Equal(["Flux_2"], _world.ChildrenNamed(Parent, "Main"));
    }

    [Fact]
    public async Task LostAnswerIsLeftPendingWithItsCandidate()
    {
        var old = await DeployedOnce();
        _deployer.OnExecute = (request, _) =>
        {
            _world.Remove(request.PreviousRootSlotId!);
            _world.Add("Flux_Lost", request.ExpectedModuleName, request.ParentSlotId); // the batch arrived; its answer did not
            return Task.FromResult(FakeDeployer.Execution(FluxDeployOutcome.Unknown, FluxDeployStage.Creating,
                FluxDeploySendStatus.Unknown, FluxPreviousRootRemoval.Removed, removed: request.PreviousRootSlotId,
                requested: "Flux_Lost", error: "connection closed"));
        };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_DEPLOY_UNVERIFIED", FluxDeployStage.Creating, FluxDeploySendStatus.Unknown, "Flux_Lost");
        Assert.Equal(old, State().Modules["main"].RootSlotId); // the complete-looking module is not adopted
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task DeployerExceptionIsUnknown()
    {
        _deployer.OnExecute = (_, _) => throw new InvalidOperationException("deployer crashed");

        var e = await Fails();

        var pending = AssertPendingKept(e, "FLUX_DEPLOY_UNVERIFIED", FluxDeployStage.Unknown, FluxDeploySendStatus.Unknown);
        Assert.Contains("deployer crashed", (string)e.Context["deployerError"]!);
        await AssertRerunBlocked();

        Discard(pending);
        _deployer.OnExecute = null;
        Assert.Equal("Flux_1", (await Deploy()).NewRootSlotId); // nothing was created, so the retry is a new deployment
    }

    [Fact]
    public async Task FaultedDeployerTaskIsUnknown()
    {
        _deployer.OnExecute = (_, _) => Task.FromException<FluxDeployExecution>(new IOException("pipe broken"));
        AssertPendingKept(await Fails(), "FLUX_DEPLOY_UNVERIFIED", FluxDeployStage.Unknown, FluxDeploySendStatus.Unknown);
    }

    [Fact]
    public async Task DeployerThatNeverAnswersIsUnknownAfterTheDeadline()
    {
        var never = new TaskCompletionSource<FluxDeployExecution>();
        CancellationToken seen = default;
        _deployer.OnExecute = (_, token) => { seen = token; return never.Task; };
        var timer = Stopwatch.StartNew();

        var e = await Fails(Request(deadline: TimeSpan.FromMilliseconds(100)));

        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(140), TimeSpan.FromSeconds(20)); // deadline 100 ms + grace 50 ms
        AssertPendingKept(e, "FLUX_DEPLOY_UNVERIFIED", FluxDeployStage.Unknown, FluxDeploySendStatus.Unknown);
        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _deployer.Executions[0].Deadline);
        await AssertRerunBlocked();
    }

    // ---- Fault injection: the readback disagrees -----------------------------------------------------------

    [Fact]
    public async Task ReportedNewRootThatIsNotThereIsLeftPending()
    {
        _deployer.OnExecute = (_, _) => Task.FromResult(FakeDeployer.Created("Flux_Ghost", FluxPreviousRootRemoval.NotAttempted));

        var e = await Fails();

        AssertPendingKept(e, "FLUX_READBACK_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_Ghost");
        Assert.Equal("newRootMissing", e.Context["reason"]);
        Assert.Empty(State().Modules);
        Assert.Equal("FLUX_READBACK_FAILED", State().Pending[0].Reason);
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task NewRootWithAnotherNameIsLeftPending()
    {
        _deployer.OnExecute = (request, _) =>
        {
            _world.Add("Flux_Named", "NotMain", request.ParentSlotId);
            return Task.FromResult(FakeDeployer.Created("Flux_Named", FluxPreviousRootRemoval.NotAttempted));
        };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_READBACK_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_Named");
        Assert.Equal("newRootNameMismatch", e.Context["reason"]);
    }

    [Fact]
    public async Task PreviousRootStillPresentIsLeftPending()
    {
        var old = await DeployedOnce();
        _deployer.OnExecute = (request, _) =>
        {
            _world.Add("Flux_New", request.ExpectedModuleName, request.ParentSlotId); // created, but the old root was not removed
            return Task.FromResult(FakeDeployer.Created("Flux_New", FluxPreviousRootRemoval.Removed, old));
        };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_READBACK_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_New");
        Assert.Equal("previousRootStillPresent", e.Context["reason"]);
        Assert.Equal(old, State().Modules["main"].RootSlotId);
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task SecondSameNameChildAfterTheDeploymentIsLeftPending()
    {
        _deployer.OnExecute = (request, _) =>
        {
            _world.Add("Flux_New", request.ExpectedModuleName, request.ParentSlotId);
            _world.Add("Reso_Twin", request.ExpectedModuleName, request.ParentSlotId);
            return Task.FromResult(FakeDeployer.Created("Flux_New", FluxPreviousRootRemoval.NotAttempted));
        };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_READBACK_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_New");
        Assert.Equal("newRootAmbiguous", e.Context["reason"]);
        Assert.Equal(["Flux_New", "Reso_Twin"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["sameNameChildIds"]));
    }

    [Fact]
    public async Task ReportedNewRootThatExistedBeforeIsLeftPending()
    {
        _deployer.Preparation = FakeDeployer.Prepared("Existing");
        _world.Add("Reso_Existing", "Elsewhere", Parent);
        _deployer.OnExecute = (_, _) =>
        {
            _world.Rename("Reso_Existing", "Existing");
            return Task.FromResult(FakeDeployer.Created("Reso_Existing", FluxPreviousRootRemoval.NotAttempted, declared: "Existing"));
        };

        var e = await Fails();

        Assert.Equal("FLUX_READBACK_FAILED", e.Code);
        Assert.Equal("newRootNotNew", e.Context["reason"]);
        Assert.Single(State().Pending);
    }

    [Fact]
    public async Task UnrelatedChildDisappearingIsLeftPending()
    {
        _deployer.OnExecute = async (request, token) =>
        {
            _world.Remove(Unrelated);
            return await _deployer.Replace(request, token);
        };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_UNEXPECTED_REMOVAL", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal([Unrelated], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["removedIds"]));
        Assert.Empty(State().Modules);
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task ParentUnreadableAfterTheDeploymentIsLeftPending()
    {
        _world.BeforeParentRead = read => { if (read == 3) throw new RLoopException("REQUEST_TIMEOUT", "slot.get timed out", ExitCodes.Timeout); };

        var e = await Fails();

        AssertPendingKept(e, "FLUX_READBACK_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal("parentReadFailed", e.Context["reason"]);
        Assert.Empty(State().Modules);
        await AssertRerunBlocked();
    }

    [Theory]
    [InlineData("session", "sessionIdChanged")]
    [InlineData("version", "versionChanged")]
    [InlineData("url", "sessionUrlChanged")]
    [InlineData("generation", "connectionChanged")]
    [InlineData("disconnected", "disconnected")]
    public async Task SessionChangingDuringTheDeploymentIsLeftPending(string change, string reason)
    {
        _deployer.OnExecute = async (request, token) =>
        {
            var result = await _deployer.Replace(request, token);
            switch (change)
            {
                case "session": _world.SessionId = "S-world-2"; break;
                case "version": _world.ResoniteVersion = "2026.10.1.1"; break;
                case "url": _world.Url = "ws://localhost:50000/"; break;
                case "generation": _world.Generation = "gen-2"; break;
                default: _world.Connected = false; break;
            }
            return result;
        };

        var e = await Fails();

        Assert.Equal("FLUX_SESSION_CHANGED", e.Code);
        Assert.Equal(7, e.ExitCode);
        Assert.Equal(reason, e.Context["reason"]);
        Assert.Equal("kept", e.Context["pending"]);
        Assert.Equal(["Flux_1"], Assert.Single(State().Pending).CandidateRootIds);
        Assert.Empty(State().Modules);
    }

    [Fact]
    public async Task StateThatCannotBeSettledKeepsThePendingRecord()
    {
        var saves = 0;
        FluxDeployStateStore.SaveFault.Value = (_, _) => { if (++saves == 3) throw new IOException("disk full"); }; // 1 pending, 2 result, 3 settle

        var e = await Fails();
        FluxDeployStateStore.SaveFault.Value = null;

        AssertPendingKept(e, "FLUX_STATE_WRITE_FAILED", FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Flux_1");
        Assert.Equal("settleFailed", e.Context["reason"]);
        Assert.Equal("Flux_1", e.Context["newRootSlotId"]);
        Assert.Empty(State().Modules);
        Assert.Equal(["Flux_1"], _world.ChildrenNamed(Parent, "Main")); // the world changed; the state did not follow
        await AssertRerunBlocked();
    }

    [Fact]
    public async Task ResultThatCannotBeStoredAfterCreationKeepsThePendingRecord()
    {
        var saves = 0;
        FluxDeployStateStore.SaveFault.Value = (_, _) => { if (++saves >= 2) throw new IOException("disk full"); };

        var e = await Fails();
        FluxDeployStateStore.SaveFault.Value = null;

        Assert.Equal("FLUX_STATE_WRITE_FAILED", e.Code);
        Assert.Equal("stateUpdateFailed", e.Context["reason"]);
        Assert.Equal("kept", e.Context["pending"]);
        var pending = Assert.Single(State().Pending);
        Assert.Equal(FluxDeployStage.Unknown, pending.Stage); // as saved before the deployer was called
        Assert.Equal(pending.OperationId, e.Context["operationId"]);
        Assert.Empty(State().Modules);
        await AssertRerunBlocked();
    }

    // ---- v1 state (P6) -------------------------------------------------------------------------------------

    private void WriteV1State(string slotId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, $$"""
            { "schemaVersion": 1, "parentSlotId": "{{Parent}}", "sessionId": "67",
              "modules": { "main": { "hash": "V1HASH", "slotId": "{{slotId}}" } } }
            """);
    }

    [Fact]
    public async Task V1RecordIsTakenOverWhenItsSlotIsTheParentsChildWithTheDeclaredName()
    {
        _world.Add("Reso_V1", "Main", Parent);
        WriteV1State("Reso_V1");

        var result = await Deploy();

        Assert.Equal(FluxDeployRecordBasis.MigratedConfirmed, result.Preconditions.RecordBasis);
        Assert.Equal("Reso_V1", _deployer.Executions[0].PreviousRootSlotId);
        var record = State().Modules["main"];
        Assert.Equal(("Flux_1", "deployed", "Main", "HASH-1"), (record.RootSlotId, record.Origin, record.ModuleName, record.InputHash));
        Assert.Equal(["Flux_1"], _world.ChildrenNamed(Parent, "Main"));
        Assert.Contains("\"kind\": \"flux-deploy\"", StateText());
    }

    [Fact]
    public async Task V1RecordKeepsItsMigrationOriginWhenTheDeployerSendsNothing()
    {
        _world.Add("Reso_V1", "Main", Parent);
        WriteV1State("Reso_V1");
        _deployer.OnExecute = (_, _) => Task.FromResult(FakeDeployer.Execution(FluxDeployOutcome.NotSent, FluxDeployStage.NotSent,
            FluxDeploySendStatus.NotSentProven, FluxPreviousRootRemoval.NotAttempted, error: "connection refused"));

        Assert.Equal("FLUX_DEPLOY_NOT_SENT", (await Fails()).Code);

        var record = State().Modules["main"];
        Assert.Equal(("Reso_V1", "migrated-v1", "Main", Parent, "V1HASH"),
            (record.RootSlotId, record.Origin, record.ModuleName, record.ParentSlotId, record.InputHash));
        Assert.Empty(State().Pending);
        Assert.Null(State().Session);
        // P6 must still work on the retry; a not-sent result never creates deployed epoch evidence.
        _deployer.OnExecute = null;
        Assert.Equal(FluxDeployRecordBasis.MigratedConfirmed, (await Deploy()).Preconditions.RecordBasis);
        Assert.Equal("Reso_V1", _deployer.Executions[1].PreviousRootSlotId);
    }

    [Fact]
    public async Task V1RecordKeepsItsMigrationOriginWhenReplacementFails()
    {
        _world.Add("Reso_V1", "Main", Parent);
        WriteV1State("Reso_V1");
        _deployer.OnExecute = (_, _) => Task.FromResult(FakeDeployer.Execution(FluxDeployOutcome.RemoveFailed,
            FluxDeployStage.Removing, FluxDeploySendStatus.Sent, FluxPreviousRootRemoval.Unknown));

        Assert.Equal("FLUX_DEPLOY_PARTIAL", (await Fails()).Code);

        Assert.Equal("migrated-v1", State().Modules["main"].Origin);
        Assert.Equal("Reso_V1", State().Modules["main"].RootSlotId);
        Assert.Null(State().Session);
        Assert.Single(State().Pending);
    }

    [Theory]
    [InlineData("Reso_Elsewhere")]
    [InlineData(null)]
    public async Task V1RecordWithAnUnmatchedRecordedParentCannotAuthorizeRemoval(string? recordedParent)
    {
        _world.Add("Reso_V1", "Main", Parent);
        WriteV1State("Reso_V1");
        FluxDeployStateStore.Mutate(StatePath, true, (_, state) =>
            state.Modules["main"] = state.Modules["main"] with { ParentSlotId = recordedParent });

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);

        Assert.Equal(FluxDeployRecordBasis.MigratedUntrusted, e.Context["recordBasis"]);
        Assert.Equal(["Reso_V1"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("moved")]
    [InlineData("gone")]
    public async Task V1RecordThatDoesNotMatchIsNoRecordAndASameNameChildStops(string mismatch)
    {
        if (mismatch == "renamed") _world.Add("Reso_V1", "SomethingElse", Parent);
        if (mismatch == "moved") _world.Add("Reso_V1", "Main", "Reso_Elsewhere");
        _world.Add("Reso_Foreign", "Main", Parent);
        WriteV1State("Reso_V1");

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6); // the v1 file is not rewritten either

        Assert.Equal(["Reso_Foreign"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        Assert.Equal(FluxDeployRecordBasis.MigratedUntrusted, e.Context["recordBasis"]);
    }

    [Fact]
    public async Task V1RecordThatDoesNotMatchIsNoRecordAndDeploysNewWhenNoSameNameChildExists()
    {
        _world.Add("Reso_V1", "SomethingElse", Parent);
        WriteV1State("Reso_V1");

        var result = await Deploy();

        Assert.Equal(FluxDeployRecordBasis.MigratedUntrusted, result.Preconditions.RecordBasis);
        Assert.Null(_deployer.Executions[0].PreviousRootSlotId);
        Assert.Contains("Reso_V1", _world.ChildIds(Parent)); // the unmatched Slot is never removed
        Assert.Equal(("Flux_1", "deployed"), (State().Modules["main"].RootSlotId, State().Modules["main"].Origin));
    }

    [Fact]
    public async Task SettlingOneModuleInANewSessionCannotTransferAnotherModulesOldId()
    {
        _deployer.ModulePorts.Clear();
        var a = Request() with { ModuleKey = "a", InputMap = null, OutputMap = null };
        var b = a with { ModuleKey = "b", Module = "B.pg" };
        var oldA = (await Deploy(a)).NewRootSlotId;
        _deployer.Preparation = FakeDeployer.Prepared("B");
        var oldB = (await Deploy(b)).NewRootSlotId;
        _world.SessionId = "S-world-2";
        _world.Remove(oldA);
        _deployer.Preparation = FakeDeployer.Prepared("Main");

        var replacement = await Deploy(a);

        Assert.Null(replacement.Preconditions.PreviousRootSlotId);
        Assert.Equal("S-world-2", State().Session!.DiscoverSessionId);
        Assert.Equal(["a"], State().Modules.Keys);
        // In S2 the old B ID and name can identify somebody else's content.
        _deployer.Preparation = FakeDeployer.Prepared("B");
        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6, b);
        Assert.Equal(FluxDeployRecordBasis.None, e.Context["recordBasis"]);
        Assert.Equal([oldB], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        Assert.Equal(3, _deployer.Executions.Count);
        Assert.Contains(oldB, _world.ChildIds(Parent));
    }

    [Fact]
    public async Task SettlingInTheSameProvenEpochPreservesOtherModuleOwnership()
    {
        _deployer.ModulePorts.Clear();
        var a = Request() with { ModuleKey = "a", InputMap = null, OutputMap = null };
        var b = a with { ModuleKey = "b", Module = "B.pg" };
        await Deploy(a);
        _deployer.Preparation = FakeDeployer.Prepared("B");
        var oldB = (await Deploy(b)).NewRootSlotId;
        var recordB = State().Modules["b"];
        _deployer.Preparation = FakeDeployer.Prepared("Main");

        await Deploy(a);

        var retainedB = State().Modules["b"];
        Assert.Equal((recordB.ParentSlotId, recordB.RootSlotId, recordB.ModuleName, recordB.InputHash, recordB.Origin),
            (retainedB.ParentSlotId, retainedB.RootSlotId, retainedB.ModuleName, retainedB.InputHash, retainedB.Origin));
        Assert.Empty(retainedB.Bindings);
        _deployer.Preparation = FakeDeployer.Prepared("B");
        Assert.Equal(oldB, (await Deploy(b)).Preconditions.PreviousRootSlotId);
        Assert.Equal(oldB, _deployer.Executions[3].PreviousRootSlotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherUrlCannotAuthorizeRemovalEvenWithTheSameSessionId(bool unknownIdentity)
    {
        if (unknownIdentity) _world.SessionId = null;
        var old = await DeployedOnce();
        _world.Url = "ws://localhost:47611/";

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);

        Assert.Equal(FluxDeployRecordBasis.RecordFromOtherSession, e.Context["recordBasis"]);
        Assert.Equal([old], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        _world.Remove(old);
        Assert.Null((await Deploy()).Preconditions.PreviousRootSlotId);
        Assert.Null(_deployer.Executions[1].PreviousRootSlotId);
    }

    [Theory]
    [InlineData("matched", "S-world-1", null)]
    [InlineData("unknown", null, null)]
    [InlineData("unknown", null, "S-world-1")]
    [InlineData("unknown", "S-world-1", "S-world-1")]
    [InlineData("matched", null, "S-world-1")]
    public async Task UnknownIdentityAtTheSameUrlCannotProveDeployedOwnership(string recordedStatus,
        string? recordedId, string? currentId)
    {
        var old = await DeployedOnce();
        FluxDeployStateStore.Mutate(StatePath, true, (_, state) =>
            state.Session = new("ws://localhost:47610/", recordedId, recordedStatus));
        _world.SessionId = currentId;

        var e = await StopsWithoutWriting("FLUX_MODULE_UNRECORDED_SIBLING", 6);

        Assert.Equal(FluxDeployRecordBasis.RecordFromOtherSession, e.Context["recordBasis"]);
        Assert.Equal([old], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
    }

    // ---- Fakes ---------------------------------------------------------------------------------------------

    private static FluxDiagnostic Diagnostic(string severity, string message) =>
        new("Main.pg", 1, 1, 1, 2, severity, message, "deployer", "compile", severity == "error");

    private sealed class FakeDeployer(FakeWorld world) : IFluxDeployer
    {
        private int _next;
        public FluxDeployPreparation Preparation { get; set; } = Prepared("Main");
        public List<FluxDeployExecuteRequest> Executions { get; } = [];
        public int PrepareCalls { get; private set; }
        public Func<FluxDeployExecuteRequest, CancellationToken, Task<FluxDeployExecution>>? OnExecute { get; set; }

        /// <summary>
        /// A clean preparation that, like the real deployer, reports the compiled ports: the default
        /// <see cref="ModulePorts"/> (Speed as input, Angle as output). Unit 5 compares the binding keys with them.
        /// </summary>
        public static FluxDeployPreparation Prepared(string name, params FluxDiagnostic[] diagnostics) =>
            new(true, name, diagnostics, PreparedPorts(("Speed", "source"), ("Angle", "drive")), null, "", "");

        public static IReadOnlyList<FluxModulePortInfo> PreparedPorts(params (string Name, string Direction)[] ports) =>
            ports.Select(port => new FluxModulePortInfo(port.Name, port.Direction, null,
                (port.Direction == "source" ? "Input:" : "Output:") + port.Name, null,
                [port.Direction == "source" ? InputCarrierType : OutputCarrierType],
                port.Direction == "source" ? InputCarrierType : OutputCarrierType)).ToArray();

        /// <summary>The ports of the compiled module (name, <c>source</c> or <c>drive</c>). A port without a map entry is created unwired.</summary>
        public List<(string Name, string Direction)> ModulePorts { get; } = [("Speed", "source"), ("Angle", "drive")];
        /// <summary>When false, the execution reports no ports (the deployer could not read them).</summary>
        public bool ReportPorts { get; set; } = true;
        /// <summary>Overrides the carrier type the execution reports as sent for a port; a present null value reports "not exactly one".</summary>
        public Dictionary<string, string?> ReportedCarrierTypes { get; } = [];

        public static FluxDeployExecution Execution(FluxDeployOutcome outcome, FluxDeployStage stage, FluxDeploySendStatus sendStatus,
            FluxPreviousRootRemoval removal, string? removed = null, string? requested = null, string? newRoot = null,
            string? error = null, IReadOnlyList<string>? failures = null, string? declared = "Main",
            IReadOnlyList<FluxModulePortInfo>? ports = null) =>
            new(outcome, stage, sendStatus, removal, declared, removed, requested, newRoot, [], failures ?? [], ports, error, "", "");

        public static FluxDeployExecution Created(string newRoot, FluxPreviousRootRemoval removal, string? removed = null, string declared = "Main",
            IReadOnlyList<FluxModulePortInfo>? ports = null) =>
            Execution(FluxDeployOutcome.Created, FluxDeployStage.Created, FluxDeploySendStatus.Sent, removal, removed, newRoot, newRoot,
                declared: declared, ports: ports);

        public Task<FluxDeployPreparation> PrepareAsync(FluxDeployPrepareRequest request, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.FromResult(Preparation);
        }

        public Task<FluxDeployExecution> ExecuteAsync(FluxDeployExecuteRequest request, CancellationToken cancellationToken = default)
        {
            Executions.Add(request);
            return (OnExecute ?? Replace)(request, cancellationToken);
        }

        /// <summary>
        /// P1-b on the fake world: remove the exact previous root, create one new root with a port Slot and a carrier
        /// per module port (wired to the mapped target, as the SDK does in the creation batch), answer its ID and ports.
        /// </summary>
        public Task<FluxDeployExecution> Replace(FluxDeployExecuteRequest request, CancellationToken cancellationToken)
        {
            var removal = FluxPreviousRootRemoval.NotAttempted;
            if (request.PreviousRootSlotId is { } previous)
                removal = world.Remove(previous) ? FluxPreviousRootRemoval.Removed : FluxPreviousRootRemoval.NotFound;
            var id = $"Flux_{++_next}";
            world.Add(id, request.ExpectedModuleName, request.ParentSlotId);
            var ports = new List<FluxModulePortInfo>();
            foreach (var (name, direction) in ModulePorts)
            {
                var input = direction == "source";
                var slotName = (input ? "Input:" : "Output:") + name;
                var slotId = PortSlotId(id, name);
                var type = input ? InputCarrierType : OutputCarrierType;
                string? target = null;
                _ = (input ? request.InputMap : request.OutputMap)?.TryGetValue(name, out target);
                world.Add(slotId, slotName, id);
                world.AddComponent(slotId, Carrier(slotId, type, input ? "Reference" : "Drive", target,
                    input ? "[FrooxEngine]FrooxEngine.IValue<float>" : "[FrooxEngine]FrooxEngine.IField<float>"));
                ports.Add(new(name, direction, null, slotName, slotId, [type],
                    ReportedCarrierTypes.TryGetValue(name, out var reported) ? reported : type));
            }
            return Task.FromResult(Created(id, removal, removal == FluxPreviousRootRemoval.Removed ? request.PreviousRootSlotId : null,
                request.ExpectedModuleName, ReportPorts ? ports : null));
        }
    }

    /// <summary>A world of Slots (ID, name, parent). Every mutation through the client is counted and refused.</summary>
    private sealed class FakeWorld : IResoniteClient, IApplySessionObservation
    {
        private readonly List<(string Id, string Name, string? ParentId)> _slots =
        [
            ("Root", "Root", null), (Parent, "ResoLoop_Test_Parent", "Root"), ("Reso_Elsewhere", "Elsewhere", "Root"),
            (Unrelated, "Unrelated", Parent)
        ];
        private readonly Dictionary<string, List<ComponentSummary>> _components = new(StringComparer.Ordinal);
        /// <summary>Slots that a read reaching them returns as reference-only entries (ID without a name).</summary>
        public HashSet<string> ReferenceOnlySlots { get; } = new(StringComparer.Ordinal);
        /// <summary>Called with the Slot ID of each read that asks for component data, before it is answered.</summary>
        public Action<string>? BeforeComponentDataRead { get; set; }
        public List<string> ComponentDataReads { get; } = [];
        public string Url { get; set; } = "ws://localhost:47610/";
        public string? SessionId { get; set; } = "S-world-1";
        public string ResoniteVersion { get; set; } = "2026.9.18.82";
        public string Generation { get; set; } = "gen-1";
        public bool Connected { get; set; } = true;
        public bool ReferenceOnlyChildren { get; set; }
        public int WriteCalls { get; private set; }
        public int SessionReads { get; private set; }
        private int _parentReads;
        /// <summary>
        /// Called with the 1-based number of each child-list read (depth 1 without component data), before it is
        /// answered. Reads that ask for component data (unit 3b) are counted separately, so the numbers the unit 3a
        /// tests inject at keep their meaning.
        /// </summary>
        public Action<int>? BeforeParentRead { get; set; }
        public Action<int>? BeforeSessionRead { get; set; }

        public void Add(string id, string name, string? parentId) => _slots.Add((id, name, parentId));
        public bool Remove(string id)
        {
            foreach (var child in ChildIds(id)) Remove(child);
            _components.Remove(id);
            return _slots.RemoveAll(slot => slot.Id == id) > 0;
        }
        public void AddComponent(string slotId, ComponentSummary component)
        {
            if (!_components.TryGetValue(slotId, out var list)) _components[slotId] = list = [];
            list.Add(component);
        }
        public List<ComponentSummary> Components(string slotId) =>
            _components.TryGetValue(slotId, out var list) ? list : _components[slotId] = [];
        public void Rename(string id, string name) { var index = _slots.FindIndex(slot => slot.Id == id); _slots[index] = (id, name, _slots[index].ParentId); }
        public void Move(string id, string parentId) { var index = _slots.FindIndex(slot => slot.Id == id); _slots[index] = (id, _slots[index].Name, parentId); }
        public string[] ChildIds(string parentId) => _slots.Where(slot => slot.ParentId == parentId).Select(slot => slot.Id).ToArray();
        public string[] ChildrenNamed(string parentId, string name) =>
            _slots.Where(slot => slot.ParentId == parentId && slot.Name == name).Select(slot => slot.Id).ToArray();
        public string Snapshot() => string.Join("\n", _slots.Select(slot => $"{slot.Id}|{slot.Name}|{slot.ParentId}"));

        public ApplySessionObservation ObserveApplySession() =>
            new(ApplySessionObservation.NormalizeUrl(Url), SessionId, SessionId is null ? "unknown" : "matched");

        public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default)
        {
            var read = ++SessionReads;
            BeforeSessionRead?.Invoke(read);
            return Task.FromResult(new SessionInfo(Url, Connected, ResoniteVersion, "0.13.1.0", "67", Generation));
        }

        public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
        {
            if (includeComponentData) { ComponentDataReads.Add(id); BeforeComponentDataRead?.Invoke(id); }
            else if (depth > 0) { var read = ++_parentReads; BeforeParentRead?.Invoke(read); }
            var index = _slots.FindIndex(slot => slot.Id == id);
            if (index < 0)
                throw new RLoopException("SLOT_NOT_FOUND", $"Slot with ID '{id}' not found.", ExitCodes.NotFound);
            var found = _slots[index];
            // As on the wire: a child beyond the requested depth is reference-only, and a component is listed
            // without members unless component data was requested.
            var children = _slots.Where(slot => slot.ParentId == id).Select(slot =>
                depth > 0 && !ReferenceOnlyChildren && !ReferenceOnlySlots.Contains(slot.Id)
                ? Slot(slot.Id, slot.Name, slot.ParentId, false, [], includeComponentData)
                : Slot(slot.Id, string.Empty, null, true, [], false)).ToArray();
            return Task.FromResult(Slot(found.Id, found.Name, found.ParentId, false, children, includeComponentData));
        }

        private SlotInfo Slot(string id, string name, string? parentId, bool referenceOnly, IReadOnlyList<SlotInfo> children, bool componentData) =>
            new(id, name, parentId, null, null, null, true, true, null, referenceOnly,
                referenceOnly || !_components.TryGetValue(id, out var components) ? []
                    : components.Select(component => componentData ? component : component with { Members = null }).ToArray(),
                children);

        private T Write<T>() { WriteCalls++; throw new InvalidOperationException("The guard must not write through its own connection."); }
        private static T Unused<T>() => throw new NotSupportedException();

        public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) => Unused<Task<ComponentInfo>>();
        public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) => Write<Task<string>>();
        public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) => Write<Task>();
        public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => Write<Task>();
        public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => Write<Task<ComponentCreateResult>>();
        public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) => Write<Task>();
        public Task SetComponentMembersAsync(string componentId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => Write<Task>();
        public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => Write<Task>();
        public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Unused<Task<IReadOnlyList<string>>>();
        public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) =>
            Unused<Task<ComponentTypeInfo>>();
        public Task<RLoop.Core.TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) =>
            Unused<Task<RLoop.Core.TypeInfo>>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

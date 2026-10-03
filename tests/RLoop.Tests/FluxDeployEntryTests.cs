using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;
using RLoop.Flux;

namespace RLoop.Tests;

// ROADMAP-9 unit 4a: deploy-manifest, watch and the single deploy all go through FluxDeployGuard. Expected values
// come from the decisions (P2-P8, P11) and the task contract, not from the output. The world, the build tool and the
// deployer are fakes; nothing connects anywhere.
public sealed class FluxDeployEntryTests : IDisposable
{
    private const string Parent = FluxTestWorld.Parent;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-flux-entry-" + Guid.NewGuid().ToString("N"));
    private readonly FluxTestWorld _world = new();
    private readonly FluxTestDeployer _deployer;

    public FluxDeployEntryTests()
    {
        Directory.CreateDirectory(_root);
        _deployer = new FluxTestDeployer(_world);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(full, recursive: true);
    }

    private string Source => Path.Combine(_root, "main.pg");
    private string StatePath => Path.Combine(_root, ".resoloop", "flux-state", "flux.json");
    private string SingleStatePath => FluxDeployStateStore.ResolveSingleDeployStatePath(_root, "Main");
    private Uri Url => new(_world.Url);
    private FluxManifestOrchestrator Orchestrator() => new(_deployer, _deployer, _world);
    private string? Text(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private string Manifest(string? parent = null)
    {
        File.WriteAllText(Source, "module Main where { 1->display }");
        var manifest = Path.Combine(_root, "flux.json");
        var parentItem = parent is null ? "" : $"\"parent\":\"{parent}\", ";
        File.WriteAllText(manifest, $$"""{ "schemaVersion":"1", {{parentItem}}"modules":[{"name":"main","source":"main.pg","module":"Main"}] }""");
        return manifest;
    }

    private void ChangeSource(int value) => File.WriteAllText(Source, $"module Main where {{ {value}->display }}");

    private Task<FluxManifestResult> Deploy(string manifest, string? parent = Parent) =>
        Orchestrator().DeployAsync(manifest, parent, Url, null, null);

    private Task<FluxSingleDeployResult> DeploySingle(string? parent = Parent) =>
        FluxSingleDeploy.DeployAsync(_world, _deployer, _root, "Main", parent, Url, null, null);

    private static FluxManifestResult ReportOf(RLoopException error) =>
        Assert.IsType<FluxManifestResult>(error.Context[FluxManifestOrchestrator.ReportContextKey]);

    private static FluxDeployPreparation CompileError() =>
        new(false, null, [FluxTestDeployer.Error("Unknown name 'missing'.")], null, "Module 'Main' has 1 compile error(s).", "", "");

    // ---- Acceptance 1: a module with errors sends nothing and leaves the state as it was ----------------------

    [Fact]
    public async Task ManifestModuleWithCompileErrorsSendsNothingAndLeavesTheStateUnchanged()
    {
        var manifest = Manifest();
        await Deploy(manifest);
        ChangeSource(2);
        _deployer.Prepare = _ => CompileError();
        var (world, state) = (_world.Snapshot(), Text(StatePath));

        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest));

        Assert.Equal("FLUX_COMPILE_FAILED", e.Code);
        Assert.Equal("none", e.Context["worldWrites"]);
        Assert.Single(_deployer.Executions);
        Assert.Equal(world, _world.Snapshot());
        Assert.Equal(state, Text(StatePath));
        Assert.Equal(("FLUX_COMPILE_FAILED", "main"), (ReportOf(e).StoppedBy!.Code, ReportOf(e).StoppedBy!.Module));
    }

    [Fact]
    public async Task ManifestBuildFailureIsReportedWithoutCallingTheGuard()
    {
        var manifest = Manifest();
        _deployer.BuildResult = new FluxResult(false, 1, "Main.pg(1,1,1,2): error: broken", "");

        var result = await Deploy(manifest);

        Assert.False(result.Success);
        Assert.Equal("build failed; deploy was skipped", Assert.Single(result.Modules).Reason);
        Assert.Empty(_deployer.Preparations);
        Assert.Empty(_deployer.Executions);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task SingleDeployWithCompileErrorsSendsNothingAndCreatesNoState()
    {
        _deployer.Prepare = _ => CompileError();
        var world = _world.Snapshot();

        var e = await Assert.ThrowsAsync<RLoopException>(() => DeploySingle());

        Assert.Equal("FLUX_COMPILE_FAILED", e.Code);
        Assert.Empty(_deployer.Executions);
        Assert.Equal(world, _world.Snapshot());
        Assert.False(File.Exists(SingleStatePath));
    }

    [Fact]
    public async Task WatchStopsOnACompileFailureWithoutSending()
    {
        var manifest = Manifest();
        _deployer.Prepare = _ => CompileError();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var e = await Assert.ThrowsAsync<RLoopException>(() =>
            Orchestrator().WatchAsync(manifest, Parent, Url, null, null, TimeSpan.FromMilliseconds(20), cancellationToken: timeout.Token));

        Assert.Equal("FLUX_COMPILE_FAILED", e.Code);
        Assert.True(ReportOf(e).WatchStopped);
        Assert.Equal("FLUX_COMPILE_FAILED", ReportOf(e).StoppedBy!.Code);
        Assert.Empty(_deployer.Executions);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task WatchReportsABuildFailureAndWaitsForTheNextChange()
    {
        var manifest = Manifest();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _deployer.BuildResult = new FluxResult(false, 1, "Main.pg(1,1,1,2): error: broken", "");
        _deployer.OnBuild = _ => stop.Cancel(); // the user stops the watch while it waits

        var result = await Orchestrator().WatchAsync(manifest, Parent, Url, null, null, TimeSpan.FromSeconds(5),
            cancellationToken: stop.Token);

        Assert.False(result.Success);
        Assert.True(result.WatchStopped);
        Assert.Null(result.StoppedBy);
        Assert.Single(_deployer.BuildRequests);
        Assert.Empty(_deployer.Preparations);
        Assert.Empty(_deployer.Executions);
        Assert.False(File.Exists(StatePath));
    }

    // ---- Acceptance 2: only the recorded previous module is replaced ----------------------------------------

    [Fact]
    public async Task AnUnrecordedSameNameChildStopsBothEntriesWithNothingWritten()
    {
        var manifest = Manifest();
        _world.Add("Reso_Foreign", "Main", Parent);
        var world = _world.Snapshot();

        var fromManifest = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest));
        var fromSingle = await Assert.ThrowsAsync<RLoopException>(() => DeploySingle());

        foreach (var e in new[] { fromManifest, fromSingle })
        {
            Assert.Equal("FLUX_MODULE_UNRECORDED_SIBLING", e.Code);
            Assert.Equal(["Reso_Foreign"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        }
        Assert.Empty(_deployer.Executions);
        Assert.Equal(world, _world.Snapshot());
        Assert.False(File.Exists(StatePath));
        Assert.False(File.Exists(SingleStatePath));
    }

    [Fact]
    public async Task ManifestReplacesTheRecordedModuleByItsExactIdAndRecordsTheNewRoot()
    {
        var manifest = Manifest();
        await Deploy(manifest);
        ChangeSource(2);

        var result = await Deploy(manifest);

        var module = Assert.Single(result.Modules);
        Assert.Equal(("update", "Flux_1", "Flux_2", true), (module.Action, module.ModuleSlotIdBefore, module.ModuleSlotIdAfter, module.Deployed));
        Assert.Equal("Flux_1", _deployer.Executions[1].PreviousRootSlotId);
        Assert.Equal(["Flux_2"], _world.ChildrenNamed(Parent, "Main"));
        var deploy = module.Deploy!;
        Assert.Equal((FluxDeployRecordBasis.Deployed, "Flux_1"), (deploy.Preconditions.RecordBasis, deploy.PreviousRootSlotId));
        Assert.Equal((FluxPreviousRootRemoval.Removed, FluxDeployStage.Created), (deploy.PreviousRootRemoval, deploy.Stage));
        Assert.Equal(Path.GetFullPath(StatePath), deploy.StateFile);
        var state = FluxDeployStateStore.Load(StatePath);
        Assert.Equal(("Flux_2", FluxDeployOrigins.Deployed, "Main", Parent), (state.Modules["main"].RootSlotId,
            state.Modules["main"].Origin, state.Modules["main"].ModuleName, state.Modules["main"].ParentSlotId));
        Assert.Empty(state.Pending);
    }

    [Fact]
    public async Task SingleDeployKeepsItsOwnStateAndReplacesItsRecordedModule()
    {
        var first = await DeploySingle();
        var second = await DeploySingle(); // the single deploy has no no-op

        Assert.Equal(("Flux_1", "Flux_2"), (first.OutputPath, second.OutputPath));
        Assert.Equal([null, "Flux_1"], _deployer.Executions.Select(request => request.PreviousRootSlotId));
        Assert.Equal(["Flux_2"], _world.ChildrenNamed(Parent, "Main"));
        Assert.Equal(Path.Combine(_root, ".resoloop", "flux-state", "deploy", "Main.json"), SingleStatePath);
        Assert.Equal(SingleStatePath, second.Deploy.StateFile);
        Assert.Equal("Flux_2", FluxDeployStateStore.Load(SingleStatePath).Modules["Main"].RootSlotId);
        Assert.False(File.Exists(StatePath)); // never the manifest's state
    }

    [Fact]
    public async Task ANoOpReadsTheRecordedRootByIdAndNeverAdoptsBySearchingNames()
    {
        var manifest = Manifest();
        await Deploy(manifest);
        var state = Text(StatePath);

        // The recorded root is gone and another Slot now carries the module name: not a no-op, and not adopted.
        _world.Remove("Flux_1");
        _world.Add("Reso_Lookalike", "Main", Parent);
        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest));

        Assert.Equal("FLUX_MODULE_UNRECORDED_SIBLING", e.Code);
        Assert.Equal(state, Text(StatePath));
        Assert.Single(_deployer.Executions);
    }

    [Theory]
    [InlineData("currentUnknown")]
    [InlineData("recordedUnknown")]
    [InlineData("bothUnknown")]
    [InlineData("sessionChanged")]
    [InlineData("urlChanged")]
    [InlineData("missingSession")]
    public async Task AnUnprovenOrDifferentEpochCannotReportADeployedRootAsUnchanged(string scenario)
    {
        var manifest = Manifest();
        await Deploy(manifest);
        if (scenario is "currentUnknown" or "bothUnknown") _world.SessionId = null;
        if (scenario == "sessionChanged") _world.SessionId = "S-world-2";
        if (scenario == "urlChanged") _world.Url = "ws://localhost:47611/";
        if (scenario is "recordedUnknown" or "bothUnknown" or "missingSession")
            FluxDeployStateStore.Mutate(StatePath, true, (_, state) => state.Session = scenario == "missingSession"
                ? null : state.Session! with { IdentityStatus = "unknown", DiscoverSessionId = null });
        var state = Text(StatePath);
        var world = _world.Snapshot();

        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest));

        Assert.Equal("FLUX_MODULE_UNRECORDED_SIBLING", e.Code);
        Assert.Equal(["Flux_1"], Assert.IsAssignableFrom<IEnumerable<string>>(e.Context["candidateIds"]));
        Assert.False(ReportOf(e).Success);
        Assert.Single(_deployer.Executions);
        Assert.Equal(state, Text(StatePath));
        Assert.Equal(world, _world.Snapshot());
    }

    // ---- P6: a v1 state ---------------------------------------------------------------------------------

    private void WriteV1State(string slotId, string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, $$"""
            { "schemaVersion": 1, "parentSlotId": "{{Parent}}", "sessionId": "67", "modules": { "main": { "hash": "{{hash}}", "slotId": "{{slotId}}" } } }
            """);
    }

    /// <summary>The v1 input hash of the manifest module (source bytes, then its empty bindings object).</summary>
    private string V1Hash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(Source));
        hash.AppendData(Encoding.UTF8.GetBytes("{}"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    [Fact]
    public async Task AV1RecordThatMatchesTheWorldIsConfirmedByTheGuardAndReplacedOnce()
    {
        var manifest = Manifest();
        _world.Add("Reso_V1", "Main", Parent);
        WriteV1State("Reso_V1", V1Hash()); // same input: a v1 CLI would have called this a no-op

        var migrated = await Deploy(manifest);
        var again = await Deploy(manifest);

        var module = Assert.Single(migrated.Modules);
        Assert.Equal(("update", "Reso_V1", "Flux_1"), (module.Action, module.ModuleSlotIdBefore, module.ModuleSlotIdAfter));
        Assert.Equal(FluxDeployRecordBasis.MigratedConfirmed, module.Deploy!.Preconditions.RecordBasis);
        Assert.Equal("Reso_V1", Assert.Single(_deployer.Executions).PreviousRootSlotId);
        Assert.Equal(["Flux_1"], _world.ChildrenNamed(Parent, "Main"));
        using (var saved = JsonDocument.Parse(File.ReadAllText(StatePath)))
        {
            Assert.Equal("flux-deploy", saved.RootElement.GetProperty("kind").GetString());
            Assert.Equal("deployed", saved.RootElement.GetProperty("modules").GetProperty("main").GetProperty("origin").GetString());
        }
        Assert.Equal("no-op", Assert.Single(again.Modules).Action);
    }

    [Fact]
    public async Task AV1RecordThatDoesNotMatchIsNotTrustedAndASameNameChildStops()
    {
        var manifest = Manifest();
        _world.Add("Reso_V1", "Renamed", Parent);
        _world.Add("Reso_Foreign", "Main", Parent);
        WriteV1State("Reso_V1", V1Hash());
        var v1 = Text(StatePath);

        var e = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest));

        Assert.Equal("FLUX_MODULE_UNRECORDED_SIBLING", e.Code);
        Assert.Equal(FluxDeployRecordBasis.MigratedUntrusted, e.Context["recordBasis"]);
        Assert.Equal(v1, Text(StatePath)); // the v1 file is not rewritten
        Assert.Empty(_deployer.Executions);
    }

    // ---- Acceptance 7 (P8): no Root default ----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Root")]
    public async Task BothEntriesRefuseAMissingParentOrRoot(string? parent)
    {
        var manifest = Manifest();
        var world = _world.Snapshot();

        var fromManifest = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest, parent));
        var fromSingle = await Assert.ThrowsAsync<RLoopException>(() => DeploySingle(parent));

        Assert.All(new[] { fromManifest, fromSingle }, e => Assert.Equal("FLUX_PARENT_ROOT_REFUSED", e.Code));
        Assert.Equal(parent is "Root" ? "parentIsRoot" : "parentMissing", fromSingle.Context["reason"]);
        Assert.Empty(_deployer.Preparations);
        Assert.Empty(_deployer.Executions);
        Assert.Equal(world, _world.Snapshot());
        Assert.False(File.Exists(StatePath));
        Assert.False(File.Exists(SingleStatePath));
    }

    // ---- watch (P5, P11) ------------------------------------------------------------------------------------

    [Fact]
    public async Task WatchStopsWhenADeploymentIsLeftPendingAndDoesNotRetry()
    {
        var manifest = Manifest();
        _deployer.Execute = (_, _) => Task.FromResult(FluxTestDeployer.Outcome(FluxDeployOutcome.Unknown,
            FluxDeployStage.Creating, FluxDeploySendStatus.Unknown));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var e = await Assert.ThrowsAsync<RLoopException>(() =>
            Orchestrator().WatchAsync(manifest, Parent, Url, null, null, TimeSpan.FromMilliseconds(20), cancellationToken: timeout.Token));

        Assert.Equal("FLUX_DEPLOY_UNVERIFIED", e.Code);
        Assert.Equal("kept", e.Context["pending"]);
        Assert.True(ReportOf(e).WatchStopped);
        Assert.Equal(("main", "FLUX_DEPLOY_UNVERIFIED"), (ReportOf(e).StoppedBy!.Module, ReportOf(e).StoppedBy!.Code));
        Assert.Single(_deployer.Executions);
        Assert.Single(FluxDeployStateStore.Load(StatePath).Pending);
    }

    [Fact]
    public async Task WatchStopsOnABusyLockWithoutSending()
    {
        var manifest = Manifest();
        using var holder = SessionWriteLock.Acquire(_world.Url, SessionWriteLock.DirectoryForTests!(_world));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var e = await Assert.ThrowsAsync<RLoopException>(() =>
            Orchestrator().WatchAsync(manifest, Parent, Url, null, null, TimeSpan.FromMilliseconds(20), cancellationToken: timeout.Token));

        Assert.Equal("APPLY_SESSION_BUSY", e.Code);
        Assert.True(ReportOf(e).WatchStopped);
        Assert.Equal("APPLY_SESSION_BUSY", ReportOf(e).StoppedBy!.Code);
        Assert.Empty(_deployer.Executions);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task WatchHoldsTheLockOnlyDuringADeploymentAndStopsWhenTheNextOneFindsItBusy()
    {
        var manifest = Manifest();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watch = Task.Run(() => Orchestrator().WatchAsync(manifest, Parent, Url, null, null, TimeSpan.FromMilliseconds(20),
            cancellationToken: stop.Token));
        await WaitUntil(() => FluxDeployStateStore.Load(StatePath).Modules.ContainsKey("main"));

        // Between deployments the watch does not hold the URL lock: another writer gets it.
        using (var other = await AcquireWithin(TimeSpan.FromSeconds(10)))
        {
            ChangeSource(2);
            var e = await Assert.ThrowsAsync<RLoopException>(() => watch.WaitAsync(TimeSpan.FromSeconds(20)));

            Assert.Equal("APPLY_SESSION_BUSY", e.Code);
            Assert.True(ReportOf(e).WatchStopped);
        }
        Assert.Single(_deployer.Executions);
        Assert.Equal("Flux_1", FluxDeployStateStore.Load(StatePath).Modules["main"].RootSlotId);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            try { if (condition()) return; }
            catch (RLoopException) { } // the state is being replaced
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met.");
            await Task.Delay(10);
        }
    }

    private async Task<SessionWriteLock> AcquireWithin(TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            try { return SessionWriteLock.Acquire(_world.Url, SessionWriteLock.DirectoryForTests!(_world)); }
            catch (RLoopException e) when (e.Code == "APPLY_SESSION_BUSY" && DateTime.UtcNow < deadline) { await Task.Delay(10); }
        }
    }

    // ---- CLI ------------------------------------------------------------------------------------------------

    private async Task<(int Exit, JsonElement Output)> Cli(params string[] args)
    {
        var report = Path.Combine(_root, "report-" + Guid.NewGuid().ToString("N") + ".json");
        var exit = await Program.RunAsync([.. args, "--url", _world.Url, "--json", "--report", report],
            _ => { _world.Connects++; return Task.FromResult<IResoniteClient>(_world); }, null, (_deployer, _deployer));
        using var document = JsonDocument.Parse(File.ReadLines(report).Last());
        return (exit, document.RootElement.Clone());
    }

    private string[] SingleArgs(params string[] extra) => ["flux", "deploy", "--project", _root, "--module", "Main", .. extra];

    [Fact]
    public async Task CliSingleDeployGoesThroughTheGuardAndKeepsItsResultShape()
    {
        var (exit, output) = await Cli(SingleArgs("--parent", Parent));

        Assert.Equal(0, exit);
        var data = output.GetProperty("data");
        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
        Assert.Equal("Flux_1", data.GetProperty("outputPath").GetString()); // the new module root, not the parent
        foreach (var name in new[] { "standardOutput", "standardError", "diagnostics", "primaryDiagnostics" })
            Assert.True(data.TryGetProperty(name, out _), name);
        var deploy = data.GetProperty("deploy");
        Assert.Equal("Flux_1", deploy.GetProperty("newRootSlotId").GetString());
        Assert.Equal("created", deploy.GetProperty("stage").GetString());
        Assert.Equal("notAttempted", deploy.GetProperty("previousRootRemoval").GetString());
        Assert.Equal(SingleStatePath, deploy.GetProperty("stateFile").GetString());
        Assert.Equal("none", deploy.GetProperty("preconditions").GetProperty("recordBasis").GetString());
        Assert.Equal("noBindings", deploy.GetProperty("bindingReadback").GetString());
        Assert.Equal("noOutputs", deploy.GetProperty("writerCheck").GetString());
        Assert.Equal("ws://localhost:47610/", deploy.GetProperty("session").GetProperty("normalizedUrl").GetString());
        Assert.False(string.IsNullOrWhiteSpace(deploy.GetProperty("operationId").GetString()));
        Assert.Equal("Flux_1", FluxDeployStateStore.Load(SingleStatePath).Modules["Main"].RootSlotId);
        // The CLI did not take the lock before the guard (that would have been APPLY_SESSION_BUSY), and after
        // success the lock still points at the Flux state (P10).
        string lockFile;
        using (var lease = SessionWriteLock.Acquire(_world.Url, SessionWriteLock.DirectoryForTests!(_world))) lockFile = lease.Path;
        Assert.Equal(JsonSerializer.Serialize(SingleStatePath), File.ReadAllText(lockFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliSingleDeployHasNoRootDefault(bool explicitRoot)
    {
        var (exit, output) = await Cli(explicitRoot ? SingleArgs("--parent", "Root") : SingleArgs());

        Assert.Equal(6, exit);
        Assert.Equal("FLUX_PARENT_ROOT_REFUSED", output.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_deployer.Preparations);
        Assert.Empty(_deployer.Executions);
        Assert.False(File.Exists(SingleStatePath));
    }

    [Fact]
    public async Task CliSingleDeployWithCompileErrorsSendsNothing()
    {
        _deployer.Prepare = _ => CompileError();

        var (exit, output) = await Cli(SingleArgs("--parent", Parent));

        Assert.Equal(9, exit);
        var error = output.GetProperty("error");
        Assert.Equal("FLUX_COMPILE_FAILED", error.GetProperty("code").GetString());
        Assert.Equal("none", error.GetProperty("context").GetProperty("worldWrites").GetString());
        Assert.Empty(_deployer.Executions);
        Assert.False(File.Exists(SingleStatePath));
    }

    [Fact]
    public async Task CliSingleDeployStopsOnABusyLockWithoutSending()
    {
        using var holder = SessionWriteLock.Acquire(_world.Url, SessionWriteLock.DirectoryForTests!(_world));

        var (exit, output) = await Cli(SingleArgs("--parent", Parent));

        Assert.Equal(7, exit);
        Assert.Equal("APPLY_SESSION_BUSY", output.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_deployer.Executions);
        Assert.False(File.Exists(SingleStatePath));
    }

    [Fact]
    public async Task CliManifestDeployAddsTheGuardResultAndHasNoRootDefault()
    {
        var manifest = Manifest();

        var (refusedExit, refused) = await Cli("flux", "deploy-manifest", manifest);
        var (exit, output) = await Cli("flux", "deploy-manifest", manifest, "--parent", Parent);

        Assert.Equal(6, refusedExit);
        var error = refused.GetProperty("error");
        Assert.Equal("FLUX_PARENT_ROOT_REFUSED", error.GetProperty("code").GetString());
        Assert.Equal("FLUX_PARENT_ROOT_REFUSED", error.GetProperty("context").GetProperty("report").GetProperty("stoppedBy").GetProperty("code").GetString());
        Assert.Equal(0, exit);
        var module = output.GetProperty("data").GetProperty("modules")[0];
        Assert.Equal(("main", "create", true), (module.GetProperty("name").GetString(), module.GetProperty("action").GetString(),
            module.GetProperty("deployed").GetBoolean()));
        Assert.Equal("Flux_1", module.GetProperty("moduleSlotIdAfter").GetString());
        Assert.Equal("Flux_1", module.GetProperty("deploy").GetProperty("newRootSlotId").GetString());
        Assert.Equal(Path.GetFullPath(StatePath), module.GetProperty("deploy").GetProperty("stateFile").GetString());
        Assert.Single(_deployer.Executions);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("deploy-manifest")]
    public async Task CliDiscardPendingRemovesOnlyThatRecordWithoutConnecting(string sub)
    {
        var manifest = Manifest();
        string[] Command(params string[] extra) => sub == "deploy" ? SingleArgs(extra) : ["flux", "deploy-manifest", manifest, .. extra];
        var statePath = sub == "deploy" ? SingleStatePath : StatePath;
        _deployer.Execute = (_, _) => Task.FromResult(FluxTestDeployer.Outcome(FluxDeployOutcome.Unknown,
            FluxDeployStage.Creating, FluxDeploySendStatus.Unknown));
        var (pendingExit, pending) = await Cli(Command("--parent", Parent));
        Assert.Equal(7, pendingExit);
        var operationId = pending.GetProperty("error").GetProperty("context").GetProperty("operationId").GetString()!;
        Assert.Equal($"--discard-pending {operationId} --yes", pending.GetProperty("error").GetProperty("context").GetProperty("discardPending").GetString());
        Assert.Equal(1, _world.Connects);

        var (unconfirmedExit, unconfirmed) = await Cli(Command("--discard-pending", operationId));
        Assert.Equal(6, unconfirmedExit);
        Assert.Equal("CONFIRMATION_REQUIRED", unconfirmed.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(FluxDeployStateStore.Load(statePath).Pending);

        var (exit, discarded) = await Cli(Command("--discard-pending", operationId, "--yes"));
        Assert.Equal(0, exit);
        Assert.Equal(operationId, discarded.GetProperty("data").GetProperty("operationId").GetString());
        Assert.Equal(Path.GetFullPath(statePath), discarded.GetProperty("data").GetProperty("stateFile").GetString());
        Assert.Empty(FluxDeployStateStore.Load(statePath).Pending);
        Assert.Equal(1, _world.Connects); // discarding never connects
        Assert.Single(_deployer.Executions);

        // The next deployment proceeds; the unknown attempt created nothing in this fake world.
        _deployer.Execute = null;
        var (againExit, _) = await Cli(Command("--parent", Parent));
        Assert.Equal(0, againExit);
    }

    [Fact]
    public async Task CliDiscardPendingIsRefusedForOtherFluxCommands()
    {
        var manifest = Manifest();

        var (exit, output) = await Cli("flux", "watch", manifest, "--discard-pending", "OP", "--yes");

        Assert.Equal(2, exit);
        Assert.Equal("INVALID_OPTION", output.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, _world.Connects);
    }
}

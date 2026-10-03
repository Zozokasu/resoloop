using RLoop.Core;
using RLoop.Cli;
using RLoop.Flux;

namespace RLoop.Tests;

// ROADMAP-9 unit 4a: apply, the direct writes and the Flux deploy guard share one session lock per URL and one
// pending rule (lock-and-state contract; P5, P10, F8). The apply expectations are those of S3; only the Flux side is new.
public sealed partial class ApplyWorkflowTests
{
    private const string FluxModule = "Main";

    private static (FluxTestWorld World, FluxTestDeployer Deployer) SharedFluxWorld(string lockDirectory)
    {
        var world = new FluxTestWorld { Url = "ws://fake/" }; // the same lock key as FakeResoniteClient ("ws://fake")
        SessionLockTestIsolation.Share(world, lockDirectory);
        return (world, new FluxTestDeployer(world));
    }

    private static Task<FluxSingleDeployResult> FluxDeploy(FluxTestWorld world, FluxTestDeployer deployer, string project) =>
        FluxSingleDeploy.DeployAsync(world, deployer, project, FluxModule, FluxTestWorld.Parent, new Uri(world.Url), null, null);

    [Fact]
    public async Task R9ApplyPendingStopsAFluxDeploymentBeforeItsDeployerRuns()
    {
        var directory = Path.Combine(_root, "locks");
        var client = new FakeResoniteClient { LoseNextSlotCreateResponse = true };
        SessionLockTestIsolation.Share(client, directory);
        var applyState = Path.Combine(_root, "first.state.json");
        await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(Document("first", "[]"), new(applyState)));
        Assert.Single(ApplyStateStore.Load(applyState, "first").Pending);
        var (world, deployer) = SharedFluxWorld(directory);
        var project = Path.Combine(_root, "flux-project");
        var before = world.Snapshot();

        var e = await Assert.ThrowsAsync<RLoopException>(() => FluxDeploy(world, deployer, project));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", e.Code);
        Assert.Equal("previousStatePending", e.Context["reason"]);
        Assert.Equal(applyState, e.Context["stateFile"]);
        Assert.Empty(deployer.Executions);
        Assert.Equal(before, world.Snapshot());
        Assert.False(File.Exists(FluxDeployStateStore.ResolveSingleDeployStatePath(project, FluxModule)));
    }

    [Fact]
    public async Task R9FluxPendingStopsApplyAndDirectWritesUntilItIsDiscarded()
    {
        var directory = Path.Combine(_root, "locks");
        var (world, deployer) = SharedFluxWorld(directory);
        deployer.Execute = (_, _) => Task.FromResult(FluxTestDeployer.Outcome(FluxDeployOutcome.Unknown, FluxDeployStage.Creating,
            FluxDeploySendStatus.Unknown));
        var project = Path.Combine(_root, "flux-project");
        var unverified = await Assert.ThrowsAsync<RLoopException>(() => FluxDeploy(world, deployer, project));
        Assert.Equal("FLUX_DEPLOY_UNVERIFIED", unverified.Code);
        var fluxState = FluxDeployStateStore.ResolveSingleDeployStatePath(project, FluxModule);
        var client = new FakeResoniteClient();
        SessionLockTestIsolation.Share(client, directory);
        var slot = await client.CreateSlotAsync(new("Root", "Direct"));
        client.ResetWriteCounts();

        var apply = await Assert.ThrowsAsync<RLoopException>(() =>
            new WorldService(client).ApplyAsync(Document("other", "[]"), new(Path.Combine(_root, "other.state.json"))));
        var direct = await Program.RunAsync(["slot", "delete", slot, "--yes", "--url", "ws://fake", "--json"],
            _ => Task.FromResult<IResoniteClient>(client));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", apply.Code);
        Assert.Equal("previousFluxStatePending", apply.Context["reason"]);
        Assert.Equal(fluxState, apply.Context["stateFile"]);
        Assert.Equal(7, direct);
        Assert.Equal(0, client.Writes);

        // Discarding the Flux pending record (offline) lets the other writers continue.
        FluxDeployPendingDiscard.Discard(fluxState, (string)unverified.Context["operationId"]!, confirmed: true);
        var result = await new WorldService(client).ApplyAsync(Document("other", "[]"), new(Path.Combine(_root, "other.state.json")));
        Assert.Equal(1, result.SlotsCreated);
    }

    [Fact]
    public async Task R9FluxDeploymentAndApplyOnTheSameUrlAreBusyForEachOther()
    {
        var directory = Path.Combine(_root, "locks");
        var (world, deployer) = SharedFluxWorld(directory);
        var project = Path.Combine(_root, "flux-project");
        var client = new FakeResoniteClient();
        SessionLockTestIsolation.Share(client, directory);

        // The guard holds the lock while its deployer runs.
        var deployerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deployerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deployer.Execute = async (request, _) =>
        {
            deployerEntered.TrySetResult();
            await deployerRelease.Task;
            return deployer.Replace(request);
        };
        var deploying = Task.Run(() => FluxDeploy(world, deployer, project));
        await deployerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var e = await Assert.ThrowsAsync<RLoopException>(() =>
                new WorldService(client).ApplyAsync(Document("other", "[]"), new(Path.Combine(_root, "other.state.json"))));
            Assert.Equal("APPLY_SESSION_BUSY", e.Code);
            Assert.Equal(0, client.Writes);
        }
        finally { deployerRelease.TrySetResult(); }
        Assert.Equal("Flux_1", (await deploying).OutputPath);

        // An apply holding the lock (inside validation) makes the guard stop before its deployer runs.
        var held = Document("held", "[]") with { Components = [new("Test.Target", null, Key: "c")] };
        client.RegisterDefinitions(held);
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnDescribe = () => { applyEntered.TrySetResult(); applyRelease.Task.GetAwaiter().GetResult(); };
        var applying = Task.Run(() => new WorldService(client).ApplyAsync(held, new(Path.Combine(_root, "held.state.json"))));
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var busy = await Assert.ThrowsAsync<RLoopException>(() => FluxDeploy(world, deployer, project));
            Assert.Equal("APPLY_SESSION_BUSY", busy.Code);
            Assert.Single(deployer.Executions);
        }
        finally { applyRelease.TrySetResult(); }
        await applying;
    }
}

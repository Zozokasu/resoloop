using RLoop.Core;
using RLoop.Flux.Deployer;

namespace RLoop.Tests;

[CollectionDefinition("Flux SDK console", DisableParallelization = true)]
public sealed class FluxSdkConsoleCollection;

[Collection("Flux SDK console")]
public sealed class FluxDeployerBoundaryTests
{
    private const string Url = "ws://localhost:47610/";
    private const string Input = "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<float>>";
    private const string Output = "[FrooxEngine]FrooxEngine.FieldDriveBase<float>+Proxy";
    private static readonly FluxModulePortInfo[] Expected =
    [new("Speed", "source", null, "Input:Speed", null, [Input], Input),
     new("Angle", "drive", null, "Output:Angle", null, [Output], Output)];
    private static FluxDeployWriterIdentity Identity(string? id = "S-one") => new(Url, id, id is null ? "unknown" : "matched", "2026.9.18.82", "0.13.1.0");
    private static FluxDeployExecuteRequest Request() => new("project", "Main", "parent", new(Url), null, null,
        new Dictionary<string, string> { ["Speed"] = "field-in" }, new Dictionary<string, string> { ["Angle"] = "field-out" },
        "Main", "old", TimeSpan.FromSeconds(2))
        { ExpectedWriterIdentity = Identity(), ExpectedPorts = Expected, RequireAllPortsBound = true };

    private sealed class Connection : IFluxWriterConnection
    {
        public int Connects, Removes, Creates, Disposals, Reads, VersionReads, ChildrenReads;
        public FluxDeployWriterIdentity Versions = Identity();
        public Action? AfterRemove, AfterCreate;
        public Func<Task<FluxDeployWriterIdentity>>? ReadVersions;
        public Func<string, FluxWriterSlot>? ReadSlot;
        public Func<Task<IReadOnlyList<FluxWriterSlot>>>? ReadChildren;
        public FluxPreviousRootRemoval RemovalAnswer = FluxPreviousRootRemoval.Removed;
        public List<FluxWriterSlot> Children = [new("old", "Main", "parent", true)];
        public readonly TaskCompletionSource<FluxDeployWriterIdentity> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsClosed => Disposals > 0 && !ResidualConnection;
        public bool ResidualConnection;
        public void Dispose() { Disposals++; Pending.TrySetCanceled(); }
        public Task<FluxDeployWriterIdentity> GetIdentityAsync() { VersionReads++; return ReadVersions?.Invoke() ?? Task.FromResult(Versions); }
        public Task<FluxWriterSlot> ReadSlotAsync(string id)
        { Reads++; return Task.FromResult(ReadSlot?.Invoke(id) ?? new FluxWriterSlot(id, id == "old" ? "Main" : "ResoLoop_Test", id == "old" ? "parent" : "Root", true)); }
        public Task<IReadOnlyList<FluxWriterSlot>> ReadChildrenAsync(string id)
        { Assert.Equal("parent", id); ChildrenReads++; return ReadChildren?.Invoke() ?? Task.FromResult<IReadOnlyList<FluxWriterSlot>>(Children.ToArray()); }
        public Task<FluxPreviousRootRemoval> RemoveAsync(string id)
        { Assert.Equal("old", id); Removes++; Children.RemoveAll(child => child.Id == id); AfterRemove?.Invoke(); return Task.FromResult(RemovalAnswer); }
        public Task<FluxBatchClassification> CreateAsync()
        { Creates++; AfterCreate?.Invoke(); return Task.FromResult(new FluxBatchClassification(true, "new", [])); }
    }
    private sealed class Discovery : IFluxWriterDiscovery
    {
        public int Calls;
        public string Id = "S-one";
        public Func<int, Uri, DateTime, CancellationToken, Task<IReadOnlyList<DiscoveredResoniteSession>>>? Observe;
        public Task<IReadOnlyList<DiscoveredResoniteSession>> ObserveAsync(Uri url, DateTime started, CancellationToken token)
        {
            Calls++;
            return Observe?.Invoke(Calls, url, started, token) ?? Task.FromResult<IReadOnlyList<DiscoveredResoniteSession>>(
                [new(Id, "session", Url, DateTime.UtcNow)]);
        }
    }
    private static Task<FluxDeployExecution> Run(Connection connection, Discovery? discovery = null, FluxDeployExecuteRequest? request = null,
        IReadOnlyList<FluxModulePortInfo>? actual = null, CancellationToken token = default) =>
        FluxWriterBoundary.ExecuteAsync(request ?? Request(), actual ?? Expected.Select(port => port with { SlotId = "unsent-new-id" }).ToArray(),
            "new", _ => { connection.Connects++; return Task.FromResult<IFluxWriterConnection>(connection); }, discovery ?? new(), token);

    [Fact]
    public async Task MatchedFreshEvidenceIsCheckedBeforeEachSendAndAfterBatch()
    {
        var connection = new Connection();
        var result = await Run(connection);
        Assert.Equal((1, 1, 1), (connection.Removes, connection.Creates, connection.Disposals));
        Assert.Equal(FluxDeployOutcome.Created, result.Outcome);
        Assert.Equal(["beforeRemoval", "beforeCreation", "afterCreation"], result.WriterObservations.Select(item => item.Checkpoint));
        Assert.All(result.WriterObservations, item => Assert.Equal("S-one", item.Identity.DiscoverSessionId));
        Assert.Equal(3, connection.VersionReads);
        Assert.Equal(Identity(), result.ExpectedWriterIdentity);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("multiple")]
    [InlineData("missing")]
    [InlineData("otherEndpoint")]
    public async Task IncompleteAnnouncementsAreUnknownNeverReusedAsMatchedEvidence(string kind)
    {
        var discovery = new Discovery { Observe = (_, _, started, _) => Task.FromResult<IReadOnlyList<DiscoveredResoniteSession>>(kind switch
        {
            "stale" => [new("S-one", "", Url, started.AddSeconds(-1))],
            "multiple" => [new("S-one", "", Url, DateTime.UtcNow), new("S-two", "", Url, DateTime.UtcNow)],
            "otherEndpoint" => [new("S-two", "", "ws://localhost:47611/", DateTime.UtcNow)],
            _ => []
        }) };
        var result = await Run(new(), discovery);
        Assert.Equal(FluxDeployOutcome.Created, result.Outcome); // F1 permits unknown identity, not false proof.
        Assert.All(result.WriterObservations, item => { Assert.Equal("unknown", item.Identity.IdentityStatus); Assert.Null(item.Identity.DiscoverSessionId); });
    }

    [Theory]
    [InlineData("session")]
    [InlineData("endpoint")]
    [InlineData("version")]
    public async Task KnownMismatchBeforeSendRefusesWithNoRemovalOrCreation(string kind)
    {
        var connection = new Connection();
        var discovery = new Discovery();
        if (kind == "session") discovery.Id = "S-two";
        if (kind == "endpoint") connection.Versions = Identity() with { NormalizedUrl = "ws://localhost:47611/" };
        if (kind == "version") connection.Versions = Identity() with { ResoniteLinkVersion = "changed" };
        var result = await Run(connection, discovery);
        Assert.Equal((FluxDeployOutcome.NotSent, FluxDeploySendStatus.NotSentProven), (result.Outcome, result.SendStatus));
        Assert.Equal((0, 0, 1), (connection.Removes, connection.Creates, connection.Disposals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionDriftAfterRemovalOrBatchKeepsUnknownEvidence(bool afterBatch)
    {
        var connection = new Connection();
        var discovery = new Discovery();
        if (afterBatch) connection.AfterCreate = () => discovery.Id = "S-two";
        else connection.AfterRemove = () => discovery.Id = "S-two";
        var result = await Run(connection, discovery);
        Assert.Equal(FluxDeployOutcome.Unknown, result.Outcome);
        Assert.Equal(FluxDeploySendStatus.Sent, result.SendStatus);
        Assert.Equal((1, afterBatch ? 1 : 0, 1), (connection.Removes, connection.Creates, connection.Disposals));
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("remove")]
    [InlineData("add")]
    [InlineData("mode")]
    [InlineData("modifier")]
    [InlineData("slotName")]
    [InlineData("carrier")]
    [InlineData("components")]
    public async Task ExactExecutionShapeDriftRefusesBeforeConnectionAndOldRootRemoval(string change)
    {
        FluxModulePortInfo[] actual = change switch
        {
            "rename" => [Expected[0] with { Name = "Velocity" }, Expected[1]],
            "remove" => [Expected[0]],
            "add" => [..Expected, new("Extra", "source", null, "Input:Extra", null, [Input], Input)],
            "mode" => [Expected[0] with { Direction = "drive" }, Expected[1]],
            "modifier" => [Expected[0] with { Modifier = "element" }, Expected[1]],
            "slotName" => [Expected[0] with { SlotName = "Input:[Elem]Speed" }, Expected[1]],
            "carrier" => [Expected[0] with { ExpectedCarrierType = "different<int>" }, Expected[1]],
            _ => [Expected[0] with { ComponentTypes = ["different<int>"] }, Expected[1]]
        };
        var connection = new Connection();
        var result = await Run(connection, actual: actual);
        Assert.Equal((FluxDeployOutcome.NotSent, FluxDeploySendStatus.NotSentProven), (result.Outcome, result.SendStatus));
        Assert.Equal((0, 0, 0, 0, 0), (connection.Connects, connection.Removes, connection.Creates, connection.Reads, connection.Disposals));
    }

    [Fact]
    public async Task ExecutionRepeatsBindingKeysModesAndCompletenessChecks()
    {
        foreach (var request in new[] { Request() with { InputMap = new Dictionary<string, string> { ["Sped"] = "x" } },
                     Request() with { OutputMap = new Dictionary<string, string> { ["Speed"] = "x" } }, Request() with { OutputMap = null },
                     Request() with { ExpectedPorts = null } })
        {
            var connection = new Connection();
            var result = await Run(connection, request: request);
            Assert.Equal(FluxDeploySendStatus.NotSentProven, result.SendStatus);
            Assert.Equal(0, connection.Reads);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineClosesAndSettlesOwnedConnectionBeforeReturning(bool afterBatch)
    {
        var connection = new Connection();
        var enteredBlockedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ReadVersions = () =>
        {
            if (afterBatch && connection.Creates == 0) return Task.FromResult(connection.Versions);
            enteredBlockedRead.TrySetResult();
            return connection.Pending.Task;
        };
        var work = Run(connection, request: Request() with { Deadline = TimeSpan.FromSeconds(1) });
        await enteredBlockedRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await work;
        Assert.Equal(1, connection.Disposals);
        Assert.True(connection.Pending.Task.IsCompleted);
        Assert.Equal(afterBatch ? FluxDeployOutcome.Unknown : FluxDeployOutcome.NotSent, result.Outcome);
        Assert.Equal(afterBatch ? 1 : 0, connection.Creates);
    }

    [Fact]
    public async Task PostBatchUnreadAndCancellationNeverReturnCreated()
    {
        var unread = new Connection();
        unread.ReadVersions = () => unread.Creates == 0 ? Task.FromResult(unread.Versions) : Task.FromException<FluxDeployWriterIdentity>(new IOException("version read lost"));
        Assert.Equal(FluxDeployOutcome.Unknown, (await Run(unread)).Outcome);
        using var cancelled = new CancellationTokenSource();
        var connection = new Connection { AfterCreate = cancelled.Cancel };
        Assert.Equal(FluxDeployOutcome.Unknown, (await Run(connection, token: cancelled.Token)).Outcome);
        Assert.Equal(1, connection.Disposals);
    }

    [Fact]
    public async Task ExactPreviousRootIsRecheckedAfterTheAnnouncementWindowBeforeRemoval()
    {
        var connection = new Connection();
        connection.ReadSlot = id => new(id, id == "old" ? "Main" : "ResoLoop_Test", connection.Reads >= 3 ? "other-parent" : "parent", true);
        var result = await Run(connection);
        Assert.Equal(FluxDeploySendStatus.NotSentProven, result.SendStatus);
        Assert.Equal((0, 0, 1), (connection.Removes, connection.Creates, connection.Disposals));
    }

    [Fact]
    public async Task NoBindingSingleDeployCanProceedWithUnknownPreparationPorts()
    {
        var result = await Run(new() { Children = [] }, request: Request() with
            { ExpectedPorts = null, InputMap = null, OutputMap = null, RequireAllPortsBound = false, PreviousRootSlotId = null });
        Assert.Equal(FluxDeployOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task UnconfirmedConnectionClosureReportsResidualUncertainty()
    {
        var connection = new Connection { ResidualConnection = true };
        var result = await Run(connection);
        Assert.Equal((FluxDeployOutcome.Unknown, FluxDeployStage.Unknown, FluxDeploySendStatus.Unknown),
            (result.Outcome, result.Stage, result.SendStatus));
        Assert.Contains("residual", result.Error);
        Assert.Equal(1, connection.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNameAddedDuringFreshIdentityWindowStopsBeforeFirstWrite(bool noPrevious)
    {
        var connection = new Connection();
        if (noPrevious) connection.Children.Clear();
        var discovery = new Discovery { Observe = (_, _, _, _) =>
        {
            connection.Children.Add(new("foreign", "Main", "parent", true));
            return Task.FromResult<IReadOnlyList<DiscoveredResoniteSession>>([new("S-one", "", Url, DateTime.UtcNow)]);
        } };
        var result = await Run(connection, discovery, Request() with { PreviousRootSlotId = noPrevious ? null : "old" });
        Assert.Equal((FluxDeployOutcome.NotSent, FluxDeploySendStatus.NotSentProven), (result.Outcome, result.SendStatus));
        Assert.Equal((0, 0, 1), (connection.Removes, connection.Creates, connection.Disposals));
        Assert.Contains("foreign", result.Error);
    }

    [Fact]
    public async Task InitiallyAbsentOldIdReappearingUnderTheSameNameIsNeverAdopted()
    {
        var connection = new Connection { Children = [], ReadSlot = id => new(id, "Main", "parent", id != "old") };
        var discovery = new Discovery { Observe = (_, _, _, _) =>
        {
            connection.Children.Add(new("old", "Main", "parent", true));
            return Task.FromResult<IReadOnlyList<DiscoveredResoniteSession>>([new("S-one", "", Url, DateTime.UtcNow)]);
        } };
        var result = await Run(connection, discovery);
        Assert.Equal((FluxDeployOutcome.NotSent, FluxDeploySendStatus.NotSentProven), (result.Outcome, result.SendStatus));
        Assert.Equal((0, 0, 1), (connection.Removes, connection.Creates, connection.Disposals));
        Assert.Contains("old", result.Error);
    }

    [Theory]
    [InlineData(FluxPreviousRootRemoval.Removed)]
    [InlineData(FluxPreviousRootRemoval.NotFound)]
    public async Task SameNameAddedAfterRemovalAnswerLeavesUnknownAndSendsNoCreation(FluxPreviousRootRemoval answer)
    {
        var connection = new Connection { RemovalAnswer = answer };
        var discovery = new Discovery { Observe = (call, _, _, _) =>
        {
            if (call == 2) connection.Children.Add(new("foreign", "Main", "parent", true));
            return Task.FromResult<IReadOnlyList<DiscoveredResoniteSession>>([new("S-one", "", Url, DateTime.UtcNow)]);
        } };
        var result = await Run(connection, discovery);
        Assert.Equal((FluxDeployOutcome.Unknown, FluxDeployStage.Removed, FluxDeploySendStatus.Sent), (result.Outcome, result.Stage, result.SendStatus));
        Assert.Equal((1, 0, 1), (connection.Removes, connection.Creates, connection.Disposals));
        Assert.Equal(answer, result.PreviousRootRemoval);
    }

    [Theory]
    [InlineData(false, "unread")]
    [InlineData(true, "unread")]
    [InlineData(false, "partial")]
    [InlineData(true, "partial")]
    [InlineData(false, "nameMissing")]
    [InlineData(true, "nameMissing")]
    [InlineData(false, "idMissing")]
    [InlineData(true, "idMissing")]
    [InlineData(false, "wrongParent")]
    [InlineData(true, "wrongParent")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "listMissing")]
    [InlineData(true, "listMissing")]
    public async Task IncompleteDirectChildrenRefuseAtTheActualWriteGate(bool afterRemoval, string fault)
    {
        var connection = new Connection();
        connection.ReadChildren = () =>
        {
            if (afterRemoval && connection.Removes == 0) return Task.FromResult<IReadOnlyList<FluxWriterSlot>>(connection.Children.ToArray());
            return fault switch
            {
                "unread" => Task.FromException<IReadOnlyList<FluxWriterSlot>>(new IOException("parent read lost")),
                "partial" => Task.FromResult<IReadOnlyList<FluxWriterSlot>>([new("foreign", "Main", "parent", false)]),
                "nameMissing" => Task.FromResult<IReadOnlyList<FluxWriterSlot>>([new("foreign", null!, "parent", true)]),
                "idMissing" => Task.FromResult<IReadOnlyList<FluxWriterSlot>>([new("", "Other", "parent", true)]),
                "wrongParent" => Task.FromResult<IReadOnlyList<FluxWriterSlot>>([new("foreign", "Other", "elsewhere", true)]),
                "duplicate" => Task.FromResult<IReadOnlyList<FluxWriterSlot>>([new("foreign", "Other", "parent", true), new("foreign", "Other", "parent", true)]),
                _ => Task.FromResult<IReadOnlyList<FluxWriterSlot>>(null!)
            };
        };
        var result = await Run(connection);
        Assert.Equal(afterRemoval ? FluxDeployOutcome.Unknown : FluxDeployOutcome.NotSent, result.Outcome);
        Assert.Equal(afterRemoval ? FluxDeploySendStatus.Sent : FluxDeploySendStatus.NotSentProven, result.SendStatus);
        Assert.Equal((afterRemoval ? 1 : 0, 0, 1), (connection.Removes, connection.Creates, connection.Disposals));
    }

    [Fact]
    public async Task DifferentNameAddedDuringBothIdentityWindowsDoesNotBlockExactReplacement()
    {
        var connection = new Connection();
        var discovery = new Discovery { Observe = (call, _, _, _) =>
        {
            if (call <= 2) connection.Children.Add(new("other-" + call, "Other", "parent", true));
            return Task.FromResult<IReadOnlyList<DiscoveredResoniteSession>>([new("S-one", "", Url, DateTime.UtcNow)]);
        } };
        var result = await Run(connection, discovery);
        Assert.Equal(FluxDeployOutcome.Created, result.Outcome);
        Assert.Equal((1, 1, 2, 1), (connection.Removes, connection.Creates, connection.ChildrenReads, connection.Disposals));
        Assert.Equal(["other-1", "other-2"], connection.Children.Select(child => child.Id));
    }

    [Fact]
    public async Task ConcurrentConsoleCapturesAreIsolatedAndRestoredOnExceptionAndCancellation()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var secondEntered = false;
        var first = FluxConsoleCapture.RunAsync<string>(async (stdout, stderr) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            firstEntered.SetResult();
            Console.Write("first-out"); Console.Error.Write("first-error");
            await release.Task;
            Interlocked.Decrement(ref active);
            return stdout + "|" + stderr;
        }, default);
        await firstEntered.Task;
        var second = FluxConsoleCapture.RunAsync<string>((stdout, stderr) =>
        {
            secondEntered = true;
            Assert.Equal(1, Interlocked.Increment(ref active));
            Console.Write("second-out"); Console.Error.Write("second-error");
            Interlocked.Decrement(ref active);
            return Task.FromResult(stdout + "|" + stderr);
        }, default);
        using var waiting = new CancellationTokenSource();
        var cancelledBodyCalled = false;
        var cancelled = FluxConsoleCapture.RunAsync<string>((_, _) => { cancelledBodyCalled = true; return Task.FromResult("bad"); }, waiting.Token);
        waiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(secondEntered);
        release.SetResult();
        Assert.Equal("first-out|first-error", await first);
        Assert.Equal("second-out|second-error", await second);
        Assert.False(cancelledBodyCalled);
        await Assert.ThrowsAsync<IOException>(() => FluxConsoleCapture.RunAsync<string>((_, _) => throw new IOException("test"), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FluxConsoleCapture.RunAsync<string>((_, _) => Task.FromCanceled<string>(new(true)), default));
        Assert.Same(originalOut, Console.Out); Assert.Same(originalError, Console.Error);
    }
}

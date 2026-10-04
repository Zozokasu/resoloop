using System.Text.Json;
using RLoop.Core;
using RLoop.Flux;

namespace RLoop.Tests;

// ROADMAP-9 unit 1: the two-step deployer contract (P1-b), the rules applied to the world's answers, and the
// orchestrator's use of them (through the deploy guard since unit 4a). The F# deployer itself needs a live world
// and is not covered here.
public sealed class FluxDeployContractTests : IDisposable
{
    [Theory]
    [InlineData("float", "source", null, "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<System.Single>>")]
    [InlineData("int", "source", null, "GlobalReference<IValue<System.Int32>>")]
    [InlineData("Slot", "source", null, "GlobalReference<SyncRef<[FrooxEngine]FrooxEngine.Slot>>")]
    [InlineData("Slot", "source", "element", "GlobalReference<[FrooxEngine]FrooxEngine.Slot>")]
    [InlineData("float3", "source", "element", "GlobalReference<Sync<[FrooxEngine]Elements.Core.float3>>")]
    [InlineData("PhysicalButton", "source", "global", "GlobalReference<[FrooxEngine]FrooxEngine.PhysicalButton>")]
    [InlineData("float?", "source", null, "GlobalReference<IValue<System.Nullable<System.Single>>>")]
    [InlineData("Dictionary<float,int>", "source", null, "GlobalReference<IValue<System.Collections.Generic.Dictionary<System.Single,System.Int32>>>")]
    [InlineData("bool", "drive", null, "[FrooxEngine]FrooxEngine.ProtoFlux.CoreNodes.FieldDriveBase<System.Boolean>+Proxy")]
    public void PreparedCarrierTypeUsesTheSameDeclaredTypeSemantics(string declaredType, string mode, string? modifier, string carrier)
    {
        var actual = FluxTestDeployer.Port("Port", mode, carrier, modifier);

        Assert.Null(FluxDeployClassifier.ValidatePreparedPortTypes(
            [new("Port", mode, declaredType, modifier)], [actual], [new("Port", mode)]));
    }

    [Theory]
    [InlineData("GlobalReference<IValue<int>>")]
    [InlineData("GlobalReference<IValue<>>")]
    [InlineData("GlobalReference<IValue<List<>>>")]
    [InlineData("GlobalReference<IValue<float,int>>")]
    [InlineData("GlobalReference<IValue<float>")]
    [InlineData("GlobalReference<Sync<float>>")]
    [InlineData("GlobalReference<float>")]
    [InlineData("GlobalValue<float>")]
    public void UnsupportedOrDifferentPreparedTypesCannotJoinFloatPreflight(string carrier)
    {
        Assert.NotNull(FluxDeployClassifier.ValidatePreparedPortTypes([new("Port", "source", "float", null)],
            [FluxTestDeployer.Port("Port", "source", carrier)], [new("Port", "source")]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("element")]
    [InlineData("global")]
    public void MutableDeclarationIsNotCertifiedFromPrefixesThatCannotRepresentIt(string? reportedModifier)
    {
        var actual = FluxTestDeployer.Port("Port", "source", "GlobalReference<Sync<float>>", reportedModifier);

        Assert.NotNull(FluxDeployClassifier.ValidatePreparedPortTypes([new("Port", "source", "float", "mutable")],
            [actual], [new("Port", "source")]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicateSame")]
    [InlineData("duplicateDifferent")]
    [InlineData("expectedNotSent")]
    [InlineData("slotNameDrift")]
    [InlineData("referenceOutput")]
    public void BoundPreparationMustHaveOneActualSupportedCarrier(string fault)
    {
        const string carrier = "GlobalReference<IValue<float>>";
        var actual = FluxTestDeployer.Port("Port", "source", carrier);
        actual = fault switch
        {
            "missing" => actual with { ExpectedCarrierType = null },
            "duplicateSame" => actual with { ComponentTypes = [carrier, carrier] },
            "duplicateDifferent" => actual with { ComponentTypes = [carrier, "GlobalReference<IValue<int>>"] },
            "expectedNotSent" => actual with { ComponentTypes = ["GlobalReference<IValue<int>>"] },
            "slotNameDrift" => actual with { SlotName = "Input:[Global]Port" },
            "referenceOutput" => FluxTestDeployer.Port("Port", "drive", "ReferenceDrive<Slot>+Proxy"),
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.NotNull(FluxDeployClassifier.ValidatePreparedPortTypes(null, [actual], [new("Port", actual.Direction)]));
    }

    [Fact]
    public void UnboundSinglePreparationDoesNotNeedCarrierTypeEvidence()
    {
        var actual = FluxTestDeployer.Port("Port", "source", "GlobalValue<float>") with { ExpectedCarrierType = null };

        Assert.Null(FluxDeployClassifier.ValidatePreparedPortTypes(null, [actual], []));
    }

    [Fact]
    public void SourceDeclarationsAreAdditivePlainMetadataOnTheGuardRequest()
    {
        var request = new FluxDeployGuardRequest("project", "Main", "main", "parent", new("ws://localhost:1"),
            null, null, null, null, "hash", null, "state", null, TimeSpan.FromMinutes(1))
            { DeclaredPorts = [new("Count", "source", "float", null)] };
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var document = JsonDocument.Parse(json);
        var port = Assert.Single(document.RootElement.GetProperty("declaredPorts").EnumerateArray());
        Assert.Equal("Count", port.GetProperty("name").GetString());
        Assert.Equal("source", port.GetProperty("direction").GetString());
        Assert.Equal("float", port.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, port.GetProperty("modifier").ValueKind);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-flux-deploy-" + Guid.NewGuid().ToString("N"));

    public FluxDeployContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // ---- inner responses of the creation batch ----

    [Fact]
    public void BatchIsCreatedOnlyWhenEveryInnerResponseIsASuccessfulNewEntityId()
    {
        var result = FluxDeployClassifier.ClassifyBatch(true, null, 3, "root-id",
            [Created(0, "root-id"), Created(1, "slot-1"), Created(2, "component-2")]);

        Assert.True(result.Created);
        Assert.Equal("root-id", result.NewRootSlotId);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void BatchWithAnInnerFailureIsNotCreatedEvenWhenTheOuterResponseSucceeded()
    {
        // Live fact U3: the batch is not atomic and its outer Success stays true.
        var result = FluxDeployClassifier.ClassifyBatch(true, null, 3, "root-id",
        [
            Created(0, "root-id"),
            new FluxBatchInnerResponse(1, "Response", false, null, "Slot with ID 'X' not found."),
            Created(2, "slot-2")
        ]);

        Assert.False(result.Created);
        Assert.Equal("root-id", result.NewRootSlotId);
        Assert.Equal("#1 Response: Slot with ID 'X' not found.", Assert.Single(result.Failures));
    }

    [Fact]
    public void BatchWithFewerInnerResponsesThanOperationsIsNotCreated()
    {
        var result = FluxDeployClassifier.ClassifyBatch(true, null, 3, "root-id", [Created(0, "root-id"), Created(1, "slot-1")]);

        Assert.False(result.Created);
        Assert.Equal("batch: 2 inner response(s) for 3 operation(s)", Assert.Single(result.Failures));
    }

    [Fact]
    public void BatchWithASuccessfulResponseThatIsNotANewEntityIdIsNotCreated()
    {
        var result = FluxDeployClassifier.ClassifyBatch(true, null, 2, "root-id",
            [Created(0, "root-id"), new FluxBatchInnerResponse(1, "Response", true, null, null)]);

        Assert.False(result.Created);
        Assert.Equal("#1 Response: expected NewEntityId", Assert.Single(result.Failures));
    }

    [Fact]
    public void BatchWhoseFirstResponseIsNotTheRootHasNoRootId()
    {
        var result = FluxDeployClassifier.ClassifyBatch(true, null, 2, "root-id",
            [new FluxBatchInnerResponse(0, "Response", false, null, "refused"), Created(1, "slot-1")]);

        Assert.False(result.Created);
        Assert.Null(result.NewRootSlotId);
    }

    [Fact]
    public void BatchWhoseRootIdDiffersFromTheRequestedIdIsNotCreated()
    {
        var result = FluxDeployClassifier.ClassifyBatch(true, null, 1, "root-id", [Created(0, "other-id")]);

        Assert.False(result.Created);
        Assert.Equal("other-id", result.NewRootSlotId);
        Assert.Contains("differs from the requested root ID 'root-id'", Assert.Single(result.Failures));
    }

    [Fact]
    public void BatchWithoutInnerResponsesOrWithAnOuterFailureIsNotCreated()
    {
        var missing = FluxDeployClassifier.ClassifyBatch(true, null, 2, "root-id", null);
        var outer = FluxDeployClassifier.ClassifyBatch(false, "refused", 1, "root-id", [Created(0, "root-id")]);
        var empty = FluxDeployClassifier.ClassifyBatch(true, null, 0, null, []);

        Assert.False(missing.Created);
        Assert.Null(missing.NewRootSlotId);
        Assert.False(outer.Created);
        Assert.Equal("batch: refused", Assert.Single(outer.Failures));
        Assert.False(empty.Created);
    }

    // ---- removal of the exact previous root ----

    [Theory]
    [InlineData("Slot with ID 'Reso_10ED9' not found.", true)]
    [InlineData("NOT FOUND", true)]
    [InlineData("Access denied.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SlotNotFoundIsRecognizedByTheSameTextTheSdkUses(string? errorInfo, bool expected) =>
        Assert.Equal(expected, FluxDeployClassifier.IsSlotNotFound(errorInfo));

    [Fact]
    public void RemovalAnswersAreClassified()
    {
        Assert.Equal(FluxPreviousRootRemoval.Removed, FluxDeployClassifier.ClassifyRemoval(true, null));
        Assert.Equal(FluxPreviousRootRemoval.NotFound,
            FluxDeployClassifier.ClassifyRemoval(false, "Slot with ID 'Reso_1' not found."));
        Assert.Equal(FluxPreviousRootRemoval.Failed, FluxDeployClassifier.ClassifyRemoval(false, "Access denied."));
        Assert.Equal(FluxPreviousRootRemoval.Failed, FluxDeployClassifier.ClassifyRemoval(false, null));
    }

    // ---- shape of the creation batch before it is sent ----

    [Fact]
    public void RootOperationMustBeAnAddSlotWithAnIdNamedAfterTheModuleBelowTheParent()
    {
        FluxSentOperation Root(string kind = "AddSlot", string? id = "root-id", string? name = "Main", string? parent = "Reso_Parent") =>
            new(0, kind, id, name, parent, null, null);

        Assert.Null(FluxDeployClassifier.ValidateRootOperation([Root()], "Reso_Parent", "Main"));
        Assert.NotNull(FluxDeployClassifier.ValidateRootOperation([], "Reso_Parent", "Main"));
        Assert.NotNull(FluxDeployClassifier.ValidateRootOperation([Root(kind: "AddComponent")], "Reso_Parent", "Main"));
        Assert.NotNull(FluxDeployClassifier.ValidateRootOperation([Root(id: null)], "Reso_Parent", "Main"));
        Assert.NotNull(FluxDeployClassifier.ValidateRootOperation([Root(parent: "Root")], "Reso_Parent", "Main"));
        Assert.NotNull(FluxDeployClassifier.ValidateRootOperation([Root(name: "main")], "Reso_Parent", "Main"));
    }

    [Fact]
    public void PortsAreReadFromThePortSlotsAndTheirComponents()
    {
        const string reference = "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.Slot>";
        const string proxy = "[FrooxEngine]FrooxEngine.FieldDriveBase<bool>+Proxy";
        FluxSentOperation[] operations =
        [
            new(0, "AddSlot", "root", "Main", "Reso_Parent", null, null),
            new(1, "AddSlot", "s-in", "Input:[Elem]Source", "root", null, null),
            new(2, "AddComponent", "c-node", null, null, "s-in", "[ProtoFluxBindings]Node"),
            new(3, "AddComponent", "c-ref", null, null, "s-in", reference),
            new(4, "AddSlot", "s-global", "Input:[Global]Speed", "root", null, null),
            new(5, "AddSlot", "s-plain", "Input:Count", "root", null, null),
            new(6, "AddSlot", "s-out", "Output:Result", "root", null, null),
            new(7, "AddComponent", "c-drive", null, null, "s-out", proxy),
            new(8, "AddSlot", "s-other", "Add", "root", null, null)
        ];

        var ports = FluxDeployClassifier.ReadPorts(operations);

        Assert.Equal(["Source", "Speed", "Count", "Result"], ports.Select(port => port.Name));
        Assert.Equal(["source", "source", "source", "drive"], ports.Select(port => port.Direction));
        Assert.Equal(["element", "global", null, null], ports.Select(port => port.Modifier));
        Assert.Equal("s-in", ports[0].SlotId);
        Assert.Equal(["[ProtoFluxBindings]Node", reference], ports[0].ComponentTypes);
        Assert.Equal(reference, ports[0].ExpectedCarrierType);
        Assert.Null(ports[1].ExpectedCarrierType);
        Assert.Equal(proxy, ports[3].ExpectedCarrierType);
    }

    [Fact]
    public void CarrierTypeIsUnknownWhenMoreThanOneCandidateExists()
    {
        FluxSentOperation[] operations =
        [
            new(0, "AddSlot", "s-in", "Input:Source", "root", null, null),
            new(1, "AddComponent", "c1", null, null, "s-in", "A.GlobalReference<int>"),
            new(2, "AddComponent", "c2", null, null, "s-in", "B.GlobalReference<float>")
        ];

        Assert.Null(Assert.Single(FluxDeployClassifier.ReadPorts(operations)).ExpectedCarrierType);
    }

    // ---- result type ----

    [Fact]
    public void OnlyCreatedIsASuccessAndStagesSerializeAsCamelCaseNames()
    {
        var created = Execution(FluxDeployOutcome.Created, FluxDeployStage.Created, FluxDeploySendStatus.Sent, "Reso_New");
        var partial = Execution(FluxDeployOutcome.PartialCreated, FluxDeployStage.Creating, FluxDeploySendStatus.Sent, "Reso_New");

        Assert.True(created.Success);
        Assert.False(partial.Success);
        Assert.All(Enum.GetValues<FluxDeployOutcome>().Where(outcome => outcome != FluxDeployOutcome.Created),
            outcome => Assert.False(Execution(outcome, FluxDeployStage.NotSent, FluxDeploySendStatus.NotSentProven, null).Success));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            Execution(FluxDeployOutcome.Unknown, FluxDeployStage.NotSent, FluxDeploySendStatus.NotSentProven, null) with
            {
                PreviousRootRemoval = FluxPreviousRootRemoval.NotAttempted
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal("unknown", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("notSent", json.RootElement.GetProperty("stage").GetString());
        Assert.Equal("notSentProven", json.RootElement.GetProperty("sendStatus").GetString());
        Assert.Equal("notAttempted", json.RootElement.GetProperty("previousRootRemoval").GetString());
        Assert.Equal(FluxDeployStage.Removing, JsonSerializer.Deserialize<FluxDeployStage>("\"removing\""));
    }

    // ---- orchestrator (through the deploy guard, ROADMAP-9 unit 4a) ----

    private readonly FluxTestWorld _world = new();
    private FluxTestDeployer? _deployer;
    private FluxTestDeployer Deployer => _deployer ??= new FluxTestDeployer(_world);
    private FluxManifestOrchestrator Orchestrator() => new(Deployer, Deployer, _world);
    private Task<FluxManifestResult> Deploy(string manifest, string parent = FluxTestWorld.Parent) =>
        Orchestrator().DeployAsync(manifest, parent, new Uri(_world.Url), null, null);

    [Fact]
    public async Task ExpectedAndActualWriterEvidenceReachManifestJsonAndExecutionPreflight()
    {
        FluxDeployWriterIdentity expected = new("ws://localhost:47610/", "S-world-1", "matched", "2026.9.18.82", "0.13.1.0");
        FluxWriterIdentityObservation actual = new("afterCreation", expected with { DiscoverSessionId = null, IdentityStatus = "unknown" });
        Deployer.Execute = (request, _) => Task.FromResult(Deployer.Replace(request) with { WriterObservations = [actual] });
        var result = await Deploy(Manifest("Main"));
        var execute = Assert.Single(Deployer.Executions);
        Assert.Equal(expected, execute.ExpectedWriterIdentity);
        Assert.Empty(execute.ExpectedPorts!);
        Assert.True(execute.RequireAllPortsBound);
        var summary = Assert.Single(result.Modules).Deploy!;
        Assert.Equal(expected, summary.ExpectedWriterIdentity);
        Assert.Equal(actual, Assert.Single(summary.WriterObservations));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(summary, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal("S-world-1", json.RootElement.GetProperty("expectedWriterIdentity").GetProperty("discoverSessionId").GetString());
        Assert.Equal("unknown", json.RootElement.GetProperty("writerObservations")[0].GetProperty("identity").GetProperty("identityStatus").GetString());
    }

    /// <summary>The run stopped with the guard's code; the error carries the run's report with that module.</summary>
    private async Task<(RLoopException Error, FluxModuleDeployment Module)> Stops(string manifest, string code, string parent = FluxTestWorld.Parent)
    {
        var error = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest, parent));
        Assert.Equal(code, error.Code);
        var report = Assert.IsType<FluxManifestResult>(error.Context[FluxManifestOrchestrator.ReportContextKey]);
        Assert.False(report.Success);
        Assert.Equal(code, report.StoppedBy!.Code);
        var module = Assert.Single(report.Modules);
        Assert.False(module.Deployed);
        return (error, module);
    }

    [Fact]
    public async Task ManifestDeployDoesNotExecuteWhenTheDeployerCompileHasErrorDiagnostics()
    {
        var manifest = Manifest("Main");
        Deployer.Prepare = _ => new FluxDeployPreparation(false, null, [FluxTestDeployer.Error("Unknown name 'missing'.")], null,
            "Module 'Main' has 1 compile error(s).", "", "");
        var world = _world.Snapshot();

        var (error, module) = await Stops(manifest, "FLUX_COMPILE_FAILED");

        Assert.Empty(Deployer.Executions);
        Assert.False(module.BuildSucceeded);
        Assert.Contains("compile error", module.Error);
        Assert.Equal("none", error.Context["worldWrites"]);
        Assert.Equal(world, _world.Snapshot());
        Assert.False(File.Exists(StatePath(manifest)));
    }

    [Fact]
    public async Task ManifestDeployTreatsAnEmptyDeclaredModuleNameAsACompileFailure()
    {
        var manifest = Manifest("Main");
        Deployer.Prepare = _ => new FluxDeployPreparation(true, "", [], [], null, "", "");

        await Stops(manifest, "FLUX_COMPILE_FAILED");

        Assert.Empty(Deployer.Executions);
        Assert.False(File.Exists(StatePath(manifest)));
    }

    [Fact]
    public async Task ManifestDeployDoesNotRecordAModuleWhenTheExecutionCompileFails()
    {
        // The source can change between the preparation and the execution; the execution compiles again.
        var manifest = Manifest("Main");
        Deployer.Execute = (_, _) => Task.FromResult(Execution(FluxDeployOutcome.CompileFailed, FluxDeployStage.NotSent,
            FluxDeploySendStatus.NotSentProven, null) with { Error = "Module 'Main' has 2 compile error(s)." });

        var (error, module) = await Stops(manifest, "FLUX_COMPILE_FAILED");

        Assert.False(module.BuildSucceeded);
        Assert.Null(module.ModuleSlotIdAfter);
        Assert.Equal("released", error.Context["pending"]);
        // The guard saves the pending record before it calls the deployer; a proven "nothing sent" releases it.
        var state = FluxDeployStateStore.Load(StatePath(manifest));
        Assert.Empty(state.Modules);
        Assert.Empty(state.Pending);
    }

    [Theory]
    [InlineData(FluxDeployOutcome.PartialCreated, FluxDeployStage.Creating, FluxDeploySendStatus.Sent, "FLUX_DEPLOY_PARTIAL")]
    [InlineData(FluxDeployOutcome.Unknown, FluxDeployStage.Creating, FluxDeploySendStatus.Unknown, "FLUX_DEPLOY_UNVERIFIED")]
    [InlineData(FluxDeployOutcome.RemoveFailed, FluxDeployStage.Removing, FluxDeploySendStatus.Sent, "FLUX_DEPLOY_PARTIAL")]
    public async Task ManifestDeployLeavesAnExecutionThatIsNotCreatedPendingAndTheNextRunStops(FluxDeployOutcome outcome,
        FluxDeployStage stage, FluxDeploySendStatus sendStatus, string code)
    {
        var manifest = Manifest("Main");
        var candidate = outcome == FluxDeployOutcome.PartialCreated ? "Reso_Candidate" : null;
        Deployer.Execute = (_, _) => Task.FromResult(Execution(outcome, stage, sendStatus, candidate));

        var (first, module) = await Stops(manifest, code);
        var second = await Assert.ThrowsAsync<RLoopException>(() => Deploy(manifest));

        Assert.True(module.BuildSucceeded);
        Assert.Null(module.ModuleSlotIdAfter);
        Assert.Equal("kept", first.Context["pending"]);
        var state = FluxDeployStateStore.Load(StatePath(manifest));
        Assert.Empty(state.Modules); // never settled, so never a no-op
        Assert.Equal(first.Context["operationId"], Assert.Single(state.Pending).OperationId);
        // P3: the next run stops on the pending record before building or calling the deployer again.
        Assert.Equal("FLUX_DEPLOY_PENDING", second.Code);
        Assert.Single(Deployer.Executions);
        Assert.Single(Deployer.BuildRequests);
    }

    [Fact]
    public async Task ManifestDeployComparesTheDeclaredModuleNameAndReplacesOnlyTheRecordedRootByItsExactId()
    {
        // The manifest's module path is 'Foo', but the source declares 'Bar': 'Bar' names the module root.
        var manifest = Manifest("Foo");
        Deployer.DeclaredNames["Foo"] = "Bar";
        _world.Add("Reso_Foo", "Foo", FluxTestWorld.Parent); // named like the path, not like the module: never touched

        var first = await Deploy(manifest);
        File.WriteAllText(Path.Combine(_root, "main.pg"), "module Bar where { 2->display }");
        var second = await Deploy(manifest);

        Assert.True(second.Success);
        Assert.Equal([null, "Flux_1"], Deployer.Executions.Select(request => request.PreviousRootSlotId));
        var request = Deployer.Executions[1];
        Assert.Equal("Foo", request.Module);
        Assert.Equal("Bar", request.ExpectedModuleName);
        Assert.Equal(FluxTestWorld.Parent, request.ParentSlotId);
        Assert.Equal(FluxDeployExecuteRequest.DefaultDeadline, request.Deadline);
        Assert.Equal("Flux_1", Assert.Single(first.Modules).ModuleSlotIdAfter);
        Assert.Equal("Flux_2", Assert.Single(second.Modules).ModuleSlotIdAfter);
        Assert.Contains("Reso_Foo", _world.ChildIds(FluxTestWorld.Parent));
        Assert.Equal(["Flux_2"], _world.ChildrenNamed(FluxTestWorld.Parent, "Bar"));
    }

    [Fact]
    public async Task ManifestDeployToAnotherParentStopsWhileTheRecordedRootStillExistsElsewhere()
    {
        var manifest = Manifest("Main");
        await Deploy(manifest);
        File.WriteAllText(Path.Combine(_root, "main.pg"), "module Main where { 2->display }");
        await Deploy(manifest);
        File.WriteAllText(Path.Combine(_root, "main.pg"), "module Main where { 3->display }");
        var state = File.ReadAllText(StatePath(manifest));

        var (error, _) = await Stops(manifest, "FLUX_MODULE_RECORD_STALE", FluxTestWorld.OtherParent);

        Assert.Equal("parentMismatch", error.Context["reason"]);
        Assert.Equal([null, "Flux_1"], Deployer.Executions.Select(request => request.PreviousRootSlotId));
        Assert.Equal(state, File.ReadAllText(StatePath(manifest)));
    }

    private string Manifest(string module)
    {
        File.WriteAllText(Path.Combine(_root, "main.pg"), "module Main where { 1->display }");
        var manifest = Path.Combine(_root, "flux.json");
        File.WriteAllText(manifest,
            $$"""{ "schemaVersion":"1", "modules":[{"name":"main","source":"main.pg","module":"{{module}}"}] }""");
        return manifest;
    }

    private string StatePath(string manifest) =>
        Path.Combine(_root, ".resoloop", "flux-state", Path.GetFileNameWithoutExtension(manifest) + ".json");

    private static FluxBatchInnerResponse Created(int index, string entityId) => new(index, "NewEntityId", true, entityId, null);

    private static FluxDeployExecution Execution(FluxDeployOutcome outcome, FluxDeployStage stage,
        FluxDeploySendStatus sendStatus, string? newRootSlotId) =>
        new(outcome, stage, sendStatus, FluxPreviousRootRemoval.NotAttempted, "Main", null, newRootSlotId, newRootSlotId,
            [], [], [], outcome == FluxDeployOutcome.Created ? null : "scripted failure", "", "");
}

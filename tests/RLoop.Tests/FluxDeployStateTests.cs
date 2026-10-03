using System.Text.Json;
using RLoop.Core;
using RLoop.Flux;

namespace RLoop.Tests;

// ROADMAP-9 unit 2: Flux deploy state v2 (kind, session identity, per-module records, pending deployments),
// reading v1 as an unconfirmed migration, and discarding a pending deployment. Expected JSON is handwritten
// from the decided shape, not produced by the serializer under test.
public sealed class FluxDeployStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-flux-state-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_root, ".resoloop", "flux-state", "flux.json");
    private static readonly FluxDeploySessionRecord Session = new("ws://localhost:1234/", "S-11111111-2222-3333-4444-555555555555", "matched");
    private static readonly DateTimeOffset Created = new(2026, 10, 3, 1, 2, 3, TimeSpan.Zero);

    public FluxDeployStateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(full, recursive: true);
    }

    internal const string HandwrittenV2 = """
        {
          "kind": "flux-deploy",
          "schemaVersion": 2,
          "session": { "normalizedUrl": "ws://localhost:1234/", "discoverSessionId": "S-aaaa", "identityStatus": "matched" },
          "modules": {
            "main": {
              "parentSlotId": "Reso_Parent",
              "rootSlotId": "Reso_Root",
              "moduleName": "Main",
              "inputHash": "ABCD",
              "sdkVersion": "1.9.0",
              "deployedAt": "2026-10-03T01:02:03+00:00",
              "bindings": [
                { "port": "Speed", "mode": "source", "targetId": "Reso_Field1" },
                { "port": "Angle", "mode": "drive", "targetId": "Reso_Field2" }
              ],
              "origin": "deployed"
            },
            "legacy": {
              "parentSlotId": "Reso_Parent",
              "rootSlotId": "Reso_Old",
              "moduleName": null,
              "inputHash": "EF01",
              "sdkVersion": null,
              "deployedAt": null,
              "bindings": [],
              "origin": "migrated-v1"
            }
          },
          "pending": [
            {
              "operationId": "op-1",
              "module": "main",
              "createdAt": "2026-10-03T02:00:00+00:00",
              "session": { "normalizedUrl": "ws://localhost:1234/", "discoverSessionId": null, "identityStatus": "unknown" },
              "parentSlotId": "Reso_Parent",
              "moduleName": "Main",
              "previousRootSlotId": "Reso_Root",
              "observedChildIds": [ "Reso_Root", "Reso_Other" ],
              "inputHash": "9999",
              "requestedRootId": "Reso_Requested",
              "stage": "creating",
              "sendStatus": "unknown",
              "candidateRootIds": [ "Reso_Requested" ],
              "reason": "deadline"
            },
            {
              "operationId": "op-2",
              "module": "other",
              "createdAt": "2026-10-03T02:05:00+00:00",
              "session": { "normalizedUrl": "ws://localhost:1234/", "discoverSessionId": "S-aaaa", "identityStatus": "matched" },
              "parentSlotId": "Reso_Parent2",
              "moduleName": "Other",
              "previousRootSlotId": null,
              "observedChildIds": [],
              "inputHash": "7777",
              "requestedRootId": null,
              "stage": "notSent",
              "sendStatus": "notSentProven",
              "candidateRootIds": []
            }
          ]
        }
        """;

    [Fact]
    public void LoadsTheDecidedV2Shape()
    {
        Write(HandwrittenV2);

        var state = FluxDeployStateStore.Load(StatePath);

        Assert.Equal("flux-deploy", state.Kind);
        Assert.Equal(2, state.SchemaVersion);
        Assert.Equal(new FluxDeploySessionRecord("ws://localhost:1234/", "S-aaaa", "matched"), state.Session);
        var main = state.Modules["main"];
        Assert.Equal(("Reso_Parent", "Reso_Root", "Main", "ABCD", "1.9.0", "deployed"),
            (main.ParentSlotId, main.RootSlotId, main.ModuleName, main.InputHash, main.SdkVersion, main.Origin));
        Assert.Equal(Created, main.DeployedAt);
        Assert.True(main.IsVerified);
        Assert.Equal(new FluxDeployBindingRecord[] { new("Speed", "source", "Reso_Field1"), new("Angle", "drive", "Reso_Field2") }, main.Bindings);
        var legacy = state.Modules["legacy"];
        Assert.Equal("migrated-v1", legacy.Origin);
        Assert.False(legacy.IsVerified);
        Assert.Null(legacy.ModuleName);
        Assert.Equal(2, state.Pending.Count);
        var first = state.Pending[0];
        Assert.Equal(("op-1", "main", "Reso_Parent", "Main", "Reso_Root", "9999", "Reso_Requested", "deadline"),
            (first.OperationId, first.Module, first.ParentSlotId, first.ModuleName, first.PreviousRootSlotId, first.InputHash, first.RequestedRootId, first.Reason));
        Assert.Equal(new FluxDeploySessionRecord("ws://localhost:1234/", null, "unknown"), first.Session);
        Assert.Equal(["Reso_Root", "Reso_Other"], first.ObservedChildIds);
        Assert.Equal(["Reso_Requested"], first.CandidateRootIds);
        Assert.Equal((FluxDeployStage.Creating, FluxDeploySendStatus.Unknown), (first.Stage, first.SendStatus));
        var second = state.Pending[1];
        Assert.Null(second.PreviousRootSlotId);
        Assert.Null(second.RequestedRootId);
        Assert.Null(second.Reason);
        Assert.Equal((FluxDeployStage.NotSent, FluxDeploySendStatus.NotSentProven), (second.Stage, second.SendStatus));
    }

    [Fact]
    public void V2RoundTripKeepsEveryValue()
    {
        Write(HandwrittenV2);
        var copy = Path.Combine(_root, "copy.json");

        FluxDeployStateStore.Save(copy, FluxDeployStateStore.Load(StatePath));

        using var expected = JsonDocument.Parse(HandwrittenV2);
        using var actual = JsonDocument.Parse(File.ReadAllText(copy));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), File.ReadAllText(copy));
    }

    [Fact]
    public void PendingIsAddedAdvancedAndSettledAsSeparateOperations()
    {
        var pending = FluxDeployPending.Begin("main", Session, "Reso_Parent", "Main", "Reso_Old", ["Reso_Old", "Reso_Sibling"], "HASH2", Created);
        Assert.Equal((FluxDeployStage.Unknown, FluxDeploySendStatus.Unknown), (pending.Stage, pending.SendStatus));
        Assert.False(string.IsNullOrWhiteSpace(pending.OperationId));

        FluxDeployStateStore.AddPending(StatePath, pending);

        using (var saved = JsonDocument.Parse(File.ReadAllText(StatePath)))
        {
            var root = saved.RootElement;
            Assert.Equal("flux-deploy", root.GetProperty("kind").GetString());
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("session").ValueKind);
            Assert.Empty(root.GetProperty("modules").EnumerateObject());
            var item = Assert.Single(root.GetProperty("pending").EnumerateArray());
            Assert.Equal(pending.OperationId, item.GetProperty("operationId").GetString());
            Assert.Equal("main", item.GetProperty("module").GetString());
            Assert.Equal("ws://localhost:1234/", item.GetProperty("session").GetProperty("normalizedUrl").GetString());
            Assert.Equal("S-11111111-2222-3333-4444-555555555555", item.GetProperty("session").GetProperty("discoverSessionId").GetString());
            Assert.Equal("matched", item.GetProperty("session").GetProperty("identityStatus").GetString());
            Assert.Equal("Reso_Parent", item.GetProperty("parentSlotId").GetString());
            Assert.Equal("Main", item.GetProperty("moduleName").GetString());
            Assert.Equal("Reso_Old", item.GetProperty("previousRootSlotId").GetString());
            Assert.Equal(["Reso_Old", "Reso_Sibling"], item.GetProperty("observedChildIds").EnumerateArray().Select(id => id.GetString()));
            Assert.Equal("HASH2", item.GetProperty("inputHash").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("requestedRootId").ValueKind);
            Assert.Equal("unknown", item.GetProperty("stage").GetString());
            Assert.Equal("unknown", item.GetProperty("sendStatus").GetString());
            Assert.Empty(item.GetProperty("candidateRootIds").EnumerateArray());
            Assert.False(item.TryGetProperty("reason", out _));
            Assert.False(root.TryGetProperty("sessionId", out _));
        }

        FluxDeployStateStore.UpdatePending(StatePath, pending.OperationId, FluxDeployStage.Creating, FluxDeploySendStatus.Sent,
            "Reso_Requested", ["Reso_Requested", "Reso_Requested"], "inner response failed");
        var advanced = Assert.Single(FluxDeployStateStore.Load(StatePath).Pending);
        Assert.Equal((FluxDeployStage.Creating, FluxDeploySendStatus.Sent, "Reso_Requested", "inner response failed"),
            (advanced.Stage, advanced.SendStatus, advanced.RequestedRootId, advanced.Reason));
        Assert.Equal(["Reso_Requested"], advanced.CandidateRootIds);
        Assert.Empty(FluxDeployStateStore.Load(StatePath).Modules);

        // A later report without IDs keeps the evidence already recorded.
        FluxDeployStateStore.UpdatePending(StatePath, pending.OperationId, FluxDeployStage.Created, FluxDeploySendStatus.Sent);
        advanced = Assert.Single(FluxDeployStateStore.Load(StatePath).Pending);
        Assert.Equal(("Reso_Requested", "inner response failed"), (advanced.RequestedRootId, advanced.Reason));
        Assert.Equal(["Reso_Requested"], advanced.CandidateRootIds);

        var settled = FluxDeployStateStore.ResolvePending(StatePath, pending.OperationId, new FluxDeployModuleRecord
        {
            ParentSlotId = "Reso_Parent", RootSlotId = "Reso_Requested", ModuleName = "Main", InputHash = "HASH2",
            SdkVersion = "1.9.0", DeployedAt = Created, Bindings = [new("Speed", "source", "Reso_Field")],
            Origin = FluxDeployOrigins.MigratedV1 // Settling always records a deployed origin.
        });
        Assert.Empty(settled.Pending);
        var reloaded = FluxDeployStateStore.Load(StatePath);
        Assert.Empty(reloaded.Pending);
        Assert.Equal(Session, reloaded.Session);
        var module = reloaded.Modules["main"];
        Assert.Equal(("Reso_Parent", "Reso_Requested", "Main", "HASH2", "deployed"),
            (module.ParentSlotId, module.RootSlotId, module.ModuleName, module.InputHash, module.Origin));
        Assert.Equal(new FluxDeployBindingRecord[] { new("Speed", "source", "Reso_Field") }, module.Bindings);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(StatePath)!, "*.tmp"));
    }

    [Fact]
    public void SettlingRequiresTheParentAndNameOfThePendingRecord()
    {
        var pending = Begin("main");
        FluxDeployStateStore.AddPending(StatePath, pending);
        var before = File.ReadAllText(StatePath);

        Assert.Throws<ArgumentException>(() => FluxDeployStateStore.ResolvePending(StatePath, pending.OperationId,
            new FluxDeployModuleRecord { ParentSlotId = "Reso_OtherParent", RootSlotId = "Reso_New", ModuleName = "Main", InputHash = "H" }));
        Assert.Throws<ArgumentException>(() => FluxDeployStateStore.ResolvePending(StatePath, pending.OperationId,
            new FluxDeployModuleRecord { ParentSlotId = "Reso_Parent", RootSlotId = "Reso_New", ModuleName = "Renamed", InputHash = "H" }));
        var missing = Assert.Throws<RLoopException>(() => FluxDeployStateStore.ResolvePending(StatePath, "other-op",
            new FluxDeployModuleRecord { ParentSlotId = "Reso_Parent", RootSlotId = "Reso_New", ModuleName = "Main", InputHash = "H" }));

        Assert.Equal("INVALID_OPTION", missing.Code);
        Assert.Equal(before, File.ReadAllText(StatePath));
    }

    [Fact]
    public void ASecondPendingIsRefusedWhileOneIsUnsettled()
    {
        var first = Begin("main");
        FluxDeployStateStore.AddPending(StatePath, first);
        var before = File.ReadAllText(StatePath);

        var e = Assert.Throws<RLoopException>(() => FluxDeployStateStore.AddPending(StatePath, Begin("other")));

        Assert.Equal("FLUX_DEPLOY_PENDING", e.Code);
        Assert.Equal(Path.GetFullPath(StatePath), e.Context["stateFile"]);
        Assert.Contains(first.OperationId, JsonSerializer.Serialize(e.Context["pending"]));
        Assert.Contains("--yes", Assert.Single(e.Suggestions));
        Assert.Equal(before, File.ReadAllText(StatePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AKnownResultThatCreatedNothingReleasesThePending(bool forgetModule)
    {
        Write(HandwrittenV2);

        var state = FluxDeployStateStore.ReleasePending(StatePath, "op-1", forgetModule);

        Assert.Equal("op-2", Assert.Single(state.Pending).OperationId);
        Assert.Equal(!forgetModule, FluxDeployStateStore.Load(StatePath).Modules.ContainsKey("main"));
        Assert.True(FluxDeployStateStore.Load(StatePath).Modules.ContainsKey("legacy"));
    }

    private const string V1 = """
        {
          "schemaVersion": 1,
          "parentSlotId": "Reso_Parent",
          "sessionId": "67",
          "modules": {
            "main": { "hash": "AAAA", "slotId": "Reso_Main" },
            "unplaced": { "hash": "BBBB", "slotId": null }
          }
        }
        """;

    [Fact]
    public void V1IsReadAsAnUnconfirmedMigrationAndTheFileIsNotRewritten()
    {
        Write(V1);

        var state = FluxDeployStateStore.Load(StatePath);

        Assert.Equal(("flux-deploy", 2), (state.Kind, state.SchemaVersion));
        Assert.Null(state.Session); // The v1 sessionId is a per-connection counter and is dropped.
        Assert.Empty(state.Pending);
        var main = state.Modules["main"];
        Assert.Equal(("Reso_Parent", "Reso_Main", "AAAA", "migrated-v1"), (main.ParentSlotId, main.RootSlotId, main.InputHash, main.Origin));
        Assert.Null(main.ModuleName); // v1 did not store the declared name; the guard supplies it when it confirms.
        Assert.False(main.IsVerified);
        Assert.Empty(main.Bindings);
        var unplaced = state.Modules["unplaced"];
        Assert.Equal(("Reso_Parent", (string?)null, "BBBB", "migrated-v1"), (unplaced.ParentSlotId, unplaced.RootSlotId, unplaced.InputHash, unplaced.Origin));
        Assert.Equal(V1, File.ReadAllText(StatePath));
    }

    [Fact]
    public void TheGuardConfirmsOrForgetsAMigratedRecordAndTheFileBecomesV2()
    {
        Write(V1);

        FluxDeployStateStore.ConfirmMigratedModule(StatePath, "main", "Reso_Parent", "Main");

        using (var saved = JsonDocument.Parse(File.ReadAllText(StatePath)))
        {
            Assert.Equal("flux-deploy", saved.RootElement.GetProperty("kind").GetString());
            Assert.Equal(2, saved.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(saved.RootElement.TryGetProperty("sessionId", out _));
            Assert.False(saved.RootElement.TryGetProperty("parentSlotId", out _));
            var main = saved.RootElement.GetProperty("modules").GetProperty("main");
            Assert.Equal("migrated-v1", main.GetProperty("origin").GetString());
            Assert.Equal("Main", main.GetProperty("moduleName").GetString());
            Assert.Equal("Reso_Main", main.GetProperty("rootSlotId").GetString());
            Assert.Equal("migrated-v1", saved.RootElement.GetProperty("modules").GetProperty("unplaced").GetProperty("origin").GetString());
        }
        // Checking a migrated ID supplies its name, but does not create epoch evidence or erase its origin.
        Assert.Throws<InvalidOperationException>(() => FluxDeployStateStore.ConfirmMigratedModule(StatePath, "unplaced", "Reso_Parent", "X"));
        var before = File.ReadAllText(StatePath);
        Assert.Throws<InvalidOperationException>(() => FluxDeployStateStore.ConfirmMigratedModule(StatePath, "main", "OtherParent", "Main"));
        Assert.Equal(before, File.ReadAllText(StatePath));
        FluxDeployStateStore.ConfirmMigratedModule(StatePath, "main", "Reso_Parent", "Main");
        Assert.Equal(before, File.ReadAllText(StatePath));
        Assert.Null(FluxDeployStateStore.Load(StatePath).Session);
        Assert.False(FluxDeployStateStore.Load(StatePath).Modules["main"].IsVerified);

        FluxDeployStateStore.ForgetModule(StatePath, "unplaced");
        Assert.Equal(["main"], FluxDeployStateStore.Load(StatePath).Modules.Keys);
    }

    private const string SettledEpochFixture = """
        {
          "kind": "flux-deploy", "schemaVersion": 2,
          "session": { "normalizedUrl": "ws://localhost:1234/", "discoverSessionId": "S-one", "identityStatus": "matched" },
          "modules": {
            "a": { "parentSlotId": "Parent", "rootSlotId": "Old_A", "moduleName": "A", "inputHash": "A1", "bindings": [], "origin": "deployed" },
            "b": { "parentSlotId": "Parent", "rootSlotId": "Old_B", "moduleName": "B", "inputHash": "B1", "bindings": [], "origin": "deployed" },
            "legacy": { "parentSlotId": "Parent", "rootSlotId": "Old_Legacy", "moduleName": null, "inputHash": "L1", "bindings": [], "origin": "migrated-v1" }
          },
          "pending": []
        }
        """;

    [Theory]
    [InlineData("same", true)]
    [InlineData("sessionChanged", false)]
    [InlineData("urlChanged", false)]
    [InlineData("currentUnknown", false)]
    [InlineData("bothUnknown", false)]
    [InlineData("recordedUnknown", false)]
    [InlineData("recordedMissingId", false)]
    [InlineData("recordedMissingSession", false)]
    public void SettlementRetainsOtherDeployedRecordsOnlyInTheSameProvenEpoch(string scenario, bool keepB)
    {
        Write(SettledEpochFixture);
        var current = new FluxDeploySessionRecord("ws://localhost:1234/", "S-one", "matched");
        if (scenario == "sessionChanged") current = current with { DiscoverSessionId = "S-two" };
        if (scenario == "urlChanged") current = current with { NormalizedUrl = "ws://localhost:1235/" };
        if (scenario is "currentUnknown" or "bothUnknown") current = current with { DiscoverSessionId = null, IdentityStatus = "unknown" };
        if (scenario is "bothUnknown" or "recordedUnknown" or "recordedMissingId" or "recordedMissingSession")
            FluxDeployStateStore.Mutate(StatePath, true, (_, state) => state.Session = scenario switch
            {
                "recordedMissingSession" => null,
                "recordedMissingId" => state.Session! with { DiscoverSessionId = null },
                "recordedUnknown" => state.Session! with { IdentityStatus = "unknown" },
                _ => state.Session! with { DiscoverSessionId = null, IdentityStatus = "unknown" }
            });
        var pending = FluxDeployPending.Begin("a", current, "Parent", "A", null, [], "A2", Created);
        FluxDeployStateStore.AddPending(StatePath, pending);

        FluxDeployStateStore.ResolvePending(StatePath, pending.OperationId, new FluxDeployModuleRecord
            { ParentSlotId = "Parent", RootSlotId = "New_A", ModuleName = "A", InputHash = "A2" });

        var state = FluxDeployStateStore.Load(StatePath);
        Assert.Equal(keepB ? new[] { "a", "b", "legacy" } : new[] { "a", "legacy" }, state.Modules.Keys.Order());
        Assert.Equal(("New_A", "A2", "deployed"),
            (state.Modules["a"].RootSlotId, state.Modules["a"].InputHash, state.Modules["a"].Origin));
        if (keepB) Assert.Equal(("Old_B", "B1"), (state.Modules["b"].RootSlotId, state.Modules["b"].InputHash));
        Assert.Equal(("Old_Legacy", "migrated-v1"), (state.Modules["legacy"].RootSlotId, state.Modules["legacy"].Origin));
        Assert.Equal(current, state.Session);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public void EpochInvalidationAndSettlementAreSavedAtomically()
    {
        Write(SettledEpochFixture);
        var pending = FluxDeployPending.Begin("a", new("ws://localhost:1234/", "S-two", "matched"),
            "Parent", "A", null, [], "A2", Created);
        FluxDeployStateStore.AddPending(StatePath, pending);
        var before = File.ReadAllText(StatePath);
        FluxDeployStateStore.SaveFault.Value = (_, _) => throw new IOException("settlement write failed");
        try
        {
            Assert.Equal("FLUX_STATE_WRITE_FAILED", Assert.Throws<RLoopException>(() =>
                FluxDeployStateStore.ResolvePending(StatePath, pending.OperationId, new FluxDeployModuleRecord
                    { ParentSlotId = "Parent", RootSlotId = "New_A", ModuleName = "A", InputHash = "A2" })).Code);
        }
        finally { FluxDeployStateStore.SaveFault.Value = null; }

        Assert.Equal(before, File.ReadAllText(StatePath));
        var state = FluxDeployStateStore.Load(StatePath);
        Assert.Equal("S-one", state.Session!.DiscoverSessionId);
        Assert.Equal("Old_A", state.Modules["a"].RootSlotId);
        Assert.Equal("Old_B", state.Modules["b"].RootSlotId);
        Assert.Equal(pending.OperationId, Assert.Single(state.Pending).OperationId);
    }

    // Unit 4a: the manifest entry reads its state with FluxDeployStateStore. The reader that shipped before v2 is gone
    // from the product; its refusal of v2 is kept below as a property of the v2 shape (unknown members).

    private string ManifestForState()
    {
        File.WriteAllText(Path.Combine(_root, "main.pg"), "module Main where { 1->display }");
        var manifest = Path.Combine(_root, "flux.json");
        File.WriteAllText(manifest, """{ "schemaVersion":"1", "modules":[{"name":"main","source":"main.pg","module":"Main"}] }""");
        return manifest;
    }

    /// <summary>The manifest entry stops on the state before any Flux tool or world read; the file is unchanged.</summary>
    private async Task<RLoopException> ManifestEntryStopsOnState(string content)
    {
        var manifest = ManifestForState();
        Write(content);
        var tool = new UnusedFluxTool();

        var e = await Assert.ThrowsAsync<RLoopException>(() => new FluxManifestOrchestrator(tool, tool, UnusedClient.Create())
            .DeployAsync(manifest, "Reso_Parent", new Uri("ws://localhost:1234"), null, null));

        Assert.Equal(content, File.ReadAllText(StatePath));
        Assert.Equal(e.Code, Assert.IsType<FluxManifestResult>(e.Context[FluxManifestOrchestrator.ReportContextKey]).StoppedBy!.Code);
        return e;
    }

    [Fact]
    public async Task TheManifestEntryReadsAV2StateWithTheCurrentReader()
    {
        // HandwrittenV2 holds pending operations op-1 and op-2: reading them is what stops the run.
        var e = await ManifestEntryStopsOnState(HandwrittenV2);

        Assert.Equal("FLUX_DEPLOY_PENDING", e.Code);
        Assert.Equal(Path.GetFullPath(StatePath), e.Context["stateFile"]);
        Assert.Equal(["op-1", "op-2"], Assert.IsAssignableFrom<IEnumerable<Dictionary<string, object?>>>(e.Context["pending"])
            .Select(pending => pending["operationId"]));
    }

    [Theory]
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 3, "session": null, "modules": {}, "pending": [] }""")]
    [InlineData("""{ "schemaVersion": 3, "modules": {} }""")]
    public async Task TheManifestEntryRefusesAnUnknownSchemaVersion(string content)
    {
        var e = await ManifestEntryStopsOnState(content);

        Assert.Equal("FLUX_STATE_VERSION_UNSUPPORTED", e.Code);
        Assert.Equal(3, e.Context["schemaVersion"]);
    }

    [Theory]
    [InlineData("""{ "kind": "something-else", "schemaVersion": 2, "session": null, "modules": {}, "pending": [] }""")]
    [InlineData("""{ "ownershipKey": "own", "schemaVersion": 3, "slots": {}, "components": {} }""")] // an apply state
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": {}, "pending": [], "future": true }""")]
    public async Task TheManifestEntryRefusesAnotherKindOrAnApplyState(string content)
    {
        var e = await ManifestEntryStopsOnState(content);

        Assert.Equal("FLUX_STATE_INVALID", e.Code);
    }

    [Fact]
    public void AReaderOfTheV1ShapeRejectsAV2State()
    {
        // The v1 reader (until unit 4a in FluxManifestOrchestrator) deserialized this shape with unknown members
        // disallowed. Any CLI that still has it refuses a v2 file instead of using or downgrading it (F3 direction).
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };

        Assert.NotNull(JsonSerializer.Deserialize<V1ReaderState>(V1, options)); // the v1 file itself is read
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<V1ReaderState>(HandwrittenV2, options));
    }

    private sealed class V1ReaderState
    {
        public int SchemaVersion { get; set; } = 1;
        public string? ParentSlotId { get; set; }
        public string? SessionId { get; set; }
        public Dictionary<string, V1ReaderModule> Modules { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed record V1ReaderModule(string Hash, string? SlotId);

    [Theory]
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 3, "modules": {}, "pending": [] }""")]
    [InlineData("""{ "schemaVersion": 3, "modules": {} }""")]
    [InlineData("""{ "schemaVersion": 0, "modules": {} }""")]
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 99 }""")]
    public void AnUnknownSchemaVersionIsRefused(string content)
    {
        Write(content);

        var e = Assert.Throws<RLoopException>(() => FluxDeployStateStore.Load(StatePath));

        Assert.Equal("FLUX_STATE_VERSION_UNSUPPORTED", e.Code);
        Assert.Equal(ExitCodes.ValidationFailed, e.ExitCode);
        Assert.Equal(Path.GetFullPath(StatePath), e.Context["stateFile"]);
        Assert.Equal(content, File.ReadAllText(StatePath));
    }

    [Theory]
    [InlineData("{")] // broken JSON
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{ "kind": "apply", "schemaVersion": 2, "session": null, "modules": {}, "pending": [] }""")] // another kind
    [InlineData("""{ "kind": 2, "schemaVersion": 2, "session": null, "modules": {}, "pending": [] }""")]
    [InlineData("""{ "schemaVersion": 3, "ownershipKey": "project", "slots": {}, "components": {}, "pending": [] }""")] // an apply state
    [InlineData("""{ "schemaVersion": 2, "session": null, "modules": {}, "pending": [] }""")] // v2 without kind
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 1, "modules": {} }""")] // kind on v1
    [InlineData("""{ "kind": "flux-deploy", "session": null, "modules": {}, "pending": [] }""")] // no version
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": "2", "session": null, "modules": {}, "pending": [] }""")]
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": {}, "pending": [], "future": 1 }""")] // unknown member
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": null, "pending": [] }""")]
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": {}, "pending": null }""")]
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "pending": [], "modules": { "m": { "parentSlotId": "P", "rootSlotId": null, "moduleName": "M", "inputHash": "H", "bindings": [], "origin": "deployed" } } }""")] // deployed without a root
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "pending": [], "modules": { "m": { "parentSlotId": "P", "rootSlotId": "R", "moduleName": "M", "inputHash": "H", "bindings": [], "origin": "adopted" } } }""")] // unknown origin
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "pending": [], "modules": { "m": { "parentSlotId": "P", "rootSlotId": "R", "moduleName": "M", "inputHash": "H", "bindings": [ { "port": "A", "mode": "both", "targetId": "T" } ], "origin": "deployed" } } }""")] // unknown binding mode
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": {}, "pending": [ { "operationId": "op", "module": "m", "createdAt": "2026-10-03T00:00:00+00:00", "session": null, "parentSlotId": "P", "moduleName": "M", "previousRootSlotId": null, "observedChildIds": [], "inputHash": "H", "requestedRootId": null, "stage": "unknown", "sendStatus": "unknown", "candidateRootIds": [] } ] }""")] // pending without a session
    [InlineData("""{ "kind": "flux-deploy", "schemaVersion": 2, "session": null, "modules": {}, "pending": [ { "operationId": "op", "module": "m", "createdAt": "2026-10-03T00:00:00+00:00", "session": { "normalizedUrl": "ws://localhost/", "discoverSessionId": null, "identityStatus": "unknown" }, "parentSlotId": "P", "moduleName": "M", "previousRootSlotId": null, "observedChildIds": [], "inputHash": "H", "requestedRootId": null, "stage": "halfway", "sendStatus": "unknown", "candidateRootIds": [] } ] }""")] // unknown stage
    [InlineData("""{ "schemaVersion": 1, "parentSlotId": "P", "sessionId": null, "modules": { "m": { "hash": "H", "slotId": "S" } }, "pending": [] }""")] // v1 with an unknown member
    [InlineData("""{ "schemaVersion": 1, "modules": { "m": { "slotId": "S" } } }""")] // v1 module without a hash
    public void AStateThatIsNotAValidFluxDeployStateIsRefused(string content)
    {
        Write(content);

        var e = Assert.Throws<RLoopException>(() => FluxDeployStateStore.Load(StatePath));

        Assert.Equal("FLUX_STATE_INVALID", e.Code);
        Assert.Equal(ExitCodes.ValidationFailed, e.ExitCode);
        Assert.Equal(Path.GetFullPath(StatePath), e.Context["stateFile"]);
        // A refused state cannot be changed either.
        Assert.Equal("FLUX_STATE_INVALID", Assert.Throws<RLoopException>(() => FluxDeployStateStore.AddPending(StatePath, Begin("m"))).Code);
        Assert.Equal(content, File.ReadAllText(StatePath));
    }

    [Fact]
    public void ARepeatedOperationIdIsRefused()
    {
        Write(HandwrittenV2.Replace("\"op-2\"", "\"op-1\""));

        Assert.Equal("FLUX_STATE_INVALID", Assert.Throws<RLoopException>(() => FluxDeployStateStore.Load(StatePath)).Code);
    }

    [Fact]
    public void AMissingStateIsEmptyUnlessItIsRequired()
    {
        var state = FluxDeployStateStore.Load(StatePath);

        Assert.Equal(("flux-deploy", 2), (state.Kind, state.SchemaVersion));
        Assert.Null(state.Session);
        Assert.Empty(state.Modules);
        Assert.Empty(state.Pending);
        var e = Assert.Throws<RLoopException>(() => FluxDeployStateStore.Load(StatePath, requireState: true));
        Assert.Equal("FLUX_STATE_NOT_FOUND", e.Code);
        Assert.Equal(ExitCodes.NotFound, e.ExitCode);
        Assert.False(Directory.Exists(Path.GetDirectoryName(StatePath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedSaveThrowsAndLeavesThePreviousStateInPlace(bool existing)
    {
        if (existing) Write(V1);
        var before = existing ? File.ReadAllText(StatePath) : null;
        FluxDeployStateStore.SaveFault.Value = (_, _) => throw new IOException("disk full");
        try
        {
            var e = Assert.Throws<RLoopException>(() => FluxDeployStateStore.AddPending(StatePath, Begin("main")));

            Assert.Equal("FLUX_STATE_WRITE_FAILED", e.Code);
            Assert.Equal(ExitCodes.OperationFailed, e.ExitCode);
            Assert.Equal(Path.GetFullPath(StatePath), e.Context["stateFile"]);
            Assert.IsType<IOException>(e.InnerException);
        }
        finally { FluxDeployStateStore.SaveFault.Value = null; }
        Assert.Equal(existing, File.Exists(StatePath));
        if (existing) Assert.Equal(before, File.ReadAllText(StatePath));
    }

    [Fact]
    public void AStateBeingChangedByAnotherProcessIsBusy()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var holder = new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var e = Assert.Throws<RLoopException>(() => FluxDeployStateStore.AddPending(StatePath, Begin("main")));

        Assert.Equal("FLUX_STATE_BUSY", e.Code);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void TheSingleDeployStateIsNamedFromTheModulePathBelowTheProject()
    {
        var path = FluxDeployStateStore.ResolveSingleDeployStatePath(_root, "Tools.Counter");
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), ".resoloop", "flux-state", "deploy", "Tools.Counter.json"), path);
        Assert.Equal("Tools.Counter", FluxDeployStateStore.SingleDeployModuleKey(" Tools.Counter "));

        var unsafePath = FluxDeployStateStore.ResolveSingleDeployStatePath(_root, "a/b:c d");
        Assert.Equal(Path.Combine(Path.GetFullPath(_root), ".resoloop", "flux-state", "deploy"), Path.GetDirectoryName(unsafePath));
        Assert.Equal("a_b_c_d.json", Path.GetFileName(unsafePath));
        Assert.Throws<ArgumentException>(() => FluxDeployStateStore.ResolveSingleDeployStatePath(_root, " "));

        // Module paths that share a file name stay apart by their keys; the manifest states live one level up.
        FluxDeployStateStore.AddPending(unsafePath, Begin(FluxDeployStateStore.SingleDeployModuleKey("a/b:c d")));
        Assert.Equal("a/b:c d", Assert.Single(FluxDeployStateStore.Load(FluxDeployStateStore.ResolveSingleDeployStatePath(_root, "a_b_c_d")).Pending).Module);
    }

    [Fact]
    public void DiscardRequiresConfirmation()
    {
        Write(HandwrittenV2);

        var e = Assert.Throws<RLoopException>(() => FluxDeployPendingDiscard.Discard(StatePath, "op-1", confirmed: false));

        Assert.Equal("CONFIRMATION_REQUIRED", e.Code);
        Assert.Equal(ExitCodes.ValidationFailed, e.ExitCode);
        Assert.Equal(HandwrittenV2, File.ReadAllText(StatePath));
    }

    [Theory]
    [InlineData("op-3")]
    [InlineData("OP-1")]
    [InlineData("")]
    public void DiscardRefusesAnOperationIdThatIsNotPending(string operationId)
    {
        Write(HandwrittenV2);

        var e = Assert.Throws<RLoopException>(() => FluxDeployPendingDiscard.Discard(StatePath, operationId, confirmed: true));

        Assert.Equal("INVALID_OPTION", e.Code);
        Assert.Equal(ExitCodes.InvalidArguments, e.ExitCode);
        Assert.Equal(HandwrittenV2, File.ReadAllText(StatePath));
    }

    [Fact]
    public void DiscardRemovesOnlyTheNamedPendingAndKeepsEveryModuleRecord()
    {
        Write(HandwrittenV2);

        var result = FluxDeployPendingDiscard.Discard(StatePath, "op-1", confirmed: true);

        Assert.Equal((Path.GetFullPath(StatePath), "op-1", "main", "Main", "Reso_Parent", "Reso_Root", "Reso_Requested"),
            (result.StateFile, result.OperationId, result.Module, result.ModuleName, result.ParentSlotId, result.PreviousRootSlotId, result.RequestedRootId));
        Assert.Equal(["Reso_Requested"], result.CandidateRootIds);
        Assert.Equal((FluxDeployStage.Creating, FluxDeploySendStatus.Unknown), (result.Stage, result.SendStatus));
        Assert.Contains("does not write to the world", result.Warning);
        Assert.Contains("does not adopt", result.Warning);
        Assert.Contains("exact ID", result.Warning);
        var state = FluxDeployStateStore.Load(StatePath);
        Assert.Equal("op-2", Assert.Single(state.Pending).OperationId);
        Assert.Equal(["legacy", "main"], state.Modules.Keys.Order());
        Assert.Equal("Reso_Root", state.Modules["main"].RootSlotId); // The candidate is not adopted.
        Assert.Equal("migrated-v1", state.Modules["legacy"].Origin);

        // The discarded ID cannot be discarded twice; the other one still can.
        Assert.Equal("INVALID_OPTION", Assert.Throws<RLoopException>(() => FluxDeployPendingDiscard.Discard(StatePath, "op-1", true)).Code);
        FluxDeployPendingDiscard.Discard(StatePath, "op-2", true);
        Assert.Empty(FluxDeployStateStore.Load(StatePath).Pending);
    }

    [Fact]
    public void DiscardOfAMissingStateCreatesNothing()
    {
        var e = Assert.Throws<RLoopException>(() => FluxDeployPendingDiscard.Discard(StatePath, "op-1", confirmed: true));

        Assert.Equal("FLUX_STATE_NOT_FOUND", e.Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(StatePath)));
    }

    [Fact]
    public void DiscardCannotReachTheWorld()
    {
        // The entry takes a state path, an operation ID and the confirmation; there is no client to write with.
        var method = Assert.Single(typeof(FluxDeployPendingDiscard).GetMethods(), m => m.Name == nameof(FluxDeployPendingDiscard.Discard));
        Assert.Equal([typeof(string), typeof(string), typeof(bool)], method.GetParameters().Select(p => p.ParameterType));
        Assert.True(method.IsStatic);
    }

    private static FluxDeployPending Begin(string module) =>
        FluxDeployPending.Begin(module, Session, "Reso_Parent", "Main", null, ["Reso_Sibling"], "HASH", Created);

    private void Write(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, content);
    }

    /// <summary>A client whose every call fails the test: the state must be refused before the world is contacted.</summary>
    public class UnusedClient : System.Reflection.DispatchProxy
    {
        public static IResoniteClient Create() => System.Reflection.DispatchProxy.Create<IResoniteClient, UnusedClient>();
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"The state must be refused before the world is read ({targetMethod?.Name}).");
    }

    private sealed class UnusedFluxTool : IFluxTool, IFluxDeployer
    {
        private static InvalidOperationException Unused() => new("The state must be refused before any Flux tool is used.");
        public Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => throw Unused();
        public Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => throw Unused();
        public Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => throw Unused();
        public Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw Unused();
        public Task<FluxDeployPreparation> PrepareAsync(FluxDeployPrepareRequest request, CancellationToken cancellationToken = default) => throw Unused();
        public Task<FluxDeployExecution> ExecuteAsync(FluxDeployExecuteRequest request, CancellationToken cancellationToken = default) => throw Unused();
    }
}

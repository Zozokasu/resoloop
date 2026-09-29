using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Workbench;

namespace RLoop.Workbench.Tests;

public sealed class WorkbenchSlotMapperTests
{
    private static JsonElement Result(JsonNode? value, string completeness = "Complete",
        JsonNode? provenance = null, JsonNode? unknownReason = null)
    {
        JsonObject result = new()
        {
            ["value"] = value,
            ["completeness"] = completeness,
            ["provenance"] = provenance,
            ["unknownReason"] = unknownReason,
        };
        return JsonDocument.Parse(result.ToJsonString()).RootElement.Clone();
    }

    private static JsonObject Snapshot(string scopeRootId, JsonObject slots,
        JsonArray? unexpanded = null, JsonNode? truncation = null, JsonArray? excluded = null) =>
        new()
        {
            ["sessionId"] = "S-1",
            ["connectionId"] = "conn-1",
            ["revision"] = 1,
            ["scopeRootId"] = scopeRootId,
            ["budget"] = new JsonObject { ["maxDepth"] = 8, ["maxSlots"] = 8192 },
            ["filter"] = new JsonObject { ["excludeUserRoots"] = true },
            ["fields"] = "Structure, ComponentTypes",
            ["source"] = "Live",
            ["startedAt"] = "2026-09-30T00:00:00+00:00",
            ["completedAt"] = "2026-09-30T00:00:01+00:00",
            ["receivedSlotCount"] = slots.Count,
            ["truncation"] = truncation ?? JsonValue.Create("None"),
            ["slots"] = slots,
            ["unexpanded"] = unexpanded ?? new JsonArray(),
            ["excluded"] = excluded ?? new JsonArray(),
        };

    private static JsonObject Slot(
        string id,
        string? name = null,
        string? parentId = null,
        bool? isActive = null,
        int level = 0,
        IEnumerable<string>? childIds = null,
        JsonArray? components = null,
        string? tag = null,
        JsonNode? transform = null,
        bool? persistent = null,
        bool isReferenceOnly = false) =>
        new()
        {
            ["id"] = id,
            ["name"] = name,
            ["parentId"] = parentId,
            ["isActive"] = isActive is { } active ? JsonValue.Create(active) : null,
            ["level"] = level,
            ["childIds"] = new JsonArray((childIds ?? []).Select(child => (JsonNode?)child).ToArray()),
            ["components"] = components ?? new JsonArray(),
            ["tag"] = tag,
            ["transform"] = transform,
            ["persistent"] = persistent is { } saved ? JsonValue.Create(saved) : null,
            ["isReferenceOnly"] = isReferenceOnly,
        };

    private static JsonObject Transform(string? position, string? rotation, string? scale) =>
        new() { ["position"] = position, ["rotation"] = rotation, ["scale"] = scale };

    private static JsonArray Components(params (string Id, string? Type)[] components) =>
        new(components.Select(component => (JsonNode?)new JsonObject
        {
            ["id"] = component.Id,
            ["componentType"] = component.Type,
        }).ToArray());

    private static JsonObject StubEntry(string id, string? name, string parentId, string reason) =>
        new() { ["id"] = id, ["name"] = name, ["parentId"] = parentId, ["reason"] = reason };

    private static JsonObject Provenance(bool isStale) => new()
    {
        ["sessionId"] = "S-1",
        ["connectionId"] = "conn-1",
        ["revision"] = 1,
        ["scopeRootId"] = "root",
        ["budget"] = new JsonObject { ["maxDepth"] = 8, ["maxSlots"] = 8192 },
        ["fields"] = "Structure, ComponentTypes",
        ["source"] = "Live",
        ["observedAt"] = "2026-09-30T00:00:01+00:00",
        ["age"] = "00:00:00",
        ["isStale"] = isStale,
        ["truncation"] = "None",
    };

    [Fact]
    public void BuildObserveParams_ValidDepth_MapsToObserveParams()
    {
        var parameters = WorkbenchSlotMapper.BuildObserveParams("root-id", 2);

        Assert.Equal("root-id", parameters.ScopeRootId);
        Assert.Equal(2, parameters.MaxDepth);
        Assert.Equal(WorkbenchLimits.MaxObserveSlots, parameters.MaxSlots);
    }

    [Fact]
    public void BuildObserveParams_DepthZero_RequestsOneLevel()
    {
        var parameters = WorkbenchSlotMapper.BuildObserveParams("root-id", 0);

        Assert.Equal(1, parameters.MaxDepth);
        Assert.Equal(WorkbenchLimits.MaxObserveSlots, parameters.MaxSlots);
    }

    [Fact]
    public void BuildObserveParams_MaxDepth_Passes()
    {
        var parameters = WorkbenchSlotMapper.BuildObserveParams("root-id", 32);

        Assert.Equal(32, parameters.MaxDepth);
    }

    [Fact]
    public void BuildObserveParams_OutOfRangeDepth_ThrowsLimitExceeded()
    {
        foreach (int depth in new[] { -1, 33 })
        {
            var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.BuildObserveParams("root", depth));
            Assert.Equal(WorkbenchLimits.LimitExceededCode, ex.Code);
        }
    }

    [Fact]
    public void MapObservation_CompleteTree_MapsRecordsToSlotInfo()
    {
        JsonObject slots = new()
        {
            ["root"] = Slot("root", "Root", null, true, 0, ["child"],
                components: Components(("comp-1", "[FrooxEngine]FrooxEngine.Grabbable")),
                tag: "test-tag",
                transform: Transform("{\"x\":1,\"y\":2,\"z\":3}", "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}", "{\"x\":2,\"y\":2,\"z\":2}"),
                persistent: true),
            ["child"] = Slot("child", "Child", "root", false, 1, ["leaf"],
                components: Components(("comp-2", null))),
            ["leaf"] = Slot("leaf", "Leaf", "child", null, 2),
        };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(Result(Snapshot("root", slots)), "root", depth: 2);

        Assert.Equal("root", root.Id);
        Assert.Equal("Root", root.Name);
        Assert.Null(root.ParentId);
        Assert.Equal(new Vector3Value(1, 2, 3), root.Position);
        Assert.Equal(new QuaternionValue(0, 0, 0, 1), root.Rotation);
        Assert.Equal(new Vector3Value(2, 2, 2), root.Scale);
        Assert.True(root.IsActive);
        Assert.True(root.IsPersistent);
        Assert.Equal("test-tag", root.Tag);
        Assert.False(root.IsReferenceOnly);
        Assert.Null(root.Path);
        Assert.Null(root.Members);
        ComponentSummary component = Assert.Single(root.Components);
        Assert.Equal("comp-1", component.Id);
        Assert.Equal("[FrooxEngine]FrooxEngine.Grabbable", component.Type);
        Assert.Null(component.Members);

        SlotInfo child = Assert.Single(root.Children);
        Assert.Equal("child", child.Id);
        Assert.Equal("root", child.ParentId);
        Assert.False(child.IsActive);
        Assert.Equal("comp-2", Assert.Single(child.Components).Id);
        Assert.Equal(string.Empty, child.Components[0].Type);

        SlotInfo leaf = Assert.Single(child.Children);
        Assert.Equal("leaf", leaf.Id);
        Assert.Equal("child", leaf.ParentId);
        Assert.Null(leaf.Position);
        Assert.Null(leaf.Rotation);
        Assert.Null(leaf.Scale);
        Assert.Null(leaf.IsActive);
        Assert.Null(leaf.IsPersistent);
        Assert.Empty(leaf.Children);
    }

    [Fact]
    public void MapObservation_NullTransform_LeavesAllTransformFieldsNull()
    {
        JsonObject slots = new()
        {
            ["root"] = Slot("root", "Root", null, true, 0, transform: null),
        };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(Result(Snapshot("root", slots)), "root", depth: 1);

        Assert.Null(root.Position);
        Assert.Null(root.Rotation);
        Assert.Null(root.Scale);
    }

    [Fact]
    public void MapObservation_PartialTransform_NullsOnlyTheMissingPart()
    {
        JsonObject slots = new()
        {
            ["root"] = Slot("root", transform: Transform("{\"x\":1,\"y\":0,\"z\":0}", null, "{\"x\":1,\"y\":1,\"z\":1}")),
        };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(Result(Snapshot("root", slots)), "root", depth: 1);

        Assert.Equal(new Vector3Value(1, 0, 0), root.Position);
        Assert.Null(root.Rotation);
        Assert.Equal(new Vector3Value(1, 1, 1), root.Scale);
    }

    [Fact]
    public void MapObservation_UnexpandedReadFailedChild_BecomesReferenceOnlyStub()
    {
        JsonObject slots = new()
        {
            ["root"] = Slot("root", "Root", null, true, 0, ["child"]),
        };
        JsonArray unexpanded = new(StubEntry("child", "Unread", "root", "ReadFailed"));

        SlotInfo root = WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", slots, unexpanded, truncation: "ReadFailed")), "root", depth: 4);

        SlotInfo stub = Assert.Single(root.Children);
        Assert.True(stub.IsReferenceOnly);
        Assert.Equal("child", stub.Id);
        Assert.Equal("Unread", stub.Name);
        Assert.Equal("root", stub.ParentId);
        Assert.Empty(stub.Children);
        Assert.Empty(stub.Components);
        Assert.Null(stub.Position);
        Assert.Null(stub.Rotation);
        Assert.Null(stub.Scale);
        Assert.Null(stub.IsActive);
        Assert.Null(stub.IsPersistent);
        Assert.Null(stub.Tag);
        Assert.Null(stub.Path);
        Assert.Null(stub.Members);
    }

    [Fact]
    public void MapObservation_DepthLimitTruncation_ChildIsStubAndNoError()
    {
        JsonObject slots = new()
        {
            ["root"] = Slot("root", "Root", null, true, 0, ["child"]),
            ["child"] = Slot("child", "Child", "root", true, 1, ["grandchild"]),
        };
        JsonArray unexpanded = new(StubEntry("grandchild", "Far", "child", "DepthLimit"));

        SlotInfo root = WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", slots, unexpanded, truncation: "DepthLimit")), "root", depth: 1);

        SlotInfo child = Assert.Single(root.Children);
        Assert.False(child.IsReferenceOnly);
        SlotInfo stub = Assert.Single(child.Children);
        Assert.True(stub.IsReferenceOnly);
        Assert.Equal("grandchild", stub.Id);
        Assert.Equal("Far", stub.Name);
        Assert.Equal("child", stub.ParentId);
    }

    [Fact]
    public void MapObservation_DepthZero_ChildrenAreStubsWithRecordNames()
    {
        // BuildObserveParams maps depth 0 to maxDepth 1, so level-1 children arrive as full
        // records; the mapper still presents them as reference-only stubs.
        JsonObject slots = new()
        {
            ["root"] = Slot("root", "Root", null, true, 0, ["child"]),
            ["child"] = Slot("child", "Child", "root", true, 1),
        };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(Result(Snapshot("root", slots)), "root", depth: 0);

        Assert.False(root.IsReferenceOnly);
        SlotInfo stub = Assert.Single(root.Children);
        Assert.True(stub.IsReferenceOnly);
        Assert.Equal("Child", stub.Name);
        Assert.Equal("root", stub.ParentId);
    }

    [Fact]
    public void MapObservation_RequestedRootDiffersFromScopeRoot_UsesScopeRootId()
    {
        JsonObject slots = new()
        {
            ["actual-root"] = Slot("actual-root", "RealRoot"),
        };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("actual-root", slots)), requestedRootId: "Root", depth: 1);

        Assert.Equal("actual-root", root.Id);
        Assert.Equal("RealRoot", root.Name);
    }

    [Fact]
    public void MapObservation_SlotLimit_ThrowsLimitExceeded()
    {
        JsonObject slots = new() { ["root"] = Slot("root") };

        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", slots, truncation: "DepthLimit, SlotLimit")), "root", depth: 2));

        Assert.Equal(WorkbenchLimits.LimitExceededCode, ex.Code);
        Assert.Equal("WORKBENCH_OBSERVE_LIMIT_EXCEEDED", ex.Code);
    }

    [Fact]
    public void MapObservation_NumericTruncation_ReadsFlagBits()
    {
        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", new JsonObject { ["root"] = Slot("root") }, truncation: 2)), "root", depth: 1));
        Assert.Equal(WorkbenchLimits.LimitExceededCode, ex.Code);

        SlotInfo root = WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", new JsonObject { ["root"] = Slot("root") }, truncation: 1)), "root", depth: 1);
        Assert.Equal("root", root.Id);
    }

    [Fact]
    public void MapObservation_Unknown_ThrowsSlotNotFound()
    {
        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(null, completeness: "Unknown", unknownReason: "Scope root-x has not been observed."),
            "root-x", depth: 2));

        Assert.Equal("SLOT_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Equal("root-x", ex.Context["slotId"]);
        Assert.Contains("has not been observed", ex.Message);
        Assert.Contains("not proof", ex.Message);
    }

    [Fact]
    public void MapObservation_NullValue_ThrowsSlotNotFound()
    {
        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(null, completeness: "Complete"), "root-x", depth: 2));

        Assert.Equal("SLOT_NOT_FOUND", ex.Code);
    }

    [Fact]
    public void MapObservation_StaleProvenance_ThrowsUnavailable()
    {
        JsonObject slots = new() { ["root"] = Slot("root") };

        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", slots), provenance: Provenance(isStale: true)), "root", depth: 1));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapObservation_BrokenTransformString_ThrowsUnavailable()
    {
        JsonObject slots = new()
        {
            ["root"] = Slot("root", transform: Transform("{not json", null, null)),
        };

        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", slots)), "root", depth: 0));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains("malformed", ex.Message);
    }

    [Fact]
    public void MapObservation_NamedFloatLiteral_ParsesNaN()
    {
        // The wire writes named float literals as quoted strings ("NaN", "Infinity").
        JsonObject slots = new()
        {
            ["root"] = Slot("root", transform: Transform("{\"x\":\"NaN\",\"y\":0,\"z\":-1.5}", null, null)),
        };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(Result(Snapshot("root", slots)), "root", depth: 0);

        Assert.NotNull(root.Position);
        Assert.True(float.IsNaN(root.Position!.X));
        Assert.Equal(-1.5f, root.Position.Z);
    }

    [Fact]
    public void MapObservation_NullName_MapsToEmptyString()
    {
        JsonObject slots = new() { ["root"] = Slot("root", name: null) };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(Result(Snapshot("root", slots)), "root", depth: 0);

        Assert.Equal(string.Empty, root.Name);
    }

    [Fact]
    public void MapObservation_ScopeRootMissingFromSlots_ThrowsUnavailable()
    {
        JsonObject slots = new() { ["other"] = Slot("other") };

        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("actual-root", slots)), "Root", depth: 1));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapObservation_MissingRequiredField_ThrowsMalformed()
    {
        JsonObject record = Slot("root");
        record.Remove("childIds");
        JsonObject slots = new() { ["root"] = record };

        var ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", slots)), "root", depth: 1));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains("malformed", ex.Message);

        JsonObject wrongType = Slot("root");
        wrongType["level"] = "zero"; // required as an integer on the wire
        JsonObject wrongTypeSlots = new() { ["root"] = wrongType };
        ex = Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", wrongTypeSlots)), "root", depth: 1));
        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapObservation_ChildAbsentEverywhere_StubHasEmptyName()
    {
        JsonObject slots = new() { ["root"] = Slot("root", "Root", null, true, 0, ["ghost"]) };

        SlotInfo root = WorkbenchSlotMapper.MapObservation(Result(Snapshot("root", slots)), "root", depth: 3);

        SlotInfo stub = Assert.Single(root.Children);
        Assert.True(stub.IsReferenceOnly);
        Assert.Equal("ghost", stub.Id);
        Assert.Equal(string.Empty, stub.Name);
        Assert.Equal("root", stub.ParentId);
    }

    [Fact]
    public void MapObservation_ExcludedChild_StubKeepsExcludedName()
    {
        JsonObject slots = new() { ["root"] = Slot("root", "Root", null, true, 0, ["user-root"]) };
        JsonArray excluded = new(StubEntry("user-root", "User", "root", "UserRoot"));

        SlotInfo root = WorkbenchSlotMapper.MapObservation(
            Result(Snapshot("root", slots, excluded: excluded)), "root", depth: 4);

        SlotInfo stub = Assert.Single(root.Children);
        Assert.True(stub.IsReferenceOnly);
        Assert.Equal("User", stub.Name);
    }

    [Fact]
    public void ParseFloat3_NullOrJsonNull_ReturnsNull()
    {
        Assert.Null(WorkbenchSlotMapper.ParseFloat3(null, "position"));
        Assert.Null(WorkbenchSlotMapper.ParseFloat3("null", "position"));
    }

    [Fact]
    public void ParseFloat3_ValidWireJson_ReturnsVector()
    {
        Assert.Equal(new Vector3Value(1, 1.5f, -2),
            WorkbenchSlotMapper.ParseFloat3("{\"x\":1,\"y\":1.5,\"z\":-2}", "position"));
    }

    [Fact]
    public void ParseFloat3_MalformedWireJson_ThrowsUnavailable()
    {
        Assert.Equal("WORKBENCH_UNAVAILABLE",
            Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.ParseFloat3("{\"x\":1,\"y\":2}", "position")).Code);
        Assert.Equal("WORKBENCH_UNAVAILABLE",
            Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.ParseFloat3("[1,2,3]", "position")).Code);
        Assert.Equal("WORKBENCH_UNAVAILABLE",
            Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.ParseFloat3("garbage", "position")).Code);
        Assert.Equal("WORKBENCH_UNAVAILABLE",
            Assert.Throws<RLoopException>(() => WorkbenchSlotMapper.ParseFloat3("{\"x\":\"abc\",\"y\":0,\"z\":0}", "position")).Code);
    }

    [Fact]
    public void ParseFloatQ_ValidWireJson_ReturnsQuaternion()
    {
        Assert.Equal(new QuaternionValue(0, 0, 0, 1),
            WorkbenchSlotMapper.ParseFloatQ("{\"x\":0,\"y\":0,\"z\":0,\"w\":1}", "rotation"));
        Assert.Null(WorkbenchSlotMapper.ParseFloatQ(null, "rotation"));
    }
}

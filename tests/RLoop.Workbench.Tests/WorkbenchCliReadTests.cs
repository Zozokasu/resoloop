using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResoniteWorkbench.Protocol;
using RLoop.Core;

namespace RLoop.Workbench.Tests;

/// <summary>
/// Drives the CLI (<see cref="RLoop.Cli.Program.Main"/>) in-process with
/// <c>--backend workbench --workbench-pipe NAME</c> against a <see cref="FakeWorkbenchServer"/>:
/// asserts the JSON each command writes through <c>--report</c> and the RPCs it issues. The
/// report file is per-invocation, which keeps assertions deterministic while xunit test classes
/// share the process-wide Console streams.
/// </summary>
public sealed class WorkbenchCliReadTests : IDisposable
{
    private const string Grabbable = "[FrooxEngine]FrooxEngine.Grabbable";
    private const string Dial = "[FrooxEngine]FrooxEngine.Dial";
    private const string Slider = "[FrooxEngine]FrooxEngine.Slider";
    private const string Alignment = "[FrooxEngine]FrooxEngine.Alignment";
    private const string GeneratedContent = "[FrooxEngine]FrooxEngine.AI_GeneratedContent";
    private const string GradientStrip = "[FrooxEngine]FrooxEngine.GradientStripTexture";
    private const string ConnectionId = "conn-1";
    private const string UniqueSessionId = "uni-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "resoloop-wb-cli-" + Guid.NewGuid().ToString("N"));

    public WorkbenchCliReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static string Param(RpcRequest request, string property) =>
        request.Params!.Value.GetProperty(property).GetString()!;

    // ----- wire fixtures (camelCase, matching WorkbenchJson.Options serialization) -----

    private static JsonObject SlotRecord(string id, string? name, string? parentId, bool? isActive,
        int level, IEnumerable<string>? childIds = null, JsonArray? components = null,
        string? tag = null, JsonNode? transform = null, bool? persistent = null) =>
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
            ["isReferenceOnly"] = false,
        };

    private static JsonArray ComponentRefs(params (string Id, string? Type)[] components) =>
        new(components.Select(c => (JsonNode?)new JsonObject
        {
            ["id"] = c.Id, ["componentType"] = c.Type,
        }).ToArray());

    private static JsonObject Transform(string? position, string? rotation, string? scale) =>
        new() { ["position"] = position, ["rotation"] = rotation, ["scale"] = scale };

    private static JsonObject StubEntry(string id, string? name, string parentId, string reason) =>
        new() { ["id"] = id, ["name"] = name, ["parentId"] = parentId, ["reason"] = reason };

    private static JsonObject Snapshot(string scopeRootId, JsonObject slots,
        JsonArray? unexpanded = null, JsonNode? truncation = null, JsonArray? excluded = null) =>
        new()
        {
            ["sessionId"] = "sess-1",
            ["connectionId"] = ConnectionId,
            ["revision"] = 7,
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

    private static JsonObject ObserveResult(JsonNode? value, string completeness = "Complete",
        JsonNode? unknownReason = null) =>
        new()
        {
            ["value"] = value,
            ["completeness"] = completeness,
            ["provenance"] = null,
            ["unknownReason"] = unknownReason,
        };

    private static JsonObject ReflectionResult(JsonNode? value, JsonNode? unknownReason = null) =>
        new() { ["value"] = value, ["provenance"] = null, ["unknownReason"] = unknownReason };

    private static JsonObject Readback(string componentId, string? componentType, string memberName,
        JsonNode? member, string? unknownReason = null) =>
        new()
        {
            ["sessionId"] = "sess-1",
            ["connectionId"] = ConnectionId,
            ["componentId"] = componentId,
            ["componentType"] = componentType,
            ["memberName"] = memberName,
            ["member"] = member,
            ["observedAt"] = "2026-09-30T00:00:00+00:00",
            ["unknownReason"] = unknownReason,
        };

    private static JsonObject Field(string id, string valueType, string? valueJson, string? enumType = null) =>
        new()
        {
            ["kind"] = "field", ["id"] = id, ["valueType"] = valueType,
            ["valueJson"] = valueJson, ["enumType"] = enumType,
        };

    private static JsonObject Reference(string id, string? targetId, string? targetType) =>
        new() { ["kind"] = "reference", ["id"] = id, ["targetId"] = targetId, ["targetType"] = targetType };

    private static JsonObject TypeDefinition(string fullTypeName, string name,
        string ns = "FrooxEngine", string assembly = "FrooxEngine",
        bool isComponent = false, bool isSyncObject = false, bool isValueType = false,
        bool isEnum = false, bool isGenericType = false, JsonNode? baseType = null) =>
        new()
        {
            ["fullTypeName"] = fullTypeName,
            ["assemblyName"] = assembly,
            ["namespace"] = ns,
            ["name"] = name,
            ["baseType"] = baseType,
            ["isComponent"] = isComponent,
            ["isSyncObject"] = isSyncObject,
            ["isAbstract"] = false,
            ["isInterface"] = false,
            ["isValueType"] = isValueType,
            ["isEnum"] = isEnum,
            ["isGenericType"] = isGenericType,
            ["genericArguments"] = new JsonArray(),
            ["interfaces"] = new JsonArray(),
        };

    private static JsonObject TypeRef(string type) =>
        new() { ["type"] = type, ["isGenericParameter"] = false, ["genericArguments"] = new JsonArray() };

    private static JsonObject MemberDef(string name, string kind, JsonNode? valueType = null,
        JsonNode? targetType = null) =>
        new()
        {
            ["name"] = name, ["kind"] = kind, ["wrapperType"] = null,
            ["valueType"] = valueType, ["targetType"] = targetType, ["element"] = null,
        };

    private static JsonObject ComponentDefinition(string fullTypeName, JsonObject members,
        string name, JsonArray? methods = null) =>
        new()
        {
            ["type"] = TypeDefinition(fullTypeName, name, isComponent: true,
                baseType: TypeRef("[FrooxEngine]FrooxEngine.Component")),
            ["categoryPath"] = "Test/Components",
            ["flattened"] = true,
            ["members"] = members,
            ["methods"] = methods ?? new JsonArray(),
        };

    private static JsonObject GrabbableDefinition() =>
        ComponentDefinition(Grabbable, new JsonObject
        {
            ["Value"] = MemberDef("Value", "Field", valueType: TypeRef("[mscorlib]System.Single")),
            ["Target"] = MemberDef("Target", "Reference", targetType: TypeRef("[FrooxEngine]FrooxEngine.Slot")),
        }, "Grabbable");

    private static JsonObject DialDefinition() =>
        ComponentDefinition(Dial, new JsonObject
        {
            ["Ratio"] = MemberDef("Ratio", "Field", valueType: TypeRef("[mscorlib]System.Single")),
        }, "Dial");

    private static JsonObject SliderDefinition() =>
        ComponentDefinition(Slider, new JsonObject
        {
            ["Value"] = MemberDef("Value", "Field", valueType: TypeRef("[mscorlib]System.Single")),
        }, "Slider");

    private static JsonObject GeneratedContentDefinition() =>
        ComponentDefinition(GeneratedContent, new JsonObject
        {
            ["Source"] = MemberDef("Source", "Field", valueType: TypeRef("[mscorlib]System.String")),
        }, "AI_GeneratedContent");

    private static JsonObject SearchResult(IEnumerable<string> types, bool truncated = false) =>
        ReflectionResult(new JsonObject
        {
            ["types"] = new JsonArray(types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["truncated"] = truncated,
        });

    private static JsonObject EnumDefinition(string typeName, bool isFlags,
        params (string Name, long Value)[] values) =>
        new()
        {
            ["status"] = "Found",
            ["typeName"] = typeName,
            ["underlyingType"] = "[mscorlib]System.Int32",
            ["isFlags"] = isFlags,
            ["values"] = new JsonArray(values.Select(v =>
                (JsonNode?)new JsonObject { ["name"] = v.Name, ["value"] = v.Value }).ToArray()),
            ["detail"] = null,
        };

    private static JsonObject SessionStatus() =>
        new()
        {
            ["state"] = "Connected",
            ["targetSession"] = new JsonObject { ["sessionId"] = "sess-1" },
            ["connection"] = new JsonObject
            {
                ["connectionId"] = ConnectionId,
                ["generation"] = 0,
                ["remote"] = new JsonObject
                {
                    ["resoniteVersion"] = "2025.9.2.1349",
                    ["resoniteLinkVersion"] = "0.13.1",
                    ["uniqueSessionId"] = UniqueSessionId,
                },
            },
            ["reconnectAttempt"] = 0,
            ["disconnectReason"] = null,
        };

    /// <summary>The world the fake reports: Root (transform/tag/persistent + Grabbable) -> Child -> Leaf -> Deep (unexpanded stub).</summary>
    private static JsonObject WorldObserveResult(string scopeRootId) =>
        ObserveResult(Snapshot(scopeRootId, new JsonObject
        {
            [scopeRootId] = SlotRecord(scopeRootId, "Root", null, true, 0, ["child-1"],
                components: ComponentRefs(("comp-1", Grabbable)),
                tag: "tag-root",
                transform: Transform("{\"x\":1,\"y\":2,\"z\":3}", "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}", "{\"x\":2,\"y\":2,\"z\":2}"),
                persistent: true),
            ["child-1"] = SlotRecord("child-1", "Child", scopeRootId, false, 1, ["leaf"]),
            ["leaf"] = SlotRecord("leaf", "Leaf", "child-1", true, 2, ["deep"]),
        }, unexpanded: new JsonArray(StubEntry("deep", "Deep", "leaf", "DepthLimit"))));

    private static JsonObject ReadbackFor(RpcRequest request, string componentType,
        Func<string, string, JsonNode?> member)
    {
        string componentId = Param(request, "componentId");
        string memberName = Param(request, "memberName");
        JsonNode? value = member(componentId, memberName);
        return Readback(componentId, componentType, memberName, value,
            value is null ? $"Component {componentId} has no member named exactly '{memberName}'." : null);
    }

    /// <summary>Answers every RPC the read commands issue for the small world above.</summary>
    private static RpcMessage Respond(RpcRequest request) => request.Method switch
    {
        RpcMethods.SessionStatus => Wb.Response(request.Id, SessionStatus().ToJsonString(), connectionId: ConnectionId),
        RpcMethods.WorldObserve => Wb.Response(request.Id,
            WorldObserveResult(Param(request, "scopeRootId")).ToJsonString(), connectionId: ConnectionId),
        RpcMethods.MemberRead => Wb.Response(request.Id, ReadbackFor(request,
            Param(request, "componentId") == "comp-9" ? Dial : Grabbable,
            (_, member) => member switch
            {
                "Value" => Field("m-value", "float", "1.5"),
                "Target" => Reference("m-target", "Reso_9", "[FrooxEngine]FrooxEngine.Slot"),
                "Ratio" => Field("m-ratio", "float", "0.75"),
                _ => null,
            }).ToJsonString(), connectionId: ConnectionId),
        RpcMethods.ReflectionComponent => Wb.Response(request.Id, (Param(request, "componentType") switch
        {
            var t when t == Grabbable => ReflectionResult(GrabbableDefinition()),
            var t when t == Dial => ReflectionResult(DialDefinition()),
            var t when t == Slider => ReflectionResult(SliderDefinition()),
            var t when t == GeneratedContent => ReflectionResult(GeneratedContentDefinition()),
            _ => ReflectionResult(null, "The type is not a component."),
        }).ToJsonString(), connectionId: ConnectionId),
        RpcMethods.ReflectionSearch => Wb.Response(request.Id, SearchResultFor(Param(request, "query")).ToJsonString(),
            connectionId: ConnectionId),
        RpcMethods.ReflectionType => Wb.Response(request.Id,
            (Param(request, "typeName") == Alignment
                ? ReflectionResult(TypeDefinition(Alignment, "Alignment", isValueType: true, isEnum: true,
                    baseType: TypeRef("[mscorlib]System.Enum")))
                : ReflectionResult(null, "The type is not known to this session.")).ToJsonString(), connectionId: ConnectionId),
        RpcMethods.ReflectionEnum => Wb.Response(request.Id,
            ReflectionResult(EnumDefinition(Alignment, isFlags: true, ("Near", 2), ("Far", 8))).ToJsonString(),
            connectionId: ConnectionId),
        _ => new RpcError(request.Id, new RpcErrorDetail("TEST_UNEXPECTED", $"unexpected {request.Method}")),
    };

    private static JsonObject SearchResultFor(string query) => query switch
    {
        "slider" => SearchResult(["[Custom]Slider", "[FrooxEngine]FrooxEngine.AlphaSlider", Slider]),
        "AI_GeneratedContent" => SearchResult([GeneratedContent]),
        _ => SearchResult([]),
    };

    /// <summary>The unknownReason the live Workbench reported for GradientStripTexture (W2B-R2 B1).</summary>
    private static string UnknownDefinitionReason(string componentType) =>
        $"ResoniteLink failed to read the definition of component type {componentType}: " +
        "Object reference not set to an instance of an object.";

    /// <summary>reflection.component answers "unknown" with no connection identity, like the live B1 case.</summary>
    private static RpcMessage UnknownComponentDefinition(RpcRequest request, string componentType) =>
        Wb.Response(request.Id,
            ReflectionResult(null, UnknownDefinitionReason(componentType)).ToJsonString(),
            connectionId: null, sessionId: null);

    private static FakeWorkbenchServer Serve(string pipeName, ConcurrentQueue<RpcRequest> log,
        Func<RpcRequest, RpcMessage>? respond = null) =>
        FakeWorkbenchServer.Start(pipeName, (s, ct) => Wb.ServeAsync(s, respond ?? Respond, ct, log));

    private static string[] WbArgs(string pipeName, params string[] args) =>
        [.. args, "--backend", "workbench", "--workbench-pipe", pipeName,
         "--timeout", "5", "--command-timeout", "30", "--json"];

    private static async Task<(int Exit, JsonElement Report)> RunCliAsync(params string[] args)
    {
        var reportPath = Path.Combine(Path.GetTempPath(), "resoloop-test-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            var exit = await RLoop.Cli.Program.Main([.. args, "--report", reportPath]);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            return (exit, document.RootElement.Clone());
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    private string WriteDoc()
    {
        var path = Path.Combine(_dir, "doc.json");
        File.WriteAllText(path, $$"""
            {"schemaVersion":"1","ownership":{"key":"wb-cli-test"},
             "slot":{"key":"root","name":"Root"},
             "components":[{"key":"dial","type":"{{Dial}}","fields":{"Ratio":0.5} } ] }
            """);
        return path;
    }

    private string WriteState()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, $$"""
            {"schemaVersion":2,"ownershipKey":"wb-cli-test","sessionId":"{{UniqueSessionId}}",
             "slots":{"root":{"id":"Root","path":"Root"} },
             "components":{"dial":{"id":"comp-9","slotKey":"root","type":"{{Dial}}","typeOrdinal":0} } }
            """);
        return path;
    }

    // ----- tests -----

    [Theory]
    [InlineData("status")]
    [InlineData("ping")]
    public async Task Status_ReportsConnectedSession(string command)
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, command));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(report.GetProperty("ok").GetBoolean());
        JsonElement data = report.GetProperty("data");
        Assert.True(data.GetProperty("connected").GetBoolean());
        Assert.Equal($"pipe:///{pipeName}", data.GetProperty("url").GetString());
        Assert.Equal("2025.9.2.1349", data.GetProperty("resoniteVersion").GetString());
        Assert.Equal("0.13.1", data.GetProperty("resoniteLinkVersion").GetString());
        Assert.Equal(UniqueSessionId, data.GetProperty("connectionId").GetString());
        Assert.Equal(new[] { RpcMethods.SessionStatus }, log.Select(r => r.Method).ToArray());
    }

    [Fact]
    public async Task Hierarchy_ObservesDepthTwoTreeWithDirectShape()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "hierarchy", "--depth", "2"));

        Assert.Equal(ExitCodes.Success, exit);
        RpcRequest observe = Assert.Single(log);
        Assert.Equal(RpcMethods.WorldObserve, observe.Method);
        Assert.Equal("Root", Param(observe, "scopeRootId"));
        Assert.Equal(2, observe.Params!.Value.GetProperty("maxDepth").GetInt32());
        Assert.Equal(WorkbenchLimits.MaxObserveSlots, observe.Params!.Value.GetProperty("maxSlots").GetInt32());

        JsonElement root = report.GetProperty("data");
        Assert.Equal("Root", root.GetProperty("id").GetString());
        Assert.Equal("Root", root.GetProperty("name").GetString());
        Assert.Equal(1.0, root.GetProperty("position").GetProperty("x").GetDouble());
        Assert.Equal(1.0, root.GetProperty("rotation").GetProperty("w").GetDouble());
        Assert.Equal(2.0, root.GetProperty("scale").GetProperty("x").GetDouble());
        Assert.True(root.GetProperty("isActive").GetBoolean());
        Assert.True(root.GetProperty("isPersistent").GetBoolean());
        Assert.Equal("tag-root", root.GetProperty("tag").GetString());
        Assert.False(root.GetProperty("isReferenceOnly").GetBoolean());

        JsonElement component = Assert.Single(root.GetProperty("components").EnumerateArray());
        Assert.Equal("comp-1", component.GetProperty("id").GetString());
        Assert.Equal(Grabbable, component.GetProperty("type").GetString());
        Assert.False(component.TryGetProperty("members", out _));

        JsonElement child = Assert.Single(root.GetProperty("children").EnumerateArray());
        Assert.Equal("child-1", child.GetProperty("id").GetString());
        Assert.Equal("Child", child.GetProperty("name").GetString());
        Assert.False(child.GetProperty("isActive").GetBoolean());
        // The fake reports no transform/persistent/tag for this slot: keys are omitted, not zero-filled.
        Assert.False(child.TryGetProperty("position", out _));
        Assert.False(child.TryGetProperty("rotation", out _));
        Assert.False(child.TryGetProperty("scale", out _));
        Assert.False(child.TryGetProperty("isPersistent", out _));
        Assert.False(child.TryGetProperty("tag", out _));

        JsonElement leaf = Assert.Single(child.GetProperty("children").EnumerateArray());
        Assert.Equal("leaf", leaf.GetProperty("id").GetString());
        Assert.False(leaf.GetProperty("isReferenceOnly").GetBoolean());
        JsonElement stub = Assert.Single(leaf.GetProperty("children").EnumerateArray());
        Assert.Equal("deep", stub.GetProperty("id").GetString());
        Assert.Equal("Deep", stub.GetProperty("name").GetString());
        Assert.True(stub.GetProperty("isReferenceOnly").GetBoolean());
    }

    [Fact]
    public async Task Find_ByName_UsesDefaultDepthEightAndReportsPaths()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "find", "--name", "Leaf"));

        Assert.Equal(ExitCodes.Success, exit);
        RpcRequest observe = Assert.Single(log);
        Assert.Equal(RpcMethods.WorldObserve, observe.Method);
        Assert.Equal(8, observe.Params!.Value.GetProperty("maxDepth").GetInt32());

        JsonElement match = Assert.Single(report.GetProperty("data").EnumerateArray());
        Assert.Equal("leaf", match.GetProperty("id").GetString());
        Assert.Equal("Leaf", match.GetProperty("name").GetString());
        Assert.Equal("Root/Child/Leaf", match.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Find_ByComponent_MatchesSlotHoldingType()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "find", "--component", "Grabbable"));

        Assert.Equal(ExitCodes.Success, exit);
        JsonElement match = Assert.Single(report.GetProperty("data").EnumerateArray());
        Assert.Equal("Root", match.GetProperty("id").GetString());
        Assert.Equal("Root", match.GetProperty("path").GetString());
        JsonElement component = Assert.Single(match.GetProperty("components").EnumerateArray());
        Assert.Equal("comp-1", component.GetProperty("id").GetString());
        Assert.Equal(Grabbable, component.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Find_ByName_MatchesReferenceOnlyStubs()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "find", "--name", "Deep"));
        Assert.Equal(ExitCodes.Success, exit);
        JsonElement stub = Assert.Single(report.GetProperty("data").EnumerateArray());
        Assert.Equal("deep", stub.GetProperty("id").GetString());
        Assert.Equal("Root/Child/Leaf/Deep", stub.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Find_ExcludeReferenceOnly_RemovesStubsFromResults()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "find", "--name", "Deep", "--exclude-reference-only"));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(report.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Inspect_DepthOne_ReportsPathsAndStubs()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "inspect", "Root", "--depth", "1"));

        Assert.Equal(ExitCodes.Success, exit);
        RpcRequest observe = Assert.Single(log);
        Assert.Equal(1, observe.Params!.Value.GetProperty("maxDepth").GetInt32());

        JsonElement root = report.GetProperty("data");
        Assert.Equal("Root", root.GetProperty("path").GetString());
        Assert.Equal("tag-root", root.GetProperty("tag").GetString());
        JsonElement child = Assert.Single(root.GetProperty("children").EnumerateArray());
        Assert.Equal("Root/Child", child.GetProperty("path").GetString());
        JsonElement leafStub = Assert.Single(child.GetProperty("children").EnumerateArray());
        Assert.Equal("leaf", leafStub.GetProperty("id").GetString());
        Assert.Equal("Leaf", leafStub.GetProperty("name").GetString());
        Assert.Equal("Root/Child/Leaf", leafStub.GetProperty("path").GetString());
        Assert.True(leafStub.GetProperty("isReferenceOnly").GetBoolean());
    }

    [Fact]
    public async Task Inspect_ComponentMember_ReadsMemberValues()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName,
            "inspect", "Root", "--depth", "1", "--component", "Grabbable", "--member", "Value"));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(
            new[] { RpcMethods.WorldObserve, RpcMethods.ReflectionComponent, RpcMethods.MemberRead, RpcMethods.MemberRead },
            log.Select(r => r.Method).ToArray());
        Assert.Contains(log, r => r.Method == RpcMethods.MemberRead && Param(r, "memberName") == "Value");

        JsonElement data = report.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        JsonElement inspected = Assert.Single(data.GetProperty("components").EnumerateArray());
        Assert.Equal("Root", inspected.GetProperty("slotId").GetString());
        Assert.Equal("Root", inspected.GetProperty("slotPath").GetString());
        JsonElement component = inspected.GetProperty("component");
        Assert.Equal("comp-1", component.GetProperty("id").GetString());
        Assert.Equal(Grabbable, component.GetProperty("type").GetString());
        JsonElement member = component.GetProperty("members").GetProperty("Value");
        Assert.Equal("field", member.GetProperty("kind").GetString());
        Assert.Equal("m-value", member.GetProperty("id").GetString());
        Assert.Equal(1.5, member.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Inspect_Members_FieldType_ComesFromDefinitionValueType()
    {
        // B2: the declared valueType fills MemberValue.Type with the CLR name the direct
        // backend reports - "[mscorlib]System.Single" shows as "System.Single".
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "inspect", "Root", "--members"));

        Assert.Equal(ExitCodes.Success, exit);
        JsonElement component = Assert.Single(report.GetProperty("data").GetProperty("components").EnumerateArray());
        JsonElement members = component.GetProperty("members");
        Assert.Equal("System.Single", members.GetProperty("Value").GetProperty("type").GetString());
        Assert.Equal("reference", members.GetProperty("Target").GetProperty("kind").GetString());
        Assert.False(members.GetProperty("Target").TryGetProperty("type", out JsonElement targetType)
            && targetType.ValueKind == JsonValueKind.String && targetType.GetString()!.Length > 0);
    }

    [Fact]
    public async Task Inspect_Members_UnknownComponentDefinition_FailsUnavailable()
    {
        // B1: an "unknown" reflection.component answer carries no connection identity; the
        // command must report the undescribable type, not "connection changed mid-read".
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log, request => request.Method switch
        {
            RpcMethods.ReflectionComponent => UnknownComponentDefinition(request, Param(request, "componentType")),
            _ => Respond(request),
        });

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "inspect", "Root", "--members"));

        Assert.Equal(ExitCodes.ConnectionFailed, exit);
        JsonElement error = report.GetProperty("error");
        Assert.Equal("WORKBENCH_UNAVAILABLE", error.GetProperty("code").GetString());
        string message = error.GetProperty("message").GetString()!;
        Assert.Contains(Grabbable, message);
        Assert.Contains("Object reference not set to an instance of an object.", message);
        Assert.DoesNotContain("connection changed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hierarchy_IncludeComponents_UnknownComponentDefinition_FailsUnavailable()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log, request => request.Method switch
        {
            RpcMethods.ReflectionComponent => UnknownComponentDefinition(request, Param(request, "componentType")),
            _ => Respond(request),
        });

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "hierarchy", "--include-components"));

        Assert.Equal(ExitCodes.ConnectionFailed, exit);
        JsonElement error = report.GetProperty("error");
        Assert.Equal("WORKBENCH_UNAVAILABLE", error.GetProperty("code").GetString());
        string message = error.GetProperty("message").GetString()!;
        Assert.Contains(Grabbable, message);
        Assert.Contains("Object reference not set to an instance of an object.", message);
        Assert.DoesNotContain("connection changed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hierarchy_IncludeComponents_OneUndescribableComponent_FailsWholeCommand()
    {
        // comp-1 (Grabbable) reads fine, but comp-2's type answers "unknown": the command
        // fails as a whole instead of returning a partially filled hierarchy.
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject observe = ObserveResult(Snapshot("Root", new JsonObject
        {
            ["Root"] = SlotRecord("Root", "Root", null, true, 0, ["child-1"],
                components: ComponentRefs(("comp-1", Grabbable))),
            ["child-1"] = SlotRecord("child-1", "Child", "Root", true, 1,
                components: ComponentRefs(("comp-2", GradientStrip))),
        }));
        await using var server = Serve(pipeName, log, request => request.Method switch
        {
            RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString(), connectionId: ConnectionId),
            RpcMethods.ReflectionComponent when Param(request, "componentType") == GradientStrip =>
                UnknownComponentDefinition(request, GradientStrip),
            _ => Respond(request),
        });

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "hierarchy", "--include-components"));

        Assert.Equal(ExitCodes.ConnectionFailed, exit);
        Assert.False(report.GetProperty("ok").GetBoolean());
        JsonElement error = report.GetProperty("error");
        Assert.Equal("WORKBENCH_UNAVAILABLE", error.GetProperty("code").GetString());
        string message = error.GetProperty("message").GetString()!;
        Assert.Contains(GradientStrip, message);
        Assert.Contains("Object reference not set to an instance of an object.", message);
    }

    [Fact]
    public async Task TypeSearch_ListsRankedMatches()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "type", "search", "slider"));

        Assert.Equal(ExitCodes.Success, exit);
        RpcRequest search = Assert.Single(log);
        Assert.Equal(RpcMethods.ReflectionSearch, search.Method);
        Assert.Equal("slider", Param(search, "query"));
        Assert.Equal(
            new[] { "[Custom]Slider", "[FrooxEngine]FrooxEngine.Slider", "[FrooxEngine]FrooxEngine.AlphaSlider" },
            report.GetProperty("data").EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public async Task TypeDescribe_Component_ReportsDefinition()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "type", "describe", Slider));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(new[] { RpcMethods.ReflectionComponent }, log.Select(r => r.Method).ToArray());
        JsonElement data = report.GetProperty("data");
        Assert.Equal(Slider, data.GetProperty("fullTypeName").GetString());
        Assert.Equal("Test/Components", data.GetProperty("categoryPath").GetString());
        Assert.False(data.GetProperty("isGeneric").GetBoolean());
        JsonElement member = Assert.Single(data.GetProperty("members").EnumerateArray());
        Assert.Equal("Value", member.GetProperty("name").GetString());
        Assert.Equal("field", member.GetProperty("kind").GetString());
        Assert.Equal("[mscorlib]System.Single", member.GetProperty("valueType").GetString());
    }

    [Fact]
    public async Task TypeDescribe_NonComponent_ReportsTypeAndEnum()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "type", "describe", Alignment));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(
            new[] { RpcMethods.ReflectionComponent, RpcMethods.ReflectionType, RpcMethods.ReflectionEnum },
            log.Select(r => r.Method).ToArray());
        JsonElement data = report.GetProperty("data");
        Assert.Equal(Alignment, data.GetProperty("fullTypeName").GetString());
        Assert.True(data.GetProperty("isEnum").GetBoolean());
        Assert.True(data.GetProperty("isFlags").GetBoolean());
        JsonElement values = data.GetProperty("enumValues");
        Assert.Equal(2, values.GetProperty("Near").GetInt64());
        Assert.Equal(8, values.GetProperty("Far").GetInt64());
    }

    [Fact]
    public async Task Observe_ResolvesStateAndReadsMembers()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);
        string statePath = WriteState();

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "observe", "$member:dial.Ratio", "--state", statePath));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(
            new[] { RpcMethods.SessionStatus, RpcMethods.MemberRead, RpcMethods.ReflectionComponent, RpcMethods.MemberRead },
            log.Select(r => r.Method).ToArray());

        JsonElement data = report.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.Equal(1, data.GetProperty("components").GetInt32());
        JsonElement observed = data.GetProperty("values").GetProperty("$member:dial.Ratio");
        Assert.Equal("comp-9", observed.GetProperty("componentId").GetString());
        Assert.Equal(Dial, observed.GetProperty("componentType").GetString());
        JsonElement member = observed.GetProperty("member");
        Assert.Equal("field", member.GetProperty("kind").GetString());
        Assert.Equal("m-ratio", member.GetProperty("id").GetString());
        Assert.Equal(0.75, member.GetProperty("value").GetDouble());
    }

    [Theory]
    [InlineData("hierarchy --depth 40")]
    [InlineData("find --name Leaf --depth -1")]
    [InlineData("inspect Root --depth 33")]
    public async Task DepthBeyondWorkbenchLimit_FailsBeforeAnyRpc(string commandLine)
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, commandLine.Split(' ')));

        Assert.Equal(ExitCodes.OperationFailed, exit);
        Assert.False(report.GetProperty("ok").GetBoolean());
        JsonElement error = report.GetProperty("error");
        Assert.Equal("WORKBENCH_OBSERVE_LIMIT_EXCEEDED", error.GetProperty("code").GetString());
        Assert.Empty(log); // no world.observe (or any RPC) was issued
    }

    [Fact]
    public async Task Hierarchy_SlotLimitTruncation_FailsWithLimitExceeded()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log, request => request.Method == RpcMethods.WorldObserve
            ? Wb.Response(request.Id, ObserveResult(Snapshot(Param(request, "scopeRootId"), new JsonObject
                {
                    [Param(request, "scopeRootId")] = SlotRecord(Param(request, "scopeRootId"), "Root", null, true, 0),
                }, truncation: "SlotLimit")).ToJsonString(), connectionId: ConnectionId)
            : Respond(request));

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "hierarchy"));

        Assert.Equal(ExitCodes.OperationFailed, exit);
        Assert.Equal("WORKBENCH_OBSERVE_LIMIT_EXCEEDED",
            report.GetProperty("error").GetProperty("code").GetString());
        Assert.Single(log);
    }

    [Fact]
    public async Task CliDepthRange_IsStillTheLinkRange_NotTheWorkbenchLimit()
    {
        // The IntOption range is unchanged: --depth 64 parses and fails at WorkbenchLimits; 65 fails CLI parsing.
        // A fake pipe accepts one connection, so each invocation gets its own server.
        string pipeName64 = Wb.NewPipeName();
        var log64 = new ConcurrentQueue<RpcRequest>();
        await using var server64 = Serve(pipeName64, log64);

        var (exit64, report64) = await RunCliAsync(WbArgs(pipeName64, "hierarchy", "--depth", "64"));
        Assert.Equal(ExitCodes.OperationFailed, exit64);
        Assert.Equal("WORKBENCH_OBSERVE_LIMIT_EXCEEDED",
            report64.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(log64);

        string pipeName65 = Wb.NewPipeName();
        var log65 = new ConcurrentQueue<RpcRequest>();
        await using var server65 = Serve(pipeName65, log65);

        var (exit65, report65) = await RunCliAsync(WbArgs(pipeName65, "hierarchy", "--depth", "65"));
        Assert.Equal(ExitCodes.InvalidArguments, exit65);
        Assert.Equal("INVALID_OPTION", report65.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(log65);
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("item audit Root")]
    [InlineData("tool audit Root")]
    [InlineData("uix audit Root")]
    [InlineData("capture doc.json")]
    [InlineData("diff doc.json")]
    [InlineData("plan doc.json")]
    [InlineData("apply doc.json")]
    public async Task UnsupportedCommands_FailBeforeConnecting(string commandLine)
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        var helloSeen = new TaskCompletionSource<RpcHello>();
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, Respond, ct, log, onHello: hello => helloSeen.TrySetResult(hello)));

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, commandLine.Split(' ')));

        Assert.Equal(ExitCodes.OperationFailed, exit);
        JsonElement error = report.GetProperty("error");
        Assert.Equal("BACKEND_UNSUPPORTED", error.GetProperty("code").GetString());
        Assert.NotEmpty(error.GetProperty("suggestions").EnumerateArray());
        Assert.False(helloSeen.Task.IsCompleted); // the gate fires before any connection attempt
    }

    [Fact]
    public async Task Validate_NonStrict_SucceedsWithoutWorkbench()
    {
        string doc = WriteDoc();
        // A pipe name that does not exist proves the offline path never connects.
        var (exit, report) = await RunCliAsync(WbArgs("no-such-pipe-" + Guid.NewGuid().ToString("N"),
            "validate", doc));

        Assert.Equal(ExitCodes.Success, exit);
        JsonElement data = report.GetProperty("data");
        Assert.True(data.GetProperty("valid").GetBoolean());
        Assert.False(data.GetProperty("strict").GetBoolean());
        Assert.Equal(1, data.GetProperty("slots").GetInt32());
        Assert.Equal(1, data.GetProperty("components").GetInt32());
    }

    [Fact]
    public async Task Validate_Strict_FailsExplicitly_WithoutValueConversion()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);
        string doc = WriteDoc();

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "validate", doc, "--strict"));

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        JsonElement error = report.GetProperty("error");
        Assert.Equal("APPLY_VALIDATION_FAILED", error.GetProperty("code").GetString());
        JsonElement issues = error.GetProperty("context").GetProperty("issues");
        Assert.Contains(issues.EnumerateArray(), i =>
            i.GetProperty("code").GetString() == "BACKEND_UNSUPPORTED");
        // Strict validation did connect and describe the component types before failing on member checks.
        Assert.Contains(log, r => r.Method == RpcMethods.ReflectionComponent);
    }

    [Fact]
    public async Task TypeCheck_Manifest_FailsExplicitly_WithoutValueConversion()
    {
        string pipeName = Wb.NewPipeName();
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = Serve(pipeName, log);
        string doc = WriteDoc();

        var (exit, report) = await RunCliAsync(WbArgs(pipeName, "type", "check", "--manifest", doc));

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        JsonElement error = report.GetProperty("error");
        Assert.Equal("APPLY_VALIDATION_FAILED", error.GetProperty("code").GetString());
        JsonElement issues = error.GetProperty("context").GetProperty("issues");
        Assert.Contains(issues.EnumerateArray(), i =>
            i.GetProperty("code").GetString() == "BACKEND_UNSUPPORTED");
    }
}

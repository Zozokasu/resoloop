using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ResoniteWorkbench.Protocol;
using RLoop.Core;
using RLoop.Workbench;

namespace RLoop.Workbench.Tests;

/// <summary>
/// Exercises the Workbench read path (world.observe + member.read + reflection.*) over a real
/// named pipe: a FakeWorkbenchServer speaks JSON-RPC frames and answers per method.
/// </summary>
public sealed class WorkbenchReadTests
{
    private const string Grabbable = "[FrooxEngine]FrooxEngine.Grabbable";
    private const string Dial = "[FrooxEngine]FrooxEngine.Dial";
    private const string Slider = "[FrooxEngine]FrooxEngine.Slider";
    private const string GradientStrip = "[FrooxEngine]FrooxEngine.GradientStripTexture";

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
            ["connectionId"] = "conn-meta-1",
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
            ["connectionId"] = "conn-meta-1",
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

    private static JsonObject TypeRef(string type, params JsonNode?[] genericArguments) =>
        new()
        {
            ["type"] = type, ["isGenericParameter"] = false,
            ["genericArguments"] = new JsonArray(genericArguments),
        };

    /// <summary>The unknownReason the live Workbench reported for GradientStripTexture (W2B-R2 B1).</summary>
    private static string UnknownDefinitionReason(string componentType) =>
        $"ResoniteLink failed to read the definition of component type {componentType}: " +
        "Object reference not set to an instance of an object.";

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

    /// <summary>A two-member Grabbable definition: field "Value" and reference "Target".</summary>
    private static JsonObject GrabbableDefinition() =>
        ComponentDefinition(Grabbable, new JsonObject
        {
            ["Value"] = MemberDef("Value", "Field", valueType: TypeRef("[mscorlib]System.Single")),
            ["Target"] = MemberDef("Target", "Reference", targetType: TypeRef("[FrooxEngine]FrooxEngine.Slot")),
        }, "Grabbable");

    /// <summary>A one-member Dial definition: field "Ratio".</summary>
    private static JsonObject DialDefinition() =>
        ComponentDefinition(Dial, new JsonObject
        {
            ["Ratio"] = MemberDef("Ratio", "Field", valueType: TypeRef("[mscorlib]System.Single")),
        }, "Dial");

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

    private static RpcMessage Unexpected(RpcRequest request) =>
        new RpcError(request.Id, new RpcErrorDetail("TEST_UNEXPECTED", $"unexpected {request.Method}"));

    /// <summary>member.read that answers Grabbable's Value/Target members, and nothing else.</summary>
    private static JsonNode? GrabbableMember(string componentId, string memberName) => memberName switch
    {
        "Value" => Field("m-value", "float", "1.5"),
        "Target" => Reference("m-target", "Reso_9", "[FrooxEngine]FrooxEngine.Slot"),
        _ => null,
    };

    private static JsonObject ReadbackFor(RpcRequest request, string componentType,
        Func<string, string, JsonNode?> member)
    {
        string componentId = Param(request, "componentId");
        string memberName = Param(request, "memberName");
        JsonNode? value = member(componentId, memberName);
        return Readback(componentId, componentType, memberName, value,
            value is null ? $"Component {componentId} has no member named exactly '{memberName}'." : null);
    }

    // ----- tests -----

    [Fact]
    public async Task ConnectAsync_HelloRequestsAllReadCapabilities()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        RpcHello? hello = null;
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            hello = await s.ReadAsync(ct) as RpcHello;
            await s.WriteAsync(Wb.Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        Assert.NotNull(hello);
        Assert.Equal(new[]
        {
            RpcCapabilities.SessionRead, RpcCapabilities.WorldRead,
            RpcCapabilities.MemberRead, RpcCapabilities.ReflectionRead,
        }, hello!.RequestedCapabilities);
    }

    [Fact]
    public async Task Reads_BeforeConnect_ThrowInvalidOperation()
    {
        await using var client = new WorkbenchResoniteClient();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSlotAsync("slot-1", 1, false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetComponentAsync("comp-1", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SearchComponentTypesAsync("slider", 5, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DescribeComponentTypeAsync(Slider, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DescribeTypeAsync(Slider, CancellationToken.None));
    }

    [Fact]
    public async Task GetSlotAsync_ObservesTreeAndSendsObserveParams()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0, ["child"],
                components: ComponentRefs(("comp-1", Grabbable)),
                tag: "tag-1",
                transform: Transform("{\"x\":1,\"y\":2,\"z\":3}", "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}", "{\"x\":2,\"y\":2,\"z\":2}"),
                persistent: true),
            ["child"] = SlotRecord("child", "Child", "root", false, 1),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        SlotInfo root = await client.GetSlotAsync("root", 3, includeComponentData: false, guard.Token);

        RpcRequest request = Assert.Single(log);
        Assert.Equal(RpcMethods.WorldObserve, request.Method);
        Assert.Equal("root", Param(request, "scopeRootId"));
        Assert.Equal(3, request.Params!.Value.GetProperty("maxDepth").GetInt32());
        Assert.Equal(WorkbenchLimits.MaxObserveSlots, request.Params!.Value.GetProperty("maxSlots").GetInt32());

        Assert.Equal("root", root.Id);
        Assert.Equal("Root", root.Name);
        Assert.Equal(new Vector3Value(1, 2, 3), root.Position);
        Assert.Equal(new QuaternionValue(0, 0, 0, 1), root.Rotation);
        Assert.Equal(new Vector3Value(2, 2, 2), root.Scale);
        Assert.Equal("tag-1", root.Tag);
        Assert.True(root.IsActive);
        Assert.True(root.IsPersistent);
        ComponentSummary component = Assert.Single(root.Components);
        Assert.Equal("comp-1", component.Id);
        Assert.Equal(Grabbable, component.Type);
        Assert.Null(component.Members);
        SlotInfo child = Assert.Single(root.Children);
        Assert.Equal("child", child.Id);
        Assert.False(child.IsReferenceOnly);
        Assert.Equal("root", client.Meta.ObservedScopeRootId);
        Assert.Equal("conn-meta-1", client.Meta.ConnectionId);
        Assert.Equal(12, client.Meta.WorldRevision);
    }

    [Fact]
    public async Task ReadAsync_CallerCancellation_Propagates()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            await s.WriteAsync(Wb.Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
            _ = await s.ReadAsync(ct); // world.observe request; never answered
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        callTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSlotAsync("root", 1, false, callTimeout.Token));
    }

    [Theory]
    [InlineData(33)]
    [InlineData(-1)]
    public async Task GetSlotAsync_DepthOutOfRange_FailsWithoutAnyRpc(int depth)
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, "{}"), ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", depth, false, guard.Token));

        Assert.Equal("WORKBENCH_OBSERVE_LIMIT_EXCEEDED", ex.Code);
        Assert.Empty(log);
    }

    [Fact]
    public async Task GetSlotAsync_SlotLimitTruncation_ThrowsLimitExceeded()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject observe = ObserveResult(Snapshot("root",
            new JsonObject { ["root"] = SlotRecord("root", "Root", null, true, 0) },
            truncation: "SlotLimit"));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 2, false, guard.Token));

        Assert.Equal("WORKBENCH_OBSERVE_LIMIT_EXCEEDED", ex.Code);
    }

    [Fact]
    public async Task GetSlotAsync_UnexpandedReadFailedChild_WithinDepth_ThrowsUnavailable()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0, ["far"]),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots,
            unexpanded: new JsonArray(StubEntry("far", "Far", "root", "ReadFailed")),
            truncation: "ReadFailed"));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 2, false, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains("far", ex.Message);
    }

    [Fact]
    public async Task GetSlotAsync_UnexpandedReadFailedChild_BelowDepth_BecomesReferenceOnlyStub()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0, ["far"]),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots,
            unexpanded: new JsonArray(StubEntry("far", "Far", "root", "ReadFailed")),
            truncation: "ReadFailed"));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        SlotInfo root = await client.GetSlotAsync("root", 0, false, guard.Token);

        SlotInfo stub = Assert.Single(root.Children);
        Assert.True(stub.IsReferenceOnly);
        Assert.Equal("far", stub.Id);
        Assert.Equal("Far", stub.Name);
        Assert.Equal("root", stub.ParentId);
        Assert.Empty(stub.Components);
        Assert.Empty(stub.Children);
    }

    [Fact]
    public async Task FindAsync_ReadFailedChildWithinDepth_FailsInsteadOfEmptySuccess()
    {
        // hierarchy/observe/inspect share this GetSlotAsync path; a within-depth read
        // failure must surface as WORKBENCH_UNAVAILABLE rather than an empty match list.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0, ["far"]),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots,
            unexpanded: new JsonArray(StubEntry("far", "Far", "root", "ReadFailed")),
            truncation: "ReadFailed"));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);
        var world = new WorldService(client);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => world.FindAsync(null, false, Grabbable, 4, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task FindAsync_DirectChildren_DoesNotMatchGrandchildStub()
    {
        // A depth-1 observe reports the grandchildren of level-1 slots as stubs; a
        // --direct-children find must evaluate only the root's direct children.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0, ["child"]),
            ["child"] = SlotRecord("child", "Child", "root", true, 1, ["grand"]),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots,
            unexpanded: new JsonArray(StubEntry("grand", "Grand", "child", "DepthLimit")),
            truncation: "DepthLimit"));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString()), ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);
        var world = new WorldService(client);

        var direct = await world.FindAsync("Grand", false, null, 8, guard.Token,
            new FindOptions(DirectChildren: true));

        Assert.Empty(direct);
        RpcRequest request = Assert.Single(log);
        Assert.Equal(RpcMethods.WorldObserve, request.Method);
        Assert.Equal(1, request.Params!.Value.GetProperty("maxDepth").GetInt32());

        // Without --direct-children the same stub is still found by name.
        var deep = await world.FindAsync("Grand", false, null, 8, guard.Token);
        Assert.Equal("grand", Assert.Single(deep).Id);
        Assert.Equal("Root/Child/Grand", deep[0].Path);
    }

    [Fact]
    public async Task GetSlotAsync_NullTransform_LeavesTransformNull()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject observe = ObserveResult(Snapshot("root",
            new JsonObject { ["root"] = SlotRecord("root", "Root", null, true, 0, transform: null) }));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        SlotInfo root = await client.GetSlotAsync("root", 1, false, guard.Token);

        Assert.Null(root.Position);
        Assert.Null(root.Rotation);
        Assert.Null(root.Scale);
    }

    [Fact]
    public async Task GetSlotAsync_IncludeComponentData_FillsMembers()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0, ["child"],
                components: ComponentRefs(("comp-1", Grabbable))),
            ["child"] = SlotRecord("child", "Child", "root", true, 1,
                components: ComponentRefs(("comp-2", Dial))),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots));
        JsonObject grabbableDef = ReflectionResult(GrabbableDefinition());
        JsonObject dialDef = ReflectionResult(DialDefinition());
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id,
                    (Param(request, "componentType") == Grabbable ? grabbableDef : dialDef).ToJsonString()),
                RpcMethods.MemberRead => Wb.Response(request.Id, ReadbackFor(request,
                    Param(request, "componentId") == "comp-1" ? Grabbable : Dial,
                    (id, member) => member == "Ratio" ? Field("m-ratio", "float", "0.75")
                        : GrabbableMember(id, member)).ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        SlotInfo root = await client.GetSlotAsync("root", 2, includeComponentData: true, guard.Token);

        ComponentSummary comp1 = Assert.Single(root.Components);
        Assert.NotNull(comp1.Members);
        Assert.Equal("field", comp1.Members!["Value"].Kind);
        Assert.Equal(1.5, comp1.Members["Value"].Value!.GetValue<double>());
        Assert.Equal("reference", comp1.Members["Target"].Kind);
        Assert.Equal("Reso_9", comp1.Members["Target"].TargetId);

        SlotInfo child = Assert.Single(root.Children);
        ComponentSummary comp2 = Assert.Single(child.Components);
        Assert.NotNull(comp2.Members);
        Assert.Equal("field", comp2.Members!["Ratio"].Kind);
        Assert.Equal(0.75, comp2.Members["Ratio"].Value!.GetValue<double>());

        // One observe + one reflection.component per type + one member.read per declared member.
        Assert.Equal(
            new[]
            {
                RpcMethods.WorldObserve, RpcMethods.ReflectionComponent,
                RpcMethods.MemberRead, RpcMethods.MemberRead,
                RpcMethods.ReflectionComponent, RpcMethods.MemberRead,
            },
            log.Select(r => r.Method).ToArray());
        Assert.Equal(new[] { "Value", "Target" },
            log.Where(r => r.Method == RpcMethods.MemberRead && Param(r, "componentId") == "comp-1")
                .Select(r => Param(r, "memberName")).ToArray());
        Assert.Equal("[FrooxEngine]FrooxEngine.Grabbable",
            Param(log.First(r => r.Method == RpcMethods.ReflectionComponent), "componentType"));
    }

    [Fact]
    public async Task GetSlotAsync_UnreadableMember_ThrowsUnavailable()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots));
        JsonObject grabbableDef = ReflectionResult(GrabbableDefinition());
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, grabbableDef.ToJsonString()),
                RpcMethods.MemberRead => Wb.Response(request.Id, ReadbackFor(request, Grabbable,
                    (_, member) => member == "Value" ? Field("m-value", "float", "1.5") : null).ToJsonString()),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains("Target", ex.Message);
    }

    [Fact]
    public async Task GetSlotAsync_StaleResponse_ThrowsUnavailable()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject observe = ObserveResult(Snapshot("root",
            new JsonObject { ["root"] = SlotRecord("root", "Root", null, true, 0) }));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString(), stale: true), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 1, false, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSlotAsync_ConnectionSwitchMidRead_ThrowsUnavailable()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots));
        JsonObject grabbableDef = ReflectionResult(GrabbableDefinition());
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString(), connectionId: "conn-a"),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, grabbableDef.ToJsonString(), connectionId: "conn-b"),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains("connection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetComponentAsync_UnknownComponentDefinitionNullMeta_ThrowsUnavailableNamingType()
    {
        // Live B1: reflection.component answered "unknown" with a null meta.connectionId and
        // meta.sessionId. That is an undescribable type, not a mid-read connection change.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject unknown = ReflectionResult(null, UnknownDefinitionReason(GradientStrip));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.MemberRead => Wb.Response(request.Id,
                    ReadbackFor(request, GradientStrip, (_, _) => null).ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, unknown.ToJsonString(),
                    connectionId: null, sessionId: null),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetComponentAsync("comp-1", guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains(GradientStrip, ex.Message);
        Assert.Contains("Object reference not set to an instance of an object.", ex.Message);
        Assert.DoesNotContain("connection changed", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The identity-less answer must not erase the ids earlier responses reported.
        Assert.Equal("conn-meta-1", client.Meta.ConnectionId);
        Assert.Equal("sess-meta-1", client.Meta.SessionId);
    }

    [Fact]
    public async Task GetSlotAsync_IncludeComponentData_UnknownComponentDefinition_ThrowsUnavailableNamingType()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", GradientStrip))),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots));
        JsonObject unknown = ReflectionResult(null, UnknownDefinitionReason(GradientStrip));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, unknown.ToJsonString(),
                    connectionId: null, sessionId: null),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains(GradientStrip, ex.Message);
        Assert.Contains("Object reference not set to an instance of an object.", ex.Message);
        Assert.DoesNotContain("connection changed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("conn-meta-1", client.Meta.ConnectionId);
        Assert.Equal("sess-meta-1", client.Meta.SessionId);
    }

    [Fact]
    public async Task GetSlotAsync_NullConnectionIdOnKnownResponse_StillReportsConnectionChange()
    {
        // Only an "unknown" reflection answer may lack a connectionId; a defined answer from
        // nowhere still means the read mixed sessions.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots));
        JsonObject grabbableDef = ReflectionResult(GrabbableDefinition());
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, grabbableDef.ToJsonString(),
                    connectionId: null),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains("connection changed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DescribeComponentTypeAsync_UnknownResultNullMeta_StillThrowsComponentTypeNotFound()
    {
        // type describe keeps mapping an unknown reflection.component to COMPONENT_TYPE_NOT_FOUND,
        // even when the answer carries no connection identity.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject unknown = ReflectionResult(null, UnknownDefinitionReason(GradientStrip));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, unknown.ToJsonString(),
                    connectionId: null, sessionId: null),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.DescribeComponentTypeAsync(GradientStrip, guard.Token));

        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
    }

    [Fact]
    public async Task GetSlotAsync_NotConnectedError_ThrowsNotConnected()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => new RpcError(request.Id,
                new RpcErrorDetail("NOT_CONNECTED", "No session is connected.")), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root", 1, false, guard.Token));

        Assert.Equal("WORKBENCH_NOT_CONNECTED", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
        Assert.Equal("Workbench is not connected to a Resonite session.", ex.Message);
    }

    [Fact]
    public async Task GetSlotAsync_UnknownResult_ThrowsSlotNotFound()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject observe = ObserveResult(null, completeness: "Unknown",
            unknownReason: "Scope root-x has not been observed.");
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, observe.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("root-x", 1, false, guard.Token));

        Assert.Equal("SLOT_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Equal("root-x", ex.Context["slotId"]);
    }

    [Fact]
    public async Task GetComponentAsync_UnknownType_ProbesThenReadsMembers()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject grabbableDef = ReflectionResult(GrabbableDefinition());
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.MemberRead => Wb.Response(request.Id,
                    ReadbackFor(request, Grabbable, GrabbableMember).ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, grabbableDef.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        Assert.Equal("comp-1", component.Id);
        Assert.Equal(Grabbable, component.Type);
        Assert.Equal("field", component.Members["Value"].Kind);
        Assert.Equal("reference", component.Members["Target"].Kind);

        // probe (1) + reflection.component (1) + member.read per member (2)
        Assert.Equal(
            new[]
            {
                RpcMethods.MemberRead, RpcMethods.ReflectionComponent,
                RpcMethods.MemberRead, RpcMethods.MemberRead,
            },
            log.Select(r => r.Method).ToArray());
        Assert.Equal("__resoloop_type_probe__", Param(log.First(), "memberName"));
        Assert.Equal("comp-1", Param(log.First(), "componentId"));
    }

    [Fact]
    public async Task GetComponentAsync_AfterSlotRead_ReusesCachedTypeAndMembers()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject slots = new()
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        };
        JsonObject observe = ObserveResult(Snapshot("root", slots));
        JsonObject grabbableDef = ReflectionResult(GrabbableDefinition());
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, grabbableDef.ToJsonString()),
                RpcMethods.MemberRead => Wb.Response(request.Id,
                    ReadbackFor(request, Grabbable, GrabbableMember).ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        _ = await client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token);
        Assert.Equal(4, log.Count); // observe + reflection.component + 2 member.read
        log.Clear();

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        Assert.Equal(Grabbable, component.Type);
        Assert.Equal(2, component.Members.Count);
        // Type came from the observe cache and member names from the reflection cache:
        // only the two live member.read calls remain.
        Assert.Equal(new[] { RpcMethods.MemberRead, RpcMethods.MemberRead },
            log.Select(r => r.Method).ToArray());
        Assert.DoesNotContain(log, r => Param(r, "memberName") == "__resoloop_type_probe__");
    }

    [Fact]
    public async Task GetComponentAsync_UnknownComponent_ThrowsComponentNotFound()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.MemberRead => Wb.Response(request.Id,
                    Readback(Param(request, "componentId"), null, Param(request, "memberName"),
                        member: null, unknownReason: "Component nope-9 was not found.").ToJsonString()),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetComponentAsync("nope-9", guard.Token));

        Assert.Equal("COMPONENT_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Equal("nope-9", ex.Context["componentId"]);
    }

    [Fact]
    public async Task GetComponentAsync_MemberKinds_MapToMemberValue()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject def = ReflectionResult(ComponentDefinition(Grabbable, new JsonObject
        {
            ["Value"] = MemberDef("Value", "Field", valueType: TypeRef("[mscorlib]System.Single")),
            ["Target"] = MemberDef("Target", "Reference", targetType: TypeRef("[FrooxEngine]FrooxEngine.Slot")),
            ["Items"] = MemberDef("Items", "List"),
        }, "Grabbable"));
        JsonNode? Member(string id, string name) => name switch
        {
            "Value" => Field("m-value", "float", "1.5"),
            "Target" => Reference("m-target", "Reso_9", "[FrooxEngine]FrooxEngine.Slot"),
            "Items" => new JsonObject
            {
                ["kind"] = "list", ["id"] = "m-items",
                ["elements"] = new JsonArray(
                    Field("e-1", "float", "1"),
                    Reference("e-2", "Reso_7", "[FrooxEngine]FrooxEngine.Slot")),
            },
            _ => null,
        };
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.MemberRead => Wb.Response(request.Id,
                    ReadbackFor(request, Grabbable, Member).ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, def.ToJsonString()),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        Assert.Equal("field", component.Members["Value"].Kind);
        Assert.Equal("reference", component.Members["Target"].Kind);
        MemberValue list = component.Members["Items"];
        Assert.Equal("list", list.Kind);
        Assert.NotNull(list.Elements);
        Assert.Equal(2, list.Elements!.Count);
        Assert.Equal("field", list.Elements[0].Kind);
        Assert.Equal("reference", list.Elements[1].Kind);
        Assert.Equal("Reso_7", list.Elements[1].TargetId);
    }

    [Fact]
    public async Task GetComponentAsync_FieldDefinitions_FillMemberValueTypes()
    {
        // B2: member.read reports wire names ("float"), so Type comes from the declared
        // valueType instead - "[mscorlib]System.Single" -> "System.Single" like the direct path.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject def = ReflectionResult(ComponentDefinition(Grabbable, new JsonObject
        {
            ["Value"] = MemberDef("Value", "Field", valueType: TypeRef("[mscorlib]System.Single")),
            ["Maybe"] = MemberDef("Maybe", "Field",
                valueType: TypeRef("[mscorlib]System.Nullable<>", TypeRef("[mscorlib]System.Single"))),
            ["Mode"] = MemberDef("Mode", "Field", valueType: TypeRef("[FrooxEngine]FrooxEngine.Alignment")),
            ["Mystery"] = MemberDef("Mystery", "Field"),
            ["Items"] = MemberDef("Items", "List"),
            ["Target"] = MemberDef("Target", "Reference", targetType: TypeRef("[FrooxEngine]FrooxEngine.Slot")),
        }, "Grabbable"));
        JsonNode? Member(string id, string name) => name switch
        {
            "Value" => Field("m-value", "float", "1.5"),
            "Maybe" => Field("m-maybe", "nullable<float>", "0.5"),
            "Mode" => Field("m-mode", "enum", "\"Near\"", "[FrooxEngine]FrooxEngine.Alignment"),
            "Mystery" => Field("m-mystery", "colorX", "\"#fff\""),
            "Items" => new JsonObject
            {
                ["kind"] = "list", ["id"] = "m-items",
                ["elements"] = new JsonArray(Field("e-1", "float", "1")),
            },
            "Target" => Reference("m-target", "Reso_9", "[FrooxEngine]FrooxEngine.Slot"),
            _ => null,
        };
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.MemberRead => Wb.Response(request.Id,
                    ReadbackFor(request, Grabbable, Member).ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, def.ToJsonString()),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        Assert.Equal("System.Single", component.Members["Value"].Type);
        // A generic definition cannot be flattened to a FullName: Type stays null.
        Assert.Null(component.Members["Maybe"].Type);
        // member.read's own enumType is never overridden by the definition.
        Assert.Equal("[FrooxEngine]FrooxEngine.Alignment", component.Members["Mode"].Type);
        // A field whose definition reports no valueType stays null.
        Assert.Null(component.Members["Mystery"].Type);
        // Non-field members and nested element members are untouched.
        Assert.Null(component.Members["Target"].Type);
        Assert.Equal("[FrooxEngine]FrooxEngine.Slot", component.Members["Target"].TargetType);
        Assert.Null(component.Members["Items"].Elements![0].Type);
    }

    [Fact]
    public async Task SearchComponentTypesAsync_RescoresByQuery_RequestsServerCap()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        // The server answers ordinal-sorted; the client re-ranks with SearchScore like the direct path.
        JsonObject search = SearchResult(
        [
            "[Custom]Slider",
            "[FrooxEngine]FrooxEngine.AlphaSlider",
            Slider,
            "[FrooxEngine]FrooxEngine.SliderThing",
            "[FrooxEngine]FrooxEngine.ZzzSlider",
        ]);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionSearch => Wb.Response(request.Id, search.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        IReadOnlyList<string> types = await client.SearchComponentTypesAsync("slider", 50, guard.Token);

        Assert.Equal(new[]
        {
            "[Custom]Slider",                              // bare name equals the query
            Slider,                                        // ends with ".Slider"
            "[FrooxEngine]FrooxEngine.SliderThing",        // last segment starts with query
            "[FrooxEngine]FrooxEngine.AlphaSlider",        // substring, ordinal order
            "[FrooxEngine]FrooxEngine.ZzzSlider",
        }, types);

        RpcRequest request = Assert.Single(log);
        Assert.Equal(RpcMethods.ReflectionSearch, request.Method);
        Assert.Equal("slider", Param(request, "query"));
        Assert.Equal(500, request.Params!.Value.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task SearchComponentTypesAsync_ClampsLimit()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject search = SearchResult(
        [
            "[Custom]Slider",
            "[FrooxEngine]FrooxEngine.AlphaSlider",
            Slider,
        ]);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, search.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        Assert.Equal(new[] { "[Custom]Slider", Slider },
            await client.SearchComponentTypesAsync("slider", 2, guard.Token));
        Assert.Equal(new[] { "[Custom]Slider" },
            await client.SearchComponentTypesAsync("slider", 0, guard.Token));
    }

    [Fact]
    public async Task SearchComponentTypesAsync_TruncatedAnswer_StillReturnsRankedSubset()
    {
        // A truncated reflection.search answer cannot reproduce the direct path's full-list
        // ranking; the ranked subset is returned anyway because the signature cannot flag it.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject search = SearchResult([Slider, "[FrooxEngine]FrooxEngine.ZzzSlider"], truncated: true);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, search.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        IReadOnlyList<string> types = await client.SearchComponentTypesAsync("slider", 10, guard.Token);

        Assert.Equal(new[] { Slider, "[FrooxEngine]FrooxEngine.ZzzSlider" }, types);
    }

    [Fact]
    public async Task SearchComponentTypesAsync_CapabilityDenied_ThrowsUnavailableWithCode()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => new RpcError(request.Id,
                new RpcErrorDetail(RpcErrorCodes.CapabilityNotGranted, "reflection.read is not granted.")), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.SearchComponentTypesAsync("slider", 10, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Contains("CAPABILITY_NOT_GRANTED", ex.Message);
    }

    [Fact]
    public async Task DescribeComponentTypeAsync_FullName_GoesStraightToReflectionComponent()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject def = ReflectionResult(ComponentDefinition(Slider, new JsonObject
        {
            ["Value"] = MemberDef("Value", "Field", valueType: TypeRef("[mscorlib]System.Single")),
        }, "Slider"));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, def.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        ComponentTypeInfo info = await client.DescribeComponentTypeAsync(Slider, guard.Token);

        Assert.Equal(Slider, info.FullTypeName);
        Assert.Equal("Test/Components", info.CategoryPath);
        Assert.Equal(new[] { "Value" }, info.Members.Select(m => m.Name));

        RpcRequest request = Assert.Single(log);
        Assert.Equal(RpcMethods.ReflectionComponent, request.Method);
        Assert.Equal(Slider, Param(request, "componentType"));
    }

    [Fact]
    public async Task DescribeComponentTypeAsync_ShortName_ResolvesViaSearchThenDescribes()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject search = SearchResult([Slider, "[FrooxEngine]FrooxEngine.SliderThing"]);
        JsonObject def = ReflectionResult(ComponentDefinition(Slider, new JsonObject
        {
            ["Value"] = MemberDef("Value", "Field", valueType: TypeRef("[mscorlib]System.Single")),
        }, "Slider"));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionSearch => Wb.Response(request.Id, search.ToJsonString()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, def.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        ComponentTypeInfo info = await client.DescribeComponentTypeAsync("Slider", guard.Token);

        Assert.Equal(Slider, info.FullTypeName);
        Assert.Equal(new[] { RpcMethods.ReflectionSearch, RpcMethods.ReflectionComponent },
            log.Select(r => r.Method).ToArray());
        // The search query is the last dot segment; only FrooxEngine.Slider resolves exactly
        // ("SliderThing" matches the substring but is not a candidate).
        Assert.Equal("Slider", Param(log.First(), "query"));
        Assert.Equal(Slider, Param(log.Last(), "componentType"));
    }

    [Fact]
    public async Task DescribeComponentTypeAsync_AmbiguousShortName_ThrowsNotFound()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject search = SearchResult([Slider, "[CustomComponents]CustomComponents.Slider"]);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionSearch => Wb.Response(request.Id, search.ToJsonString()),
                _ => Unexpected(request),
            }, ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.DescribeComponentTypeAsync("Slider", guard.Token));

        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Contains("ambiguous", ex.Message);
    }

    [Fact]
    public async Task DescribeComponentTypeAsync_NoMatch_ThrowsNotFound()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        JsonObject search = SearchResult(["[FrooxEngine]FrooxEngine.SliderThing"]);
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, search.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.DescribeComponentTypeAsync("Nope.Missing", guard.Token));

        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task DescribeTypeAsync_ComponentType_MapsWithoutEnumCall()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject typeResult = ReflectionResult(TypeDefinition(Slider, "Slider", isComponent: true,
            baseType: TypeRef("[FrooxEngine]FrooxEngine.Component")));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionType => Wb.Response(request.Id, typeResult.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        Core.TypeInfo info = await client.DescribeTypeAsync(Slider, guard.Token);

        Assert.Equal(Slider, info.FullTypeName);
        Assert.True(info.IsComponent);
        Assert.True(info.IsWorldElement);
        Assert.Null(info.EnumValues);

        RpcRequest request = Assert.Single(log);
        Assert.Equal(RpcMethods.ReflectionType, request.Method);
        Assert.Equal(Slider, Param(request, "typeName"));
    }

    [Fact]
    public async Task DescribeTypeAsync_EnumType_AlsoCallsReflectionEnum()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        const string alignment = "[FrooxEngine]FrooxEngine.Alignment";
        JsonObject typeResult = ReflectionResult(TypeDefinition(alignment, "Alignment",
            isValueType: true, isEnum: true,
            baseType: TypeRef("[mscorlib]System.Enum")));
        JsonObject enumResult = ReflectionResult(EnumDefinition(alignment, isFlags: true,
            ("Near", 2), ("Far", 8)));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionType => Wb.Response(request.Id, typeResult.ToJsonString()),
                RpcMethods.ReflectionEnum => Wb.Response(request.Id, enumResult.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        Core.TypeInfo info = await client.DescribeTypeAsync(alignment, guard.Token);

        Assert.True(info.IsEnum);
        Assert.True(info.IsFlags);
        Assert.NotNull(info.EnumValues);
        Assert.Equal(2, info.EnumValues!["Near"]);
        Assert.Equal(8, info.EnumValues["Far"]);
        Assert.False(info.IsWorldElement);

        Assert.Equal(new[] { RpcMethods.ReflectionType, RpcMethods.ReflectionEnum },
            log.Select(r => r.Method).ToArray());
        Assert.Equal(alignment, Param(log.Last(), "typeName"));
    }

    [Fact]
    public async Task DescribeTypeAsync_UndecidableWorldElement_ThrowsUnsupported()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        const string helper = "[FrooxEngine]FrooxEngine.SomeHelper";
        JsonObject typeResult = ReflectionResult(TypeDefinition(helper, "SomeHelper",
            baseType: TypeRef("[mscorlib]System.Object")));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, typeResult.ToJsonString()), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.DescribeTypeAsync(helper, guard.Token));

        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
    }

    [Fact]
    public async Task DescribeTypeAsync_UnknownFullName_ThrowsTypeNotFound()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject unknown = ReflectionResult(null, "The type is not known to this session.");
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => Wb.Response(request.Id, unknown.ToJsonString()), ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.DescribeTypeAsync("[FrooxEngine]FrooxEngine.Missing", guard.Token));

        Assert.Equal("TYPE_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        // A [Assembly]-qualified name is never resolved through search.
        Assert.Single(log);
        Assert.Equal(RpcMethods.ReflectionType, log.First().Method);
    }

    [Fact]
    public async Task DescribeTypeAsync_ShortName_ResolvesThenRetriesType()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject unknown = ReflectionResult(null, "The type is not known to this session.");
        JsonObject search = SearchResult([Slider]);
        JsonObject typeResult = ReflectionResult(TypeDefinition(Slider, "Slider", isComponent: true,
            baseType: TypeRef("[FrooxEngine]FrooxEngine.Component")));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.ReflectionType => Wb.Response(request.Id,
                    (Param(request, "typeName") == Slider ? typeResult : unknown).ToJsonString()),
                RpcMethods.ReflectionSearch => Wb.Response(request.Id, search.ToJsonString()),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        Core.TypeInfo info = await client.DescribeTypeAsync("Slider", guard.Token);

        Assert.Equal(Slider, info.FullTypeName);
        Assert.Equal(
            new[] { RpcMethods.ReflectionType, RpcMethods.ReflectionSearch, RpcMethods.ReflectionType },
            log.Select(r => r.Method).ToArray());
    }
}

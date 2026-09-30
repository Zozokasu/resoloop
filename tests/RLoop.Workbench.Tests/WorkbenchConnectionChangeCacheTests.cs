using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ResoniteWorkbench.Protocol;
using RLoop.Core;
using RLoop.Workbench;

namespace RLoop.Workbench.Tests;

/// <summary>
/// The _componentTypes/_memberNames caches describe the session they were learned from, so a
/// session.status answer reporting a different connectionId must drop them before the next
/// read. These tests drive GetSlotAsync -> GetSessionInfoAsync -> GetComponentAsync over a
/// fake Workbench peer and assert which RPCs the second read sends.
/// </summary>
public sealed class WorkbenchConnectionChangeCacheTests
{
    private const string Grabbable = "[FrooxEngine]FrooxEngine.Grabbable";
    private const string Dial = "[FrooxEngine]FrooxEngine.Dial";

    private static string Param(RpcRequest request, string property) =>
        request.Params!.Value.GetProperty(property).GetString()!;

    // ----- wire fixtures (camelCase, matching WorkbenchJson.Options serialization) -----

    private static JsonObject SlotRecord(string id, string? name, string? parentId, bool? isActive,
        int level, IEnumerable<string>? childIds = null, JsonArray? components = null) =>
        new()
        {
            ["id"] = id,
            ["name"] = name,
            ["parentId"] = parentId,
            ["isActive"] = isActive is { } active ? JsonValue.Create(active) : null,
            ["level"] = level,
            ["childIds"] = new JsonArray((childIds ?? []).Select(child => (JsonNode?)child).ToArray()),
            ["components"] = components ?? new JsonArray(),
            ["isReferenceOnly"] = false,
        };

    private static JsonArray ComponentRefs(params (string Id, string? Type)[] components) =>
        new(components.Select(c => (JsonNode?)new JsonObject
        {
            ["id"] = c.Id, ["componentType"] = c.Type,
        }).ToArray());

    private static JsonObject Snapshot(string scopeRootId, JsonObject slots) =>
        new()
        {
            ["sessionId"] = "sess-1",
            ["connectionId"] = "conn-a",
            ["revision"] = 7,
            ["scopeRootId"] = scopeRootId,
            ["budget"] = new JsonObject { ["maxDepth"] = 8, ["maxSlots"] = 8192 },
            ["fields"] = "Structure, ComponentTypes",
            ["source"] = "Live",
            ["receivedSlotCount"] = slots.Count,
            ["truncation"] = JsonValue.Create("None"),
            ["slots"] = slots,
            ["unexpanded"] = new JsonArray(),
            ["excluded"] = new JsonArray(),
        };

    private static JsonObject ObserveResult(JsonNode? value) =>
        new()
        {
            ["value"] = value,
            ["completeness"] = "Complete",
            ["provenance"] = null,
            ["unknownReason"] = null,
        };

    private static JsonObject ReflectionResult(JsonNode? value) =>
        new() { ["value"] = value, ["provenance"] = null, ["unknownReason"] = null };

    private static JsonObject SessionConnected(string connectionId) =>
        new()
        {
            ["state"] = "Connected",
            ["connection"] = new JsonObject
            {
                ["connectionId"] = connectionId,
                ["generation"] = 0,
                ["remote"] = new JsonObject
                {
                    ["resoniteVersion"] = "2025.9.2.1349",
                    ["resoniteLinkVersion"] = "0.13.1",
                    ["uniqueSessionId"] = "uni-1",
                },
            },
        };

    /// <summary>A session.status result reporting the drop: state Disconnected and no connection payload.</summary>
    private const string SessionDisconnected =
        """{"state":"Disconnected","connection":null,"reconnectAttempt":0,"disconnectReason":"user"}""";

    private static JsonObject Readback(string componentId, string? componentType, string memberName,
        JsonNode? member, string? unknownReason = null) =>
        new()
        {
            ["sessionId"] = "sess-1",
            ["connectionId"] = "conn-a",
            ["componentId"] = componentId,
            ["componentType"] = componentType,
            ["memberName"] = memberName,
            ["member"] = member,
            ["observedAt"] = "2026-09-30T00:00:00+00:00",
            ["unknownReason"] = unknownReason,
        };

    private static JsonObject Field(string id, string valueType, string? valueJson) =>
        new()
        {
            ["kind"] = "field", ["id"] = id, ["valueType"] = valueType,
            ["valueJson"] = valueJson, ["enumType"] = null,
        };

    private static JsonObject Reference(string id, string? targetId, string? targetType) =>
        new() { ["kind"] = "reference", ["id"] = id, ["targetId"] = targetId, ["targetType"] = targetType };

    private static JsonObject TypeDefinition(string fullTypeName, string name) =>
        new()
        {
            ["fullTypeName"] = fullTypeName,
            ["assemblyName"] = "FrooxEngine",
            ["namespace"] = "FrooxEngine",
            ["name"] = name,
            ["baseType"] = TypeRef("[FrooxEngine]FrooxEngine.Component"),
            ["isComponent"] = true,
            ["isSyncObject"] = false,
            ["isAbstract"] = false,
            ["isInterface"] = false,
            ["isValueType"] = false,
            ["isEnum"] = false,
            ["isGenericType"] = false,
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

    private static JsonObject ComponentDefinition(string fullTypeName, JsonObject members, string name) =>
        new()
        {
            ["type"] = TypeDefinition(fullTypeName, name),
            ["categoryPath"] = "Test/Components",
            ["flattened"] = true,
            ["members"] = members,
            ["methods"] = new JsonArray(),
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

    private static RpcMessage Unexpected(RpcRequest request) =>
        new RpcError(request.Id, new RpcErrorDetail("TEST_UNEXPECTED", $"unexpected {request.Method}"));

    private static JsonNode? GrabbableMember(string componentId, string memberName) => memberName switch
    {
        "Value" => Field("m-value", "float", "1.5"),
        "Target" => Reference("m-target", "Reso_9", "[FrooxEngine]FrooxEngine.Slot"),
        _ => null,
    };

    private static JsonNode? DialMember(string componentId, string memberName) => memberName switch
    {
        "Ratio" => Field("m-ratio", "float", "0.75"),
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
    public async Task GetSessionInfoAsync_ConnectionChange_ClearsTypeAndMemberCaches()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        var connectionId = "conn-a";
        JsonObject observe = ObserveResult(Snapshot("root", new JsonObject
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        }));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request =>
            {
                // session.status is the moment the Workbench reports the session switch.
                if (request.Method == RpcMethods.SessionStatus)
                    connectionId = "conn-b";
                return request.Method switch
                {
                    RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString(), connectionId),
                    RpcMethods.SessionStatus => Wb.Response(request.Id,
                        SessionConnected(connectionId).ToJsonString(), connectionId),
                    RpcMethods.ReflectionComponent => Wb.Response(request.Id, ReflectionResult(
                        Param(request, "componentType") == Grabbable
                            ? GrabbableDefinition() : DialDefinition()).ToJsonString(), connectionId),
                    RpcMethods.MemberRead => Wb.Response(request.Id, ReadbackFor(request,
                            connectionId == "conn-a" ? Grabbable : Dial,
                            connectionId == "conn-a" ? GrabbableMember : DialMember)
                        .ToJsonString(), connectionId),
                    _ => Unexpected(request),
                };
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        // First read on conn-a: comp-1 is a Grabbable; its type and member names get cached.
        _ = await client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token);
        Assert.Equal("conn-a", client.Meta.ConnectionId);
        Assert.Equal(
            new[]
            {
                RpcMethods.WorldObserve, RpcMethods.ReflectionComponent,
                RpcMethods.MemberRead, RpcMethods.MemberRead,
            },
            log.Select(r => r.Method).ToArray());
        log.Clear();

        _ = await client.GetSessionInfoAsync(guard.Token);
        Assert.Equal("conn-b", client.Meta.ConnectionId);

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        // On conn-b comp-1 is a Dial; only a fresh probe + reflection.component can answer that.
        // A stale cache would have reused Grabbable and its member list without any probe.
        Assert.Equal(Dial, component.Type);
        Assert.Equal("field", component.Members["Ratio"].Kind);
        Assert.Equal(
            new[]
            {
                RpcMethods.SessionStatus, RpcMethods.MemberRead,
                RpcMethods.ReflectionComponent, RpcMethods.MemberRead,
            },
            log.Select(r => r.Method).ToArray());
        Assert.Equal("__resoloop_type_probe__", Param(log.Skip(1).First(), "memberName"));
        Assert.Equal(Dial, Param(log.Skip(2).First(), "componentType"));
        Assert.Equal("Ratio", Param(log.Last(), "memberName"));
    }

    [Fact]
    public async Task GetSessionInfoAsync_SameConnection_KeepsTypeAndMemberCaches()
    {
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        JsonObject observe = ObserveResult(Snapshot("root", new JsonObject
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        }));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, request => request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString(), "conn-a"),
                RpcMethods.SessionStatus => Wb.Response(request.Id,
                    SessionConnected("conn-a").ToJsonString(), "conn-a"),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id,
                    ReflectionResult(GrabbableDefinition()).ToJsonString(), "conn-a"),
                RpcMethods.MemberRead => Wb.Response(request.Id,
                    ReadbackFor(request, Grabbable, GrabbableMember).ToJsonString(), "conn-a"),
                _ => Unexpected(request),
            }, ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        _ = await client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token);
        log.Clear();

        _ = await client.GetSessionInfoAsync(guard.Token);
        Assert.Equal("conn-a", client.Meta.ConnectionId);

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        Assert.Equal(Grabbable, component.Type);
        Assert.Equal(2, component.Members.Count);
        // The cached type and member names are still valid: only the two member.read calls run.
        Assert.Equal(
            new[] { RpcMethods.SessionStatus, RpcMethods.MemberRead, RpcMethods.MemberRead },
            log.Select(r => r.Method).ToArray());
        Assert.Equal(new[] { "Value", "Target" },
            log.Where(r => r.Method == RpcMethods.MemberRead)
                .Select(r => Param(r, "memberName")).ToArray());
    }

    /// <summary>The drop-then-switch script: conn-a answers, the first session.status reports
    /// Disconnected with no identity, and everything afterwards is conn-b.</summary>
    private static Func<RpcRequest, RpcMessage> DisconnectThenSwitch(
        JsonObject observe, Func<string> connectionId, Func<int> nextSessionStatusCall)
    {
        return request =>
        {
            if (request.Method == RpcMethods.SessionStatus && nextSessionStatusCall() == 1)
            {
                return Wb.Response(request.Id, SessionDisconnected,
                    connectionId: null, sessionId: null);
            }
            return request.Method switch
            {
                RpcMethods.WorldObserve => Wb.Response(request.Id, observe.ToJsonString(), connectionId()),
                RpcMethods.SessionStatus => Wb.Response(request.Id,
                    SessionConnected(connectionId()).ToJsonString(), connectionId()),
                RpcMethods.ReflectionComponent => Wb.Response(request.Id, ReflectionResult(
                    Param(request, "componentType") == Grabbable
                        ? GrabbableDefinition() : DialDefinition()).ToJsonString(), connectionId()),
                RpcMethods.MemberRead => Wb.Response(request.Id, ReadbackFor(request,
                        connectionId() == "conn-a" ? Grabbable : Dial,
                        connectionId() == "conn-a" ? GrabbableMember : DialMember)
                    .ToJsonString(), connectionId()),
                _ => Unexpected(request),
            };
        };
    }

    [Fact]
    public async Task GetSessionInfoAsync_DisconnectThenDifferentConnection_ClearsCaches()
    {
        // The disconnect answer carries a null meta.connectionId, so the conn-b answer must be
        // compared against the last non-null id (conn-a), not the null the drop left behind.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        var connectionId = "conn-a";
        var sessionStatusCalls = 0;
        JsonObject observe = ObserveResult(Snapshot("root", new JsonObject
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        }));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, DisconnectThenSwitch(observe,
                () => connectionId,
                () =>
                {
                    if (++sessionStatusCalls == 1) connectionId = "conn-b";
                    return sessionStatusCalls;
                }), ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        // Learn comp-1 -> Grabbable and Grabbable's member definitions on conn-a.
        _ = await client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token);
        Assert.Equal("conn-a", client.Meta.ConnectionId);
        log.Clear();

        // The session drops; the answer reports no connection identity.
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));
        Assert.Equal("WORKBENCH_NOT_CONNECTED", ex.Code);
        Assert.Null(client.Meta.ConnectionId);

        // The next status answer is the new connection; conn-a's caches are already unusable.
        _ = await client.GetSessionInfoAsync(guard.Token);
        Assert.Equal("conn-b", client.Meta.ConnectionId);

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        // On conn-b comp-1 is a Dial; only a fresh probe + reflection.component can answer that.
        Assert.Equal(Dial, component.Type);
        Assert.Equal("field", component.Members["Ratio"].Kind);
        Assert.Equal(
            new[]
            {
                RpcMethods.SessionStatus, RpcMethods.SessionStatus, RpcMethods.MemberRead,
                RpcMethods.ReflectionComponent, RpcMethods.MemberRead,
            },
            log.Select(r => r.Method).ToArray());
        Assert.Equal("__resoloop_type_probe__", Param(log.Skip(2).First(), "memberName"));
        Assert.Equal(Dial, Param(log.Skip(3).First(), "componentType"));
        Assert.Equal("Ratio", Param(log.Last(), "memberName"));
    }

    [Fact]
    public async Task ReadPath_DisconnectThenDifferentConnection_ClearsCaches()
    {
        // Same drop, but no second session.status: the read path itself observes conn-b first,
        // so the stale entries must not be consulted before the first conn-b answer arrives.
        string pipeName = Wb.NewPipeName();
        using var guard = new CancellationTokenSource(Wb.GuardTimeout);
        var log = new ConcurrentQueue<RpcRequest>();
        var connectionId = "conn-a";
        var sessionStatusCalls = 0;
        JsonObject observe = ObserveResult(Snapshot("root", new JsonObject
        {
            ["root"] = SlotRecord("root", "Root", null, true, 0,
                components: ComponentRefs(("comp-1", Grabbable))),
        }));
        await using var server = FakeWorkbenchServer.Start(pipeName, (s, ct) =>
            Wb.ServeAsync(s, DisconnectThenSwitch(observe,
                () => connectionId,
                () =>
                {
                    if (++sessionStatusCalls == 1) connectionId = "conn-b";
                    return sessionStatusCalls;
                }), ct, log));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(Wb.PipeUri(pipeName), Wb.ConnectTimeout, guard.Token);

        _ = await client.GetSlotAsync("root", 1, includeComponentData: true, guard.Token);
        Assert.Equal("conn-a", client.Meta.ConnectionId);
        log.Clear();

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));
        Assert.Equal("WORKBENCH_NOT_CONNECTED", ex.Code);
        Assert.Null(client.Meta.ConnectionId);

        ComponentInfo component = await client.GetComponentAsync("comp-1", guard.Token);

        Assert.Equal(Dial, component.Type);
        Assert.Equal("field", component.Members["Ratio"].Kind);
        Assert.Equal(
            new[]
            {
                RpcMethods.SessionStatus, RpcMethods.MemberRead,
                RpcMethods.ReflectionComponent, RpcMethods.MemberRead,
            },
            log.Select(r => r.Method).ToArray());
        Assert.Equal("__resoloop_type_probe__", Param(log.Skip(1).First(), "memberName"));
        Assert.Equal(Dial, Param(log.Skip(2).First(), "componentType"));
        Assert.Equal("Ratio", Param(log.Last(), "memberName"));
    }
}

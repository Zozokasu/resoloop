using System.Collections.Concurrent;
using System.Text.Json;
using ResoniteWorkbench.Protocol;
using RLoop.Core;

namespace RLoop.Workbench;

/// <summary>
/// The read side of <see cref="WorkbenchResoniteClient"/>: slot trees through
/// <c>world.observe</c>, component data through <c>member.read</c>, and type metadata
/// through <c>reflection.*</c>. One logical read may take several RPC calls; they run
/// sequentially and must all come from the connection the first answer reported.
/// </summary>
public sealed partial class WorkbenchResoniteClient
{
    /// <summary>
    /// member.read reports componentType even when the member does not exist, so one probe with
    /// an impossible member name learns the type of a component no observation described.
    /// </summary>
    private const string TypeProbeMember = "__resoloop_type_probe__";

    /// <summary>reflection.search accepts at most 500 names; asks for the cap before re-ranking.</summary>
    private const int ReflectionSearchLimit = 500;

    /// <summary>componentId → componentType, learned from world.observe records and type probes.</summary>
    private readonly ConcurrentDictionary<string, string> _componentTypes = new(StringComparer.Ordinal);

    /// <summary>componentType → declared member names and field value types; only successful reflection.component answers are stored.</summary>
    private readonly ConcurrentDictionary<string, WorkbenchReflectionMapper.DeclaredMembers> _memberNames = new(StringComparer.Ordinal);

    public async Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData,
        CancellationToken cancellationToken = default)
    {
        _ = RequireClient();
        WorldObserveParams parameters = WorkbenchSlotMapper.BuildObserveParams(id, depth);
        var operation = new ReadOperation();
        RpcResponse response = await CallReadAsync(RpcMethods.WorldObserve, parameters, operation, cancellationToken)
            .ConfigureAwait(false);
        JsonElement result = ResultOf(response);
        SlotInfo tree = WorkbenchSlotMapper.MapObservation(result, id, depth);
        Meta = Meta with { ObservedScopeRootId = ScopeRootOf(result) ?? id };
        RememberComponentTypes(tree);
        if (!includeComponentData)
            return tree;
        return await FillComponentMembersAsync(tree, operation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
    {
        _ = RequireClient();
        var operation = new ReadOperation();
        string type = await RequireComponentTypeAsync(id, null, operation, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, MemberValue> members =
            await ReadComponentMembersAsync(id, type, operation, cancellationToken).ConfigureAwait(false);
        return new ComponentInfo(id, type, members);
    }

    public async Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default)
    {
        _ = RequireClient();
        var operation = new ReadOperation();
        // Fetch the server's cap, then re-rank like the direct path does over its full list.
        RpcResponse response = await CallReadAsync(RpcMethods.ReflectionSearch,
            new ReflectionSearchParams(query, ReflectionSearchLimit), operation, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> names = WorkbenchReflectionMapper.MapSearch(ResultOf(response), out bool truncated);
        // truncated means candidates beyond the 500-name cap never arrived, so the re-ranked
        // order can differ from the direct path's; the signature cannot report that.
        return names
            .Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => SearchScore(name, query)).ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public async Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        _ = RequireClient();
        var operation = new ReadOperation();
        // "[Assembly]Namespace.Name" names go straight to reflection.component; short names
        // resolve through reflection.search the way the direct path resolves them.
        string resolved = type.StartsWith('[')
            ? type
            : await ResolveComponentTypeAsync(type, operation, cancellationToken).ConfigureAwait(false);
        RpcResponse response = await CallReadAsync(RpcMethods.ReflectionComponent,
            new ReflectionComponentParams(resolved), operation, cancellationToken).ConfigureAwait(false);
        return WorkbenchReflectionMapper.MapComponentType(ResultOf(response), type);
    }

    public async Task<Core.TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        _ = RequireClient();
        var operation = new ReadOperation();
        string queried = type;
        JsonElement result = ResultOf(await CallReadAsync(RpcMethods.ReflectionType,
            new ReflectionTypeParams(queried), operation, cancellationToken).ConfigureAwait(false));

        // Same order as the direct path (ResoniteLinkClientAdapter.DescribeTypeCoreAsync): retry
        // a short name once, resolved against the session's component types.
        if (IsUnknown(result) && !type.StartsWith('['))
        {
            string resolved;
            try
            {
                resolved = await ResolveComponentTypeAsync(type, operation, cancellationToken).ConfigureAwait(false);
            }
            catch (RLoopException ex) when (ex.Code == "COMPONENT_TYPE_NOT_FOUND")
            {
                throw new RLoopException("TYPE_NOT_FOUND",
                    $"Runtime type '{type}' was not found. Non-component types may require an assembly-qualified name.",
                    ExitCodes.NotFound,
                    suggestions: ["Copy the exact [Assembly]Namespace.Type from Reflection, or use type describe COMPONENT --member FIELD to inspect its field type and enum values."],
                    innerException: ex);
            }
            queried = resolved;
            result = ResultOf(await CallReadAsync(RpcMethods.ReflectionType,
                new ReflectionTypeParams(queried), operation, cancellationToken).ConfigureAwait(false));
        }

        JsonElement? enumResult = null;
        if (TryReadEnum(result, out string? fullTypeName))
        {
            enumResult = ResultOf(await CallReadAsync(RpcMethods.ReflectionEnum,
                new ReflectionEnumParams(fullTypeName ?? queried), operation, cancellationToken).ConfigureAwait(false));
        }
        return WorkbenchReflectionMapper.MapType(result, enumResult, type);
    }

    /// <summary>
    /// The Workbench exposes no value-conversion preflight, so validate --strict and
    /// type check --manifest must fail explicitly instead of silently skipping checks
    /// the direct backend would run.
    /// </summary>
    public Task ValidateComponentMemberAsync(string componentType, string member, string rawValue,
        CancellationToken cancellationToken = default) =>
        Task.FromException(WorkbenchErrors.Unsupported(
            "Value conversion preflight is not available on the workbench backend; " +
            $"'{componentType}.{member}' cannot be validated against '{rawValue}'."));

    /// <summary>
    /// Every read call goes through here: unconnected → InvalidOperationException like
    /// GetSessionInfoAsync, NOT_CONNECTED → WORKBENCH_NOT_CONNECTED with the same message,
    /// transport/other RPC failures → WORKBENCH_UNAVAILABLE with the server code in the message,
    /// a stale response → WORKBENCH_UNAVAILABLE, and each response refreshes <see cref="Meta"/>.
    /// Caller cancellation propagates as OperationCanceledException.
    /// </summary>
    private async Task<RpcResponse> CallReadAsync(string method, object? parameters,
        ReadOperation operation, CancellationToken cancellationToken)
    {
        WorkbenchRpcClient client = RequireClient();

        RpcResponse response;
        try
        {
            response = await client.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcCallException ex) when (ex.Code == "NOT_CONNECTED")
        {
            throw new RLoopException("WORKBENCH_NOT_CONNECTED",
                "Workbench is not connected to a Resonite session.",
                ExitCodes.ConnectionFailed,
                suggestions: ["Connect the Workbench App to a Resonite session, then retry."],
                innerException: ex);
        }
        catch (RpcCallException ex)
        {
            throw WorkbenchErrors.Unavailable($"The Workbench {method} call failed ({ex.Code}): {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw WorkbenchErrors.Unavailable($"The Workbench {method} call failed: {ex.Message}", ex);
        }

        // A reconnect invalidates everything learned from earlier responses.
        InvalidateCachesIfConnectionChanged(Meta.ConnectionId, response.Meta.ConnectionId);

        // An "unknown" reflection.* answer reports no connection identity (meta.connectionId
        // and meta.sessionId are null): it means the Workbench could not describe the type,
        // not that the connection changed. Its null fields must neither erase the known ids
        // nor feed the mid-read connection check; a different non-null id still does both.
        bool unknownReflection = IsUnknownReflectionResult(method, response);
        Meta = new WorkbenchConnectionMeta(
            response.Meta.ConnectionId ?? (unknownReflection ? Meta.ConnectionId : null),
            response.Meta.SessionId ?? (unknownReflection ? Meta.SessionId : null),
            response.Meta.WorldRevision, Meta.ObservedScopeRootId);

        if (response.Meta.Stale == true)
        {
            throw WorkbenchErrors.Unavailable(
                $"The Workbench {method} response is stale; the connection changed during the read.");
        }

        if (!unknownReflection || response.Meta.ConnectionId is not null)
            operation.Observe(response.Meta.ConnectionId, method);
        return response;
    }

    /// <summary>
    /// Clears the learned component-type and member-name caches when a response comes from a
    /// different connection than the last one recorded; entries learned from the previous
    /// session would misidentify components on the new one.
    /// </summary>
    private void InvalidateCachesIfConnectionChanged(string? previous, string? current)
    {
        if (previous is not null && current is not null
            && !string.Equals(previous, current, StringComparison.Ordinal))
        {
            _componentTypes.Clear();
            _memberNames.Clear();
        }
    }

    private WorkbenchRpcClient RequireClient() =>
        _client ?? throw new InvalidOperationException(
            "WorkbenchResoniteClient is not connected; call ConnectAsync first.");

    /// <summary>The response's result element; Undefined when absent, which mappers reject as malformed.</summary>
    private static JsonElement ResultOf(RpcResponse response) => response.Result ?? default;

    private static string? ScopeRootOf(JsonElement queryResult) =>
        queryResult.ValueKind == JsonValueKind.Object
        && queryResult.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("scopeRootId", out JsonElement scope) && scope.ValueKind == JsonValueKind.String
            ? scope.GetString()
            : null;

    /// <summary>A ReflectionResult&lt;T&gt; whose value is absent: the Workbench could not answer.</summary>
    private static bool IsUnknown(JsonElement reflectionResult) =>
        reflectionResult.ValueKind == JsonValueKind.Object
        && (!reflectionResult.TryGetProperty("value", out JsonElement value)
            || value.ValueKind == JsonValueKind.Null);

    /// <summary>
    /// True for a reflection.* response carrying an "unknown" result. Scoped to reflection.*:
    /// member.read results have no "value" property and would classify as unknown otherwise.
    /// </summary>
    private static bool IsUnknownReflectionResult(string method, RpcResponse response) =>
        method.StartsWith("reflection.", StringComparison.Ordinal)
        && response.Result is { } result
        && IsUnknown(result);

    /// <summary>True when the reflection.type result is a known enum type; also reads its reported name.</summary>
    private static bool TryReadEnum(JsonElement typeResult, out string? fullTypeName)
    {
        fullTypeName = null;
        if (typeResult.ValueKind != JsonValueKind.Object
            || !typeResult.TryGetProperty("value", out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
            return false;
        fullTypeName = WireJson.OptionalString(value, "fullTypeName");
        return value.TryGetProperty("isEnum", out JsonElement isEnum) && isEnum.ValueKind == JsonValueKind.True;
    }

    /// <summary>Records every observed componentId → componentType so GetComponentAsync can skip probing.</summary>
    private void RememberComponentTypes(SlotInfo slot)
    {
        foreach (ComponentSummary component in slot.Components)
            if (component.Type.Length > 0)
                _componentTypes[component.Id] = component.Type;
        foreach (SlotInfo child in slot.Children)
            RememberComponentTypes(child);
    }

    /// <summary>Rebuilds the tree with ComponentSummary.Members filled on every fully read slot.</summary>
    private async Task<SlotInfo> FillComponentMembersAsync(SlotInfo slot, ReadOperation operation,
        CancellationToken cancellationToken)
    {
        if (slot.IsReferenceOnly || (slot.Components.Count == 0 && slot.Children.Count == 0))
            return slot;

        IReadOnlyList<ComponentSummary> components = slot.Components;
        if (components.Count > 0)
        {
            var filled = new ComponentSummary[components.Count];
            for (var i = 0; i < components.Count; i++)
            {
                ComponentSummary summary = components[i];
                filled[i] = summary with
                {
                    Members = await ReadComponentMembersAsync(summary.Id, summary.Type, operation, cancellationToken)
                        .ConfigureAwait(false)
                };
            }
            components = filled;
        }

        IReadOnlyList<SlotInfo> children = slot.Children;
        if (children.Count > 0)
        {
            var filled = new SlotInfo[children.Count];
            for (var i = 0; i < children.Count; i++)
                filled[i] = await FillComponentMembersAsync(children[i], operation, cancellationToken)
                    .ConfigureAwait(false);
            children = filled;
        }
        return slot with { Components = components, Children = children };
    }

    /// <summary>
    /// The component's type: the observed type when known, the cache of earlier observations,
    /// else one member.read probe. COMPONENT_NOT_FOUND when no source can name it.
    /// </summary>
    private async Task<string> RequireComponentTypeAsync(string componentId, string? componentType,
        ReadOperation operation, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(componentType)) return componentType;
        if (_componentTypes.TryGetValue(componentId, out string? known) && known.Length > 0) return known;

        RpcResponse probe = await CallReadAsync(RpcMethods.MemberRead,
            new MemberReadParams(componentId, TypeProbeMember), operation, cancellationToken).ConfigureAwait(false);
        MemberReadResult readback = WorkbenchMemberMapper.MapReadback(ResultOf(probe));
        if (readback.ComponentType is { Length: > 0 } probed)
        {
            _componentTypes[componentId] = probed;
            return probed;
        }
        throw WorkbenchErrors.NotFound("COMPONENT_NOT_FOUND",
            $"Component '{componentId}' was not found; member.read reported no component type" +
            (readback.UnknownReason is null ? "." : $": {readback.UnknownReason}"),
            new Dictionary<string, object?> { ["componentId"] = componentId });
    }

    /// <summary>Declared members of a component type, cached by type name.</summary>
    private async Task<WorkbenchReflectionMapper.DeclaredMembers> MemberDefinitionsAsync(
        string componentType, ReadOperation operation, CancellationToken cancellationToken)
    {
        if (_memberNames.TryGetValue(componentType, out WorkbenchReflectionMapper.DeclaredMembers? cached))
            return cached;
        RpcResponse response = await CallReadAsync(RpcMethods.ReflectionComponent,
            new ReflectionComponentParams(componentType), operation, cancellationToken).ConfigureAwait(false);
        JsonElement result = ResultOf(response);
        // An "unknown" reflection.component answer is not "no such type": the direct backend
        // can still read the members, so report the Workbench's own reason as Unavailable
        // (and cache nothing) instead of the mapper's COMPONENT_TYPE_NOT_FOUND.
        if (IsUnknown(result))
        {
            throw WorkbenchErrors.Unavailable(
                $"The Workbench could not describe component type '{componentType}' " +
                $"(reflection.component returned no definition): {UnknownReason(result)}; " +
                "the members of a component of this type cannot be read. " +
                "Use --backend link for this subtree.");
        }
        WorkbenchReflectionMapper.DeclaredMembers declared =
            WorkbenchReflectionMapper.MemberDefinitions(result, componentType);
        _memberNames[componentType] = declared;
        return declared;
    }

    /// <summary>The unknownReason an "unknown" reflection result reports, or a note that it reported none.</summary>
    private static string UnknownReason(JsonElement reflectionResult) =>
        reflectionResult.ValueKind == JsonValueKind.Object
        && reflectionResult.TryGetProperty("unknownReason", out JsonElement reason)
        && reason.ValueKind == JsonValueKind.String
        && reason.GetString() is { Length: > 0 } text
            ? text
            : "the Workbench reported no reason";

    /// <summary>
    /// Reads every declared member live, one member.read call each, in declaration order.
    /// A single unreadable member fails the whole component instead of returning a partial map.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MemberValue>> ReadComponentMembersAsync(
        string componentId, string? componentType, ReadOperation operation, CancellationToken cancellationToken)
    {
        string type = await RequireComponentTypeAsync(componentId, componentType, operation, cancellationToken)
            .ConfigureAwait(false);
        WorkbenchReflectionMapper.DeclaredMembers declared =
            await MemberDefinitionsAsync(type, operation, cancellationToken).ConfigureAwait(false);

        var members = new Dictionary<string, MemberValue>(StringComparer.Ordinal);
        foreach (string name in declared.Names)
        {
            RpcResponse response = await CallReadAsync(RpcMethods.MemberRead,
                new MemberReadParams(componentId, name), operation, cancellationToken).ConfigureAwait(false);
            MemberReadResult readback = WorkbenchMemberMapper.MapReadback(ResultOf(response));
            if (readback.Member is null)
            {
                throw WorkbenchErrors.Unavailable(
                    $"member.read could not read member '{name}' of component '{componentId}'" +
                    (readback.UnknownReason is null ? "." : $": {readback.UnknownReason}"));
            }
            members[name] = WithDeclaredFieldType(readback.Member, name, declared);
        }
        return members;
    }

    /// <summary>
    /// member.read reports wire value types ("float"), not the CLR names the direct backend puts
    /// in MemberValue.Type ("System.Single"), so the mapper leaves it null on plain fields; the
    /// reflection.component definition's valueType supplies it. Only a top-level field with no
    /// Type of its own is filled - enum types from member.read and nested members stay as mapped.
    /// </summary>
    private static MemberValue WithDeclaredFieldType(MemberValue member, string name,
        WorkbenchReflectionMapper.DeclaredMembers declared) =>
        member is { Kind: "field", Type: null }
        && declared.FieldValueTypes.TryGetValue(name, out string? fieldType)
            ? member with { Type = fieldType }
            : member;

    /// <summary>
    /// Resolves a short component type name the way the direct path's ResolveComponentTypeAsync
    /// does, over the names reflection.search reports: the assembly-stripped name must equal
    /// <paramref name="type"/> or end in ".{type}" (OrdinalIgnoreCase, like the direct path's
    /// candidate pass), and exactly one candidate may exist. A truncated search can never
    /// prove uniqueness, so even a single candidate is refused.
    /// </summary>
    private async Task<string> ResolveComponentTypeAsync(string type, ReadOperation operation,
        CancellationToken cancellationToken)
    {
        string query = type[(type.LastIndexOf('.') + 1)..];
        if (query.Length == 0)
        {
            throw WorkbenchErrors.NotFound("COMPONENT_TYPE_NOT_FOUND",
                $"Component type '{type}' was not found.",
                new Dictionary<string, object?> { ["type"] = type });
        }

        RpcResponse response = await CallReadAsync(RpcMethods.ReflectionSearch,
            new ReflectionSearchParams(query, ReflectionSearchLimit), operation, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> names = WorkbenchReflectionMapper.MapSearch(ResultOf(response), out bool truncated);
        var matches = names.Where(name =>
        {
            string bare = WorkbenchReflectionMapper.StripAssembly(name);
            return bare.Equals(type, StringComparison.OrdinalIgnoreCase)
                || bare.EndsWith("." + type, StringComparison.OrdinalIgnoreCase);
        }).Take(20).ToArray();

        if (matches.Length == 1 && !truncated) return matches[0];
        if (matches.Length == 1)
        {
            throw WorkbenchErrors.NotFound("COMPONENT_TYPE_NOT_FOUND",
                $"Component type '{type}' matched '{matches[0]}', but the type search was truncated at " +
                $"{ReflectionSearchLimit} names, so uniqueness cannot be proven.",
                new Dictionary<string, object?> { ["type"] = type, ["candidates"] = matches });
        }
        throw WorkbenchErrors.NotFound("COMPONENT_TYPE_NOT_FOUND",
            matches.Length == 0
                ? $"Component type '{type}' was not found."
                : $"Component type '{type}' is ambiguous ({matches.Length} candidates).",
            new Dictionary<string, object?>
            {
                ["type"] = type,
                ["candidates"] = matches.Take(10).ToArray(),
            });
    }

    // Duplicated from ResoniteLinkClientAdapter.SearchScore (RLoop.ResoniteLink is not referenced):
    // exact bare match < name ending < last-segment prefix < substring.
    private static int SearchScore(string type, string query)
    {
        var bare = WorkbenchReflectionMapper.StripAssembly(type);
        if (bare.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (bare.EndsWith('.' + query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (bare.Split('.').Last().StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    /// <summary>
    /// The connectionId of one logical read. Any change mid-operation means the answers no
    /// longer describe one session, so the read fails instead of mixing them.
    /// </summary>
    private sealed class ReadOperation
    {
        private string? _connectionId;
        private bool _observed;

        public void Observe(string? connectionId, string method)
        {
            if (!_observed)
            {
                _observed = true;
                _connectionId = connectionId;
                return;
            }
            if (!string.Equals(_connectionId, connectionId, StringComparison.Ordinal))
            {
                throw WorkbenchErrors.Unavailable(
                    $"The Workbench {method} response came from connection '{connectionId ?? "<none>"}', " +
                    $"but this read was using '{_connectionId ?? "<none>"}'; the connection changed mid-read.");
            }
        }
    }
}

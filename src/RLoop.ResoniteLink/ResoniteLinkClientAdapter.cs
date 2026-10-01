using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RLoop.Core;
using Link = ResoniteLink;

namespace RLoop.ResoniteLink;

public sealed class ResoniteLinkClientAdapter : IResoniteClient, IResoniteClientDiagnostics, IReflectionMetadataClient
{
    private readonly Link.LinkInterface _link = new();
    private readonly IMetadataLink _meta;
    private readonly Func<Link.GetSlot, Task<Link.SlotData>> _getSlotData;
    private readonly TimeSpan _requestTimeout;
    private readonly Dictionary<string, Link.ComponentDefinition> _componentDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeInfo> _typeDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableMetric> _metrics = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _allComponentTypes;
    // Guards generation checks and cache reads/writes against the reconnect clear (never held across an await).
    private readonly object _cacheLock = new();
    /// <summary>Test seam: runs inside the cache lock right after a successful generation check, before the cache update.</summary>
    internal Action? AfterGenerationCheckForTests;
    /// <summary>Test seam: runs inside the cache lock right after the cache update, before the lock is released.</summary>
    internal Action? AfterCommitForTests;
    /// <summary>Test seam: runs inside ConnectAsync's cache lock (the reconnect clear), so a test can record when the reconnect got the lock.</summary>
    internal Action? InsideReconnectLockForTests;
    /// <summary>Test seam: after capturing the generation, before entering a metadata cache read lock.</summary>
    internal Action<string>? BeforeCacheReadForTests;
    /// <summary>Test seam: after copying a cache hit and its evidence, while still holding the read lock.</summary>
    internal Action<string>? AfterCacheReadForTests;
    private int _cacheHits;
    private string? _generation;
    private Uri? _uri;
    private ReflectionCacheOptions _cacheOptions;
    private ReflectionMetadataCache? _diskCache;
    private SessionInfo? _cacheSession;
    private bool _forceLiveMetadata;
    private readonly Dictionary<string, (DateTimeOffset ObservedAt, bool Live)> _componentEvidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset ObservedAt, bool Live)> _typeEvidence = new(StringComparer.Ordinal);

    public ResoniteLinkClientAdapter(TimeSpan? requestTimeout = null, ReflectionCacheOptions? reflectionCache = null)
        : this(null, requestTimeout, reflectionCache)
    {
    }

    internal ResoniteLinkClientAdapter(IMetadataLink? metadataLink, TimeSpan? requestTimeout = null, ReflectionCacheOptions? reflectionCache = null,
        Func<Link.GetSlot, Task<Link.SlotData>>? getSlotData = null)
    {
        _meta = metadataLink ?? new SdkMetadataLink(_link);
        _getSlotData = getSlotData ?? _link.GetSlotData;
        _cacheOptions = reflectionCache ?? new();
        _cacheOptions.Validate();
        _requestTimeout = requestTimeout is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(30);
    }

    public async Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // One adapter serves one connection. Re-connecting a live adapter would let in-flight metadata
        // reads of the old connection write into the new connection's caches.
        if (_meta.IsConnected)
            throw new RLoopException("ALREADY_CONNECTED", "The ResoniteLink client is already connected; dispose it and create a new client to connect elsewhere.",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["url"] = _uri?.ToString() });
        lock (_cacheLock)
        {
            InsideReconnectLockForTests?.Invoke();
            Volatile.Write(ref _generation, null);
            ClearReflectionMemory();
            _diskCache = null;
            _cacheSession = null;
            _forceLiveMetadata = false;
        }
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _meta.Connect(uri, timeoutCts.Token).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _uri = uri;
                Volatile.Write(ref _generation, Guid.NewGuid().ToString("N"));
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RLoopException("CONNECTION_TIMEOUT", $"Connection to '{uri}' timed out after {timeout.TotalSeconds:0.#} seconds.",
                ExitCodes.Timeout, new Dictionary<string, object?> { ["url"] = uri.ToString(), ["timeoutSeconds"] = timeout.TotalSeconds },
                ["Verify ResoniteLink is enabled for the active world and the port is current."], ex);
        }
        catch (Exception ex)
        {
            throw new RLoopException("CONNECTION_FAILED", $"Could not connect to ResoniteLink at '{uri}': {ex.Message}",
                ExitCodes.ConnectionFailed, new Dictionary<string, object?> { ["url"] = uri.ToString() },
                ["Verify Resonite is running, ResoniteLink is enabled, and RESONITE_LINK_URL contains the current port."], ex);
        }
    }

    public async Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var generation = CaptureGeneration();
        var response = await Wait(_meta.GetSessionData(), "session.get", cancellationToken);
        EnsureGeneration(generation);
        EnsureSuccess(response.Success, response.ErrorInfo, "SESSION_INFO_FAILED");
        return CommitIfCurrent(generation, () => _cacheSession = new SessionInfo(_uri!.ToString(), true, response.ResoniteVersion, response.ResoniteLinkVersion, response.UniqueSessionId, generation.Length == 0 ? null : generation));
    }

    public async Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_getSlotData(new Link.GetSlot { SlotID = id, Depth = depth, IncludeComponentData = includeComponentData }), "slot.get", cancellationToken);
        EnsureSuccess(response, IsSlotNotFound(id, response.ErrorInfo) ? "SLOT_NOT_FOUND" : "RESONITE_OPERATION_FAILED",
            new Dictionary<string, object?> { ["slotId"] = id, ["errorInfo"] = response.ErrorInfo });
        return ModelMapper.MapSlot(response.Data);
    }

    // Read-only live record c04-live/raw-getslot.txt (2026-10-01): Resonite 2026.9.18.82,
    // ResoniteLink 0.13.1.0; only the exact requested-ID absence response is evidence of NotFound.
    private static bool IsSlotNotFound(string id, string? errorInfo) =>
        string.Equals(errorInfo?.Trim(), $"Slot with ID '{id}' not found.", StringComparison.Ordinal);

    public async Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.GetComponentData(new Link.GetComponent { ComponentID = id }), "component.get", cancellationToken);
        EnsureSuccess(response, "COMPONENT_NOT_FOUND", new Dictionary<string, object?> { ["componentId"] = id });
        return ModelMapper.MapComponent(response.Data);
    }

    public async Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var slot = new Link.Slot
        {
            ID = request.RequestedId,
            Parent = new Link.Reference { TargetID = request.ParentId },
            Name = new Link.Field_string { Value = request.Name },
            Position = request.Position is null ? null : new Link.Field_float3 { Value = ToLink(request.Position) },
            Rotation = request.Rotation is null ? null : new Link.Field_floatQ { Value = ToLink(request.Rotation) },
            Scale = request.Scale is null ? null : new Link.Field_float3 { Value = ToLink(request.Scale) }
        };
        var response = await Wait(_link.AddSlot(new Link.AddSlot { Data = slot }), "slot.add", cancellationToken);
        EnsureSuccess(response, "SLOT_CREATE_FAILED", new Dictionary<string, object?> { ["parentId"] = request.ParentId, ["name"] = request.Name });
        return response.EntityId;
    }

    public async Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var slot = new Link.Slot
        {
            ID = request.Id,
            Parent = request.ParentId is null ? null : new Link.Reference { TargetID = request.ParentId },
            Name = request.Name is null ? null : new Link.Field_string { Value = request.Name },
            Position = request.Position is null ? null : new Link.Field_float3 { Value = ToLink(request.Position) },
            Rotation = request.Rotation is null ? null : new Link.Field_floatQ { Value = ToLink(request.Rotation) },
            Scale = request.Scale is null ? null : new Link.Field_float3 { Value = ToLink(request.Scale) }
        };
        var response = await Wait(_link.UpdateSlot(new Link.UpdateSlot { Data = slot }), "slot.update", cancellationToken);
        EnsureSuccess(response, "SLOT_UPDATE_FAILED", new Dictionary<string, object?> { ["slotId"] = request.Id });
    }

    public async Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.RemoveSlot(new Link.RemoveSlot { SlotID = id }), "slot.remove", cancellationToken);
        EnsureSuccess(response, "SLOT_DELETE_FAILED", new Dictionary<string, object?> { ["slotId"] = id });
    }

    public async Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var members = await ParseMembersAsync(componentType, fields, cancellationToken);
        var definition = await GetComponentDefinitionCachedAsync(componentType, cancellationToken);
        var resolvedType = definition.Type.FullTypeName;

        var response = await Wait(_link.AddComponent(new Link.AddComponent
        {
            ContainerSlotId = slotId,
            Data = new Link.Component { ComponentType = resolvedType, Members = members }
        }), "component.add", cancellationToken);
        if (!response.Success) InvalidateDiskEvidence();
        EnsureSuccess(response, "COMPONENT_ADD_FAILED", new Dictionary<string, object?> { ["slotId"] = slotId, ["componentType"] = resolvedType });
        return new ComponentCreateResult(response.EntityId, resolvedType);
    }

    public async Task ValidateComponentMemberAsync(string componentType, string member, string rawValue,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        _ = await ParseMembersAsync(componentType, new Dictionary<string, string> { [member] = rawValue }, cancellationToken);
    }

    public async Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var componentResponse = await Wait(_link.GetComponentData(new Link.GetComponent { ComponentID = componentId }), "component.get", cancellationToken);
        EnsureSuccess(componentResponse, "COMPONENT_NOT_FOUND", new Dictionary<string, object?> { ["componentId"] = componentId });
        await SetComponentMembersAsync(componentId, componentResponse.Data.ComponentType,
            new Dictionary<string, string> { [member] = rawValue }, cancellationToken);
    }

    public async Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (fields.Count == 0) return;
        var members = await ParseMembersAsync(componentType, fields, cancellationToken);
        var response = await Wait(_link.UpdateComponent(new Link.UpdateComponent
        {
            Data = new Link.Component { ID = componentId, Members = members }
        }), "component.update", cancellationToken);
        if (!response.Success) InvalidateDiskEvidence();
        EnsureSuccess(response, "COMPONENT_UPDATE_FAILED", new Dictionary<string, object?>
            { ["componentId"] = componentId, ["members"] = fields.Keys.ToArray() });
    }

    public async Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var response = await Wait(_link.RemoveComponent(new Link.RemoveComponent { ComponentID = componentId }), "component.remove", cancellationToken);
        EnsureSuccess(response, "COMPONENT_REMOVE_FAILED", new Dictionary<string, object?> { ["componentId"] = componentId });
    }

    public async Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var types = await GetAllComponentTypeNames(cancellationToken);
        // An empty list cannot prove "no match"; only a non-empty complete list can.
        if (types.Count == 0) throw TypeListUnknown($"searching for '{query}'", query);
        return types
            .Where(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => SearchScore(x, query)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public async Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return (await DescribeComponentMetadataAsync(type, false, cancellationToken)).Value;
    }

    public async Task<ReflectionMetadata<ComponentTypeInfo>> DescribeComponentMetadataAsync(string type, bool refresh = false, CancellationToken ct = default)
    {
        EnsureConnected();
        var (definition, observedAt, live) = await GetComponentDefinitionWithEvidenceAsync(type, ct, refresh);
        var members = definition.Members.Select(x => ModelMapper.MapMemberDefinition(x.Key, x.Value)).ToArray();
        var methods = definition.Methods.Select(ModelMapper.MapMethodDefinition).ToArray();
        return new(new ComponentTypeInfo(definition.Type.FullTypeName, definition.CategoryPath,
            ModelMapper.Render(definition.Type.BaseType), definition.Type.IsGenericType, members, methods), observedAt, live);
    }

    public async Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        return (await DescribeTypeCoreAsync(type, cancellationToken, false)).Value;
    }

    public async Task<ReflectionMetadata<TypeInfo>> DescribeTypeMetadataAsync(string type, bool refresh = false, CancellationToken ct = default)
    {
        var (value, observedAt, live) = await DescribeTypeCoreAsync(type, ct, refresh);
        return new(value, observedAt, live);
    }

    /// <summary>Returns the definition together with the evidence recorded for it, read under the same lock as the cache.</summary>
    private async Task<(TypeInfo Value, DateTimeOffset ObservedAt, bool Live)> DescribeTypeCoreAsync(string type, CancellationToken cancellationToken, bool refresh)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();
        var generation = CaptureGeneration();
        BeforeCacheReadForTests?.Invoke("type");
        lock (_cacheLock)
        {
            EnsureGeneration(generation);
            refresh |= _forceLiveMetadata;
            if (_typeDefinitions.TryGetValue(type, out var cached) && _typeEvidence.TryGetValue(type, out var cachedEvidence) &&
                (!refresh || cachedEvidence.Live))
            {
                Interlocked.Increment(ref _cacheHits);
                AfterCacheReadForTests?.Invoke("type");
                return (cached, cachedEvidence.ObservedAt, cachedEvidence.Live);
            }
        }
        var disk = await GetReflectionCacheAsync(cancellationToken);
        EnsureGeneration(generation);
        if (refresh) disk.Invalidate("type", type);
        var stored = refresh ? null : disk.Read<TypeInfo>("type", type, t => !string.IsNullOrEmpty(t.FullTypeName) &&
            (!t.IsEnum || t.EnumValues is not null && t.IsFlags is not null));
        if (stored is not null)
        {
            CommitIfCurrent(generation, () => RememberType(type, stored.Value, stored.ObservedAt, false));
            return (stored.Value, stored.ObservedAt, false);
        }
        var response = await Wait(_meta.GetTypeDefinition(type), "type.get", cancellationToken);
        // A failure answer of an old connection says nothing about the current one: check the generation first.
        EnsureGeneration(generation);
        if (!response.Success)
        {
            try
            {
                var resolved = await ResolveComponentTypeAsync(type, cancellationToken);
                EnsureGeneration(generation);
                response = await Wait(_meta.GetTypeDefinition(resolved), "type.get", cancellationToken);
                EnsureGeneration(generation);
            }
            catch (RLoopException ex) when (ex.Code == "COMPONENT_TYPE_NOT_FOUND")
            {
                // A component-only list says nothing about the existence of enums or other runtime types.
                // Keep the original failed definition response and classify it as unreadable below.
            }
        }
        if (!response.Success)
        {
            // The response has no structured absence signal. A failed read does not prove type absence.
            var detail = string.IsNullOrWhiteSpace(response.ErrorInfo) ? "ResoniteLink operation failed." : response.ErrorInfo!;
            throw new RLoopException("TYPE_DEFINITION_UNREADABLE",
                $"ResoniteLink could not read the definition of type '{type}'; its existence cannot be determined from this response: {detail}",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["type"] = type, ["errorInfo"] = response.ErrorInfo },
                ["Use inspect --members on a live component, or type describe COMPONENT (without --member)."]);
        }
        IReadOnlyDictionary<string, long>? enumValues = null;
        bool? isFlags = null;
        if (response.Definition.IsEnum)
        {
            var enumResponse = await Wait(_meta.GetEnumDefinition(response.Definition.FullTypeName), "enum.get", cancellationToken);
            EnsureGeneration(generation);
            EnsureSuccess(enumResponse.Success, enumResponse.ErrorInfo, "ENUM_DESCRIBE_FAILED");
            enumValues = enumResponse.Values;
            isFlags = enumResponse.IsFlags;
        }
        var mapped = ModelMapper.MapType(response.Definition, enumValues, isFlags);
        var observedAt = DateTimeOffset.UtcNow;
        CommitIfCurrent(generation, () =>
        {
            RememberType(type, mapped, observedAt, true);
            disk.Write("type", type, mapped, observedAt);
            if (type != mapped.FullTypeName) disk.Write("type", mapped.FullTypeName, mapped, observedAt);
        });
        return (mapped, observedAt, true);
    }

    public async Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, JsonElement>? arguments = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var converted = new Dictionary<string, Link.Data>(StringComparer.Ordinal);
        foreach (var argument in arguments ?? new Dictionary<string, JsonElement>())
            converted[argument.Key] = ConvertMethodArgument(argument.Value);
        var response = await Wait(_link.CallMethod(new Link.CallSyncMethod
        {
            TargetID = componentId,
            MethodName = method,
            Arguments = converted
        }), "method.call", cancellationToken);
        return new SyncMethodCallResult(response.Success,
            response.Result is null ? null : JsonSerializer.SerializeToNode(response.Result, response.Result.GetType()),
            response.Success ? null : response.ErrorInfo);
    }

    public async Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        Link.AssetData response = asset.Kind.ToLowerInvariant() switch
        {
            "texture" or "texture2d" => await Wait(_link.ImportTexture(new Link.ImportTexture2DFile { FilePath = resolvedSource }), "asset.texture.import", cancellationToken),
            "audio" or "audioclip" => await Wait(_link.ImportAudioClip(new Link.ImportAudioClipFile { FilePath = resolvedSource }), "asset.audio.import", cancellationToken),
            "mesh" => await ImportMeshJson(resolvedSource, cancellationToken),
            _ => throw new RLoopException("ASSET_KIND_UNSUPPORTED", $"Asset kind '{asset.Kind}' is not importable. Use a resdb URI for material and other runtime assets.", ExitCodes.ValidationFailed)
        };
        EnsureSuccess(response, "ASSET_IMPORT_FAILED", new Dictionary<string, object?> { ["kind"] = asset.Kind, ["source"] = resolvedSource });
        return response.AssetURL?.ToString() ?? throw new RLoopException("ASSET_URL_MISSING", "Asset import succeeded without an AssetURL.", ExitCodes.OperationFailed);
    }

    private async Task<Link.AssetData> ImportMeshJson(string path, CancellationToken cancellationToken)
    {
        var request = MeshImportDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        if (MeshImportDocument.ToRawStatic(request) is { } raw)
            return await Wait(_link.ImportMesh(raw), "asset.mesh.import", cancellationToken);
        return await Wait(_link.ImportMesh(request), "asset.mesh.import", cancellationToken);
    }

    private static Link.Data ConvertMethodArgument(JsonElement value)
    {
        var suffix = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Number when value.TryGetInt32(out _) => "int",
            JsonValueKind.Number => "float",
            _ => throw new RLoopException("METHOD_ARGUMENT_UNSUPPORTED", $"Method argument kind '{value.ValueKind}' is not supported.", ExitCodes.ValidationFailed)
        };
        var type = typeof(Link.Data).Assembly.GetType("ResoniteLink.Data_" + suffix)
                   ?? throw new RLoopException("METHOD_ARGUMENT_UNSUPPORTED", $"ResoniteLink has no Data_{suffix} wrapper.", ExitCodes.ValidationFailed);
        var data = (Link.Data)Activator.CreateInstance(type)!;
        var property = type.GetProperty("Value") ?? type.GetProperty("BoxedValue")
            ?? throw new RLoopException("METHOD_ARGUMENT_UNSUPPORTED", $"ResoniteLink Data_{suffix} has no writable value.", ExitCodes.ValidationFailed);
        object converted = suffix switch
        {
            "string" => value.GetString() ?? string.Empty,
            "bool" => value.GetBoolean(),
            "int" => value.GetInt32(),
            _ => value.GetSingle()
        };
        property.SetValue(data, converted);
        return data;
    }

    private async Task<string> ResolveComponentTypeAsync(string query, CancellationToken cancellationToken)
    {
        var types = await GetAllComponentTypeNames(cancellationToken);
        // An empty list cannot prove absence: report unknown, never NotFound.
        if (types.Count == 0) throw TypeListUnknown("resolving the type", query);
        var exact = types.FirstOrDefault(x => x.Equals(query, StringComparison.Ordinal) || StripAssembly(x).Equals(query, StringComparison.Ordinal));
        if (exact is not null) return exact;
        var matches = types.Where(x => StripAssembly(x).EndsWith('.' + query, StringComparison.Ordinal) ||
                                       StripAssembly(x).Equals(query, StringComparison.OrdinalIgnoreCase)).Take(20).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1)
            throw new RLoopException("COMPONENT_TYPE_AMBIGUOUS",
                $"Component type '{query}' matches {matches.Length}{(matches.Length == 20 ? " or more" : "")} types; use the full [Assembly]Namespace.Type name.",
                ExitCodes.InvalidArguments, new Dictionary<string, object?> { ["query"] = query, ["candidates"] = matches },
                matches.Take(10).ToArray());
        throw new RLoopException("COMPONENT_TYPE_NOT_FOUND", $"Component type '{query}' was not found.",
            ExitCodes.NotFound, new Dictionary<string, object?> { ["query"] = query }, []);
    }

    // Category walk bounds. Values follow the resonite-workbench regression suite (commit 7d40c92,
    // ResoniteLinkSessionLink MaxCategories = 5000, depth 32); they guard a runaway or cyclic tree.
    internal const int MaxTypeCategories = 5000;
    internal const int MaxTypeCategoryDepth = 32;

    private async Task<IReadOnlyList<string>> GetAllComponentTypeNames(CancellationToken cancellationToken)
    {
        var generation = CaptureGeneration();
        BeforeCacheReadForTests?.Invoke("component-types");
        lock (_cacheLock)
        {
            EnsureGeneration(generation);
            var cached = _allComponentTypes;
            if (cached is not null)
            {
                Interlocked.Increment(ref _cacheHits);
                AfterCacheReadForTests?.Invoke("component-types");
                return cached;
            }
        }
        var response = await Wait(_meta.GetAllComponentTypes(), "component-types.get-all", cancellationToken);
        // Check the generation before judging the answer: a failure of an old connection must abort, not become a type error.
        EnsureGeneration(generation);
        EnsureSuccess(response.Success, response.ErrorInfo, "TYPE_SEARCH_FAILED");
        if (response.ComponentTypes is { Count: > 0 })
            return CommitIfCurrent(generation, () => _allComponentTypes = response.ComponentTypes!);
        EnsureGeneration(generation);

        var results = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        await CollectCategory(string.Empty, 0, results, visited, generation, cancellationToken);
        var collected = results.ToArray();
        // Do not remember an empty list: it is "unknown", and a later call may see the loaded world.
        if (collected.Length == 0) { EnsureGeneration(generation); return collected; }
        return CommitIfCurrent(generation, () => _allComponentTypes = collected);
    }

    /// <summary>Any failure here leaves the list incomplete (unknown); the caller never receives partial results.</summary>
    private async Task CollectCategory(string category, int depth, HashSet<string> results, HashSet<string> visited,
        string generation, CancellationToken cancellationToken)
    {
        if (visited.Contains(category)) return;
        if (depth > MaxTypeCategoryDepth)
            throw CategoryWalkIncomplete(category, $"the category tree is deeper than {MaxTypeCategoryDepth} levels");
        if (visited.Count >= MaxTypeCategories)
            throw CategoryWalkIncomplete(category, $"the category tree has more than {MaxTypeCategories} categories");
        visited.Add(category);
        LinkTypeList response;
        try { response = await Wait(_meta.GetComponentTypes(category), "component-types.get-category", cancellationToken); }
        catch (Exception ex) when (ex is not (OperationCanceledException or RLoopException))
        {
            // Order: generation, then connection, then the kind of failure. An old connection's failure is never a type error.
            EnsureGeneration(generation);
            EnsureConnected();
            throw CategoryWalkIncomplete(category, $"{ex.GetType().Name}: {ex.Message}", ex);
        }
        // Same order for answers: an answer of an old connection (failure or success) is discarded before it is read.
        EnsureGeneration(generation);
        if (!response.Success)
        {
            // A failure answer that arrives with a dropped connection is a connection failure (same as the exception path).
            EnsureConnected();
            throw CategoryWalkIncomplete(category, string.IsNullOrWhiteSpace(response.ErrorInfo) ? "ResoniteLink reported a failure" : response.ErrorInfo);
        }
        if (response.ComponentTypes is null) throw CategoryWalkIncomplete(category, "the response has no ComponentTypes");
        if (response.SubCategories is null) throw CategoryWalkIncomplete(category, "the response has no SubCategories");
        foreach (var type in response.ComponentTypes) results.Add(type);
        foreach (var child in response.SubCategories)
        {
            // A child name is one segment. Empty or separator-carrying names would request a path that does not exist.
            if (string.IsNullOrWhiteSpace(child) || child.Contains('/') || child.Contains('\\'))
                throw CategoryWalkIncomplete(category, $"the sub-category name '{child}' is empty or contains a path separator");
            var childPath = string.IsNullOrEmpty(category) ? child : category + "/" + child;
            await CollectCategory(childPath, depth + 1, results, visited, generation, cancellationToken);
        }
    }

    private static RLoopException CategoryWalkIncomplete(string category, string reason, Exception? inner = null) =>
        new("TYPE_SEARCH_INCOMPLETE",
            $"The component type list could not be collected completely (category '{category}': {reason}), so type existence is unknown.",
            ExitCodes.OperationFailed, new Dictionary<string, object?> { ["category"] = category, ["reason"] = reason },
            ["Retry after the world finishes loading, or read the component with inspect --members."], inner);

    private static RLoopException TypeListUnknown(string what, string query) =>
        new("TYPE_SEARCH_INCOMPLETE",
            $"The component type list returned by ResoniteLink was empty while {what}, so whether '{query}' exists is unknown.",
            ExitCodes.OperationFailed, new Dictionary<string, object?> { ["query"] = query },
            ["Retry after the world finishes loading, or read the component with inspect --members."]);

    private async Task<Link.ComponentDefinition> GetComponentDefinitionCachedAsync(string type,
        CancellationToken cancellationToken, bool refresh = false) =>
        (await GetComponentDefinitionWithEvidenceAsync(type, cancellationToken, refresh)).Definition;

    /// <summary>Returns the definition together with the evidence recorded for it, read under the same lock as the cache.</summary>
    private async Task<(Link.ComponentDefinition Definition, DateTimeOffset ObservedAt, bool Live)> GetComponentDefinitionWithEvidenceAsync(
        string type, CancellationToken cancellationToken, bool refresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = CaptureGeneration();
        BeforeCacheReadForTests?.Invoke("component");
        if (TryGetCachedComponent(type, generation, ref refresh, out var hit))
        {
            Interlocked.Increment(ref _cacheHits);
            return hit;
        }
        var disk = await GetReflectionCacheAsync(cancellationToken);
        EnsureGeneration(generation);
        if (refresh) disk.Invalidate("component-sdk", type);
        var stored = refresh ? null : disk.Read<Link.ComponentDefinition>("component-sdk", type, ValidComponentDefinition);
        if (stored is not null)
        {
            CommitIfCurrent(generation, () => RememberComponent(type, stored.Value, stored.ObservedAt, false));
            return (stored.Value, stored.ObservedAt, false);
        }
        var first = await TryGetComponentDefinitionAsync(type, cancellationToken);
        if (first.Response is not { Success: true })
        {
            EnsureGeneration(generation);
            var resolved = await ResolveComponentTypeAsync(type, cancellationToken);
            EnsureGeneration(generation);
            if (TryGetCachedComponent(resolved, generation, ref refresh, out _))
            {
                // Read again inside the commit lock, after the generation check, so a reconnect clear cannot race it.
                var aliased = CommitIfCurrent(generation, () =>
                {
                    if (!TryGetCachedComponent(resolved, generation, ref refresh, out var again)) return ((Link.ComponentDefinition, DateTimeOffset, bool)?)null;
                    _componentDefinitions[type] = again.Definition;
                    _componentEvidence[type] = (again.ObservedAt, again.Live);
                    return again;
                });
                if (aliased is { } value)
                {
                    Interlocked.Increment(ref _cacheHits);
                    return value;
                }
            }
            first = await TryGetComponentDefinitionAsync(resolved, cancellationToken);
            EnsureGeneration(generation);
            if (first.Response is not { Success: true })
            {
                // The type is in the type list, so this is "definition unreadable", never "not found".
                var detail = first.Error is not null ? first.Error.Message
                    : string.IsNullOrWhiteSpace(first.Response?.ErrorInfo) ? "ResoniteLink operation failed." : first.Response!.ErrorInfo!;
                throw new RLoopException("COMPONENT_DEFINITION_UNREADABLE",
                    $"Component type '{resolved}' exists but ResoniteLink could not read its definition: {detail}",
                    ExitCodes.OperationFailed, new Dictionary<string, object?>
                    {
                        ["resolvedType"] = resolved,
                        ["errorInfo"] = first.Response?.ErrorInfo,
                        ["exception"] = first.Error?.GetType().FullName
                    }, ["Use inspect --members on a live component, or type describe (without --member) for type information only."],
                    first.Error);
            }
        }
        var response = first.Response;
        var definition = response.Definition;
        var observedAt = DateTimeOffset.UtcNow;
        CommitIfCurrent(generation, () =>
        {
            RememberComponent(type, definition, observedAt, true);
            disk.Write("component-sdk", type, definition, observedAt);
            if (type != definition.Type.FullTypeName) disk.Write("component-sdk", definition.Type.FullTypeName, definition, observedAt);
        });
        return (definition, observedAt, true);
    }

    private bool TryGetCachedComponent(string type, string generation, ref bool refresh, out (Link.ComponentDefinition Definition, DateTimeOffset ObservedAt, bool Live) hit)
    {
        lock (_cacheLock)
        {
            EnsureGeneration(generation);
            refresh |= _forceLiveMetadata;
            if (_componentDefinitions.TryGetValue(type, out var definition) && _componentEvidence.TryGetValue(type, out var evidence) &&
                (!refresh || evidence.Live))
            {
                hit = (definition, evidence.ObservedAt, evidence.Live);
                AfterCacheReadForTests?.Invoke("component");
                return true;
            }
        }
        hit = default;
        return false;
    }

    /// <summary>Reads one component definition; an SDK exception is kept as data so callers can classify it as unreadable.</summary>
    private async Task<(LinkComponentDefinition? Response, Exception? Error)> TryGetComponentDefinitionAsync(string type, CancellationToken cancellationToken)
    {
        try { return (await Wait(_meta.GetComponentDefinition(type), "component-definition.get", cancellationToken), null); }
        catch (Exception ex) when (ex is not (OperationCanceledException or RLoopException)) { return (null, ex); }
    }

    public void ConfigureReflectionCache(ReflectionCacheOptions options)
    {
        options.Validate();
        lock (_cacheLock)
        {
            _cacheOptions = options;
            _diskCache = null;
            _forceLiveMetadata = false;
            ClearReflectionMemory();
        }
    }

    public ReflectionCacheStatistics SnapshotReflectionCache() => new(_diskCache?.DiskHits ?? 0, _diskCache?.DiskMisses ?? 0, _diskCache?.WriteFailures ?? 0);

    private async Task<ReflectionMetadataCache> GetReflectionCacheAsync(CancellationToken ct)
    {
        if (_diskCache is not null) return _diskCache;
        var generation = CaptureGeneration();
        var session = _cacheSession ?? (_cacheOptions.Mode == "off"
            ? new SessionInfo(_uri?.ToString() ?? string.Empty, true, null, null, null)
            : await GetSessionInfoAsync(ct));
        return CommitIfCurrent(generation, () => _diskCache ??= new ReflectionMetadataCache(session, _cacheOptions));
    }

    private void ClearReflectionMemory()
    {
        _componentDefinitions.Clear(); _typeDefinitions.Clear(); _allComponentTypes = null;
        _componentEvidence.Clear(); _typeEvidence.Clear();
    }

    private void RememberComponent(string name, Link.ComponentDefinition value, DateTimeOffset observedAt, bool live)
    {
        _componentDefinitions[name] = _componentDefinitions[value.Type.FullTypeName] = value;
        _componentEvidence[name] = _componentEvidence[value.Type.FullTypeName] = (observedAt, live);
    }

    private void RememberType(string name, TypeInfo value, DateTimeOffset observedAt, bool live)
    {
        _typeDefinitions[name] = _typeDefinitions[value.FullTypeName] = value;
        _typeEvidence[name] = _typeEvidence[value.FullTypeName] = (observedAt, live);
    }

    internal static bool ValidComponentDefinition(Link.ComponentDefinition value) =>
        value.Type is { FullTypeName.Length: > 0 } && value.Members is not null && value.Methods is not null &&
        value.Methods.All(m => m is not null && !string.IsNullOrEmpty(m.Name)) &&
        value.Members.All(p => !string.IsNullOrEmpty(p.Key) && ValidMemberDefinition(p.Value));

    private static bool ValidMemberDefinition(Link.MemberDefinition? value) => value switch
    {
        Link.FieldDefinition field => ValidTypeReference(field.ValueType),
        Link.ReferenceDefinition reference => ValidTypeReference(reference.TargetType),
        Link.ListDefinition list => ValidMemberDefinition(list.ElementDefinition),
        Link.DictionaryDefinition dictionary => ValidTypeReference(dictionary.KeyType) && ValidMemberDefinition(dictionary.ElementDefinition),
        Link.ArrayDefinition array => ValidTypeReference(array.ValueType),
        Link.SyncObjectMemberDefinition sync => ValidTypeReference(sync.Type),
        Link.EmptyMemberDefinition or Link.SyncPlaybackDefinition => true,
        _ => false
    };

    private static bool ValidTypeReference(Link.TypeReference? value) => value is { Type.Length: > 0 } &&
        (value.GenericArguments is null || value.GenericArguments.All(ValidTypeReference));

    private async Task<Dictionary<string, Link.Member>> ParseMembersAsync(string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var definition = await GetComponentDefinitionCachedAsync(componentType, ct);
                var members = new Dictionary<string, Link.Member>();
                foreach (var field in fields)
                {
                    if (!definition.Members.TryGetValue(field.Key, out var memberDefinition))
                        throw UnknownMember(componentType, field.Key, definition.Members.Keys);
                    members[field.Key] = await ValueCodec.ParseAsync(_link, memberDefinition, field.Value, ct, _requestTimeout, RecordMetric, DescribeTypeAsync);
                }
                return members;
            }
            catch (RLoopException ex) when (attempt == 0 && ex.ExitCode is ExitCodes.ValidationFailed or ExitCodes.NotFound &&
                (_componentEvidence.Values.Any(e => !e.Live) || _typeEvidence.Values.Any(e => !e.Live)))
            {
                // Retry conversion only, before any mutation. Never replay a failed world write here.
                InvalidateDiskEvidence();
            }
        }
    }

    private void InvalidateDiskEvidence()
    {
        foreach (var name in _componentEvidence.Where(p => !p.Value.Live).Select(p => p.Key)) _diskCache?.Invalidate("component-sdk", name);
        foreach (var name in _typeEvidence.Where(p => !p.Value.Live).Select(p => p.Key)) _diskCache?.Invalidate("type", name);
        lock (_cacheLock)
        {
            ClearReflectionMemory();
            _forceLiveMetadata = true;
        }
    }

    private static RLoopException UnknownMember(string type, string member, IEnumerable<string> members)
    {
        var suggestions = members.Where(x => x.Contains(member, StringComparison.OrdinalIgnoreCase)).Take(10).ToArray();
        return new RLoopException("COMPONENT_MEMBER_NOT_FOUND", $"Member '{member}' does not exist on '{type}'.", ExitCodes.NotFound,
            new Dictionary<string, object?> { ["componentType"] = type, ["member"] = member }, suggestions);
    }

    private static int SearchScore(string type, string query)
    {
        var bare = StripAssembly(type);
        if (bare.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (bare.EndsWith('.' + query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (bare.Split('.').Last().StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static string StripAssembly(string type)
    {
        var end = type.IndexOf(']');
        return end >= 0 ? type[(end + 1)..] : type;
    }

    private static Link.float3 ToLink(Vector3Value value) => new() { x = value.X, y = value.Y, z = value.Z };
    private static Link.floatQ ToLink(QuaternionValue value) => new() { x = value.X, y = value.Y, z = value.Z, w = value.W };

    private async Task<T> Wait<T>(Task<T> task, string operation, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            return await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RLoopException("REQUEST_TIMEOUT", $"ResoniteLink request '{operation}' timed out after {_requestTimeout.TotalSeconds:0.#} seconds.",
                ExitCodes.Timeout, new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["timeoutSeconds"] = _requestTimeout.TotalSeconds
                }, ["Retry after checking Resonite responsiveness; apply checkpoints make retry safe."], ex);
        }
        finally
        {
            stopwatch.Stop();
            RecordMetric(operation, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private void RecordMetric(string operation, double elapsedMs)
    {
        lock (_metrics)
        {
            if (!_metrics.TryGetValue(operation, out var metric)) _metrics[operation] = metric = new MutableMetric();
            metric.Requests++;
            metric.ElapsedMs += elapsedMs;
        }
    }

    public void ResetMetrics()
    {
        lock (_metrics) _metrics.Clear();
        Interlocked.Exchange(ref _cacheHits, 0);
        _diskCache?.ResetMetrics();
    }

    public ClientMetrics SnapshotMetrics()
    {
        lock (_metrics)
        {
            var operations = _metrics.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new ClientOperationMetric(x.Key, x.Value.Requests, x.Value.ElapsedMs)).ToArray();
            return new ClientMetrics(operations.Sum(x => x.Requests), Volatile.Read(ref _cacheHits),
                operations.Sum(x => x.ElapsedMs), operations, SnapshotReflectionCache());
        }
    }

    private sealed class MutableMetric
    {
        public int Requests { get; set; }
        public double ElapsedMs { get; set; }
    }

    // Liveness is EnsureConnected's job; a never-connected adapter (offline seam tests) has the empty generation.
    private string CaptureGeneration() => Volatile.Read(ref _generation) ?? string.Empty;

    /// <summary>
    /// Re-checks the generation and applies the cache update atomically with the reconnect clear (same lock), so an answer
    /// of an old connection can neither be remembered nor written to disk after the connection changed. The lock is a plain
    /// monitor that is never held across an await; the disk write inside it is a small local file write.
    /// </summary>
    private T CommitIfCurrent<T>(string generation, Func<T> commit)
    {
        lock (_cacheLock)
        {
            EnsureGeneration(generation);
            AfterGenerationCheckForTests?.Invoke();
            var result = commit();
            AfterCommitForTests?.Invoke();
            return result;
        }
    }

    private void CommitIfCurrent(string generation, Action commit) => CommitIfCurrent(generation, () => { commit(); return 0; });

    /// <summary>Fails when the connection changed while a metadata read was in flight, so a stale answer is never cached.</summary>
    private void EnsureGeneration(string captured)
    {
        if (!string.Equals(CaptureGeneration(), captured, StringComparison.Ordinal))
            throw new RLoopException("CONNECTION_GENERATION_CHANGED",
                "The ResoniteLink connection changed while a metadata request was in flight; the answer was discarded and not cached.",
                ExitCodes.OperationFailed, suggestions: ["Retry the command on the current connection."]);
    }

    private void EnsureConnected()
    {
        if (!_meta.IsConnected) throw new RLoopException("NOT_CONNECTED", "The ResoniteLink client is not connected.", ExitCodes.ConnectionFailed);
    }

    private static void EnsureSuccess(Link.Response response, string code,
        IReadOnlyDictionary<string, object?>? context = null, IReadOnlyList<string>? suggestions = null) =>
        EnsureSuccess(response.Success, response.ErrorInfo, code, context, suggestions);

    private static void EnsureSuccess(bool success, string? errorInfo, string code,
        IReadOnlyDictionary<string, object?>? context = null, IReadOnlyList<string>? suggestions = null)
    {
        if (success) return;
        throw new RLoopException(code, string.IsNullOrWhiteSpace(errorInfo) ? "ResoniteLink operation failed." : errorInfo,
            code.Contains("NOT_FOUND", StringComparison.Ordinal) ? ExitCodes.NotFound : ExitCodes.OperationFailed, context, suggestions);
    }

    /// <summary>The exception swallowed by <see cref="DisposeAsync"/>, kept for diagnostics.</summary>
    public Exception? DisposeException { get; private set; }

    public ValueTask DisposeAsync()
    {
        // ResoniteLink 0.13.1 dereferences its socket when Dispose is called
        // before Connect created one. Keep that Beta quirk inside this adapter.
        // A release failure must never replace the failure that made the caller dispose the client
        // (a throwing DisposeAsync in `await using` would hide the command's own exception).
        try { _meta.Dispose(); }
        catch (Exception ex) { DisposeException = ex; }
        return ValueTask.CompletedTask;
    }
}

internal static class ModelMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static SlotInfo MapSlot(Link.Slot slot) => new(
        slot.ID ?? string.Empty,
        slot.Name?.Value ?? string.Empty,
        slot.Parent?.TargetID,
        slot.Position is null ? null : new Vector3Value(slot.Position.Value.x, slot.Position.Value.y, slot.Position.Value.z),
        slot.Rotation is null ? null : new QuaternionValue(slot.Rotation.Value.x, slot.Rotation.Value.y, slot.Rotation.Value.z, slot.Rotation.Value.w),
        slot.Scale is null ? null : new Vector3Value(slot.Scale.Value.x, slot.Scale.Value.y, slot.Scale.Value.z),
        slot.IsActive?.Value,
        slot.IsPersistent?.Value,
        slot.Tag?.Value,
        slot.IsReferenceOnly,
        (slot.Components ?? []).Select(x => new ComponentSummary(
            x.ID ?? string.Empty,
            x.ComponentType ?? string.Empty,
            x.Members is null ? null : x.Members.ToDictionary(member => member.Key, member => MapMember(member.Value), StringComparer.Ordinal))).ToArray(),
        (slot.Children ?? []).Select(MapSlot).ToArray(), Members: new Dictionary<string, Link.Member?>
        {
            ["Parent"] = slot.Parent, ["Position"] = slot.Position, ["Rotation"] = slot.Rotation,
            ["Scale"] = slot.Scale, ["Name"] = slot.Name, ["Tag"] = slot.Tag,
            ["IsActive"] = slot.IsActive, ["IsPersistent"] = slot.IsPersistent, ["OrderOffset"] = slot.OrderOffset
        }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => MapMember(pair.Value!)));

    public static ComponentInfo MapComponent(Link.Component component) => new(
        component.ID ?? string.Empty,
        component.ComponentType ?? string.Empty,
        (component.Members ?? []).ToDictionary(x => x.Key, x => MapMember(x.Value), StringComparer.Ordinal));

    public static MemberValue MapMember(Link.Member member) => member switch
    {
        Link.SyncPlayback playback => new MemberValue("SyncPlayback", playback.ID,
            "[FrooxEngine]FrooxEngine.SyncPlayback", JsonSerializer.SerializeToNode(playback, JsonOptions)),
        Link.Field_Enum enumField => new MemberValue("field", enumField.ID, enumField.EnumType,
            JsonValue.Create(enumField.Value)),
        Link.Field_Nullable_Enum enumField => new MemberValue("field", enumField.ID, enumField.EnumType,
            JsonValue.Create(enumField.Value)),
        Link.Field field => new MemberValue("field", field.ID, field.ValueType.FullName,
            JsonSerializer.SerializeToNode(field.BoxedValue, field.BoxedValue?.GetType() ?? typeof(object), JsonOptions)),
        Link.Reference reference => new MemberValue("reference", reference.ID, TargetId: reference.TargetID, TargetType: reference.TargetType),
        Link.SyncDictionary dictionary => new MemberValue("dictionary", dictionary.ID,
            Members: MapDictionary(dictionary)),
        Link.SyncObject syncObject => new MemberValue("syncObject", syncObject.ID,
            Members: (syncObject.Members ?? []).ToDictionary(x => x.Key, x => MapMember(x.Value))),
        Link.SyncList list => new MemberValue("list", list.ID, Elements: (list.Elements ?? []).Select(MapMember).ToArray()),
        Link.EmptyElement empty => new MemberValue("empty", empty.ID),
        _ => new MemberValue(member.GetType().Name, member.ID, Value: JsonSerializer.SerializeToNode(member, member.GetType(), JsonOptions))
    };

    private static IReadOnlyDictionary<string, MemberValue> MapDictionary(Link.SyncDictionary dictionary)
    {
        var elements = dictionary.GetType().GetProperty("Elements")?.GetValue(dictionary) as System.Collections.IDictionary;
        var result = new Dictionary<string, MemberValue>(StringComparer.Ordinal);
        if (elements is null) return result;
        foreach (System.Collections.DictionaryEntry entry in elements)
            if (entry.Value is Link.Member member) result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = MapMember(member);
        return result;
    }

    public static MemberDefinitionInfo MapMemberDefinition(string name, Link.MemberDefinition definition) => definition switch
    {
        Link.FieldDefinition field => new MemberDefinitionInfo(name, "field", Render(field.Type), Render(field.ValueType), null),
        Link.ReferenceDefinition reference => new MemberDefinitionInfo(name, "reference", Render(reference.Type), null, Render(reference.TargetType)),
        Link.ListDefinition list => new MemberDefinitionInfo(name, "list", Render(list.Type), null, null),
        Link.ArrayDefinition array => new MemberDefinitionInfo(name, "array", Render(array.Type), null, null),
        Link.DictionaryDefinition dictionary => new MemberDefinitionInfo(name, "dictionary", Render(dictionary.Type), null, null),
        _ => new MemberDefinitionInfo(name, definition.GetType().Name, Render(definition.Type), null, null)
    };

    public static SyncMethodInfo MapMethodDefinition(Link.SyncMethodDefinition definition) => new(
        definition.Name,
        (definition.Parameters ?? []).ToDictionary(x => x.Key, x => Render(x.Value), StringComparer.Ordinal),
        Render(definition.ReturnType), definition.IsStatic, definition.IsAsync);

    public static TypeInfo MapType(Link.TypeDefinition type, IReadOnlyDictionary<string, long>? enumValues, bool? isFlags) => new(
        type.FullTypeName, type.AssemblyName, type.Namespace, type.Name, Render(type.BaseType), type.IsAbstract,
        type.IsInterface, type.IsGenericType, type.IsEnum, type.IsComponent, type.IsSyncObject, type.IsWorldElement,
        (type.GenericParameters ?? []).Select(x => x.Name).ToArray(),
        (type.Interfaces ?? []).Select(Render).Where(x => x is not null).Cast<string>().ToArray(), enumValues, isFlags);

    public static string? Render(Link.TypeReference? reference)
    {
        if (reference is null) return null;
        var args = reference.GenericArguments;
        if (args is null || args.Count == 0) return reference.Type;
        var rendered = args.Select(Render).ToArray();
        // Upstream TypeReference.Type is an open definition. Arguments belong in its
        // placeholders, including declaring types: Slider<>+Direction + float.
        var placeholders = System.Text.RegularExpressions.Regex.Matches(reference.Type, @"<,*>");
        if (placeholders.Count > 0 && placeholders.Sum(match => match.Length - 1) == rendered.Length)
        {
            var index = 0;
            return System.Text.RegularExpressions.Regex.Replace(reference.Type, @"<,*>", match =>
            {
                var count = match.Length - 1;
                var value = "<" + string.Join(',', rendered.Skip(index).Take(count)) + ">";
                index += count;
                return value;
            });
        }
        return $"{reference.Type}<{string.Join(',', rendered)}>";
    }
}

public static class ValueCodec
{
    public static async Task<Link.Member> ParseAsync(Link.LinkInterface link, Link.MemberDefinition definition, string raw,
        CancellationToken cancellationToken = default, TimeSpan? requestTimeout = null,
        Action<string, double>? requestCompleted = null,
        Func<string, CancellationToken, Task<TypeInfo>>? describeType = null)
    {
        if (definition is Link.ReferenceDefinition)
        {
            if (raw.StartsWith('"') || raw.EndsWith('"'))
                throw new RLoopException("REFERENCE_VALUE_QUOTED", "Reference values must contain the ID itself, without literal JSON quote characters.", ExitCodes.ValidationFailed,
                    suggestions: ["Pass Reso_123 as the reference value. Shell quotes may group an argument, but literal quote characters are not part of an ID."]);
            return new Link.Reference { TargetID = raw.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : raw };
        }
        if (definition is Link.ListDefinition list) return await ParseListAsync(link, list, raw, cancellationToken, requestTimeout, requestCompleted, describeType);
        if (definition is Link.DictionaryDefinition dictionary) return await ParseDictionaryAsync(link, dictionary, raw, cancellationToken, requestTimeout, requestCompleted, describeType);
        if (definition is Link.SyncObjectMemberDefinition syncObject) return await ParseSyncObjectAsync(link, syncObject, raw, cancellationToken, requestTimeout, requestCompleted, describeType);
        if (definition is not Link.FieldDefinition field)
            throw new RLoopException("MEMBER_TYPE_UNSUPPORTED", $"Setting {definition.GetType().Name} members is not supported in v0.1.", ExitCodes.ValidationFailed);

        var type = ModelMapper.Render(field.ValueType) ?? string.Empty;
        var simple = SimpleType(type);
        try
        {
            return simple switch
            {
                "bool" or "boolean" => new Link.Field_bool { Value = bool.Parse(raw) },
                "byte" => new Link.Field_byte { Value = byte.Parse(raw, CultureInfo.InvariantCulture) },
                "short" or "int16" => new Link.Field_short { Value = short.Parse(raw, CultureInfo.InvariantCulture) },
                "ushort" or "uint16" => new Link.Field_ushort { Value = ushort.Parse(raw, CultureInfo.InvariantCulture) },
                "int" or "int32" => new Link.Field_int { Value = int.Parse(raw, CultureInfo.InvariantCulture) },
                "uint" or "uint32" => new Link.Field_uint { Value = uint.Parse(raw, CultureInfo.InvariantCulture) },
                "long" or "int64" => new Link.Field_long { Value = long.Parse(raw, CultureInfo.InvariantCulture) },
                "ulong" or "uint64" => new Link.Field_ulong { Value = ulong.Parse(raw, CultureInfo.InvariantCulture) },
                "float" or "single" => new Link.Field_float { Value = float.Parse(raw, CultureInfo.InvariantCulture) },
                "double" => new Link.Field_double { Value = double.Parse(raw, CultureInfo.InvariantCulture) },
                "string" => new Link.Field_string { Value = raw },
                "uri" => new Link.Field_Uri { Value = new Uri(raw, UriKind.RelativeOrAbsolute) },
                "type" => new Link.Field_Type { Type = raw },
                "float2" => Float2(raw),
                "float3" => Float3(raw),
                "float4" => Float4(raw),
                "floatq" or "quaternion" => FloatQ(raw),
                "color" => Color(raw),
                "colorx" => ColorX(raw),
                _ => await ParseEnumOrReflection(link, type, raw, cancellationToken, requestTimeout, requestCompleted, describeType)
            };
        }
        catch (RLoopException) { throw; }
        catch (Exception ex) when (ex is FormatException or OverflowException or UriFormatException)
        {
            throw new RLoopException("VALUE_CONVERSION_FAILED", $"Cannot convert '{raw}' to '{type}'.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["value"] = raw, ["targetType"] = type }, innerException: ex);
        }
    }

    private static async Task<Link.SyncDictionary> ParseDictionaryAsync(Link.LinkInterface link, Link.DictionaryDefinition definition,
        string raw, CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted,
        Func<string, CancellationToken, Task<TypeInfo>>? describeType)
    {
        if (definition.ElementDefinition is null)
            throw new RLoopException("DICTIONARY_ELEMENT_TYPE_MISSING", "The runtime dictionary definition did not include a value type.", ExitCodes.ValidationFailed);
        JsonObject source;
        try { source = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Expected an object."); }
        catch (JsonException ex) { throw new RLoopException("DICTIONARY_VALUE_INVALID", "Dictionary values must use a JSON object.", ExitCodes.ValidationFailed, innerException: ex); }
        var keyType = ModelMapper.Render(definition.KeyType) ?? "string";
        var simpleKey = SimpleType(keyType);
        var concrete = typeof(Link.SyncDictionary).Assembly.GetTypes().FirstOrDefault(type =>
            !type.IsAbstract && typeof(Link.SyncDictionary).IsAssignableFrom(type) &&
            type.Name.Equals("SyncDictionary_" + simpleKey, StringComparison.OrdinalIgnoreCase));
        if (concrete is null)
            throw new RLoopException("DICTIONARY_KEY_UNSUPPORTED", $"Dictionary key type '{keyType}' is not supported by this ResoniteLink build.", ExitCodes.ValidationFailed);
        var result = (Link.SyncDictionary)Activator.CreateInstance(concrete)!;
        var property = concrete.GetProperty("Elements")!;
        var elements = (System.Collections.IDictionary)Activator.CreateInstance(property.PropertyType)!;
        var dictionaryKeyType = property.PropertyType.GetGenericArguments()[0];
        foreach (var pair in source)
        {
            var key = Convert.ChangeType(pair.Key, dictionaryKeyType, CultureInfo.InvariantCulture);
            var valueRaw = pair.Value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : pair.Value?.ToJsonString() ?? "null";
            elements.Add(key!, await ParseAsync(link, definition.ElementDefinition, valueRaw, cancellationToken, requestTimeout, requestCompleted, describeType));
        }
        property.SetValue(result, elements);
        return result;
    }

    private static async Task<Link.SyncObject> ParseSyncObjectAsync(Link.LinkInterface link, Link.SyncObjectMemberDefinition member,
        string raw, CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted,
        Func<string, CancellationToken, Task<TypeInfo>>? describeType)
    {
        JsonObject source;
        try { source = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Expected an object."); }
        catch (JsonException ex) { throw new RLoopException("SYNC_OBJECT_VALUE_INVALID", "SyncObject values must use a JSON object.", ExitCodes.ValidationFailed, innerException: ex); }
        var type = ModelMapper.Render(member.Type) ?? throw new RLoopException("SYNC_OBJECT_TYPE_MISSING", "SyncObject member has no runtime type.", ExitCodes.ValidationFailed);
        var response = await WaitValueRequest(link.GetSyncObjectDefinition(new Link.GetSyncObjectDefinition { SyncObjectType = type, Flattened = true }), "sync-object-definition.get", requestTimeout,
            cancellationToken, requestCompleted);
        if (!response.Success) throw new RLoopException("SYNC_OBJECT_DESCRIBE_FAILED", response.ErrorInfo, ExitCodes.OperationFailed);
        var definition = response.Definition;
        var members = new Dictionary<string, Link.Member>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            if (!definition.Members.TryGetValue(pair.Key, out var memberDefinition))
                throw new RLoopException("SYNC_OBJECT_MEMBER_NOT_FOUND", $"SyncObject member '{pair.Key}' was not found.", ExitCodes.ValidationFailed,
                    suggestions: definition.Members.Keys.Take(30).ToArray());
            var valueRaw = pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : pair.Value?.ToJsonString() ?? "null";
            members[pair.Key] = await ParseAsync(link, memberDefinition, valueRaw, cancellationToken, requestTimeout, requestCompleted, describeType);
        }
        return new Link.SyncObject { Members = members };
    }

    private static async Task<Link.SyncList> ParseListAsync(Link.LinkInterface link, Link.ListDefinition definition,
        string raw, CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted,
        Func<string, CancellationToken, Task<TypeInfo>>? describeType)
    {
        if (definition.ElementDefinition is null)
            throw new RLoopException("LIST_ELEMENT_TYPE_MISSING", "The runtime list definition did not include an element type.", ExitCodes.ValidationFailed);

        IReadOnlyList<string> values;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected an array.");
                values = document.RootElement.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText()).ToArray();
            }
            catch (JsonException ex)
            {
                throw new RLoopException("LIST_VALUE_INVALID", $"Cannot parse '{raw}' as a JSON array.", ExitCodes.ValidationFailed,
                    suggestions: ["Pass a JSON array such as [\"Reso_1\",\"Reso_2\"]."], innerException: ex);
            }
        }
        else
        {
            values = string.IsNullOrWhiteSpace(trimmed) ? [] : [trimmed];
        }

        var elements = new List<Link.Member>(values.Count);
        foreach (var value in values)
            elements.Add(await ParseAsync(link, definition.ElementDefinition, value, cancellationToken, requestTimeout, requestCompleted, describeType));
        return new Link.SyncList { Elements = elements };
    }

    private static async Task<Link.Member> ParseEnumOrReflection(Link.LinkInterface link, string type, string raw,
        CancellationToken cancellationToken, TimeSpan? requestTimeout, Action<string, double>? requestCompleted,
        Func<string, CancellationToken, Task<TypeInfo>>? describeType)
    {
        var reflected = TryParseReflectedField(type, raw);
        if (reflected is not null) return reflected;
        var underlying = ReflectedMemberType.UnwrapNullable(type);
        if (describeType is not null)
        {
            TypeInfo? metadata = null;
            try { metadata = await describeType(underlying, cancellationToken); }
            catch (RLoopException ex) when (ex.Code is "TYPE_NOT_FOUND" or "COMPONENT_TYPE_NOT_FOUND") { }
            if (metadata is { IsEnum: true, EnumValues: not null, IsFlags: not null })
                return EnumField(underlying, underlying != type, metadata.EnumValues, metadata.IsFlags.Value, raw);
            throw new RLoopException("VALUE_TYPE_UNSUPPORTED", $"Field type '{type}' is not supported by the v0.1 converter.", ExitCodes.ValidationFailed);
        }
        var typeResponse = await WaitValueRequest(link.GetTypeDefinition(underlying), "type.get", requestTimeout, cancellationToken, requestCompleted);
        if (typeResponse.Success && typeResponse.Definition.IsEnum)
        {
            var enumResponse = await WaitValueRequest(link.GetEnumDefinition(underlying), "enum.get", requestTimeout, cancellationToken, requestCompleted);
            if (!enumResponse.Success) throw new RLoopException("ENUM_DESCRIBE_FAILED", enumResponse.ErrorInfo, ExitCodes.OperationFailed);
            var values = enumResponse.Definition.Values;
            return EnumField(underlying, underlying != type, values, enumResponse.Definition.IsFlags, raw);
        }
        throw new RLoopException("VALUE_TYPE_UNSUPPORTED", $"Field type '{type}' is not supported by the v0.1 converter.", ExitCodes.ValidationFailed,
            suggestions: ["Use resoloop type describe to confirm the runtime type, then open an issue with this type."]);
    }

    internal static Link.Field EnumField(string type, bool nullable, IReadOnlyDictionary<string, long> values, bool isFlags, string raw)
    {
        var value = nullable && raw == "null" ? null : ValidateEnumValue(type, values, isFlags, raw);
        return nullable ? new Link.Field_Nullable_Enum { EnumType = type, Value = value } :
            new Link.Field_Enum { EnumType = type, Value = value };
    }

    internal static string ValidateEnumValue(string type, IReadOnlyDictionary<string, long> values, bool isFlags, string raw)
    {
        var requested = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0) throw new RLoopException("ENUM_VALUE_INVALID", "Enum value cannot be empty.", ExitCodes.ValidationFailed);
        var unknown = requested.Where(x => !values.ContainsKey(x) && !long.TryParse(x, out _)).ToArray();
        if (unknown.Length > 0)
            throw new RLoopException("ENUM_VALUE_INVALID", $"'{string.Join(',', unknown)}' is not valid for '{type}'.", ExitCodes.ValidationFailed,
                suggestions: values.Keys.Take(30).ToArray());
        if (!isFlags && requested.Length > 1)
            throw new RLoopException("ENUM_FLAGS_INVALID", $"Enum '{type}' is not marked with Flags and accepts one value.", ExitCodes.ValidationFailed,
                suggestions: values.Keys.Take(30).ToArray());
        return string.Join(',', requested);
    }

    private static Link.Member? TryParseReflectedField(string type, string raw)
    {
        var underlying = ReflectedMemberType.UnwrapNullable(type);
        var nullable = underlying != type;
        var simple = SimpleType(underlying);
        var expectedName = "Field_" + (nullable ? "Nullable_" : string.Empty) + simple;
        var concrete = typeof(Link.Field).Assembly.GetTypes().FirstOrDefault(candidate =>
            !candidate.IsAbstract && typeof(Link.Field).IsAssignableFrom(candidate) &&
            candidate.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
        if (concrete is null) return null;
        var field = (Link.Field)Activator.CreateInstance(concrete)!;
        var property = concrete.GetProperty("Value") ?? concrete.GetProperty("BoxedValue");
        if (property is null || !property.CanWrite) return null;
        if (raw.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            property.SetValue(field, null);
            return field;
        }
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? value;
        if (targetType == typeof(string)) value = raw;
        else if (targetType == typeof(Uri)) value = new Uri(raw, UriKind.RelativeOrAbsolute);
        else if (MemberValueSyntax.IsStructuredTuple(underlying, out _))
            value = ParseReflectedTuple(targetType, underlying, raw);
        else
        {
            var json = raw;
            if (targetType.IsEnum) value = Enum.Parse(targetType, raw, true);
            else value = JsonSerializer.Deserialize(json, targetType, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
        }
        property.SetValue(field, value);
        return field;
    }

    private static object ParseReflectedTuple(Type targetType, string tupleType, string raw)
    {
        var tuple = MemberValueSyntax.ParseTuple(tupleType, raw);
        var result = Activator.CreateInstance(targetType) ??
            throw new RLoopException("VALUE_TYPE_UNSUPPORTED", $"Tuple type '{tupleType}' cannot be constructed.", ExitCodes.ValidationFailed);
        var names = SimpleType(tupleType) is "color" or "colorx"
            ? new[] { "r", "g", "b", "a" }
            : new[] { "x", "y", "z", "w" };
        for (var index = 0; index < tuple.Count; index++)
        {
            var member = targetType.GetField(names[index]) as System.Reflection.MemberInfo ?? targetType.GetProperty(names[index]);
            var memberType = member switch
            {
                System.Reflection.FieldInfo field => field.FieldType,
                System.Reflection.PropertyInfo property => property.PropertyType,
                _ => throw new RLoopException("VALUE_TYPE_UNSUPPORTED",
                    $"Tuple type '{tupleType}' does not expose '{names[index]}'.", ExitCodes.ValidationFailed)
            };
            var converted = JsonSerializer.Deserialize(tuple[index]!.ToJsonString(), memberType);
            if (member is System.Reflection.FieldInfo targetField) targetField.SetValue(result, converted);
            else ((System.Reflection.PropertyInfo)member).SetValue(result, converted);
        }
        return result;
    }

    private static async Task<T> WaitValueRequest<T>(Task<T> task, string operation, TimeSpan? timeout,
        CancellationToken cancellationToken, Action<string, double>? requestCompleted)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return timeout is { } value
                ? await task.WaitAsync(value, cancellationToken).ConfigureAwait(false)
                : await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new RLoopException("REQUEST_TIMEOUT", $"ResoniteLink request '{operation}' timed out after {timeout!.Value.TotalSeconds:0.#} seconds.",
                ExitCodes.Timeout, new Dictionary<string, object?> { ["operation"] = operation, ["timeoutSeconds"] = timeout.Value.TotalSeconds },
                ["Retry after checking Resonite responsiveness; apply checkpoints make retry safe."], ex);
        }
        finally
        {
            stopwatch.Stop();
            requestCompleted?.Invoke(operation, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static Link.Member Float2(string raw) { var v = Parts("float2", raw); return new Link.Field_float2 { Value = new Link.float2 { x = v[0], y = v[1] } }; }
    private static Link.Member Float3(string raw) { var v = Parts("float3", raw); return new Link.Field_float3 { Value = new Link.float3 { x = v[0], y = v[1], z = v[2] } }; }
    private static Link.Member Float4(string raw) { var v = Parts("float4", raw); return new Link.Field_float4 { Value = new Link.float4 { x = v[0], y = v[1], z = v[2], w = v[3] } }; }
    private static Link.Member FloatQ(string raw) { var v = Parts("floatQ", raw); return new Link.Field_floatQ { Value = new Link.floatQ { x = v[0], y = v[1], z = v[2], w = v[3] } }; }
    private static Link.Member Color(string raw) { var v = Parts("color", raw); return new Link.Field_color { Value = new Link.color { r = v[0], g = v[1], b = v[2], a = v[3] } }; }
    private static Link.Member ColorX(string raw) { var v = Parts("colorX", raw); return new Link.Field_colorX { Value = new Link.colorX { r = v[0], g = v[1], b = v[2], a = v[3] } }; }

    private static float[] Parts(string type, string raw)
    {
        var tuple = MemberValueSyntax.ParseTuple(type, raw);
        return tuple.Select(value => (float)(value?.GetValue<double>() ?? throw new FormatException())).ToArray();
    }

    private static string SimpleType(string type)
    {
        var end = type.IndexOf(']');
        if (end >= 0) type = type[(end + 1)..];
        return type.Split('.').Last().TrimEnd('?').ToLowerInvariant();
    }
}

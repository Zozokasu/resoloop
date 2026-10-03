using RLoop.Core;

namespace RLoop.Tests;

// ROADMAP-9 unit 4a: fakes for the deploy entries (deploy-manifest, watch, the single deploy, the CLI). The world and
// the deployer are fakes; nothing connects anywhere. Writes through the client are refused and counted: only the
// deployer (its own connection in the product) changes the world.

/// <summary>A world of Slots (ID, name, parent) with components. The guard's connection may only read it.</summary>
internal sealed class FluxTestWorld : IResoniteClient, IApplySessionObservation
{
    public const string Parent = "Reso_Parent";
    public const string OtherParent = "Reso_OtherParent";
    private readonly List<(string Id, string Name, string? ParentId)> _slots =
    [
        ("Root", "Root", null), (Parent, "ResoLoop_Test_Parent", "Root"), (OtherParent, "ResoLoop_Test_Other", "Root")
    ];
    private readonly Dictionary<string, List<ComponentSummary>> _components = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string Url { get; set; } = "ws://localhost:47610/";
    public string? SessionId { get; set; } = "S-world-1";
    public string Generation { get; set; } = "gen-1";
    public int WriteCalls { get; private set; }
    public int Connects { get; set; }

    public void Add(string id, string name, string? parentId) { lock (_gate) _slots.Add((id, name, parentId)); }
    public bool Remove(string id)
    {
        lock (_gate)
        {
            foreach (var child in ChildIds(id)) Remove(child);
            _components.Remove(id);
            return _slots.RemoveAll(slot => slot.Id == id) > 0;
        }
    }
    public void AddComponent(string slotId, ComponentSummary component)
    {
        lock (_gate)
        {
            if (!_components.TryGetValue(slotId, out var list)) _components[slotId] = list = [];
            list.Add(component);
        }
    }
    public string[] ChildIds(string parentId) { lock (_gate) return _slots.Where(slot => slot.ParentId == parentId).Select(slot => slot.Id).ToArray(); }
    public string[] ChildrenNamed(string parentId, string name)
    { lock (_gate) return _slots.Where(slot => slot.ParentId == parentId && slot.Name == name).Select(slot => slot.Id).ToArray(); }
    public string Snapshot() { lock (_gate) return string.Join("\n", _slots.Select(slot => $"{slot.Id}|{slot.Name}|{slot.ParentId}")); }

    public ApplySessionObservation ObserveApplySession() =>
        new(ApplySessionObservation.NormalizeUrl(Url), SessionId, SessionId is null ? "unknown" : "matched");

    public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionInfo(Url, true, "2026.9.18.82", "0.13.1.0", "67", Generation));

    public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var index = _slots.FindIndex(slot => slot.Id == id);
            if (index < 0) throw new RLoopException("SLOT_NOT_FOUND", $"Slot with ID '{id}' not found.", ExitCodes.NotFound);
            var found = _slots[index];
            var children = _slots.Where(slot => slot.ParentId == id).Select(slot => depth > 0
                ? Slot(slot.Id, slot.Name, slot.ParentId, false, [], includeComponentData)
                : Slot(slot.Id, string.Empty, null, true, [], false)).ToArray();
            return Task.FromResult(Slot(found.Id, found.Name, found.ParentId, false, children, includeComponentData));
        }
    }

    private SlotInfo Slot(string id, string name, string? parentId, bool referenceOnly, IReadOnlyList<SlotInfo> children, bool componentData) =>
        new(id, name, parentId, null, null, null, true, true, null, referenceOnly,
            referenceOnly || !_components.TryGetValue(id, out var components) ? []
                : components.Select(component => componentData ? component : component with { Members = null }).ToArray(),
            children);

    private T Write<T>() { WriteCalls++; throw new InvalidOperationException("Only the deployer may write; the guard's connection reads."); }
    private static T Unused<T>() => throw new NotSupportedException();

    public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) => Unused<Task<ComponentInfo>>();
    public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) => Write<Task<string>>();
    public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) => Write<Task>();
    public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => Write<Task>();
    public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => Write<Task<ComponentCreateResult>>();
    public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) => Write<Task>();
    public Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => Write<Task>();
    public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => Write<Task>();
    public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) =>
        Unused<Task<IReadOnlyList<string>>>();
    public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) =>
        Unused<Task<ComponentTypeInfo>>();
    /// <summary>Type information answered by <see cref="DescribeTypeAsync"/>; a type without an entry cannot be read.</summary>
    public Dictionary<string, RLoop.Core.TypeInfo> Types { get; } = new(StringComparer.Ordinal);
    public List<string> TypeReads { get; } = [];
    public Task<RLoop.Core.TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        TypeReads.Add(type);
        return Types.TryGetValue(type, out var info) ? Task.FromResult(info) : Unused<Task<RLoop.Core.TypeInfo>>();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Flux-SDK build tool and deployer. By default the build passes, the preparation declares the module path as its
/// name, and the execution does what the real deployer does (P1-b): removes the exact previous root, creates one new
/// root below the parent with a port Slot and a carrier wired to the target for every map entry, and answers its ID.
/// </summary>
internal sealed class FluxTestDeployer(FluxTestWorld world) : IFluxTool, IFluxDeployer
{
    public const string InputCarrierType = "[FrooxEngine]FrooxEngine.ProtoFlux.GlobalReference<[FrooxEngine]FrooxEngine.IValue<float>>";
    public const string OutputCarrierType = "[FrooxEngine]FrooxEngine.FieldDriveBase<float>+Proxy";
    private int _next;

    public FluxResult BuildResult { get; set; } = new(true, 0, "Packing 1 ProtoFlux nodes and 0 comments.", "");
    public Action<FluxBuildRequest>? OnBuild { get; set; }
    /// <summary>Declared module name per module path; a path without an entry declares itself.</summary>
    public Dictionary<string, string> DeclaredNames { get; } = new(StringComparer.Ordinal);
    public Func<FluxDeployPrepareRequest, FluxDeployPreparation>? Prepare { get; set; }
    public Func<FluxDeployExecuteRequest, CancellationToken, Task<FluxDeployExecution>>? Execute { get; set; }

    public List<FluxBuildRequest> BuildRequests { get; } = [];
    public List<FluxDeployPrepareRequest> Preparations { get; } = [];
    public List<FluxDeployExecuteRequest> Executions { get; } = [];
    /// <summary>Module paths of every execution, in order.</summary>
    public List<string> Deployed { get; } = [];

    public Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default)
    {
        BuildRequests.Add(request);
        OnBuild?.Invoke(request);
        return Task.FromResult(BuildResult);
    }
    public Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => BuildAsync(request, cancellationToken);
    public Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) => BuildAsync(request, cancellationToken);
    public Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new FluxToolStatus(true, "fake", "1.9.0"));

    public string DeclaredName(string module) => DeclaredNames.TryGetValue(module, out var name) ? name : module;

    public static FluxDiagnostic Error(string message) => new("Main.pg", 1, 21, 1, 30, "error", message, "deployer", "compiler", true);

    public static FluxDeployExecution Outcome(FluxDeployOutcome outcome, FluxDeployStage stage, FluxDeploySendStatus sendStatus,
        string? candidate = null, string? declared = null) =>
        new(outcome, stage, sendStatus, FluxPreviousRootRemoval.NotAttempted, declared, null, candidate, candidate, [], [], [],
            outcome == FluxDeployOutcome.Created ? null : "scripted outcome", "", "");

    /// <summary>
    /// Independent declarations for the handwritten entry fixtures. An unlisted fixture has no ports;
    /// unknown-port scenarios explicitly set Prepare. No preparation expectation is synthesized from maps.
    /// </summary>
    public Dictionary<string, IReadOnlyList<FluxModulePortInfo>> PreparedPorts { get; } = new(StringComparer.Ordinal);

    public static IReadOnlyList<FluxModulePortInfo> Ports(params (string Name, string Direction)[] ports) =>
        ports.Select(port => new FluxModulePortInfo(port.Name, port.Direction, null,
            (port.Direction == "source" ? "Input:" : "Output:") + port.Name, null,
            [port.Direction == "source" ? InputCarrierType : OutputCarrierType],
            port.Direction == "source" ? InputCarrierType : OutputCarrierType)).ToArray();

    public static FluxModulePortInfo Port(string name, string direction, string carrierType, string? modifier = null) =>
        new(name, direction, modifier,
            (direction == "drive" ? "Output:" : modifier == "element" ? "Input:[Elem]" : modifier == "global" ? "Input:[Global]" : "Input:") + name,
            null, [carrierType], carrierType);

    public Task<FluxDeployPreparation> PrepareAsync(FluxDeployPrepareRequest request, CancellationToken cancellationToken = default)
    {
        Preparations.Add(request);
        return Task.FromResult(Prepare?.Invoke(request) ?? new FluxDeployPreparation(true, DeclaredName(request.Module), [],
            PreparedPorts.TryGetValue(request.Module, out var ports) ? ports : [], null, "", ""));
    }

    public Task<FluxDeployExecution> ExecuteAsync(FluxDeployExecuteRequest request, CancellationToken cancellationToken = default)
    {
        Executions.Add(request);
        Deployed.Add(request.Module);
        return Execute is null ? Task.FromResult(Replace(request)) : Execute(request, cancellationToken);
    }

    public FluxDeployExecution Replace(FluxDeployExecuteRequest request)
    {
        var removal = FluxPreviousRootRemoval.NotAttempted;
        if (request.PreviousRootSlotId is { } previous)
            removal = world.Remove(previous) ? FluxPreviousRootRemoval.Removed : FluxPreviousRootRemoval.NotFound;
        var id = $"Flux_{++_next}";
        world.Add(id, request.ExpectedModuleName, request.ParentSlotId);
        var ports = new List<FluxModulePortInfo>();
        foreach (var (name, target, input) in (request.InputMap ?? new Dictionary<string, string>()).Select(pair => (pair.Key, pair.Value, true))
                     .Concat((request.OutputMap ?? new Dictionary<string, string>()).Select(pair => (pair.Key, pair.Value, false))))
        {
            var prepared = PreparedPorts.GetValueOrDefault(request.Module)?.SingleOrDefault(port => port.Name == name);
            var slotName = prepared?.SlotName ?? (input ? "Input:" : "Output:") + name;
            var slotId = $"{id}_Port_{name}";
            var type = prepared?.ExpectedCarrierType ?? (input ? InputCarrierType : OutputCarrierType);
            var member = input ? "Reference" : "Drive";
            world.Add(slotId, slotName, id);
            world.AddComponent(slotId, new(slotId + "_Carrier", type, new Dictionary<string, MemberValue>
            {
                [member] = new("reference", slotId + "_Carrier_" + member, null, null, target,
                    input ? "[FrooxEngine]FrooxEngine.IValue<float>" : "[FrooxEngine]FrooxEngine.IField<float>")
            }));
            ports.Add(new(name, input ? "source" : "drive", prepared?.Modifier, slotName, slotId, [type], type));
        }
        return new(FluxDeployOutcome.Created, FluxDeployStage.Created, FluxDeploySendStatus.Sent, removal,
            request.ExpectedModuleName, removal == FluxPreviousRootRemoval.Removed ? request.PreviousRootSlotId : null,
            id, id, [], [], ports, null, "", "");
    }
}

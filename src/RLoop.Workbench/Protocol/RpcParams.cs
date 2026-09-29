using System.Text.Json;

namespace ResoniteWorkbench.Protocol;

// Request parameters. Results are the Core result records serialized with WorkbenchJson.Options,
// so they are not duplicated here.

public sealed record EmptyParams;

/// <summary>Exactly one of the two: a session from session.list, or a loopback ws:// endpoint.</summary>
public sealed record SessionConnectParams(string? SessionId = null, string? Endpoint = null);

public sealed record WorldHierarchyParams(string ScopeRootId);

/// <summary>
/// Observes <paramref name="ScopeRootId"/> synchronously and returns it like <c>world.hierarchy</c>,
/// so a caller whose target lies outside what the background loop's fixed scope keeps observed (for
/// example a working subtree of a large, partially observed world) can bring it under live
/// observation on demand.
/// </summary>
/// <param name="MaxDepth">Deepest level kept, the scope root counted as level 0; 1 to 32. Defaults to <c>ObservationBudget.Default.MaxDepth</c>.</param>
/// <param name="MaxSlots">Most slots kept, the scope root included; 1 to 8192. Defaults to <c>ObservationBudget.Default.MaxSlots</c>.</param>
public sealed record WorldObserveParams(string ScopeRootId, int? MaxDepth = null, int? MaxSlots = null);

public sealed record WorldSlotParams(string SlotId);

public sealed record MemberReadParams(string ComponentId, string MemberName);

public sealed record ReflectionComponentParams(string ComponentType);

public sealed record ReflectionTypeParams(string TypeName);

public sealed record ReflectionMemberParams(string ComponentType, string MemberName);

/// <param name="Query">A case-insensitive substring matched against the session's component type names.</param>
/// <param name="Limit">Most type names returned; 1 to 500, defaults to 50.</param>
public sealed record ReflectionSearchParams(string Query, int? Limit = null);

public sealed record ReflectionEnumParams(string TypeName);

/// <param name="ExcludeUserRoots">Skips user subtrees; a skipped subtree may hold the writer.</param>
public sealed record WritersFindParams(
    string TargetId,
    string? ScopeRootId = null,
    bool ExcludeUserRoots = false,
    int? MaxComponents = null,
    int? MaxHits = null,
    string? ExpectedConnectionId = null);

public sealed record ResearchTypeParams(string AssemblyPath, string FullTypeName);

public sealed record ResearchMethodParams(
    string AssemblyPath,
    string FullTypeName,
    string MethodName,
    string? Signature = null,
    int? MaxInstructions = null);

/// <param name="Value">
/// A member write with a <c>kind</c> discriminator: <c>{"kind":"field","valueType":"float","valueJson":"2"}</c>
/// or <c>{"kind":"reference","targetId":"..."}</c>.
/// </param>
public sealed record DiagnosticValueRevertParams(
    string ExpectedConnectionId,
    string ComponentId,
    string MemberName,
    JsonElement Value,
    int? ReadbackSamples = null,
    int? ReadbackIntervalMs = null,
    string? WriterScope = null,
    int? MaxScannedComponents = null);

/// <param name="Operations">
/// The ordered operations, each with a <c>kind</c> discriminator, for example
/// <c>{"kind":"createSlot","parentId":"…","name":"…","label":"box"}</c>.
/// </param>
/// <param name="Preconditions">Live checks, each with a <c>kind</c> discriminator (<c>memberValue</c>, <c>slotTransform</c>).</param>
/// <param name="ExpectedScopeRootId">
/// When set, the plan is checked against the latest observation of exactly this scope (from
/// <c>world.observe</c> or the background loop) instead of whatever scope was observed most
/// recently on any scope; that scope must be currently observed. Null keeps the previous behavior.
/// </param>
public sealed record MutationValidateParams(
    string RequestId,
    string PlanId,
    string ExpectedConnectionId,
    long ExpectedWorldRevision,
    JsonElement Operations,
    JsonElement? Preconditions = null,
    bool Destructive = false,
    string? ExpectedScopeRootId = null);

/// <param name="ConfirmationToken">Required when the plan is destructive: the token <c>mutation.validate</c> returned for it.</param>
/// <param name="ReadbackSamples">How many times each member write is read back.</param>
/// <param name="ReadbackIntervalMs">The wait between member readbacks.</param>
/// <param name="ExpectedScopeRootId">See <see cref="MutationValidateParams.ExpectedScopeRootId"/>.</param>
public sealed record MutationExecuteParams(
    string RequestId,
    string PlanId,
    string ExpectedConnectionId,
    long ExpectedWorldRevision,
    JsonElement Operations,
    JsonElement? Preconditions = null,
    bool Destructive = false,
    string? ConfirmationToken = null,
    int? ReadbackSamples = null,
    int? ReadbackIntervalMs = null,
    string? ExpectedScopeRootId = null);

public sealed record MutationOutcomeParams(string RequestId);

/// <summary>Flux-SDK build options; every field is optional and part of the build key.</summary>
/// <param name="DebugOutputs">Any of <c>record</c>, <c>components</c>, <c>parse</c>; empty requests all. <c>record</c> and <c>parse</c> are always produced.</param>
public sealed record FluxBuildOptionsParams(
    string? Profile = null,
    string? LayoutEngine = null,
    string? OptimizationPreset = null,
    IReadOnlyList<string>? Optimizations = null,
    bool SkipRestore = true,
    bool NoDefaultAssets = false,
    IReadOnlyList<string>? DebugOutputs = null);

/// <param name="ProjectRoot">The ProtoGraph project directory; a fully qualified local path.</param>
/// <param name="EntryFile">The <c>.pg</c> entry module, relative to <paramref name="ProjectRoot"/>.</param>
/// <param name="TimeoutSeconds">Overrides the Workbench's build time limit.</param>
/// <param name="ExcludedDirectories">Further directories not hashed into the build key, absolute or relative to the project root.</param>
public sealed record FluxBuildParams(
    string ProjectRoot,
    string EntryFile,
    FluxBuildOptionsParams? Options = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<string>? ExcludedDirectories = null);

/// <summary>Exactly one of <paramref name="BuildKey"/> and <paramref name="RequestId"/>, plus at most one lookup.</summary>
/// <param name="BuildKey">A build key from <c>flux.build</c>: reads that build's debug map from the cache.</param>
/// <param name="RequestId">An executed <c>flux.deploy.execute</c> request: reads its deployment debug map.</param>
/// <param name="Line">With <paramref name="Column"/>: only the nodes whose source span contains this 1-based position.</param>
/// <param name="SlotId">Deployment maps only: the node the live slot came from.</param>
/// <param name="MaxEntries">Upper bound for each list in the returned map.</param>
public sealed record FluxDebugMapParams(
    string? BuildKey = null,
    string? RequestId = null,
    int? Line = null,
    int? Column = null,
    string? SlotId = null,
    int? MaxEntries = null);

/// <param name="Kind">Must be exactly <c>"outputTargetsUncontested"</c>; the only kind Workbench currently understands.</param>
public sealed record FluxDeployWriterPreconditionParams(string Kind);

/// <param name="RequestId">Identifies one execution; a repeated ID returns the remembered outcome.</param>
/// <param name="ProjectRoot">The ProtoGraph project directory; a fully qualified local path.</param>
/// <param name="EntryFile">The <c>.pg</c> entry module, relative to <paramref name="ProjectRoot"/>.</param>
/// <param name="ParentSlotId">Exact ID of the slot the module is deployed under.</param>
/// <param name="InputMap">Module <c>in</c> port → exact ID it reads from.</param>
/// <param name="OutputMap">Module <c>out</c> port → exact ID it drives.</param>
/// <param name="MemberOwners">Member binding target → exact ID of the observed component holding it.</param>
/// <param name="ExpectedScopeRootId">See <see cref="MutationValidateParams.ExpectedScopeRootId"/>.</param>
/// <param name="WriterPrecondition">Re-verify, immediately before writing, that every outputMap target is not contested by a foreign writer. Omitted keeps the previous behavior. Must equal the value sent to flux.deploy.validate for a subsequent flux.deploy.execute of the same request.</param>
public sealed record FluxDeployValidateParams(
    string RequestId,
    string ProjectRoot,
    string EntryFile,
    string ParentSlotId,
    string ExpectedConnectionId,
    long ExpectedWorldRevision,
    FluxBuildOptionsParams? BuildOptions = null,
    IReadOnlyDictionary<string, string>? InputMap = null,
    IReadOnlyDictionary<string, string>? OutputMap = null,
    IReadOnlyDictionary<string, string>? MemberOwners = null,
    string? ExpectedScopeRootId = null,
    FluxDeployWriterPreconditionParams? WriterPrecondition = null);

/// <param name="ConfirmationToken">
/// Required when the deployment replaces existing children: the token <c>flux.deploy.validate</c> returned.
/// Sending one needs the <c>flux.deploy.replace</c> capability.
/// </param>
/// <param name="ExpectedScopeRootId">See <see cref="MutationValidateParams.ExpectedScopeRootId"/>.</param>
/// <param name="WriterPrecondition">Re-verify, immediately before writing, that every outputMap target is not contested by a foreign writer. Omitted keeps the previous behavior. Must equal the value sent to flux.deploy.validate for a subsequent flux.deploy.execute of the same request.</param>
public sealed record FluxDeployExecuteParams(
    string RequestId,
    string ProjectRoot,
    string EntryFile,
    string ParentSlotId,
    string ExpectedConnectionId,
    long ExpectedWorldRevision,
    FluxBuildOptionsParams? BuildOptions = null,
    IReadOnlyDictionary<string, string>? InputMap = null,
    IReadOnlyDictionary<string, string>? OutputMap = null,
    IReadOnlyDictionary<string, string>? MemberOwners = null,
    string? ConfirmationToken = null,
    string? ExpectedScopeRootId = null,
    FluxDeployWriterPreconditionParams? WriterPrecondition = null);

public sealed record FluxDeployOutcomeParams(string RequestId);

public sealed record RpcDescription(
    IReadOnlyList<int> ProtocolVersions,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, string?> Methods,
    int MaxFrameBytes,
    int MaxInFlightRequests,
    int UsageCatalogVersion = 1,
    IReadOnlyDictionary<string, string>? MethodSummaries = null,
    IReadOnlyDictionary<string, string>? Workflows = null);

public sealed record RefreshScheduled(bool Scheduled);

using System.Text.Json;

namespace ResoniteWorkbench.Protocol;

public sealed record RpcUsageParams(string? Method = null, string? Workflow = null);

public sealed record RpcUsageSummary(string? Capability, string Summary);

public sealed record RpcUsageOverview(
    int UsageCatalogVersion,
    string WorkbenchVersion,
    int ProtocolVersion,
    IReadOnlyDictionary<string, RpcUsageSummary> Methods,
    IReadOnlyDictionary<string, string> Workflows,
    IReadOnlyList<RpcUsageError> ConnectionErrors);

public sealed record RpcUsageError(string Code, string Description);

public sealed record RpcMethodUsage(
    string Name,
    string? Capability,
    string Summary,
    string Description,
    JsonElement ParamsSchema,
    JsonElement ResultSchema,
    IReadOnlyList<RpcUsageError> RpcErrors,
    IReadOnlyList<RpcUsageError> ResultIssues,
    IReadOnlyList<RpcUsageError> ConnectionErrors,
    string RequestExample,
    string? ResponseExample,
    IReadOnlyList<string> RelatedMethods,
    IReadOnlyList<string> Constraints,
    string? ExtraCapability = null);

public sealed record RpcUsageStep(string Method, string Purpose, string? Caution = null);

public sealed record RpcWorkflowUsage(string Name, string Summary, IReadOnlyList<RpcUsageStep> Steps);

public sealed record RpcUsageResult(
    string Kind,
    RpcUsageOverview? Overview = null,
    RpcMethodUsage? Method = null,
    RpcWorkflowUsage? Workflow = null)
{
    public const string OverviewKind = "overview";
    public const string MethodKind = "method";
    public const string WorkflowKind = "workflow";

    public static RpcUsageResult ForOverview(RpcUsageOverview overview) => new(OverviewKind, Overview: overview);

    public static RpcUsageResult ForMethod(RpcMethodUsage method) => new(MethodKind, Method: method);

    public static RpcUsageResult ForWorkflow(RpcWorkflowUsage workflow) => new(WorkflowKind, Workflow: workflow);
}

public sealed record RpcUsageMethod(
    string? Capability,
    string Summary,
    string Description,
    string RequestExample,
    string? ResponseExample,
    IReadOnlyList<string> RelatedMethods,
    IReadOnlyList<string> Constraints,
    string? ExtraCapability = null,
    IReadOnlyList<string>? RpcErrors = null,
    IReadOnlyList<string>? ResultIssues = null,
    IReadOnlyList<string>? ConnectionErrors = null);

public static class RpcUsageErrorText
{
    public static IReadOnlyDictionary<string, string> RpcErrors { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CAPABILITY_NOT_GRANTED"] = "The method, or these particular params, needs a capability the client did not request at handshake.",
        ["METHOD_NOT_FOUND"] = "No method with this name is registered.",
        ["METHOD_UNAVAILABLE"] = "The method's backing service is absent in this Workbench (no Flux services, or no re-observe loop).",
        ["INVALID_PARAMS"] = "params is not a JSON object, fails strict deserialization (unknown property, missing required member, null in a non-nullable position), or violates a method-level rule.",
        ["INVALID_REQUEST"] = "The frame is not a well-formed request.",
        ["CANCELLED"] = "The request was cancelled or the connection was lost while it ran.",
        ["TOO_MANY_REQUESTS"] = "More requests are in flight than the server allows.",
        ["DUPLICATE_REQUEST_ID"] = "Another in-flight request already uses this id.",
        ["INTERNAL_ERROR"] = "The handler failed unexpectedly.",
        ["NOT_CONNECTED"] = "No session is connected.",
        ["TRANSPORT_FAILED"] = "The connection to the session could not be opened or broke.",
        ["ALREADY_CONNECTED"] = "A session is already connected; disconnect first.",
        ["INVALID_STATE"] = "The connection is not in a state that allows this call.",
        ["OUTCOME_NOT_FOUND"] = "No outcome is remembered for this requestId; it was never executed or has been forgotten.",
        ["FLUX_BUILD_NOT_FOUND"] = "No complete build with this key is cached.",
        ["FLUX_DEBUG_MAP_NOT_FOUND"] = "The cached build or deployment has no debug map.",
        ["FLUX_DEBUG_MAP_INVALID"] = "The stored debug map could not be read.",
    };

    public static IReadOnlyDictionary<string, string> ResultIssues { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["NotConnected"] = "No session is connected.",
        ["ConnectionMismatch"] = "The plan's raw IDs belong to another connection.",
        ["NotObserved"] = "The world has not been observed on the current connection, so no revision can be checked.",
        ["ObservationFailed"] = "The world could not be re-observed to check the revision.",
        ["StaleRevision"] = "The world changed after the revision the plan was made against.",
        ["InvalidPlan"] = "The plan itself is malformed.",
        ["InvalidOperation"] = "An operation is malformed or refers to something impossible.",
        ["RootTarget"] = "The operation would move, delete or change the world root or the observed scope root.",
        ["UnobservedTarget"] = "The target is not among the slots and components the revision observed.",
        ["UnobservedSubtree"] = "Part of the subtree a deletion would destroy was not observed.",
        ["UnknownLabel"] = "An operation refers to a label no earlier operation defines.",
        ["DuplicateLabel"] = "Two operations define the same label.",
        ["DeletedTarget"] = "An earlier operation of the plan deletes the target.",
        ["ParentCycle"] = "The new parent is the slot itself or lies under it.",
        ["TypeUnverified"] = "Reflection did not confirm the component type.",
        ["MemberMismatch"] = "The member does not exist, or does not take this kind or type of value.",
        ["PreconditionFailed"] = "A precondition read a value that does not hold.",
        ["PreconditionUnknown"] = "A precondition could not be read, so it cannot be known to hold.",
        ["DestructiveFlagMismatch"] = "The plan's destructive flag does not match its operations.",
        ["ConfirmationRequired"] = "A destructive plan was executed without a confirmation from validating it.",
        ["ConfirmationInvalid"] = "The confirmation is unknown, expired, used, or was issued for another plan or revision.",
        ["DuplicateRequest"] = "A plan with this request ID was already executed.",
        ["FLUX_SDK_NOT_FOUND"] = "The Flux-SDK executable could not be started (not installed, not on PATH, or wrong path).",
        ["FLUX_SDK_VERSION_FAILED"] = "flux-sdk --version ran but failed, or did not finish in time.",
        ["FLUX_SDK_VERSION_UNKNOWN"] = "The version output held no recognisable semantic version.",
        ["FLUX_SDK_VERSION_UNTESTED"] = "The Flux-SDK version is outside the series this Workbench has been tested with.",
        ["FLUX_LIBRARY_PATH_NOT_CONFIGURED"] = "No library path was configured and RESONITE_MANAGED_DATA_PATH is unset.",
        ["FLUX_LIBRARY_PATH_NOT_FOUND"] = "The configured library path is not an existing directory.",
        ["FLUX_LIBRARY_MISSING"] = "Required ProtoFlux assemblies are missing from the library path.",
        ["FLUX_LIBRARY_UNREADABLE"] = "A library file exists but could not be read, or has no .NET metadata.",
        ["FLUX_BUILD_INVALID_REQUEST"] = "The build request is invalid (project root not a local absolute path, unknown debug output, bad timeout).",
        ["FLUX_BUILD_INPUT_INVALID"] = "The project inputs could not be read (missing project or entry file, input limits exceeded, I/O error).",
        ["FLUX_BUILD_COMPILATION_FAILED"] = "flux-sdk build exited with a non-zero exit code.",
        ["FLUX_BUILD_TIMED_OUT"] = "The build did not finish within its time limit; the process tree was killed.",
        ["FLUX_BUILD_OUTPUT_MISSING"] = "The build exited successfully but an expected output file is missing or empty.",
        ["FLUX_BUILD_DEBUG_OUTPUT_INVALID"] = "A debug output exists but could not be read as the expected format.",
        ["FLUX_BUILD_CACHE_ENTRY_INCOMPLETE"] = "A cache entry for the key exists but lacks the files a build of that key always stores.",
        ["FLUX_BUILD_CACHE_WRITE_FAILED"] = "Writing the build outputs into the cache failed.",
        ["FLUX_DEPLOY_INVALID_REQUEST"] = "The request is malformed (missing IDs, a non-relative entry file, blank map entries).",
        ["FLUX_DEPLOY_BUILD_FAILED"] = "The build failed; nothing was deployed. The build diagnostics say why.",
        ["FLUX_DEPLOY_PREPARE_FAILED"] = "FluxSDK could not load its libraries or compile the module in process.",
        ["FLUX_DEPLOY_NOT_CONNECTED"] = "No session is connected.",
        ["FLUX_DEPLOY_CONNECTION_MISMATCH"] = "The request was made on another connection; its IDs are not valid on this one.",
        ["FLUX_DEPLOY_NOT_OBSERVED"] = "The world has not been observed on the current connection.",
        ["FLUX_DEPLOY_OBSERVATION_FAILED"] = "The world could not be re-observed to check the deployment.",
        ["FLUX_DEPLOY_STALE_REVISION"] = "The world is no longer at the revision the request was planned against.",
        ["FLUX_DEPLOY_ROOT_TARGET"] = "The parent is the world root, which Workbench never deploys under.",
        ["FLUX_DEPLOY_PARENT_UNOBSERVED"] = "The parent is not a slot the checked revision read in full.",
        ["FLUX_DEPLOY_PARENT_READ_FAILED"] = "The parent's children could not be read to determine what the deployment replaces.",
        ["FLUX_DEPLOY_BINDING_PORTS_MISMATCH"] = "The input/output map keys do not equal the module's declared ports.",
        ["FLUX_DEPLOY_BINDING_TARGET_UNVERIFIED"] = "A binding target is not an observed slot or component, nor a verified member of one.",
        ["FLUX_DEPLOY_LIBRARY_PATH_UNAVAILABLE"] = "The Flux-SDK library directory for in-process deployment is not configured or not absolute.",
        ["FLUX_DEPLOY_LINK_UNAVAILABLE"] = "The connection cannot lend its link to FluxSDK, or closed before it could.",
        ["FLUX_DEPLOY_CONFIRMATION_REQUIRED"] = "The deployment replaces existing children and needs the token validation returned.",
        ["FLUX_DEPLOY_CONFIRMATION_INVALID"] = "The token is unknown, expired, used, or was issued for another request, connection or revision.",
        ["FLUX_DEPLOY_DUPLICATE_REQUEST"] = "The request ID was executed before, but its outcome is no longer remembered.",
        ["FLUX_DEPLOY_FAILED"] = "FluxSDK answered with an error; the world may be partially changed.",
        ["FLUX_DEPLOY_FAULTED"] = "FluxSDK threw; whether and how far the replacement ran is unknown.",
        ["FLUX_DEPLOY_DEADLINE_EXCEEDED"] = "FluxSDK did not finish within the deploy deadline; the link was retired.",
        ["FLUX_DEPLOY_READBACK_FAILED"] = "A post-deployment read failed, so a stage could not be established.",
        ["FLUX_DEPLOY_REPLACES_UNOBSERVED"] = "A child the deployment would replace was not read in full by the checked revision.",
        ["FLUX_DEPLOY_CHILDREN_CHANGED"] = "The parent's children named like the module changed just before FluxSDK started; the deployment was abandoned without writing.",
        ["FLUX_DEPLOY_UNEXPECTED_REMOVAL"] = "A direct child of the parent other than the confirmed ones disappeared during the deployment.",
        ["FLUX_DEPLOY_BINDING_TARGET_MISSING"] = "A mapped binding target no longer exists after the deployment.",
        ["FLUX_DEPLOY_BINDING_PORT_SLOT_MISSING"] = "A mapped port's Input:/Output: slot is absent from the module's fully read direct children.",
        ["FLUX_DEPLOY_BINDING_PORT_SLOT_DUPLICATE"] = "More than one direct child of the module slot carries the mapped port's slot name.",
        ["FLUX_DEPLOY_BINDING_MEMBER_UNREAD"] = "Warning: a port's binding member was not read, so whether it targets the mapped ID is unknown.",
        ["FLUX_DEPLOY_BINDING_TARGET_UNBOUND"] = "A port's binding member read back as a null reference; the port is not wired.",
        ["FLUX_DEPLOY_BINDING_TARGET_MISMATCH"] = "A port's binding member read back targeting another ID than the mapped target.",
        ["FLUX_DEPLOY_BINDING_CARRIER_MISSING"] = "A fully read port slot or its carrier component lacks the component or member that carries the binding.",
        ["FLUX_DEPLOY_BINDING_CARRIER_TYPE_MISMATCH"] = "A port slot's binding carrier is a different type than the build's port slot carries.",
        ["FLUX_DEPLOY_LIBRARY_PATH_MISMATCH"] = "Warning: the CLI build and the in-process deployment use different library directories, so the build's debug map may not describe what was deployed.",
        ["FLUX_DEPLOY_POST_DEPLOY_INCOMPLETE"] = "Processing after FluxSDK did not complete; what the world holds is unknown.",
        ["FLUX_DEPLOY_WRITER_PRECONDITION_FAILED"] = "A writer precondition scan found a component other than a verified carrier driving an output target; nothing was written.",
        ["FLUX_DEPLOY_WRITER_PRECONDITION_UNKNOWN"] = "A writer precondition scan could not establish every output target's writer set; nothing was written.",
    };

    public static IReadOnlyDictionary<string, string> ConnectionErrors { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["INCOMPATIBLE_PROTOCOL"] = "Client and server share no protocol version; the connection is rejected at handshake.",
        ["HANDSHAKE_REQUIRED"] = "A request arrived before the hello/welcome handshake completed; the connection closes.",
        ["TRANSPORT_FAILED"] = "The connection to the session could not be opened or broke.",
        ["ALREADY_CONNECTED"] = "A session is already connected; disconnect first.",
        ["NOT_CONNECTED"] = "No session is connected.",
        ["CONNECTION_LOST"] = "The connection dropped while the call was in flight.",
        ["RECONNECT_FAILED"] = "A reconnect attempt failed.",
        ["RECONNECT_EXHAUSTED"] = "All reconnect attempts failed; the session is gone.",
        ["INVALID_STATE"] = "The connection is not in a state that allows this call.",
        ["CANCELLED"] = "The call was cancelled.",
        ["DISCOVERY_FAILED"] = "Session discovery failed.",
    };
}

public static class RpcUsageCatalog
{
    public const int Version = 4;

    private static readonly string[] NoCapErrors = ["INVALID_PARAMS", "CANCELLED", "INTERNAL_ERROR"];

    private static readonly string[] MutationIssues =
    [
        "NotConnected", "ConnectionMismatch", "NotObserved", "ObservationFailed", "StaleRevision", "InvalidPlan", "InvalidOperation",
        "RootTarget", "UnobservedTarget", "UnobservedSubtree", "UnknownLabel", "DuplicateLabel", "DeletedTarget", "ParentCycle",
        "TypeUnverified", "MemberMismatch", "PreconditionFailed", "PreconditionUnknown", "DestructiveFlagMismatch",
        "ConfirmationRequired", "ConfirmationInvalid", "DuplicateRequest",
    ];

    private static readonly string[] FluxSdkIssues =
    [
        "FLUX_SDK_NOT_FOUND", "FLUX_SDK_VERSION_FAILED", "FLUX_SDK_VERSION_UNKNOWN", "FLUX_SDK_VERSION_UNTESTED",
        "FLUX_LIBRARY_PATH_NOT_CONFIGURED", "FLUX_LIBRARY_PATH_NOT_FOUND", "FLUX_LIBRARY_MISSING", "FLUX_LIBRARY_UNREADABLE",
    ];

    private static readonly string[] FluxBuildIssues =
    [
        "FLUX_BUILD_INVALID_REQUEST", "FLUX_BUILD_INPUT_INVALID", "FLUX_BUILD_COMPILATION_FAILED", "FLUX_BUILD_TIMED_OUT",
        "FLUX_BUILD_OUTPUT_MISSING", "FLUX_BUILD_DEBUG_OUTPUT_INVALID", "FLUX_BUILD_CACHE_ENTRY_INCOMPLETE", "FLUX_BUILD_CACHE_WRITE_FAILED",
    ];

    private static readonly string[] FluxDeployIssues =
    [
        "FLUX_DEPLOY_INVALID_REQUEST", "FLUX_DEPLOY_BUILD_FAILED", "FLUX_DEPLOY_PREPARE_FAILED", "FLUX_DEPLOY_NOT_CONNECTED",
        "FLUX_DEPLOY_CONNECTION_MISMATCH", "FLUX_DEPLOY_NOT_OBSERVED", "FLUX_DEPLOY_OBSERVATION_FAILED", "FLUX_DEPLOY_STALE_REVISION",
        "FLUX_DEPLOY_ROOT_TARGET", "FLUX_DEPLOY_PARENT_UNOBSERVED", "FLUX_DEPLOY_PARENT_READ_FAILED", "FLUX_DEPLOY_BINDING_PORTS_MISMATCH",
        "FLUX_DEPLOY_BINDING_TARGET_UNVERIFIED", "FLUX_DEPLOY_LIBRARY_PATH_UNAVAILABLE", "FLUX_DEPLOY_LINK_UNAVAILABLE",
        "FLUX_DEPLOY_CONFIRMATION_REQUIRED", "FLUX_DEPLOY_CONFIRMATION_INVALID", "FLUX_DEPLOY_DUPLICATE_REQUEST", "FLUX_DEPLOY_FAILED",
        "FLUX_DEPLOY_FAULTED", "FLUX_DEPLOY_DEADLINE_EXCEEDED", "FLUX_DEPLOY_READBACK_FAILED", "FLUX_DEPLOY_REPLACES_UNOBSERVED",
        "FLUX_DEPLOY_CHILDREN_CHANGED", "FLUX_DEPLOY_UNEXPECTED_REMOVAL", "FLUX_DEPLOY_BINDING_TARGET_MISSING",
        "FLUX_DEPLOY_BINDING_PORT_SLOT_MISSING", "FLUX_DEPLOY_BINDING_PORT_SLOT_DUPLICATE", "FLUX_DEPLOY_BINDING_MEMBER_UNREAD",
        "FLUX_DEPLOY_BINDING_TARGET_UNBOUND", "FLUX_DEPLOY_BINDING_TARGET_MISMATCH", "FLUX_DEPLOY_BINDING_CARRIER_MISSING",
        "FLUX_DEPLOY_BINDING_CARRIER_TYPE_MISMATCH",
        "FLUX_DEPLOY_LIBRARY_PATH_MISMATCH", "FLUX_DEPLOY_POST_DEPLOY_INCOMPLETE",
        "FLUX_DEPLOY_WRITER_PRECONDITION_FAILED", "FLUX_DEPLOY_WRITER_PRECONDITION_UNKNOWN",
    ];

    private static string[] Errors(params string[] extra) =>
        ["CAPABILITY_NOT_GRANTED", "INVALID_PARAMS", "CANCELLED", "INTERNAL_ERROR", .. extra];

    private const string RefusedValidation =
        """{"accepted":false,"requestId":"req-example","planId":"plan-example","sessionId":"","connectionId":"","scopeRootId":null,"checkedRevision":null,"issues":[{"code":"NotConnected","message":"No session is connected."}],"impact":[],"confirmationToken":null,"confirmationExpiresAt":null,"validatedAt":"2025-01-01T00:00:00Z"}""";

    private const string AcceptedValidation =
        """{"accepted":true,"requestId":"req-example","planId":"plan-example","sessionId":"session-example","connectionId":"conn-example","scopeRootId":"Root","checkedRevision":12,"issues":[],"impact":[],"confirmationToken":null,"confirmationExpiresAt":null,"validatedAt":"2025-01-01T00:00:00Z"}""";

    private const string Evidence =
        """{"subject":"plan-example","createdAt":"2025-01-01T00:00:00Z","facts":[],"diagnostics":[],"hypotheses":[],"nextActions":[],"unsupported":[],"issues":[]}""";

    private const string RefusedOutcome =
        """{"status":"Refused","requestId":"req-example","planId":"plan-example","sessionId":"","connectionId":"","beforeRevision":null,"afterRevision":null,"validation":"""
        + RefusedValidation
        + ""","operations":[],"createdIds":{},"startedAt":"2025-01-01T00:00:00Z","completedAt":"2025-01-01T00:00:01Z","evidence":"""
        + Evidence
        + """}""";

    private const string StoredOutcome =
        """{"status":"Applied","requestId":"req-example","planId":"plan-example","sessionId":"session-example","connectionId":"conn-example","beforeRevision":12,"afterRevision":13,"validation":"""
        + AcceptedValidation
        + ""","operations":[{"index":0,"operation":{"kind":"updateSlot","slotId":"slot-example","name":"New name"},"status":"Applied","targetId":"slot-example","createdId":null,"before":null,"after":null,"memberWrite":null,"detail":null}],"createdIds":{},"startedAt":"2025-01-01T00:00:00Z","completedAt":"2025-01-01T00:00:01Z","evidence":"""
        + Evidence
        + """}""";

    private const string RefusedDeployValidation =
        """{"accepted":false,"requestId":"req-example","sessionId":"","connectionId":"","checkedRevision":null,"parentSlotId":"slot-example","moduleName":null,"buildStatus":null,"buildKey":null,"replaces":[],"replaceCount":0,"bindingVerification":"NotApplicable","issues":[{"code":"FLUX_DEPLOY_NOT_CONNECTED","message":"No session is connected.","severity":"Error"}],"issueCount":1,"buildDiagnostics":[],"buildDiagnosticCount":0,"primaryBuildDiagnosticCount":0,"confirmationToken":null,"confirmationExpiresAt":null,"validatedAt":"2025-01-01T00:00:00Z"}""";

    private const string RefusedDeployOutcome =
        """{"status":"Refused","requestId":"req-example","sessionId":"","connectionId":"","beforeRevision":null,"afterRevision":null,"validation":"""
        + RefusedDeployValidation
        + ""","build":{"status":"Skipped","detail":"The request was invalid."},"deploy":{"status":"Skipped","detail":"Nothing was deployed."},"load":{"status":"Skipped","detail":"Nothing was deployed."},"binding":{"status":"Skipped","detail":"Nothing was deployed."},"moduleSlotId":null,"issues":[{"code":"FLUX_DEPLOY_NOT_CONNECTED","message":"No session is connected.","severity":"Error"}],"issueCount":1,"debugMap":null,"startedAt":"2025-01-01T00:00:00Z","completedAt":"2025-01-01T00:00:01Z"}""";

    private const string AcceptedDeployValidation =
        """{"accepted":true,"requestId":"req-example","sessionId":"session-example","connectionId":"conn-example","checkedRevision":12,"parentSlotId":"slot-example","moduleName":"module-example","buildStatus":"Succeeded","buildKey":"0000000000000000000000000000000000000000000000000000000000000000","replaces":[],"replaceCount":0,"bindingVerification":"NotApplicable","issues":[],"issueCount":0,"buildDiagnostics":[],"buildDiagnosticCount":0,"primaryBuildDiagnosticCount":0,"confirmationToken":null,"confirmationExpiresAt":null,"validatedAt":"2025-01-01T00:00:00Z"}""";

    private const string ProvisionalDeployOutcome =
        """{"status":"Unknown","requestId":"req-example","sessionId":"session-example","connectionId":"conn-example","beforeRevision":12,"afterRevision":null,"validation":"""
        + AcceptedDeployValidation
        + ""","build":{"status":"Succeeded"},"deploy":{"status":"Unknown","detail":"FluxSDK was started; the result is not established yet."},"load":{"status":"Unknown","detail":"FluxSDK was started; the result is not established yet."},"binding":{"status":"Unknown","detail":"FluxSDK was started; the result is not established yet."},"moduleSlotId":null,"issues":[{"code":"FLUX_DEPLOY_POST_DEPLOY_INCOMPLETE","message":"Post-deploy processing did not complete (yet); what the world holds is unknown.","severity":"Error"}],"issueCount":1,"debugMap":null,"startedAt":"2025-01-01T00:00:00Z","completedAt":"2025-01-01T00:00:01Z"}""";

    private const string UnknownWorldQuery =
        """{"value":null,"completeness":"Unknown","provenance":null,"unknownReason":"No observation covers this target."}""";

    private const string UnknownReflection =
        """{"value":null,"provenance":null,"unknownReason":"No definition is known for this name."}""";

    private const string NotFoundMetadata =
        """{"status":"NotFound","value":null,"assembly":null,"candidates":[],"detail":"No symbol has exactly this name."}""";

    private const string DisconnectedStatus =
        """{"state":"Disconnected","targetSession":null,"connection":null,"reconnectAttempt":0,"disconnectReason":null}""";

    public static IReadOnlyDictionary<string, RpcUsageMethod> Methods { get; } = new Dictionary<string, RpcUsageMethod>(StringComparer.Ordinal)
    {
        [RpcMethods.Describe] = new(null,
            "Protocol versions, capabilities and the method catalogue.",
            "Handshake-level self-description: the protocol versions spoken, every capability name, every method and its required capability, and the transport limits. Requires no capability and no session.",
            "{}",
            """{"protocolVersions":[1],"capabilities":["session.read","world.read"],"methods":{"rpc.describe":null,"world.slot":"world.read"},"maxFrameBytes":8388608,"maxInFlightRequests":16,"usageCatalogVersion":1,"methodSummaries":{"world.slot":"One slot with its components."},"workflows":{"mutation":"Plan, validate, execute and confirm a world mutation."}}""",
            [RpcMethods.Usage], [], RpcErrors: NoCapErrors),

        [RpcMethods.Usage] = new(null,
            "Self-describing usage: overview, per-method detail, or a workflow.",
            "Without selectors returns the overview (catalog version, method summaries, workflows, connection-level errors). With method returns that method's schemas, errors, example and constraints; with workflow returns ordered steps. Requires no capability and no session.",
            "{}",
            """{"kind":"overview","overview":{"usageCatalogVersion":1,"workbenchVersion":"0.1.0","protocolVersion":1,"methods":{"world.slot":{"capability":"world.read","summary":"One slot with its components."}},"workflows":{"mutation":"Plan, validate, execute and confirm a world mutation."},"connectionErrors":[{"code":"HANDSHAKE_REQUIRED","description":"A request arrived before the hello/welcome handshake completed."}]},"method":null,"workflow":null}""",
            [RpcMethods.Describe],
            ["Give exactly one of method or workflow, or neither for the overview; an empty string is invalid.", "Unknown method or workflow names are INVALID_PARAMS.", "The params schema describes the serialized shape only; semantic rules are listed under constraints."],
            RpcErrors: NoCapErrors),

        [RpcMethods.SessionStatus] = new(RpcCapabilities.SessionRead,
            "Connection state, target session and reconnect attempt.",
            "Snapshot of the session supervisor. connection is non-null only while state is Connected; targetSession is kept after disconnecting for display.",
            "{}", DisconnectedStatus,
            [RpcMethods.SessionList, RpcMethods.SessionConnect, RpcMethods.SessionDisconnect], [], RpcErrors: Errors()),

        [RpcMethods.SessionList] = new(RpcCapabilities.SessionRead,
            "Sessions the Workbench currently knows.",
            "The remembered session candidates, whether discovered or added by session.connect.",
            "{}", "[]",
            [RpcMethods.SessionDiscover, RpcMethods.SessionConnect], [], RpcErrors: Errors()),

        [RpcMethods.SessionDiscover] = new(RpcCapabilities.SessionRead,
            "Scan for ResoniteLink sessions and return the known list.",
            "Runs local session discovery, then returns the same list session.list would.",
            "{}", "[]",
            [RpcMethods.SessionList, RpcMethods.SessionConnect], [], RpcErrors: Errors()),

        [RpcMethods.SessionConnect] = new(RpcCapabilities.SessionControl,
            "Connect to one known session or loopback endpoint.",
            "Connects the Workbench's single session. Errors keep the connection's own codes (TRANSPORT_FAILED, ALREADY_CONNECTED).",
            """{"sessionId":"session-example"}""",
            """{"state":"Connected","targetSession":{"sessionId":"session-example","displayName":"Example","endpoint":"ws://127.0.0.1:1","discoveredAt":"2025-01-01T00:00:00Z","source":"Manual"},"connection":{"connectionId":"conn-example","generation":1,"session":{"sessionId":"session-example","displayName":"Example","endpoint":"ws://127.0.0.1:1","discoveredAt":"2025-01-01T00:00:00Z","source":"Manual"},"connectedAt":"2025-01-01T00:00:00Z","remote":{"resoniteVersion":"2025.1.1.1","resoniteLinkVersion":"0.13.0","uniqueSessionId":"session-example"}},"reconnectAttempt":0,"disconnectReason":null}""",
            [RpcMethods.SessionList, RpcMethods.SessionDiscover, RpcMethods.SessionDisconnect],
            ["Give exactly one of sessionId or endpoint.", "endpoint must be an absolute loopback ws:// or wss:// URI.", "sessionId must name a session session.list or session.discover knows."],
            RpcErrors: Errors("TRANSPORT_FAILED", "ALREADY_CONNECTED", "INVALID_STATE")),

        [RpcMethods.SessionDisconnect] = new(RpcCapabilities.SessionControl,
            "Disconnect the current session and return the new status.",
            "Drops the session connection; safe to call when nothing is connected.",
            "{}", DisconnectedStatus,
            [RpcMethods.SessionConnect, RpcMethods.SessionStatus], [], RpcErrors: Errors()),

        [RpcMethods.WorldHierarchy] = new(RpcCapabilities.WorldRead,
            "The observed hierarchy under a scope root, from the latest observation.",
            "Answers from the World Mirror's latest observation of scopeRootId; it does not force a live read. Check meta.stale and result.provenance before trusting it.",
            """{"scopeRootId":"Root"}""", UnknownWorldQuery,
            [RpcMethods.WorldObserve, RpcMethods.WorldSlot, RpcMethods.WorldRefresh],
            ["params must be a JSON object.", "Answers come from the latest observation, not a fresh read; call world.observe first when nothing was observed."],
            RpcErrors: Errors()),

        [RpcMethods.WorldObserve] = new(RpcCapabilities.WorldRead,
            "Observe a scope root synchronously and return its hierarchy.",
            "Brings scopeRootId under live observation with the given budget and returns the snapshot like world.hierarchy. Use for targets outside the background loop's fixed scope.",
            """{"scopeRootId":"Root","maxDepth":4,"maxSlots":500}""", UnknownWorldQuery,
            [RpcMethods.WorldHierarchy, RpcMethods.WorldSlot, RpcMethods.WorldRefresh],
            ["params must be a JSON object.", "scopeRootId must be non-blank.", "maxDepth is 1-32, maxSlots is 1-8192; unset values fall back to the defaults.", "The world revision tracks structural changes (slots and components); member value writes do not advance it - pin values with a memberValue precondition instead.", "Fails with NOT_CONNECTED when no session is connected."],
            RpcErrors: Errors("NOT_CONNECTED")),

        [RpcMethods.WorldSlot] = new(RpcCapabilities.WorldRead,
            "One slot with its components, from the latest observation.",
            "Looks the slot up in the World Mirror's latest observation; completeness is Unknown when the slot is not observed.",
            """{"slotId":"slot-example"}""", UnknownWorldQuery,
            [RpcMethods.WorldHierarchy, RpcMethods.WorldObserve],
            ["params must be a JSON object.", "slotId must be non-blank."],
            RpcErrors: Errors()),

        [RpcMethods.WorldRefresh] = new(RpcCapabilities.WorldRead,
            "Ask the re-observe loop to refresh now.",
            "Schedules a refresh on the background mirror loop. METHOD_UNAVAILABLE when this Workbench has no re-observe loop.",
            "{}", """{"scheduled":true}""",
            [RpcMethods.WorldObserve, RpcMethods.WorldHierarchy],
            ["METHOD_UNAVAILABLE when the Workbench does not re-observe the world."],
            RpcErrors: Errors("METHOD_UNAVAILABLE")),

        [RpcMethods.MemberRead] = new(RpcCapabilities.MemberRead,
            "Read one component member live.",
            "Reads the member's current value over the live connection; member is null and unknownReason says why when it could not be read.",
            """{"componentId":"component-example","memberName":"member-name-example"}""",
            """{"sessionId":"","connectionId":"","componentId":"component-example","componentType":null,"memberName":"member-name-example","member":null,"observedAt":"2025-01-01T00:00:00Z","unknownReason":"The component or member is not known."}""",
            [RpcMethods.ReflectionMember, RpcMethods.WritersFind],
            ["params must be a JSON object.", "componentId and memberName name a live object; IDs from a disconnected session are not valid.", "When no session is connected the read answers unknown (member null, unknownReason set) rather than failing."],
            RpcErrors: Errors()),

        [RpcMethods.ReflectionCapabilities] = new(RpcCapabilities.ReflectionRead,
            "What the connected session's reflection supports.",
            "The session capability report, or null when no report is available (for example no session connected).",
            "{}", "null",
            [RpcMethods.ReflectionComponent, RpcMethods.ReflectionType, RpcMethods.ReflectionMember],
            ["The result may be null; a null result is not an error."],
            RpcErrors: Errors()),

        [RpcMethods.ReflectionComponent] = new(RpcCapabilities.ReflectionRead,
            "A component's member definitions, by component type name.",
            "Resolves the component type through the connected session's reflection; unknownReason explains misses.",
            """{"componentType":"[FrooxEngine]FrooxEngine.Slot"}""", UnknownReflection,
            [RpcMethods.ReflectionMember, RpcMethods.ReflectionCapabilities, RpcMethods.ReflectionType],
            ["params must be a JSON object.", "componentType uses the session's type naming (assembly-qualified names like [FrooxEngine]FrooxEngine.Slot).", "A missing connection is reported as an unknown result, not an error."],
            RpcErrors: Errors()),

        [RpcMethods.ReflectionType] = new(RpcCapabilities.ReflectionRead,
            "A value type's definition, by type name.",
            "Resolves a non-component type (enum, struct) through the connected session's reflection.",
            """{"typeName":"[FrooxEngine]FrooxEngine.colorX"}""", UnknownReflection,
            [RpcMethods.ReflectionComponent, RpcMethods.ReflectionCapabilities],
            ["params must be a JSON object.", "A missing connection is reported as an unknown result, not an error."],
            RpcErrors: Errors()),

        [RpcMethods.ReflectionMember] = new(RpcCapabilities.ReflectionRead,
            "One member's definition on a component type.",
            "Resolves memberName on the component type through the connected session's reflection.",
            """{"componentType":"[FrooxEngine]FrooxEngine.Slot","memberName":"member-name-example"}""", UnknownReflection,
            [RpcMethods.ReflectionComponent, RpcMethods.MemberRead],
            ["params must be a JSON object.", "A missing connection is reported as an unknown result, not an error."],
            RpcErrors: Errors()),

        [RpcMethods.ReflectionSearch] = new(RpcCapabilities.ReflectionRead,
            "Component type names matching a substring.",
            "Searches the connected session's component type names for query as a case-insensitive substring; matches are sorted by ordinal and capped at limit, truncated flagging the cut.",
            """{"query":"light"}""",
            """{"value":{"types":["[FrooxEngine]FrooxEngine.Light","[FrooxEngine]FrooxEngine.LightProbe"],"truncated":false},"provenance":{"sessionId":"session-example","connectionId":"conn-example","resoniteVersion":"2025.1.1.1","observedAt":"2025-01-01T00:00:00Z","source":"Live"},"unknownReason":null}""",
            [RpcMethods.ReflectionComponent, RpcMethods.ReflectionType, RpcMethods.ReflectionCapabilities],
            ["params must be a JSON object.", "query must be a non-blank string.", "limit is 1-500 and defaults to 50; truncated is true when more than limit names matched.", "Type names are returned exactly as the session reports them; matching ignores case but never completes or normalizes names.", "A missing connection is reported as an unknown result, not an error."],
            RpcErrors: Errors()),

        [RpcMethods.ReflectionEnum] = new(RpcCapabilities.ReflectionRead,
            "An enum type's declared values, by type name.",
            "Resolves the type through the connected session's reflection; status Found carries underlyingType, isFlags and the values in the order the engine reports them, while NotAnEnum carries detail instead.",
            """{"typeName":"[FrooxEngine]FrooxEngine.BlendMode"}""",
            """{"value":{"status":"Found","typeName":"[FrooxEngine]FrooxEngine.BlendMode","underlyingType":"System.Int32","isFlags":false,"values":[{"name":"Normal","value":0},{"name":"Add","value":1},{"name":"Multiply","value":2}],"detail":null},"provenance":{"sessionId":"session-example","connectionId":"conn-example","resoniteVersion":"2025.1.1.1","observedAt":"2025-01-01T00:00:00Z","source":"Live"},"unknownReason":null}""",
            [RpcMethods.ReflectionType, RpcMethods.ReflectionComponent, RpcMethods.ReflectionCapabilities],
            ["params must be a JSON object.", "typeName must be a non-blank string, named like reflection.type (assembly-qualified names such as [FrooxEngine]FrooxEngine.BlendMode).", "A type name the engine cannot resolve, a missing connection and a lost session are all reported as an unknown result (value null, unknownReason set), like reflection.type.", "A type that resolves but is not an enum is a known result with status NotAnEnum; then underlyingType is null and values is empty, with detail explaining the miss.", "values keep the order ResoniteLink reports them; they are not re-sorted and the order is not guaranteed to be the enum's declaration order."],
            RpcErrors: Errors()),

        [RpcMethods.WritersFind] = new(RpcCapabilities.WorldRead,
            "Find what drives or may write a member.",
            "Scans the observed world for writers of the target (drivers, ValueCopy sources, shared references). coverage reports whether the whole world was scanned.",
            """{"targetId":"member-example","scopeRootId":"Root"}""",
            """{"status":"NotDriven","targetId":"member-example","writers":[],"otherReferences":[],"coverage":{"scopeRootId":"Root","coversWholeWorld":true,"scannedSlots":1,"scannedComponents":0,"excluded":[],"truncationReason":null},"sessionId":"session-example","connectionId":"conn-example","observedAt":"2025-01-01T00:00:00Z","detail":"Nothing drives the target."}""",
            [RpcMethods.WorldObserve, RpcMethods.MemberRead, RpcMethods.DiagnosticValueRevert],
            ["params must be a JSON object.", "maxComponents and maxHits are bounded by their defaults (200000 and 64); larger values are INVALID_PARAMS.", "excludeUserRoots skips user subtrees; a skipped subtree may hold the writer.", "A missing connection is reported in the result's coverage and detail, not as an error."],
            RpcErrors: Errors()),

        [RpcMethods.ResearchType] = new(RpcCapabilities.ResearchRead,
            "Inspect a type's metadata in a local .NET assembly.",
            "Static inspection only: fields, methods, properties and attributes of the named type, without loading the assembly.",
            """{"assemblyPath":"C:/example/FrooxEngine.dll","fullTypeName":"FrooxEngine.Slot"}""", NotFoundMetadata,
            [RpcMethods.ResearchMethod],
            ["params must be a JSON object.", "assemblyPath must be a fully qualified local path; UNC, device and network-drive paths are rejected.", "status NotFound/Ambiguous/NoBody/Unreadable describe misses; value is null unless Found."],
            RpcErrors: Errors()),

        [RpcMethods.ResearchMethod] = new(RpcCapabilities.ResearchRead,
            "Inspect one method's IL in a local .NET assembly.",
            "Static inspection of the method body (instruction list), bounded by maxInstructions.",
            """{"assemblyPath":"C:/example/FrooxEngine.dll","fullTypeName":"FrooxEngine.Slot","methodName":"get_Tag"}""", NotFoundMetadata,
            [RpcMethods.ResearchType],
            ["params must be a JSON object.", "assemblyPath must be a fully qualified local path.", "signature disambiguates overloads; Ambiguous returns the candidates.", "maxInstructions caps the decoded body (up to 10000)."],
            RpcErrors: Errors()),

        [RpcMethods.DiagnosticValueRevert] = new(RpcCapabilities.DiagnosticWrite,
            "Reproduce a member value being reverted and classify the writer.",
            "Writes the given value to the member and reads it back on a schedule to see whether another writer restores it.",
            """{"expectedConnectionId":"conn-example","componentId":"component-example","memberName":"member-name-example","value":{"kind":"field","valueType":"float","valueJson":"2"}}""",
            """{"classification":"Unknown","write":{"status":"OutcomeUnknown","sessionId":"","connectionId":"","componentId":"component-example","memberName":"member-name-example","componentType":null,"definition":null,"definitionProvenance":null,"before":null,"writtenValue":null,"samples":[],"startedAt":"2025-01-01T00:00:00Z","detail":"example"},"writers":null,"evidence":""" + Evidence + "}",
            [RpcMethods.WritersFind, RpcMethods.MemberRead],
            ["params must be a JSON object.", "value is a member write with a kind discriminator: field (valueType + valueJson), reference (targetId), or referenceList (targetIds: full ordered element list for a list-of-reference member such as MeshRenderer.Materials; overwrites by position and appends; cannot shrink a list).", "expectedConnectionId pins the connection the IDs came from.", "readbackSamples/readbackIntervalMs shape the readback schedule.", "A missing connection is reported in the write's status and the classification, not as an error."],
            RpcErrors: Errors()),

        [RpcMethods.MutationValidate] = new(RpcCapabilities.WorldMutate,
            "Check a mutation plan against the observed world.",
            "Validates the plan's operations and preconditions against the latest observation (or expectedScopeRootId's scope) and returns issues plus a confirmation token destructive plans need.",
            """{"requestId":"req-example","planId":"plan-example","expectedConnectionId":"conn-example","expectedWorldRevision":12,"operations":[{"kind":"updateSlot","slotId":"slot-example","name":"New name"}]}""",
            RefusedValidation,
            [RpcMethods.MutationExecute, RpcMethods.MutationOutcome, RpcMethods.WorldObserve],
            ["params must be a JSON object.", "operations and preconditions entries carry a kind discriminator; operation kinds are createSlot, updateSlot, deleteSlot, addComponent, setMember, removeComponent; precondition kinds are memberValue and slotTransform.", "member writes use kind field (valueType + valueJson), reference (targetId), or referenceList (targetIds: full ordered element list for a list-of-reference member such as MeshRenderer.Materials; overwrites by position and appends; cannot shrink a list); for colorX give profile explicitly - a null profile can auto-populate sRGB and make the value diverge.", "expectedWorldRevision is checked against the latest observation, or the expectedScopeRootId scope when given; the world revision does not advance on member writes, so pin values with a memberValue precondition.", "destructive must match whether the operations delete or remove anything.", "A missing connection is the NotConnected issue, not an RPC error."],
            RpcErrors: Errors(), ResultIssues: MutationIssues),

        [RpcMethods.MutationExecute] = new(RpcCapabilities.WorldMutate,
            "Run a validated mutation plan.",
            "Applies the plan's operations in order, then reads written members back. Destructive plans need the world.mutate.destructive capability and the confirmationToken mutation.validate issued.",
            """{"requestId":"req-example","planId":"plan-example","expectedConnectionId":"conn-example","expectedWorldRevision":12,"operations":[{"kind":"createSlot","parentId":"slot-example","name":"Child"}]}""",
            RefusedOutcome,
            [RpcMethods.MutationValidate, RpcMethods.MutationOutcome],
            ["params must be a JSON object.", "Same plan rules as mutation.validate.", "A destructive plan (destructive flag, or a delete/remove operation) needs the world.mutate.destructive capability before the handler runs.", "confirmationToken is required for destructive plans and must come from validating this exact plan and revision.", "requestId is idempotent: a repeated ID returns the remembered outcome.", "A missing connection is the NotConnected issue, not an RPC error."],
            ExtraCapability: RpcCapabilities.WorldMutateDestructive,
            RpcErrors: Errors(), ResultIssues: MutationIssues),

        [RpcMethods.MutationOutcome] = new(RpcCapabilities.WorldMutate,
            "The remembered outcome of an executed plan.",
            "Looks an execution up by requestId; only outcomes the Workbench still remembers are available.",
            """{"requestId":"req-example"}""", StoredOutcome,
            [RpcMethods.MutationExecute, RpcMethods.MutationValidate],
            ["params must be a JSON object.", "Unknown or forgotten requestIds fail with OUTCOME_NOT_FOUND."],
            RpcErrors: Errors("OUTCOME_NOT_FOUND"), ResultIssues: MutationIssues),

        [RpcMethods.FluxStatus] = new(RpcCapabilities.FluxRead,
            "The located Flux-SDK installation.",
            "Locates the flux-sdk executable and its libraries, reports the version and whether it is a tested series.",
            "{}",
            """{"available":false,"executableFound":false,"executable":"flux-sdk","version":null,"versionOutput":null,"compatibility":{"status":"Unknown","testedSeries":"1.9","version":null,"message":"The Flux-SDK executable could not be started."},"libraryPath":null,"library":null,"issues":[{"code":"FLUX_SDK_NOT_FOUND","severity":"Error","message":"flux-sdk is not installed."}]}""",
            [RpcMethods.FluxBuild],
            ["METHOD_UNAVAILABLE when this Workbench provides no Flux services."],
            RpcErrors: Errors("METHOD_UNAVAILABLE"), ResultIssues: FluxSdkIssues),

        [RpcMethods.FluxBuild] = new(RpcCapabilities.FluxBuild,
            "Compile one ProtoGraph module into the build cache.",
            "Runs flux-sdk build and stores the outputs keyed by content; a complete entry with the same key is a CacheHit and is not recompiled. Returns diagnostics, issues, the cache entry and artifactSha256 (the manifest's hash of the compiled artifact).",
            """{"projectRoot":"C:/example/project","entryFile":"Main.pg"}""",
            """{"status":"Failed","key":null,"entry":null,"debugMap":null,"diagnostics":[],"primaryDiagnostics":[],"diagnosticCount":0,"issues":[{"code":"FLUX_SDK_NOT_FOUND","severity":"Error","message":"flux-sdk is not installed."}],"sdkVersion":null,"libraryIdentityHash":null,"duration":"00:00:00.001","exitCode":null,"timedOut":false,"artifactSha256":null}""",
            [RpcMethods.FluxDebugMap, RpcMethods.FluxDeployValidate, RpcMethods.FluxStatus],
            ["params must be a JSON object.", "projectRoot must be a fully qualified local path; UNC, device and network-drive paths are rejected.", "entryFile is relative to projectRoot.", "timeoutSeconds is 1-3600.", "options.debugOutputs entries must be record, components or parse; record and parse are always produced.", "status Failed still returns a result - check issues and diagnostics.", "METHOD_UNAVAILABLE when this Workbench provides no Flux services."],
            RpcErrors: Errors("METHOD_UNAVAILABLE"), ResultIssues: [.. FluxSdkIssues, .. FluxBuildIssues]),

        [RpcMethods.FluxDebugMap] = new(RpcCapabilities.FluxRead,
            "Read a build's or deployment's debug map, whole or at a position.",
            "By buildKey reads the cached build map; by requestId reads a deployment's map. With line and column (1-based) returns only the nodes whose span contains the position; with slotId (deployment maps) the node the slot came from.",
            """{"buildKey":"0000000000000000000000000000000000000000000000000000000000000000","line":1,"column":1}""",
            """{"kind":"Build","buildKey":"0000000000000000000000000000000000000000000000000000000000000000","requestId":null,"buildMap":null,"deploymentMap":null,"totalNodes":0,"truncated":false,"totals":{"nodes":0,"ambiguousSpans":0,"unmappedTags":0,"unmatchedLiveTags":0,"ports":0,"bindings":0,"notes":0,"tagModuleNames":0,"replacedSlotIds":0,"unexpectedlyRemovedSlotIds":0},"buildMatches":[],"deploymentMatches":null}""",
            [RpcMethods.FluxBuild, RpcMethods.FluxDeployOutcome],
            ["params must be a JSON object.", "Give exactly one of buildKey or requestId.", "line and column must be given together and are 1-based; slotId only works on deployment maps.", "maxEntries bounds every list (default and ceiling 1000); truncated flags lists cut to the bound.", "METHOD_UNAVAILABLE when this Workbench provides no Flux services."],
            RpcErrors: Errors("METHOD_UNAVAILABLE", "OUTCOME_NOT_FOUND", "FLUX_BUILD_NOT_FOUND", "FLUX_DEBUG_MAP_NOT_FOUND", "FLUX_DEBUG_MAP_INVALID")),

        [RpcMethods.FluxDeployValidate] = new(RpcCapabilities.FluxDeploy,
            "Check a deployment against the observed world, without writing.",
            "Builds the module (reusing the cache), verifies bindings, computes what the deployment would replace and returns a confirmationToken when it replaces existing children.",
            """{"requestId":"req-example","projectRoot":"C:/example/project","entryFile":"Main.pg","parentSlotId":"slot-example","expectedConnectionId":"conn-example","expectedWorldRevision":12,"writerPrecondition":{"kind":"outputTargetsUncontested"}}""",
            RefusedDeployValidation,
            [RpcMethods.FluxDeployExecute, RpcMethods.FluxDeployOutcome, RpcMethods.FluxBuild],
            ["params must be a JSON object.", "inputMap/outputMap/memberOwners map module ports to live IDs; the keys must equal the declared ports.", "expectedScopeRootId pins validation to that scope's latest observation.", "METHOD_UNAVAILABLE when this Workbench provides no Flux services.", "FLUX_DEPLOY_NOT_CONNECTED is a result issue, not an RPC error.", "writerPrecondition (optional) re-verifies, immediately before writing, that every outputMap target is not contested by a foreign writer; execute's check is authoritative, validate's is only a preview. Omitting it keeps the previous behavior. Does not guarantee exclusion of a writer outside the leased scan window or detect runtime (non-reference) writes."],
            RpcErrors: Errors("METHOD_UNAVAILABLE"), ResultIssues: [.. FluxSdkIssues, .. FluxBuildIssues, .. FluxDeployIssues]),

        [RpcMethods.FluxDeployExecute] = new(RpcCapabilities.FluxDeploy,
            "Deploy a validated ProtoGraph module under a parent slot.",
            "Runs the deployment for real. When it replaces existing children the confirmationToken from flux.deploy.validate is required and sending one needs the flux.deploy.replace capability.",
            """{"requestId":"req-example","projectRoot":"C:/example/project","entryFile":"Main.pg","parentSlotId":"slot-example","expectedConnectionId":"conn-example","expectedWorldRevision":12,"writerPrecondition":{"kind":"outputTargetsUncontested"}}""",
            RefusedDeployOutcome,
            [RpcMethods.FluxDeployValidate, RpcMethods.FluxDeployOutcome, RpcMethods.FluxDebugMap],
            ["params must be a JSON object.", "Same request rules as flux.deploy.validate.", "Sending confirmationToken needs the flux.deploy.replace capability, checked before the handler runs.", "requestId is idempotent: a repeated ID returns the remembered outcome.", "METHOD_UNAVAILABLE when this Workbench provides no Flux services.", "FLUX_DEPLOY_NOT_CONNECTED is a result issue, not an RPC error.", "writerPrecondition (optional) re-verifies, immediately before writing, that every outputMap target is not contested by a foreign writer; execute's check is authoritative, validate's is only a preview. Omitting it keeps the previous behavior. Does not guarantee exclusion of a writer outside the leased scan window or detect runtime (non-reference) writes."],
            ExtraCapability: RpcCapabilities.FluxDeployReplace,
            RpcErrors: Errors("METHOD_UNAVAILABLE"), ResultIssues: [.. FluxSdkIssues, .. FluxBuildIssues, .. FluxDeployIssues]),

        [RpcMethods.FluxDeployOutcome] = new(RpcCapabilities.FluxDeploy,
            "The remembered outcome of a deployment.",
            "Looks a deployment up by requestId; only outcomes the Workbench still remembers are available.",
            """{"requestId":"req-example"}""", ProvisionalDeployOutcome,
            [RpcMethods.FluxDeployExecute, RpcMethods.FluxDebugMap],
            ["params must be a JSON object.", "Unknown or forgotten requestIds fail with OUTCOME_NOT_FOUND.", "METHOD_UNAVAILABLE when this Workbench provides no Flux services."],
            RpcErrors: Errors("METHOD_UNAVAILABLE", "OUTCOME_NOT_FOUND"), ResultIssues: [.. FluxSdkIssues, .. FluxBuildIssues, .. FluxDeployIssues]),
    };

    public static IReadOnlyDictionary<string, RpcWorkflowUsage> Workflows { get; } = new Dictionary<string, RpcWorkflowUsage>(StringComparer.Ordinal)
    {
        ["mutation"] = new("mutation",
            "Plan, validate, execute and confirm a world mutation.",
            [
                new(RpcMethods.WorldObserve, "Bring the target scope under observation and learn its revision."),
                new(RpcMethods.MutationValidate, "Check the plan against the observed revision and get a confirmation token for destructive plans.", "The plan binds to expectedWorldRevision; re-validate if the world changed."),
                new(RpcMethods.MutationExecute, "Apply the plan.", "Destructive plans need world.mutate.destructive and the validation token."),
                new(RpcMethods.MutationOutcome, "Read the remembered outcome by requestId."),
                new(RpcMethods.MemberRead, "Read back the members the plan wrote."),
            ]),
        ["flux"] = new("flux",
            "Build, deploy and verify a ProtoGraph module.",
            [
                new(RpcMethods.FluxBuild, "Compile the module into the cache and get its key."),
                new(RpcMethods.FluxDebugMap, "Inspect the build debug map by key.", "Optional; for source attribution and troubleshooting."),
                new(RpcMethods.FluxDeployValidate, "Check the deployment against the observed world and get a confirmation token if it replaces children."),
                new(RpcMethods.FluxDeployExecute, "Run the deployment.", "Replacing children needs flux.deploy.replace and the validation token."),
                new(RpcMethods.FluxDeployOutcome, "Read the remembered deployment outcome by requestId."),
                new(RpcMethods.FluxDebugMap, "Read the deployment debug map by requestId.", "Optional."),
                new(RpcMethods.MemberRead, "Read back members the deployment bound."),
            ]),
        ["object"] = new("object",
            "Resolve an object's stable identity from observation, diff its public properties against desired values, plan and apply only what changed, and read back what was written. Verified end to end by Phase 7 M2's reference client (plan.md Section 7).",
            [
                new(RpcMethods.WorldObserve, "Observe the scope containing the object, one level at a time, to resolve its stable identity by name and shape.", "Identity is the client's own responsibility: Workbench does not interpret selectors or object definitions, and a name match alone is not enough - verify the expected shape before writing."),
                new(RpcMethods.ReflectionComponent, "Learn the members of the component types found, once per type."),
                new(RpcMethods.MemberRead, "Read each public property's current value and its member ID."),
                new(RpcMethods.WritersFind, "Check the member ID for a live writer before planning to overwrite it.", "Only skip the write when the status is NotDriven with coverage.coversWholeWorld true; any other status (a resolved writer, RuntimeFlux, or incomplete coverage) means the value must not be overwritten."),
                new(RpcMethods.MutationValidate, "Check the planned change against expectedConnectionId/expectedWorldRevision/expectedScopeRootId.", "Include a memberValue precondition for every member write: the world revision advances only on slot/component structure changes, never on a member value alone."),
                new(RpcMethods.MutationExecute, "Apply the change.", "A StaleRevision or PreconditionFailed result means nothing was written - re-observe and re-plan, bounded, rather than resending blindly."),
                new(RpcMethods.MutationOutcome, "Confirm the outcome by requestId, especially after an OutcomeUnknown response.", "Never resend the same change under a new requestId until a fresh observation proves it is still needed."),
                new(RpcMethods.MemberRead, "Read back every written member.", "Report success only once every operation is Applied and its readback matches what was written."),
            ]),
        ["object.driven"] = new("object.driven",
            "Edit an object whose public properties include driver-owned values, and deploy its ProtoGraph module: classify every writer before writing, then build, deploy and verify the port bindings. Verified end to end by Phase 7 M3's flicker-Lamp reference client (plan.md Section 8).",
            [
                new(RpcMethods.WorldObserve, "Observe the scope containing the object, one level at a time, to resolve its stable identity by name and shape.", "Judge completeness per parent and region, not globally: for each parent the selector searches, its childIds and every child's name must be known, and the slots the shape check reads must have their children and components lists present - a missing, unreadable or stale list is Unknown, not absence. A global completeness of Partial alone is normal on real worlds; it flags truncation elsewhere in the scope, not that the searched parent was unread."),
                new(RpcMethods.MemberRead, "Read each public property's bound member, including driver-owned members - they are runtime state, never write targets."),
                new(RpcMethods.WritersFind, "Check every member the desired state would write for a live writer.", "Classify each answer: none (NotDriven with coverage.coversWholeWorld true), driverOwned (the only writer is exactly this object's own verified Flux output carrier - the proxy on its Output:<port> slot driving via its Drive member), conflict (any other writer), or unknown (the scan could not establish the writer set). Only 'none' allows a normal write; driverOwned values are read as runtime state; conflict and unknown refuse rather than fight the driver."),
                new(RpcMethods.MutationValidate, "Check the planned change - only the members still differing - against expectedConnectionId/expectedWorldRevision/expectedScopeRootId.", "Include a memberValue precondition for every member write: the world revision advances only on slot/component structure changes, never on a member value alone."),
                new(RpcMethods.MutationExecute, "Apply the change.", "A StaleRevision or PreconditionFailed result means nothing was written - re-observe and re-plan, bounded, rather than resending blindly."),
                new(RpcMethods.MutationOutcome, "Confirm the outcome by requestId, especially after an OutcomeUnknown response.", "Never resend the same change under a new requestId until a fresh observation proves it is still needed."),
                new(RpcMethods.MemberRead, "Read back every written member.", "Report success only once every operation is Applied and its readback matches what was written."),
                new(RpcMethods.FluxBuild, "For a Flux-driven object: compile the ProtoGraph module; the build key decides whether a redeploy is needed.", "A CacheHit for the recorded key means the deployed module is current; a changed key redeploys."),
                new(RpcMethods.FluxDebugMap, "Read the build's debug map and cross-check its declared ports against the fixture's port bindings before deploying.", "The port table must be Known and its ports - names, directions, types - exactly the ports the deploy will map; an unknown or mismatched declaration refuses rather than deploying on guessed inputMap/outputMap keys."),
                new(RpcMethods.WritersFind, "Re-check every member an outputMap port would drive, immediately before the deploy.", "Prefer flux.deploy.execute's writerPrecondition:{kind:'outputTargetsUncontested'} instead of a manual re-check: it re-scans every outputMap target inside the write lease, immediately before FluxSDK starts, and refuses before writing (FLUX_DEPLOY_WRITER_PRECONDITION_FAILED/_UNKNOWN) rather than racing a client-side check. It does not exclude a writer outside that scan window atomically, nor detect a runtime (non-reference) write, so this manual writers.find check is still useful as an earlier, cheaper signal before build/deploy - just not as the sole guarantee."),
                new(RpcMethods.FluxDeployValidate, "Validate the deployment: inputMap/outputMap map declared port names to exact member IDs, memberOwners maps each member to the component holding it.", "The map keys must equal the module's declared ports; a deployment that replaces existing children returns the confirmation token execute needs."),
                new(RpcMethods.FluxDeployExecute, "Run the deployment.", "Replacing children needs the flux.deploy.replace capability and the validation token. Send the same writerPrecondition validate used, if any - it is part of the confirmation fingerprint."),
                new(RpcMethods.FluxDeployOutcome, "Read the remembered deployment outcome by requestId when the execute result is unknown.", "Query the SAME requestId - never re-execute under a new one. The outcome's per-port bindings report Verified/Failed/Unknown: Verified means the requested wiring read back correctly, not that nothing else drives the member - writers.find answers that."),
            ]),
    };
}

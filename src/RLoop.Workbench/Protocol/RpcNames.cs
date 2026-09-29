namespace ResoniteWorkbench.Protocol;

public static class RpcPipeNames
{
    public const string Default = "ResoniteWorkbench.Rpc.v1";
}

/// <summary>
/// What a client may do. Reads, session control and anything that writes to the world are
/// separate grants, and a grant is only given when the client asks for it by name.
/// </summary>
public static class RpcCapabilities
{
    public const string SessionRead = "session.read";
    public const string SessionControl = "session.control";
    public const string WorldRead = "world.read";
    public const string MemberRead = "member.read";
    public const string ReflectionRead = "reflection.read";
    public const string ResearchRead = "research.read";

    /// <summary>Diagnostics that write to the world to reproduce a symptom.</summary>
    public const string DiagnosticWrite = "diagnostic.write";

    /// <summary>Validating and executing mutation plans that create or change, but never destroy.</summary>
    public const string WorldMutate = "world.mutate";

    /// <summary>
    /// Executing plans that delete slots or remove components. Granted on top of
    /// <see cref="WorldMutate"/>, never instead of it.
    /// </summary>
    public const string WorldMutateDestructive = "world.mutate.destructive";

    /// <summary>Flux-SDK status and debug-map lookups; no side effects.</summary>
    public const string FluxRead = "flux.read";

    /// <summary>Running Flux builds: a local <c>flux-sdk</c> process that writes only to the build cache.</summary>
    public const string FluxBuild = "flux.build";

    /// <summary>Validating and executing Flux deployments that add a new module under a parent slot.</summary>
    public const string FluxDeploy = "flux.deploy";

    /// <summary>
    /// Executing a Flux deployment that replaces existing children of the parent. Granted on top of
    /// <see cref="FluxDeploy"/>, never instead of it.
    /// </summary>
    public const string FluxDeployReplace = "flux.deploy.replace";

    public static IReadOnlyList<string> ReadOnly { get; } =
        [SessionRead, WorldRead, MemberRead, ReflectionRead, ResearchRead, FluxRead];

    public static IReadOnlyList<string> All { get; } =
    [
        SessionRead, SessionControl, WorldRead, MemberRead, ReflectionRead, ResearchRead, DiagnosticWrite, WorldMutate, WorldMutateDestructive,
        FluxRead, FluxBuild, FluxDeploy, FluxDeployReplace,
    ];
}

public static class RpcMethods
{
    public const string Describe = "rpc.describe";
    public const string Usage = "rpc.usage";
    public const string SessionStatus = "session.status";
    public const string SessionList = "session.list";
    public const string SessionDiscover = "session.discover";
    public const string SessionConnect = "session.connect";
    public const string SessionDisconnect = "session.disconnect";
    public const string WorldHierarchy = "world.hierarchy";
    public const string WorldObserve = "world.observe";
    public const string WorldSlot = "world.slot";
    public const string WorldRefresh = "world.refresh";
    public const string MemberRead = "member.read";
    public const string ReflectionCapabilities = "reflection.capabilities";
    public const string ReflectionComponent = "reflection.component";
    public const string ReflectionType = "reflection.type";
    public const string ReflectionMember = "reflection.member";
    public const string ReflectionSearch = "reflection.search";
    public const string ReflectionEnum = "reflection.enum";
    public const string WritersFind = "writers.find";
    public const string ResearchType = "research.type";
    public const string ResearchMethod = "research.method";
    public const string DiagnosticValueRevert = "diagnostic.valueRevert";
    public const string MutationValidate = "mutation.validate";
    public const string MutationExecute = "mutation.execute";
    public const string MutationOutcome = "mutation.outcome";
    public const string FluxStatus = "flux.status";
    public const string FluxBuild = "flux.build";
    public const string FluxDebugMap = "flux.debugMap";
    public const string FluxDeployValidate = "flux.deploy.validate";
    public const string FluxDeployExecute = "flux.deploy.execute";
    public const string FluxDeployOutcome = "flux.deploy.outcome";
}

/// <summary>
/// RPC-level error codes. Errors raised by a Core service keep the service's own code, such as
/// <c>NOT_CONNECTED</c>.
/// </summary>
public static class RpcErrorCodes
{
    public const string IncompatibleProtocol = "INCOMPATIBLE_PROTOCOL";
    public const string HandshakeRequired = "HANDSHAKE_REQUIRED";
    public const string CapabilityNotGranted = "CAPABILITY_NOT_GRANTED";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string MethodUnavailable = "METHOD_UNAVAILABLE";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Cancelled = "CANCELLED";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
    public const string DuplicateRequestId = "DUPLICATE_REQUEST_ID";
    public const string InternalError = "INTERNAL_ERROR";
}

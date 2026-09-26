using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResoniteWorkbench.Protocol;

/// <summary>One frame on the wire, discriminated by <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RpcHello), "hello")]
[JsonDerivedType(typeof(RpcWelcome), "welcome")]
[JsonDerivedType(typeof(RpcReject), "reject")]
[JsonDerivedType(typeof(RpcRequest), "request")]
[JsonDerivedType(typeof(RpcResponse), "response")]
[JsonDerivedType(typeof(RpcError), "error")]
[JsonDerivedType(typeof(RpcCancel), "cancel")]
public abstract record RpcMessage;

/// <summary>The first frame a client sends.</summary>
/// <param name="ProtocolVersions">Every protocol version the client can speak.</param>
/// <param name="RequestedCapabilities">Only these are ever granted; unknown names are ignored.</param>
public sealed record RpcHello(
    string ClientName,
    string ClientVersion,
    IReadOnlyList<int> ProtocolVersions,
    IReadOnlyList<string> RequestedCapabilities) : RpcMessage;

/// <summary>The session connection at handshake time; a snapshot, not a subscription.</summary>
public sealed record RpcActiveConnection(string ConnectionId, string SessionId);

public sealed record RpcWelcome(
    string ServerVersion,
    int SelectedProtocol,
    IReadOnlyList<string> AvailableCapabilities,
    IReadOnlyList<string> GrantedCapabilities,
    RpcActiveConnection? ActiveConnection) : RpcMessage;

/// <summary>Sent instead of a welcome when no protocol version is shared; the server then disconnects.</summary>
public sealed record RpcReject(string Code, string Message, IReadOnlyList<int> SupportedProtocols) : RpcMessage;

/// <param name="Id">Chosen by the client; unique among its requests still in flight.</param>
public sealed record RpcRequest(string Id, string Method, JsonElement? Params = null) : RpcMessage;

public sealed record RpcResponse(string Id, ResultMeta Meta, JsonElement? Result) : RpcMessage;

public sealed record RpcErrorDetail(string Code, string Message);

/// <param name="Id">The request that failed, or null for an error about the connection itself.</param>
public sealed record RpcError(string? Id, RpcErrorDetail Error) : RpcMessage;

/// <summary>Asks the server to stop an in-flight request. Unknown or finished IDs are ignored.</summary>
public sealed record RpcCancel(string Id) : RpcMessage;

/// <summary>
/// Where a result came from and how far it can be trusted. Fields that do not apply to a
/// method, or that were not observed, are null; null never means "none".
/// </summary>
/// <param name="ServedAt">When the server formed the response.</param>
/// <param name="WorldRevision">The World Mirror revision; null for direct live reads.</param>
/// <param name="Source">Live, Cache or Probe.</param>
/// <param name="Completeness">Complete, Partial or Unknown.</param>
/// <param name="Stale">True when the result is cached, too old, or from a connection that is no longer current.</param>
public sealed record ResultMeta(
    DateTimeOffset ServedAt,
    string? ConnectionId = null,
    string? SessionId = null,
    long? WorldRevision = null,
    DateTimeOffset? ObservedAt = null,
    string? Source = null,
    string? Completeness = null,
    bool? Stale = null);

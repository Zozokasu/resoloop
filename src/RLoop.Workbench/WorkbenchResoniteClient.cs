using System.Text.Json;
using ResoniteWorkbench.Protocol;
using RLoop.Core;

namespace RLoop.Workbench;

/// <summary>
/// An <see cref="IResoniteClient"/> over the Workbench named-pipe RPC transport. The endpoint is
/// expressed with the internal convention <c>pipe:///&lt;pipeName&gt;</c>. W1 supports only
/// <c>session.status</c>; every other member reports BACKEND_UNSUPPORTED.
/// </summary>
public sealed class WorkbenchResoniteClient : IResoniteClient
{
    /// <summary>The URI scheme identifying a Workbench pipe endpoint: pipe:///&lt;pipeName&gt;.</summary>
    public const string UriScheme = "pipe";

    private const string ClientVersion = "1.0.0";

    private WorkbenchRpcClient? _client;
    private string? _pipeName;

    /// <summary>The negotiated handshake; null until <see cref="ConnectAsync"/> succeeds.</summary>
    public WorkbenchHandshakeInfo? Handshake { get; private set; }

    /// <summary>Metadata carried by the most recent RPC response (or the handshake).</summary>
    public WorkbenchConnectionMeta Meta { get; private set; } = new(null, null, null, null);

    public async Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.Equals(uri.Scheme, UriScheme, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Workbench connections use '{UriScheme}:///<pipeName>' URIs, not '{uri.Scheme}'.", nameof(uri));

        string pipeName = uri.AbsolutePath.TrimStart('/');
        var hello = new RpcHello("ResoLoop", ClientVersion, [1], [RpcCapabilities.SessionRead]);

        WorkbenchRpcClient client;
        try
        {
            client = await WorkbenchRpcClient.ConnectAsync(pipeName, hello, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw Unavailable($"No Workbench answered on pipe '{pipeName}' within {timeout.TotalSeconds:0.#} seconds.", ex);
        }
        catch (RpcHandshakeException ex) when (ex.Rejection?.Code == RpcErrorCodes.IncompatibleProtocol)
        {
            throw new RLoopException("WORKBENCH_PROTOCOL_INCOMPATIBLE",
                $"The Workbench on pipe '{pipeName}' rejected the protocol handshake: {ex.Message}",
                ExitCodes.ConnectionFailed,
                new Dictionary<string, object?> { ["supportedProtocols"] = ex.Rejection.SupportedProtocols },
                ["Upgrade ResoLoop or the Workbench App so that both share an RPC protocol version."], ex);
        }
        catch (Exception ex) when (ex is RpcHandshakeException or IOException)
        {
            throw Unavailable($"Could not complete the Workbench handshake on pipe '{pipeName}': {ex.Message}", ex);
        }

        _client = client;
        _pipeName = pipeName;
        RpcWelcome welcome = client.Welcome;
        Handshake = new WorkbenchHandshakeInfo(
            welcome.ServerVersion,
            welcome.SelectedProtocol,
            welcome.AvailableCapabilities,
            welcome.GrantedCapabilities,
            welcome.ActiveConnection?.ConnectionId,
            welcome.ActiveConnection?.SessionId);
        Meta = new WorkbenchConnectionMeta(
            welcome.ActiveConnection?.ConnectionId,
            welcome.ActiveConnection?.SessionId,
            null,
            null);
    }

    public async Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        WorkbenchRpcClient client = _client
            ?? throw new InvalidOperationException("WorkbenchResoniteClient is not connected; call ConnectAsync first.");

        RpcResponse response;
        try
        {
            response = await client.CallAsync(RpcMethods.SessionStatus, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or RpcCallException)
        {
            throw Unavailable($"The Workbench session.status call failed: {ex.Message}", ex);
        }

        Meta = new WorkbenchConnectionMeta(
            response.Meta.ConnectionId, response.Meta.SessionId, response.Meta.WorldRevision, null);

        // SessionStatus is read leniently: absent members are treated as null.
        string? state = null;
        JsonElement connection = default;
        bool hasConnection = false;
        if (response.Result is { } result && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("state", out JsonElement stateElement) && stateElement.ValueKind == JsonValueKind.String)
                state = stateElement.GetString();
            if (result.TryGetProperty("connection", out JsonElement connectionElement) && connectionElement.ValueKind == JsonValueKind.Object)
            {
                connection = connectionElement;
                hasConnection = true;
            }
        }

        if (response.Meta.Stale == true)
        {
            throw Unavailable("The Workbench session.status response is stale; the connection changed during the read.");
        }

        if (string.Equals(state, "Disconnected", StringComparison.Ordinal))
        {
            if (hasConnection)
            {
                throw Unavailable(
                    "The Workbench session.status response reports Disconnected but includes a connection payload.");
            }
            throw new RLoopException("WORKBENCH_NOT_CONNECTED",
                "Workbench is not connected to a Resonite session.",
                ExitCodes.ConnectionFailed,
                suggestions: ["Connect the Workbench App to a Resonite session, then retry."]);
        }

        if (!string.Equals(state, "Connected", StringComparison.Ordinal))
        {
            throw Unavailable(
                $"The Workbench session.status response is not a definite state (state: {state ?? "<missing>"}).");
        }

        string? connectionId = hasConnection ? ReadString(connection, "connectionId") : null;
        if (string.IsNullOrEmpty(connectionId))
        {
            throw Unavailable(
                "The Workbench session.status response reports Connected but has no connection.connectionId.");
        }

        if (!string.Equals(response.Meta.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            throw Unavailable("The Workbench session.status response meta does not match the reported connection.");
        }

        string? resoniteVersion = null;
        string? resoniteLinkVersion = null;
        string? uniqueSessionId = null;
        if (connection.TryGetProperty("remote", out JsonElement remote) && remote.ValueKind == JsonValueKind.Object)
        {
            resoniteVersion = ReadString(remote, "resoniteVersion");
            resoniteLinkVersion = ReadString(remote, "resoniteLinkVersion");
            uniqueSessionId = ReadString(remote, "uniqueSessionId");
        }

        return new SessionInfo($"{UriScheme}:///{_pipeName}", true, resoniteVersion, resoniteLinkVersion, uniqueSessionId);
    }

    public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default) =>
        Task.FromException<SlotInfo>(Unsupported(nameof(GetSlotAsync)));

    public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromException<ComponentInfo>(Unsupported(nameof(GetComponentAsync)));

    public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Unsupported(nameof(CreateSlotAsync)));

    public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException(Unsupported(nameof(UpdateSlotAsync)));

    public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromException(Unsupported(nameof(DeleteSlotAsync)));

    public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) =>
        Task.FromException<ComponentCreateResult>(Unsupported(nameof(AddComponentAsync)));

    public Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default) =>
        Task.FromException(Unsupported(nameof(SetComponentMemberAsync)));

    public Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) =>
        Task.FromException(Unsupported(nameof(SetComponentMembersAsync)));

    public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) =>
        Task.FromException(Unsupported(nameof(RemoveComponentAsync)));

    public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<string>>(Unsupported(nameof(SearchComponentTypesAsync)));

    public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) =>
        Task.FromException<ComponentTypeInfo>(Unsupported(nameof(DescribeComponentTypeAsync)));

    public Task<Core.TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) =>
        Task.FromException<Core.TypeInfo>(Unsupported(nameof(DescribeTypeAsync)));

    public Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, JsonElement>? arguments = null, CancellationToken cancellationToken = default) =>
        Task.FromException<SyncMethodCallResult>(Unsupported(nameof(CallComponentMethodAsync)));

    public Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Unsupported(nameof(ImportAssetAsync)));

    public async ValueTask DisposeAsync()
    {
        WorkbenchRpcClient? client = _client;
        _client = null;
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
    }

    private static RLoopException Unavailable(string message, Exception? innerException = null) =>
        new("WORKBENCH_UNAVAILABLE", message, ExitCodes.ConnectionFailed, innerException: innerException);

    private static RLoopException Unsupported(string method) =>
        new("BACKEND_UNSUPPORTED", $"{method} is not yet supported on the workbench backend.", ExitCodes.OperationFailed);

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

namespace RLoop.Workbench;

/// <summary>
/// The negotiated Workbench RPC handshake captured when
/// <see cref="WorkbenchResoniteClient.ConnectAsync"/> succeeded; a snapshot, not a subscription.
/// </summary>
public sealed record WorkbenchHandshakeInfo(
    string ServerVersion,
    int SelectedProtocol,
    IReadOnlyList<string> AvailableCapabilities,
    IReadOnlyList<string> GrantedCapabilities,
    string? ActiveConnectionId,
    string? ActiveSessionId);

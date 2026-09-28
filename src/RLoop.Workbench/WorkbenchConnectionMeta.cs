namespace RLoop.Workbench;

/// <summary>
/// Metadata the Workbench carried on the most recent RPC response (or the handshake
/// before any call). Null fields mean "not observed on that response", never "none".
/// </summary>
/// <param name="ObservedScopeRootId">
/// Reserved for the W2/W4 world-observation scope; W1 issues only session.status, so
/// this is always null here.
/// </param>
public sealed record WorkbenchConnectionMeta(
    string? ConnectionId,
    string? SessionId,
    long? WorldRevision,
    string? ObservedScopeRootId);

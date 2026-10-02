using System.Net;

namespace RLoop.Core;

/// <summary>Announcement evidence, separate from the per-connection session counter.</summary>
public sealed record ApplySessionObservation(string NormalizedUrl, string? DiscoverSessionId, string IdentityStatus)
{
    /// <summary>
    /// Produces the local write-lock key: lowercased IDN host with trailing dots removed, URI-canonicalized
    /// scheme and port, and loopback addresses collapsed to localhost (including 127.0.0.2 and ::1).
    /// URI path and query remain in the key, and different schemes remain different. This is a coordination
    /// key, not proof that two URLs reach the same server or that an alias is accepted by the listener.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var uri = new Uri(url);
        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        if (host == "localhost" || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) host = "localhost";
        return new UriBuilder(uri.Scheme.ToLowerInvariant(), host, uri.Port, uri.AbsolutePath)
            { Query = uri.Query.TrimStart('?') }.Uri.AbsoluteUri;
    }
    public static ApplySessionObservation Observe(string url, IEnumerable<DiscoveredResoniteSession>? announcements = null)
    {
        var normalized = NormalizeUrl(url);
        var ids = (announcements ?? []).Where(a => a.SessionId.StartsWith("S-", StringComparison.Ordinal) &&
            NormalizeUrl(a.Url) == normalized).Select(a => a.SessionId).Distinct(StringComparer.Ordinal).ToArray();
        return new(normalized, ids.Length == 1 ? ids[0] : null, ids.Length == 1 ? "matched" : "unknown");
    }
}
public interface IApplySessionObservation { ApplySessionObservation ObserveApplySession(); }
/// <summary>Local evidence of reaching the SDK mutation boundary, not server acceptance.</summary>
public interface IApplySendEvidence
{
    void BeginApplySend();
    bool ApplySendStarted { get; }
    bool ApplyResponseReceived { get; }
    bool ApplyResponseAccepted { get; }
}
public sealed record ApplyConnectionObservation(bool Connected, string? Generation);
/// <summary>A local connection guard. This does not exclude external writers.</summary>
public interface IApplyConnectionGuard
{
    ApplyConnectionObservation ObserveApplyConnection();
    IDisposable GuardApplyWrites(string? plannedGeneration);
}

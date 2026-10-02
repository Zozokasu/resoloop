using System.Net;

namespace RLoop.Core;

/// <summary>Announcement evidence, separate from the per-connection session counter.</summary>
public sealed record ApplySessionObservation(string NormalizedUrl, string? DiscoverSessionId, string IdentityStatus)
{
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
public sealed record ApplyConnectionObservation(bool Connected, string? Generation);
/// <summary>A local connection guard. This does not exclude external writers.</summary>
public interface IApplyConnectionGuard
{
    ApplyConnectionObservation ObserveApplyConnection();
    IDisposable GuardApplyWrites(string? plannedGeneration);
}

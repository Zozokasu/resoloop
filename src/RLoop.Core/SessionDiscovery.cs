namespace RLoop.Core;

public sealed record DiscoveredResoniteSession(string SessionId, string SessionName, string Url, DateTime LastSeenUtc);

public interface IResoniteSessionDiscovery
{
    Task<IReadOnlyList<DiscoveredResoniteSession>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

public static class SessionDiscovery
{
    public const int DefaultSeconds = 12;

    public static async Task<Uri> ResolveUrlAsync(RLoopConfig config, IResoniteSessionDiscovery discovery,
        int seconds = DefaultSeconds, string? session = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(config.ResoniteLinkUrl, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (session is not null)
                throw new RLoopException("DISCOVERY_SELECTOR_REQUIRES_AUTO", "--session requires --url auto.", ExitCodes.InvalidArguments);
            return ConfigResolver.RequireUrl(config);
        }
        ValidateSeconds(seconds);
        var sessions = await discovery.DiscoverAsync(TimeSpan.FromSeconds(seconds), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var matches = sessions.Where(candidate => session is null || candidate.SessionId == session || candidate.SessionName == session).ToArray();
        if (matches.Length != 1)
            throw new RLoopException(matches.Length == 0 ? "RESONITE_DISCOVERY_NOT_FOUND" : "RESONITE_DISCOVERY_AMBIGUOUS",
                matches.Length == 0 ? "No matching ResoniteLink session was announced during discovery." : "Multiple ResoniteLink sessions match; choose an explicit session or URL.",
                ExitCodes.ConfigurationError, new Dictionary<string, object?> { ["sessions"] = sessions, ["selector"] = session },
                ["Run resoloop discover --json, then pass --url auto --session SESSION_ID or --url ws://HOST:PORT. Enable ResoniteLink in the intended world; discovery needs UDP 12512 announcements."]);
        return ConfigResolver.RequireUrl(new RLoopConfig(matches[0].Url));
    }

    public static void ValidateSeconds(int seconds)
    {
        if (seconds is < 1 or > 60)
            throw new RLoopException("INVALID_OPTION", "--discovery-seconds must be between 1 and 60.", ExitCodes.InvalidArguments);
    }
}

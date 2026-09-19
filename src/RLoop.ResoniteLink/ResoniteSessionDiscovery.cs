using System.Net.Sockets;
using System.Net;
using System.Net.NetworkInformation;
using ResoniteLink;
using RLoop.Core;

namespace RLoop.ResoniteLink;

/// <summary>Consumes official ResoniteLink announcements without probing ports or modifying a world.</summary>
public sealed class ResoniteSessionDiscovery : IResoniteSessionDiscovery
{
    public async Task<IReadOnlyList<DiscoveredResoniteSession>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration.TotalSeconds is < 1 or > 60 || !double.IsFinite(duration.TotalSeconds))
            throw new RLoopException("INVALID_OPTION", "Discovery duration must be between 1 and 60 seconds.", ExitCodes.InvalidArguments);
        cancellationToken.ThrowIfCancellationRequested();
        using var listener = new LinkSessionListener();
        try
        {
            listener.Start();
            // Wait for a complete observation window rather than selecting the first announcer.
            await Task.Delay(duration, cancellationToken);
            var sessions = new List<ResoniteLinkSession>();
            listener.GetDiscoveredSessions(sessions);
            var now = DateTime.UtcNow;
            var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(network => network.GetIPProperties().UnicastAddresses).Select(address => address.Address).ToHashSet();
            return sessions.Select(session => Map(session, now, localAddresses)).OfType<DiscoveredResoniteSession>()
                .OrderBy(session => session.SessionName, StringComparer.Ordinal)
                .ThenBy(session => session.SessionId, StringComparer.Ordinal).ToArray();
        }
        catch (SocketException ex)
        {
            throw new RLoopException("RESONITE_DISCOVERY_UNAVAILABLE", "Could not listen for ResoniteLink announcements on UDP 12512.",
                ExitCodes.OperationFailed, new Dictionary<string, object?> { ["socketError"] = ex.SocketErrorCode.ToString() },
                ["Check local UDP access, or use an explicit --url ws://HOST:PORT."], ex);
        }
    }

    internal static DiscoveredResoniteSession? Map(ResoniteLinkSession session, DateTime now, IReadOnlySet<IPAddress>? localAddresses = null)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.LinkEndPoint is null || session.LinkPort is < 1 or > 65535 ||
            now - session.LastUpdateTimestamp > LinkSessionListener.ANNOUNCE_INTERVAL * 2.5) return null;
        // The UDP sender's port is not the advertised WebSocket port.
        var address = session.LinkEndPoint.Address;
        // Local announcements can originate from the LAN interface although the WebSocket
        // server expects localhost (the Unity SDK also connects local sessions this way).
        var host = IPAddress.IsLoopback(address) || localAddresses?.Contains(address) == true ? "localhost" : address.ToString();
        var url = new UriBuilder("ws", host, session.LinkPort).Uri.AbsoluteUri;
        return new(session.SessionId, session.SessionName ?? string.Empty, url, session.LastUpdateTimestamp);
    }
}

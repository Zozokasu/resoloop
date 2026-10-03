namespace RLoop.Flux.Deployer

open System
open System.Net
open System.Net.NetworkInformation
open System.Collections.Generic
open System.Threading.Tasks
open ResoniteLink
open RLoop.Core

/// Each checkpoint collects a new complete announcement window. No cached guard announcements are reused.
type internal FreshWriterDiscovery() =
    interface IFluxWriterDiscovery with
        member _.ObserveAsync(url, started, cancellationToken) =
            task {
                use listener = new LinkSessionListener()
                try
                    listener.Start()
                    do! Task.Delay(LinkSessionListener.ANNOUNCE_INTERVAL, cancellationToken)
                    let sessions = List<ResoniteLinkSession>()
                    listener.GetDiscoveredSessions(sessions)
                    let local = NetworkInterface.GetAllNetworkInterfaces() |> Seq.collect(fun network -> network.GetIPProperties().UnicastAddresses) |> Seq.map(fun address -> address.Address) |> fun addresses -> HashSet<IPAddress>(addresses)
                    let now = DateTime.UtcNow
                    let matched =
                        sessions |> Seq.choose(fun session ->
                            if isNull (box session.LinkEndPoint) || session.LinkPort < 1 || session.LinkPort > 65535 ||
                               String.IsNullOrWhiteSpace(session.SessionId) || session.LastUpdateTimestamp < started ||
                               session.LastUpdateTimestamp > now || now - session.LastUpdateTimestamp > LinkSessionListener.ANNOUNCE_INTERVAL * 2.5 then None
                            else
                                let address = session.LinkEndPoint.Address
                                let host = if IPAddress.IsLoopback(address) || local.Contains(address) then "localhost" else address.ToString()
                                let endpoint = UriBuilder("ws", host, session.LinkPort).Uri.AbsoluteUri
                                if ApplySessionObservation.NormalizeUrl(endpoint) <> ApplySessionObservation.NormalizeUrl(url.AbsoluteUri) then None
                                else Some(DiscoveredResoniteSession(session.SessionId, session.SessionName, endpoint, session.LastUpdateTimestamp))) |> Seq.toArray
                    return matched :> IReadOnlyList<DiscoveredResoniteSession>
                with
                | :? OperationCanceledException as error -> return raise error
                | _ -> return [||] :> IReadOnlyList<DiscoveredResoniteSession> // F1: unavailable announcement evidence is unknown.
            }

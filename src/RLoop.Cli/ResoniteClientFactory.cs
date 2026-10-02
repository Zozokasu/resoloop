using RLoop.Core;
using RLoop.ResoniteLink;
using RLoop.Workbench;

namespace RLoop.Cli;

/// <summary>
/// Creates a connected <see cref="IResoniteClient"/> for the configured backend. The "link" backend
/// resolves a ResoniteLink WebSocket URL and returns a <see cref="ResoniteLinkClientAdapter"/>; the
/// "workbench" backend never resolves a URL and connects to the configured named pipe instead.
/// </summary>
public static class ResoniteClientFactory
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Uri, IReadOnlyList<DiscoveredResoniteSession>> announcements = new();
    private sealed class RecordingDiscovery : IResoniteSessionDiscovery
    {
        public IReadOnlyList<DiscoveredResoniteSession> Sessions { get; private set; } = [];
        public async Task<IReadOnlyList<DiscoveredResoniteSession>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default) =>
            Sessions = await new ResoniteSessionDiscovery().DiscoverAsync(duration, ct);
    }
    /// <summary>Resolves the ResoniteLink WebSocket URL for the "link" backend (--url, discovery).</summary>
    public static async Task<Uri> ResolveConnectionUrlAsync(ParsedArguments args, RLoopConfig config, CancellationToken cancellationToken)
    {
        var discovery = new RecordingDiscovery();
        var uri = await SessionDiscovery.ResolveUrlAsync(config, discovery,
            args.IntOption("discovery-seconds", SessionDiscovery.DefaultSeconds, 1, 60), args.Option("session"), cancellationToken);
        RememberAnnouncements(uri, discovery.Sessions);
        return uri;
    }
    internal static void RememberAnnouncements(Uri uri, IReadOnlyList<DiscoveredResoniteSession> evidence) =>
        announcements.AddOrUpdate(uri, evidence);

    /// <summary>
    /// Creates and connects a client for <see cref="RLoopConfig.Backend"/>. Pass
    /// <paramref name="linkUri"/> when the link URL was already resolved by the caller so it is not
    /// resolved twice; the parameter is ignored on the workbench backend.
    /// </summary>
    public static Task<IResoniteClient> ConnectAsync(ParsedArguments args, RLoopConfig config,
        ReflectionCacheOptions reflectionCache, CancellationToken cancellationToken, Uri? linkUri = null) =>
        ConnectAsync(args, config, reflectionCache, cancellationToken, linkUri, null, null);

    internal static async Task<IResoniteClient> ConnectAsync(ParsedArguments args, RLoopConfig config,
        ReflectionCacheOptions reflectionCache, CancellationToken cancellationToken, Uri? linkUri,
        Func<IResoniteClient>? workbenchClientFactory, Func<IResoniteClient>? linkClientFactory)
    {
        var timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        if (string.Equals(config.Backend, "workbench", StringComparison.Ordinal))
        {
            var workbench = workbenchClientFactory?.Invoke() ?? new WorkbenchResoniteClient();
            try
            {
                await workbench.ConnectAsync(new Uri($"{WorkbenchResoniteClient.UriScheme}:///{config.WorkbenchPipe}"), timeout, cancellationToken);
                return workbench;
            }
            catch (Exception connectFailure)
            {
                await DisposeQuietlyAsync(workbench, connectFailure);
                throw;
            }
        }
        var uri = linkUri ?? await ResolveConnectionUrlAsync(args, config, cancellationToken);
        var link = linkClientFactory?.Invoke() ?? new ResoniteLinkClientAdapter(timeout, reflectionCache);
        try
        {
            await link.ConnectAsync(uri, timeout, cancellationToken);
            if (link is ResoniteLinkClientAdapter adapter && announcements.TryGetValue(uri, out var evidence))
                adapter.SetApplyAnnouncements(evidence);
            return link;
        }
        catch (Exception connectFailure)
        {
            await DisposeQuietlyAsync(link, connectFailure);
            throw;
        }
    }

    /// <summary>
    /// Disposes a client whose connect attempt failed. A disposal failure must never replace the
    /// original exception, so it is attached to the original exception's <see cref="Exception.Data"/>
    /// when possible and otherwise discarded.
    /// </summary>
    private static async Task DisposeQuietlyAsync(IResoniteClient client, Exception connectFailure)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception disposeFailure)
        {
            try
            {
                connectFailure.Data["disposeException"] = disposeFailure.Message;
            }
            catch
            {
                // Exception.Data can be read-only; the disposal failure is then discarded.
            }
        }
    }
}

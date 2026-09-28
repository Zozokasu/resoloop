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
    /// <summary>Resolves the ResoniteLink WebSocket URL for the "link" backend (--url, discovery).</summary>
    public static Task<Uri> ResolveConnectionUrlAsync(ParsedArguments args, RLoopConfig config, CancellationToken cancellationToken) =>
        SessionDiscovery.ResolveUrlAsync(config, new ResoniteSessionDiscovery(),
            args.IntOption("discovery-seconds", SessionDiscovery.DefaultSeconds, 1, 60), args.Option("session"), cancellationToken);

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
            catch
            {
                await workbench.DisposeAsync();
                throw;
            }
        }
        var uri = linkUri ?? await ResolveConnectionUrlAsync(args, config, cancellationToken);
        var link = linkClientFactory?.Invoke() ?? new ResoniteLinkClientAdapter(timeout, reflectionCache);
        try
        {
            await link.ConnectAsync(uri, timeout, cancellationToken);
            return link;
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }
}

using System.Text;
using System.Text.Json;
using Link = ResoniteLink;

namespace RLoop.CatalogExport;

/// <summary>Developer-only acquisition. The transport exposes only three read requests.</summary>
internal static class SdkProbe
{
    internal const int MaxSlots = 64; // Per connection; at most 128 across the two passes.
    internal const int MaxComponents = 512; // Per connection.
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(2);

    internal interface IReadConnection : IDisposable
    {
        Task Connect(Uri url, CancellationToken ct);
        Task<Link.SessionData> Session();
        Task<Link.SlotData> Slot(Link.GetSlot request);
        Task<Link.ComponentData> Component(Link.GetComponent request);
    }

    private sealed class SdkConnection : IReadConnection
    {
        private readonly Link.LinkInterface link = new();
        public Task Connect(Uri url, CancellationToken ct) => link.Connect(url, ct);
        public Task<Link.SessionData> Session() => link.GetSessionData();
        public Task<Link.SlotData> Slot(Link.GetSlot request) => link.GetSlotData(request);
        public Task<Link.ComponentData> Component(Link.GetComponent request) => link.GetComponentData(request);
        public void Dispose() => link.Dispose();
    }

    internal sealed record SlotRead(string RequestedId, int Depth, Link.SlotData Response);
    internal sealed record ComponentRead(string RequestedId, Link.ComponentData Response);
    internal sealed class Pass(int number)
    {
        public int Number { get; } = number;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public Link.SessionData? SessionBefore { get; set; }
        public Link.SessionData? SessionAfter { get; set; }
        public List<SlotRead> Slots { get; } = [];
        public List<ComponentRead> Components { get; } = [];
        public List<string> Diagnostics { get; } = [];
        public bool CompleteWithinRequestedDepth => Diagnostics.Count == 0;
    }
    internal sealed record Result(string Url, string SlotId, int RequestedDepth, int MaxSlotsPerConnection,
        int MaxComponentsPerConnection, double TimeLimitSeconds, List<Pass> Connections);

    internal static async Task<Result> ReadAsync(Uri url, string slotId, int depth, CancellationToken ct = default,
        Func<IReadConnection>? factory = null, int maxSlots = MaxSlots, int maxComponents = MaxComponents,
        TimeSpan? timeLimit = null)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("ws" or "wss") || string.IsNullOrWhiteSpace(url.Host))
            throw new ArgumentException("An explicit absolute ws:// or wss:// URL is required.");
        if (string.IsNullOrWhiteSpace(slotId)) throw new ArgumentException("An explicit Slot ID is required.");
        if (depth is < 0 or > 8) throw new ArgumentException("Depth must be 0..8; unbounded depth is not supported.");
        if (maxSlots is < 1 or > MaxSlots || maxComponents is < 1 or > MaxComponents)
            throw new ArgumentException("Probe budgets exceed their hard limits.");
        var limit = timeLimit ?? TimeLimit;
        if (limit <= TimeSpan.Zero || limit > TimeLimit) throw new ArgumentException("Time budget must be positive and at most two minutes.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        var result = new Result(url.AbsoluteUri, slotId, depth, maxSlots, maxComponents, limit.TotalSeconds, []);
        for (var number = 1; number <= 2; number++)
        {
            var pass = new Pass(number);
            result.Connections.Add(pass);
            if (deadline.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); pass.Diagnostics.Add("time-limit: connection not attempted"); continue; }
            using var reader = (factory ?? (() => new SdkConnection()))();
            try
            {
                await reader.Connect(url, deadline.Token).WaitAsync(deadline.Token);
                pass.SessionBefore = await reader.Session().WaitAsync(deadline.Token);
                if (!pass.SessionBefore.Success) pass.Diagnostics.Add("session-before: unsuccessful response");
                var queue = new Queue<(string Id, int Depth)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var components = new HashSet<string>(StringComparer.Ordinal);
                queue.Enqueue((slotId, 0));
                while (queue.TryDequeue(out var next))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!seen.Add(next.Id)) continue;
                    if (pass.Slots.Count >= maxSlots) { pass.Diagnostics.Add("slot-limit: subtree not fully read"); break; }
                    // Depth 0 keeps each reply local. Child references remain in the untouched SDK reply.
                    var response = await reader.Slot(new Link.GetSlot
                    { SlotID = next.Id, Depth = 0, IncludeComponentData = false }).WaitAsync(deadline.Token);
                    pass.Slots.Add(new(next.Id, next.Depth, response));
                    if (response.Depth != 0) pass.Diagnostics.Add($"slot: unexpected response depth for {next.Id}");
                    if (!response.Success || response.Data is null || response.Data.IsReferenceOnly || response.Data.ID != next.Id)
                    { pass.Diagnostics.Add($"slot: unsuccessful, reference-only, missing or mismatched data for {next.Id}"); continue; }
                    var data = response.Data;
                    if (data.Children is null || data.Components is null)
                        pass.Diagnostics.Add($"slot: missing children/components list for {next.Id}");
                    foreach (var component in data.Components ?? [])
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(component.ID)) { pass.Diagnostics.Add("component: missing ID"); continue; }
                        if (!components.Add(component.ID)) continue;
                        if (pass.Components.Count >= maxComponents)
                        { if (!pass.Diagnostics.Contains("component-limit: members not fully read")) pass.Diagnostics.Add("component-limit: members not fully read"); break; }
                        var full = await reader.Component(new Link.GetComponent { ComponentID = component.ID }).WaitAsync(deadline.Token);
                        pass.Components.Add(new(component.ID, full));
                        if (!full.Success || full.Data is null || full.Data.IsReferenceOnly || full.Data.ID != component.ID || full.Data.Members is null)
                            pass.Diagnostics.Add($"component: unsuccessful, reference-only, missing or mismatched data for {component.ID}");
                    }
                    if (next.Depth < depth)
                        foreach (var child in data.Children ?? [])
                            if (!string.IsNullOrWhiteSpace(child.ID)) queue.Enqueue((child.ID, next.Depth + 1));
                            else pass.Diagnostics.Add("child: missing ID");
                }
                pass.SessionAfter = await reader.Session().WaitAsync(deadline.Token);
                if (!pass.SessionAfter.Success) pass.Diagnostics.Add("session-after: unsuccessful response");
                if (pass.SessionBefore.UniqueSessionId != pass.SessionAfter.UniqueSessionId ||
                    pass.SessionBefore.ResoniteVersion != pass.SessionAfter.ResoniteVersion ||
                    pass.SessionBefore.ResoniteLinkVersion != pass.SessionAfter.ResoniteLinkVersion)
                    pass.Diagnostics.Add("session: observed values changed within connection");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { pass.Diagnostics.Add(deadline.IsCancellationRequested ? "time-limit: partial acquisition" : $"{ex.GetType().Name}: {ex.Message}"); }
            // Dispose happens before creating the second connection. No auto-discovery or retry.
        }
        return result;
    }

    internal static string Serialize(Result result) => JsonSerializer.Serialize(result,
        new JsonSerializerOptions(Link.LinkInterface.SerializationOptions) { WriteIndented = true });

    internal static void Save(Result result, string path) => File.WriteAllText(path, Serialize(result), new UTF8Encoding(false));

    internal static async Task<bool> RunCommandAsync(string[] args)
    {
        if (args.Length is not (7 or 9) || args[1] != "--live" || args[2] != "--url" || args[4] != "--slot" ||
            (args.Length == 9 && args[6] != "--depth"))
            throw new ArgumentException("Usage: probe --live --url ws://localhost:PORT --slot SLOT_ID [--depth N] OUTPUT.json");
        var depth = args.Length == 9 ? int.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var result = await ReadAsync(new Uri(args[3], UriKind.Absolute), args[5], depth);
        Save(result, args[^1]);
        return result.Connections.All(p => p.CompleteWithinRequestedDepth);
    }
}

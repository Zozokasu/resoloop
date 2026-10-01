using RLoop.Core;
using Link = ResoniteLink;

namespace RLoop.ResoniteLink;

/// <summary>Explicit developer-tool entry point. Only reads metadata; never used by normal CLI validation.</summary>
public static class CatalogCapture
{
    // A narrow internal seam keeps fake acquisition on the same bounded path as the SDK.
    internal sealed record Reader(
        Func<string, Task<Link.ComponentDefinitionData>> Component,
        Func<string, Task<Link.TypeDefinitionData>> Type,
        Func<string, Task<Link.SyncObjectDefinitionData>> SyncObject,
        Func<string, Task<Link.EnumDefinitionData>> Enum,
        Func<bool> Connected);

    public static async Task<CatalogSnapshot> ReadAsync(Uri url, IReadOnlyList<string> componentNames, CancellationToken ct)
    {
        ValidateNames(componentNames);
        using var link = new Link.LinkInterface();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await link.Connect(url, deadline.Token).WaitAsync(deadline.Token);
        var session = await link.GetSessionData().WaitAsync(deadline.Token);
        if (!session.Success || string.IsNullOrWhiteSpace(session.ResoniteVersion) || string.IsNullOrWhiteSpace(session.ResoniteLinkVersion))
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Session versions could not be observed.", ExitCodes.ValidationFailed);
        var identity = new CatalogIdentity(session.ResoniteVersion, session.ResoniteLinkVersion, CatalogMapper.ClientPackageVersion, ApplyCatalog.CurrentMapperVersion, DateTimeOffset.UtcNow);
        var reader = new Reader(name => link.GetComponentDefinition(name, true), name => link.GetTypeDefinition(name),
            name => link.GetSyncObjectDefinition(name, true), name => link.GetEnumDefinition(name), () => link.IsConnected);
        var snapshot = await AcquireAsync(identity, componentNames, reader, ct, deadline.Token);
        try
        {
            var after = await link.GetSessionData().WaitAsync(deadline.Token);
            if (!after.Success || after.ResoniteVersion != identity.ResoniteVersion || after.ResoniteLinkVersion != identity.ResoniteLinkVersion)
                throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Session identity changed during acquisition.", ExitCodes.ValidationFailed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // A deadline in the final identity read must also leave a reviewable snapshot.
            var content = snapshot.Content with { AcquisitionFailures = [.. snapshot.Content.AcquisitionFailures ?? [],
                new("*", "session", "time-limit: final session identity was not rechecked")] };
            snapshot = snapshot with { Content = content, ContentHash = CatalogMapper.SnapshotHash(content), Source = "unverified" };
        }
        if (!link.IsConnected) throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Connection lost during acquisition.", ExitCodes.ValidationFailed);
        return snapshot;
    }

    private static void ValidateNames(IReadOnlyList<string> names)
    {
        if (names.Count is 0 or > 512) throw new ArgumentException("Require 1..512 explicit full Component names.");
    }

    internal static async Task<CatalogSnapshot> AcquireAsync(CatalogIdentity identity, IReadOnlyList<string> componentNames,
        Reader reader, CancellationToken ct = default, CancellationToken deadline = default)
    {
        ValidateNames(componentNames);
        var components = new List<Link.ComponentDefinition>(); var types = new List<Link.TypeDefinition>(); var syncs = new List<Link.SyncObjectDefinition>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal); var enums = new Dictionary<string, CatalogEnum>(StringComparer.Ordinal);
        var failures = new List<CatalogAcquisitionFailure>();
        static string Reason(string? error, string fallback) => string.IsNullOrWhiteSpace(error) ? fallback : error;
        var pending = new Queue<(string Name, string Kind)>();
        var seen = new HashSet<(string Name, string Kind)>();
        var typeBudget = new HashSet<string>(StringComparer.Ordinal);
        void Enqueue(Link.TypeReference? type, bool sync = false)
        {
            if (type is null || type.IsGenericParameter) return;
            var name = ModelMapper.Render(type)!;
            pending.Enqueue((name, "type"));
            if (sync) pending.Enqueue((name, "syncObject"));
            foreach (var arg in type.GenericArguments ?? []) Enqueue(arg);
        }
        void Members(IEnumerable<Link.MemberDefinition> members)
        {
            foreach (var member in members)
            {
                Enqueue(member.Type, member is Link.SyncObjectMemberDefinition);
                switch (member)
                {
                    case Link.FieldDefinition field: Enqueue(field.ValueType); break;
                    case Link.ReferenceDefinition reference: Enqueue(reference.TargetType); break;
                    case Link.ArrayDefinition array: Enqueue(array.ValueType); break;
                    case Link.ListDefinition list when list.ElementDefinition is not null: Members([list.ElementDefinition]); break;
                    case Link.DictionaryDefinition dictionary:
                        Enqueue(dictionary.KeyType); if (dictionary.ElementDefinition is not null) Members([dictionary.ElementDefinition]); break;
                }
            }
        }
        void Parents(Link.TypeDefinition type) { Enqueue(type.BaseType); foreach (var parent in type.Interfaces ?? []) Enqueue(parent); }
        foreach (var name in componentNames.Distinct(StringComparer.Ordinal))
        {
            pending.Enqueue((name, "component"));
            pending.Enqueue((name, "type"));
        }
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (name, kind) = pending.Dequeue();
            if (!seen.Add((name, kind))) continue;
            if (deadline.IsCancellationRequested) { failures.Add(new(name, kind, "time-limit: acquisition reached two minutes")); continue; }
            if (!typeBudget.Contains(name) && typeBudget.Count >= 512)
            { failures.Add(new(name, kind, "type-limit: acquisition reached 512 types")); continue; }
            typeBudget.Add(name);
            try
            {
                switch (kind)
                {
                    case "component":
                        var component = await reader.Component(name).WaitAsync(combined.Token);
                        if (!component.Success || component.Definition is not { } c || c.Type is null || c.Type.FullTypeName != name || c.Members is null)
                        { failures.Add(new(name, kind, Reason(component.ErrorInfo, "unsuccessful response or mismatched/missing exact definition"))); break; }
                        components.Add(c); Parents(c.Type); Members(c.Members.Values);
                        break;
                    case "syncObject":
                        var sync = await reader.SyncObject(name).WaitAsync(combined.Token);
                        if (!sync.Success || sync.Definition is not { } s || s.Type is null || s.Type.FullTypeName != name || s.Members is null)
                        { failures.Add(new(name, kind, Reason(sync.ErrorInfo, "unsuccessful response or mismatched/missing exact definition"))); break; }
                        syncs.Add(s); Parents(s.Type); Members(s.Members.Values);
                        break;
                    case "type":
                        var response = await reader.Type(name).WaitAsync(combined.Token);
                        if (!response.Success || response.Definition is not { } t || string.IsNullOrWhiteSpace(t.FullTypeName))
                        { failures.Add(new(name, kind, Reason(response.ErrorInfo, "unsuccessful response or missing definition"))); break; }
                        types.Add(t); Parents(t);
                        if (t.FullTypeName != name) aliases[name] = t.FullTypeName;
                        if (t.IsEnum) pending.Enqueue((t.FullTypeName, "enum"));
                        break;
                    case "enum":
                        var values = await reader.Enum(name).WaitAsync(combined.Token);
                        if (values.Success && values.Definition is { } data && data.Values is not null) enums[name] = new(data.Values, data.IsFlags);
                        else failures.Add(new(name, kind, Reason(values.ErrorInfo, "unsuccessful response or missing definition")));
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (reader.Connected())
            {
                failures.Add(new(name, kind, deadline.IsCancellationRequested
                    ? "time-limit: acquisition reached two minutes" : $"{ex.GetType().Name}: {ex.Message}"));
            }
            if (!reader.Connected()) throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Connection lost during acquisition.", ExitCodes.ValidationFailed);
        }
        var content = new CatalogSnapshotContent(components, types, syncs, aliases, enums, failures);
        return new(identity, identity, "live", false, CatalogMapper.SnapshotHash(content), content);
    }
}

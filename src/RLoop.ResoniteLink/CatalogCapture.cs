using RLoop.Core;
using Link = ResoniteLink;

namespace RLoop.ResoniteLink;

/// <summary>Explicit developer-tool entry point. Only reads metadata; never used by normal CLI validation.</summary>
public static class CatalogCapture
{
    public static async Task<CatalogSnapshot> ReadAsync(Uri url, IReadOnlyList<string> componentNames, CancellationToken ct)
    {
        if (componentNames.Count is 0 or > 512) throw new ArgumentException("Require 1..512 explicit full Component names.");
        using var link = new Link.LinkInterface();
        await link.Connect(url, ct).WaitAsync(ct);
        var session = await link.GetSessionData().WaitAsync(ct);
        if (!session.Success || string.IsNullOrWhiteSpace(session.ResoniteVersion) || string.IsNullOrWhiteSpace(session.ResoniteLinkVersion))
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Session versions could not be observed.", ExitCodes.ValidationFailed);
        var identity = new CatalogIdentity(session.ResoniteVersion, session.ResoniteLinkVersion, CatalogMapper.ClientPackageVersion, ApplyCatalog.CurrentMapperVersion, DateTimeOffset.UtcNow);
        var components = new List<Link.ComponentDefinition>(); var types = new List<Link.TypeDefinition>(); var syncs = new List<Link.SyncObjectDefinition>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal); var enums = new Dictionary<string, CatalogEnum>(StringComparer.Ordinal);
        var pending = new Queue<(string Name, bool Sync)>(); var seen = new HashSet<(string Name, bool Sync)>();
        void Enqueue(Link.TypeReference? type, bool sync = false)
        {
            if (type is null || type.IsGenericParameter) return;
            pending.Enqueue((ModelMapper.Render(type)!, sync));
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
            var response = await link.GetComponentDefinition(name, true).WaitAsync(ct);
            if (!response.Success || response.Definition?.Type.FullTypeName != name)
                throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", $"Exact Component '{name}' was not acquired.", ExitCodes.ValidationFailed);
            components.Add(response.Definition); seen.Add((name, false)); Parents(response.Definition.Type); Members(response.Definition.Members.Values);
        }
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (name, sync) = pending.Dequeue();
            if (!seen.Add((name, sync))) continue;
            if (seen.Count > 512) throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Acquisition exceeded 512 types; narrow the request.", ExitCodes.ValidationFailed);
            if (sync)
            {
                var response = await link.GetSyncObjectDefinition(new Link.GetSyncObjectDefinition { SyncObjectType = name, Flattened = true }).WaitAsync(ct);
                if (response.Success && response.Definition is { } definition && definition.Type.FullTypeName == name)
                { syncs.Add(definition); Parents(definition.Type); Members(definition.Members.Values); }
            }
            else
            {
                var response = await link.GetTypeDefinition(name).WaitAsync(ct);
                if (response.Success && response.Definition is { } definition && !string.IsNullOrWhiteSpace(definition.FullTypeName))
                {
                    types.Add(definition); Parents(definition);
                    if (definition.FullTypeName != name) aliases[name] = definition.FullTypeName;
                    if (definition.IsEnum)
                    {
                        var values = await link.GetEnumDefinition(definition.FullTypeName).WaitAsync(ct);
                        if (values.Success && values.Definition is { } data) enums[definition.FullTypeName] = new(data.Values, data.IsFlags);
                    }
                }
                // Unknown remains absent. Export/validation will not invent a closure.
            }
        }
        var after = await link.GetSessionData().WaitAsync(ct);
        if (!after.Success || after.ResoniteVersion != identity.ResoniteVersion || after.ResoniteLinkVersion != identity.ResoniteLinkVersion)
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Session identity changed during acquisition.", ExitCodes.ValidationFailed);
        var content = new CatalogSnapshotContent(components, types, syncs, aliases, enums);
        return new(identity, identity, "live", false, CatalogMapper.SnapshotHash(content), content);
    }
}

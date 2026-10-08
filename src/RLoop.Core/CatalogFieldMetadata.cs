namespace RLoop.Core;

internal sealed record CatalogFieldTargetEvidence(string Representation, string ValueType, string TargetType);

internal sealed record CatalogMemberEvidence(string Kind, string CatalogKind, bool Confirmed,
    string? MemberType, string? ValueType, string? TargetType, string? Scalar, string? FieldInterface,
    CatalogFieldTargetEvidence? FieldTarget);

/// <summary>Derives authoring hints from positive catalog evidence. Validation remains authoritative.</summary>
internal static class CatalogFieldMetadata
{
    private const string FieldInterfacePrefix = "[FrooxEngine]FrooxEngine.IField<";

    public static CatalogMemberEvidence Describe(ApplyCatalog catalog, CatalogType owner, CatalogMember member)
    {
        var kind = member.Kind switch { "field" => "field", "reference" => "reference", _ => "other" };
        var confirmed = owner.Confirmed && member.Confirmed;
        string? scalar = null;
        string? fieldInterface = null;
        CatalogFieldTargetEvidence? fieldTarget = null;

        if (confirmed && kind == "field")
        {
            var value = SupportedScalar(catalog, member.ValueType);
            scalar = value?.Representation;
            var sourceValue = catalog.Find(member.ValueType);
            if (sourceValue is not null && SupportedScalar(catalog, sourceValue.FullName) is not null &&
                FindFieldInterface(catalog, member.MemberType, sourceValue.FullName) is { } field)
                fieldInterface = field.Target.FullName;
        }
        else if (confirmed && kind == "reference" && (!owner.IsGeneric || owner.ClosureComplete) &&
            TryFieldInterface(catalog, member.TargetType) is { } target)
        {
            fieldTarget = new(target.ValueType.Representation, target.ValueType.FullName, target.Target.FullName);
        }

        return new(kind, member.Kind, confirmed, member.MemberType, member.ValueType, member.TargetType,
            scalar, fieldInterface, fieldTarget);
    }

    private static CatalogType? SupportedScalar(ApplyCatalog catalog, string? name)
    {
        var type = catalog.Find(name);
        if (type is null || type.IsGeneric && !type.ClosureComplete ||
            type.Representation is not ("boolean" or "int32" or "string" or "single")) return null;
        return type;
    }

    private static (CatalogType Target, CatalogType ValueType)? FindFieldInterface(ApplyCatalog catalog, string? start,
        string expectedValueType)
    {
        if (start is null) return null;
        var pending = new Queue<(CatalogType Type, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (catalog.Find(start) is { } source) pending.Enqueue((source, 0));
        while (pending.TryDequeue(out var item))
        {
            if (!visited.Add(item.Type.FullName)) continue;
            if (TryFieldInterface(catalog, item.Type.FullName) is { } field &&
                string.Equals(field.ValueType.FullName, expectedValueType, StringComparison.Ordinal)) return field;
            if (item.Depth >= 64) continue;
            if (item.Type.BaseType is { } baseName && catalog.Find(baseName) is { } baseType)
                pending.Enqueue((baseType, item.Depth + 1));
            foreach (var interfaceName in item.Type.Interfaces)
                if (catalog.Find(interfaceName) is { } interfaceType)
                    pending.Enqueue((interfaceType, item.Depth + 1));
        }
        return null;
    }

    private static (CatalogType Target, CatalogType ValueType)? TryFieldInterface(ApplyCatalog catalog, string? name)
    {
        var target = catalog.Find(name);
        if (target is null || !target.Confirmed || !target.ClosureComplete ||
            !TrySingleArgument(target.FullName, out var argument)) return null;
        var valueType = SupportedScalar(catalog, argument);
        return valueType is null ? null : (target, valueType);
    }

    private static bool TrySingleArgument(string name, out string argument)
    {
        argument = string.Empty;
        if (!name.StartsWith(FieldInterfacePrefix, StringComparison.Ordinal) || !name.EndsWith('>')) return false;
        var start = FieldInterfacePrefix.Length;
        var end = name.Length - 1;
        if (start >= end) return false;
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            switch (name[i])
            {
                case '<': case '[': depth++; break;
                case '>': case ']':
                    if (--depth < 0) return false;
                    break;
                case ',' when depth == 0: return false;
            }
        }
        if (depth != 0) return false;
        argument = name[start..end];
        return argument.Length != 0 && string.Equals(argument, argument.Trim(), StringComparison.Ordinal);
    }
}

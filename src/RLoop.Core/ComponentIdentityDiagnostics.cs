using System.Text.Json;

namespace RLoop.Core;

/// <summary>Conservative authoring warning, not a proof of runtime identity or permission to adopt.</summary>
public static class ComponentIdentityDiagnostics
{
    public static IReadOnlyList<ApplyValidationIssue> Analyze(ApplyDocument document,
        IReadOnlyDictionary<string, string>? resolvedTypes = null)
    {
        var issues = new List<ApplyValidationIssue>();
        Visit(document.Components, document.Children, "$");
        return issues;

        void Visit(IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children, string path)
        {
            foreach (var group in (components ?? []).GroupBy(c => Normalize(resolvedTypes?.GetValueOrDefault(c.Type) ?? c.Type)))
            {
                var candidates = group.ToArray();
                if (candidates.Length < 2) continue;
                var uncertain = candidates.Where(a => candidates.Any(b => !ReferenceEquals(a, b) && !Distinguishable(a, b))).ToArray();
                if (uncertain.Length == 0) continue;
                issues.Add(new("APPLY_COMPONENT_IDENTITY_RISK",
                    $"{uncertain.Length} same-type components [{string.Join(", ", uncertain.Take(8).Select(c => c.Key ?? "(no key)"))}{(uncertain.Length > 8 ? ", ..." : "")}] on one Slot lack distinct declared identity evidence. Keys alone do not identify live Components after reconnect/reordering. For new providers use separate named Slots (manifest scaffold --kind provider); alternatively declare immutable, distinct identityFields. This is a conservative warning; runtime reference topology may distinguish them. Existing checkpoints do not gain identity values merely by editing the manifest; inspect candidates and preserve state before explicit recovery.",
                    path + ".components", "warning"));
            }
            for (var i = 0; i < (children?.Count ?? 0); i++)
                if (children![i] is { } child) Visit(child.Components, child.Children, $"{path}.children[{i}]");
        }
    }

    private static string Normalize(string type)
    {
        var name = type.Trim();
        if (name.StartsWith('[') && name.IndexOf(']') is var end && end >= 0) name = name[(end + 1)..];
        return name.StartsWith("FrooxEngine.", StringComparison.Ordinal) ? name[12..] : name;
    }

    private static bool Distinguishable(ApplyComponentSpec left, ApplyComponentSpec right) =>
        (left.IdentityFields ?? []).Intersect(right.IdentityFields ?? [], StringComparer.Ordinal).Any(name =>
            Value(left, name) is { } a && Value(right, name) is { } b &&
            a.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null) &&
            b.ValueKind == a.ValueKind && !IsSelector(a) && !IsSelector(b) &&
            (a.ValueKind == JsonValueKind.Number ? Math.Abs(a.GetDouble() - b.GetDouble()) > .00001 * Math.Max(1, Math.Max(Math.Abs(a.GetDouble()), Math.Abs(b.GetDouble()))) : a.ToString() != b.ToString()));

    private static bool IsSelector(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString()!.StartsWith('$');
    private static JsonElement? Value(ApplyComponentSpec component, string name) =>
        component.Fields?.TryGetValue(name, out var value) == true ? value :
        component.InitialFields?.TryGetValue(name, out value) == true ? value : null;
}

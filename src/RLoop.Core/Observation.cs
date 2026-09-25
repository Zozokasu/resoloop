namespace RLoop.Core;

public sealed record ObservedMember(string ComponentId, string ComponentType, MemberValue Member);
public sealed record ObservationResult(int Count, int Components, IReadOnlyDictionary<string, ObservedMember> Values);

public sealed partial class WorldService
{
    /// <summary>One read-only, bounded observation; selected components are read once within this call.
    /// Values from different components are sequential observations, not an atomic world snapshot.</summary>
    public async Task<ObservationResult> ObserveAsync(IReadOnlyList<string> selectors, string stateFile,
        CancellationToken cancellationToken = default)
    {
        if (selectors.Count is < 1 or > 64)
            throw new RLoopException("OBSERVE_LIMIT", "observe requires 1..64 explicit $member:key.Name selectors.", ExitCodes.InvalidArguments);
        var parsed = selectors.Distinct(StringComparer.Ordinal).Select(selector => (Selector: selector, Syntax: StableSelectorSyntax.Parse(selector))).ToArray();
        if (parsed.Any(item => item.Syntax.Kind != "member"))
            throw new RLoopException("OBSERVE_SELECTOR_INVALID", "observe accepts only $member:key.Name selectors; use inspect for Slot structure.", ExitCodes.InvalidArguments);
        _ = RequireStateFile(stateFile, selectors[0]);
        var session = await client.GetSessionInfoAsync(cancellationToken);
        var cache = new Dictionary<string, ComponentInfo>(StringComparer.Ordinal);
        var values = new Dictionary<string, ObservedMember>(StringComparer.Ordinal);
        foreach (var group in parsed.GroupBy(item => item.Syntax.Key, StringComparer.Ordinal))
        {
            var resolved = await ResolveStableReferenceCoreAsync(stateFile, "$component:" + group.Key,
                session.UniqueSessionId, new HashSet<string>(StringComparer.Ordinal), cancellationToken, cache);
            var component = cache[resolved.Id];
            foreach (var item in group)
            {
                var member = component.Members.FirstOrDefault(pair => pair.Key.Equals(item.Syntax.MemberName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(member.Key))
                    throw new RLoopException("OBSERVE_MEMBER_NOT_FOUND", $"Member '{item.Syntax.MemberName}' was not found on '{item.Syntax.Key}'.",
                        ExitCodes.NotFound, suggestions: component.Members.Keys.Take(30).ToArray());
                values.Add(item.Selector, new(component.Id, component.Type, member.Value));
            }
        }
        return new(values.Count, values.Values.Select(value => value.ComponentId).Distinct(StringComparer.Ordinal).Count(), values);
    }
}

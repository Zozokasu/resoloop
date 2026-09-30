using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RLoop.Core;

/// <summary>Read-only bounded observation, projection, snapshot, and comparison. Never mutates the world.</summary>
public sealed class ObservationService(IResoniteClient client)
{
    private const int DefaultMaxSlots = 10000;
    private const int DefaultLimit = 100;

    internal static readonly string[] VolatileSlotMembers = ["GlobalPosition", "GlobalRotation", "GlobalScale"];

    internal static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true
    };

    public async Task<ObservationEnvelope<HierarchyProfile>> ProfileAsync(string selector, string resolvedId,
        int maxDepth = 64, int maxSlots = DefaultMaxSlots, string groupBy = "depth", bool excludeUserRoots = false,
        CancellationToken cancellationToken = default)
    {
        ValidateBudget(maxDepth, maxSlots);
        if (groupBy is not ("depth" or "component-type" or "name"))
            throw new RLoopException("INVALID_OPTION", "--group-by accepts depth, component-type, or name.", ExitCodes.InvalidArguments);

        var timer = Stopwatch.StartNew();
        ResetMetrics();
        var session = await client.GetSessionInfoAsync(cancellationToken);
        var observation = await ObserveAsync(selector, resolvedId, maxDepth, maxSlots, false, excludeUserRoots, cancellationToken);

        var depths = new Dictionary<int, int>();
        var componentTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalComponents = 0;
        var maxObservedDepth = 0;
        var maxSiblings = 0;
        string? maxSiblingParentId = null;
        string? maxSiblingParentPath = null;
        var referenceOnly = 0;
        var empty = 0;

        foreach (var node in observation.Nodes)
        {
            depths[node.Depth] = depths.GetValueOrDefault(node.Depth) + 1;
            maxObservedDepth = Math.Max(maxObservedDepth, node.Depth);
            if (node.Slot.IsReferenceOnly) referenceOnly++;
            if (node.Slot.Components.Count == 0) empty++;
            names[node.Slot.Name] = names.GetValueOrDefault(node.Slot.Name) + 1;
            // Sibling breadth is a property of the parent; only descended parents have a trustworthy child count.
            if (node.ChildrenObserved && node.Slot.Children.Count > maxSiblings)
            {
                maxSiblings = node.Slot.Children.Count;
                maxSiblingParentId = node.Slot.Id;
                maxSiblingParentPath = node.Path;
            }
            foreach (var component in node.Slot.Components)
            {
                totalComponents++;
                var type = NormalizeType(component.Type);
                componentTypes[type] = componentTypes.GetValueOrDefault(type) + 1;
            }
        }

        var groups = groupBy switch
        {
            "component-type" => Ranked(componentTypes),
            "name" => Ranked(names),
            _ => depths.OrderBy(pair => pair.Key).Select(pair => new HierarchyGroupCount(pair.Key.ToString(), pair.Value)).ToArray()
        };

        var profile = new HierarchyProfile(observation.Nodes.Count, totalComponents, maxObservedDepth,
            depths.OrderBy(pair => pair.Key).Select(pair => new HierarchyDepthCount(pair.Key, pair.Value)).ToArray(),
            groups, groupBy, maxSiblings, maxSiblingParentId, maxSiblingParentPath, referenceOnly, empty);

        return Envelope(session, observation, maxDepth, maxSlots,
            ["slot.id", "slot.path", "component.type"], profile, timer);
    }

    public async Task<ObservationEnvelope<HierarchyQueryResult>> QueryAsync(string selector, string resolvedId,
        HierarchyQueryFilter filter, IReadOnlyList<string> select, int limit = DefaultLimit, int maxDepth = 64,
        int maxSlots = DefaultMaxSlots, string? cursor = null, CancellationToken cancellationToken = default)
    {
        ValidateBudget(maxDepth, maxSlots);
        if (limit is < 1 or > 1000)
            throw new RLoopException("INVALID_OPTION", "--limit must be between 1 and 1000.", ExitCodes.InvalidArguments);

        var projection = ParseSelect(select);
        var regex = CompileRegex(filter.NameRegex);
        var timer = Stopwatch.StartNew();
        ResetMetrics();
        var session = await client.GetSessionInfoAsync(cancellationToken);

        var fingerprint = Fingerprint(resolvedId, filter, projection.Fields, maxDepth, maxSlots);
        // The cursor never asserts which connection it came from: the session id is a per-connection counter and
        // a generation only lives inside one process. Continuation is re-validated against the matched content instead.
        var continuation = cursor is null ? null : DecodeCursor(cursor, fingerprint);
        var offset = continuation?.Offset ?? 0;

        var observation = await ObserveAsync(selector, resolvedId, filter.DirectChildren ? 1 : maxDepth, maxSlots,
            projection.NeedsMembers || filter.MemberName is not null || filter.ReferenceToId is not null, filter.ExcludeUserRoots,
            cancellationToken);

        var matches = new List<HierarchyQueryMatch>();
        var matched = 0;
        using var skippedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var nextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var node in observation.Nodes)
        {
            if (node.Depth == 0) continue;
            if (filter.DirectChildren && node.Depth != 1) continue;
            if (!Matches(node.Slot, filter, regex)) continue;
            var identity = Encoding.UTF8.GetBytes(node.Slot.Id + "\u001f" + node.Path + "\n");
            if (matched++ < offset) { skippedHash.AppendData(identity); nextHash.AppendData(identity); continue; }
            if (matches.Count < limit) { matches.Add(Project(node, projection)); nextHash.AppendData(identity); }
        }

        if (continuation is not null &&
            (matched < offset || !StringComparer.Ordinal.Equals(Convert.ToHexStringLower(skippedHash.GetHashAndReset()), continuation.PrefixDigest)))
            throw new RLoopException("CURSOR_STALE",
                "The results before this cursor's position are no longer the same as when it was issued (different connection, world, or changed content), so continuing would skip or repeat rows.",
                ExitCodes.ValidationFailed, suggestions: ["Re-run the query without --cursor to start a fresh observation."]);

        var more = matched > offset + matches.Count;
        var truncation = observation.Truncation ?? (more
            ? new ObservationTruncation("result-limit", null, matches.Count)
            : null);
        if (more) truncation = truncation! with { Continuation = EncodeCursor(fingerprint, offset + matches.Count, Convert.ToHexStringLower(nextHash.GetHashAndReset())) };

        var result = new HierarchyQueryResult(matches, matches.Count, matched, observation.Nodes.Count, CacheUsable());
        return Envelope(session, observation with { Truncation = truncation }, filter.DirectChildren ? 1 : maxDepth,
            maxSlots, projection.Fields, result, timer);
    }

    public async Task<SnapshotDocument> CreateSnapshotAsync(string selector, string resolvedId, string outputPath,
        string memberScope = "references", IReadOnlyList<string>? selectedMembers = null, int maxDepth = 64,
        int maxSlots = DefaultMaxSlots, bool excludeUserRoots = false, CancellationToken cancellationToken = default)
    {
        ValidateBudget(maxDepth, maxSlots);
        if (memberScope is not ("references" or "selected" or "all"))
            throw new RLoopException("INVALID_OPTION", "--members accepts references, selected, or all.", ExitCodes.InvalidArguments);
        if (memberScope == "selected" && (selectedMembers is null || selectedMembers.Count == 0))
            throw new RLoopException("OPTION_REQUIRED", "--members selected requires at least one --member NAME.", ExitCodes.InvalidArguments);

        ResetMetrics();
        var session = await client.GetSessionInfoAsync(cancellationToken);
        // Every scope needs member data observed; the scope decides only what is kept.
        var observation = await ObserveAsync(selector, resolvedId, maxDepth, maxSlots, true, excludeUserRoots, cancellationToken);

        var excluded = new SortedSet<string>(StringComparer.Ordinal);
        var slots = observation.Nodes.Select(node =>
        {
            var slotMembers = CaptureMembers(node.Slot.Members, memberScope, selectedMembers, excluded, out var slotUnreadable);
            return new SnapshotSlot(
                node.Path, node.Slot.Name, node.ParentPath, node.Slot.Id, node.Slot.IsReferenceOnly, node.ChildrenObserved,
                node.Slot.Components
                    .GroupBy(component => NormalizeType(component.Type), StringComparer.Ordinal)
                    .SelectMany(group => group.Select((component, ordinal) =>
                    {
                        var members = CaptureMembers(component.Members, memberScope, selectedMembers, excluded, out var unreadable);
                        return new SnapshotComponent(group.Key, ordinal, component.Id, members,
                            component.Members is not null, unreadable.Count == 0 ? null : unreadable);
                    }))
                    .OrderBy(component => component.Type, StringComparer.Ordinal).ThenBy(component => component.Ordinal).ToArray(),
                slotMembers,
                // A reference-only Slot carries no component list, and a Slot without member data is unread, not empty.
                ComponentsObserved: !node.Slot.IsReferenceOnly,
                MembersObserved: !node.Slot.IsReferenceOnly && node.Slot.Members is not null,
                UnreadableMembers: slotUnreadable.Count == 0 ? null : slotUnreadable);
        }).ToArray();

        var document = new SnapshotDocument(SnapshotDocument.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            session.ResoniteVersion, session.ResoniteLinkVersion, session.UniqueSessionId,
            new ObservationRoot(selector, resolvedId, observation.RootPath), maxDepth, maxSlots,
            observation.Truncation is null, observation.Truncation, memberScope, [.. excluded], slots);

        CheckpointFiles.Write(outputPath, JsonSerializer.Serialize(document, SnapshotJson));
        return document;
    }

    public static SnapshotDiffResult Diff(string beforePath, string afterPath, bool changesOnly = false, string groupBy = "slot")
    {
        if (groupBy is not ("slot" or "component" or "member"))
            throw new RLoopException("INVALID_OPTION", "--group-by accepts slot, component, or member.", ExitCodes.InvalidArguments);
        var before = LoadSnapshot(beforePath);
        var after = LoadSnapshot(afterPath);

        var issues = new List<SnapshotDiffIssue>();
        if (!before.Complete || !after.Complete)
            issues.Add(new("SNAPSHOT_PARTIAL", "warning",
                "At least one snapshot is partial; removals are reported only where the newer snapshot observed the parent."));
        if (before.MemberScope != after.MemberScope)
            issues.Add(new("SNAPSHOT_MEMBER_SCOPE_MISMATCH", "warning",
                $"Snapshots captured different member scopes ('{before.MemberScope}' and '{after.MemberScope}'); member changes are not compared."));
        if (before.ConnectionId is not null && after.ConnectionId is not null && before.ConnectionId != after.ConnectionId)
            issues.Add(new("SNAPSHOT_CONNECTION_CHANGED", "warning",
                "Snapshots come from different ResoniteLink connections; raw ids and reference targets are not comparable."));

        var compareMembers = before.MemberScope == after.MemberScope;
        var beforeSlots = IndexByPath(before, beforePath);
        var afterSlots = IndexByPath(after, afterPath);
        var changes = new List<SnapshotChange>();
        var unobserved = new List<SnapshotUnobserved>();

        foreach (var slot in after.Slots)
        {
            if (!beforeSlots.TryGetValue(slot.Path, out var original))
            {
                if (Observed(beforeSlots, before, slot.ParentPath)) changes.Add(new("slot.created", slot.Path));
                else unobserved.Add(new(slot.Path, null, null, "slot-presence-unobserved"));
                continue;
            }
            if (!StringComparer.Ordinal.Equals(original.Name, slot.Name))
                changes.Add(new("slot.renamed", slot.Path, Before: JsonValue.Create(original.Name), After: JsonValue.Create(slot.Name)));
            if (!StringComparer.Ordinal.Equals(original.ParentPath, slot.ParentPath))
                changes.Add(new("slot.moved", slot.Path, Before: JsonValue.Create(original.ParentPath), After: JsonValue.Create(slot.ParentPath)));
            DiffComponents(changes, unobserved, original, slot, compareMembers);
            if (compareMembers)
            {
                if (original.MembersObserved && slot.MembersObserved && !original.IsReferenceOnly && !slot.IsReferenceOnly)
                    DiffMembers(changes, unobserved, slot.Path, null, original.Members, slot.Members, original.UnreadableMembers, slot.UnreadableMembers);
                else unobserved.Add(new(slot.Path, null, null, "members-not-observed"));
            }
        }

        foreach (var slot in before.Slots)
        {
            if (afterSlots.ContainsKey(slot.Path)) continue;
            // Absence is only evidence of removal where the newer snapshot actually descended into the parent.
            if (Observed(afterSlots, after, slot.ParentPath)) changes.Add(new("slot.removed", slot.Path));
            else unobserved.Add(new(slot.Path, null, null, "slot-presence-unobserved"));
        }

        var ordered = changes.OrderBy(change => change.Path, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.ComponentType ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(change => change.Member ?? string.Empty, StringComparer.Ordinal).ToArray();
        var groups = ordered
            .GroupBy(change => groupBy switch
            {
                "component" => change.ComponentType ?? "(slot)",
                "member" => change.Member ?? "(structure)",
                _ => change.Path
            }, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SnapshotChangeGroup(group.Key, group.ToArray())).ToArray();

        if (unobserved.Count > 0)
            issues.Add(new("SNAPSHOT_UNOBSERVED", "warning",
                $"{unobserved.Count} comparison(s) could not be made because one side did not observe the data; they are listed in unobserved and are not reported as changes or removals."));
        var listed = unobserved
            .OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.ComponentType ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Member ?? string.Empty, StringComparer.Ordinal).ThenBy(item => item.Reason, StringComparer.Ordinal)
            .Take(MaxListedUnobserved).ToArray();

        return new SnapshotDiffResult(before.Complete && after.Complete && unobserved.Count == 0, groupBy, ordered.Length,
            changesOnly && ordered.Length == 0 ? [] : groups, issues, unobserved.Count, listed.Length == 0 ? null : listed);
    }

    private const int MaxListedUnobserved = 200;

    private static bool Observed(IReadOnlyDictionary<string, SnapshotSlot> slots, SnapshotDocument document, string? parentPath)
    {
        if (document.Complete) return true;
        if (parentPath is null) return true;
        return slots.TryGetValue(parentPath, out var parent) && parent.ChildrenObserved;
    }

    private static void DiffComponents(List<SnapshotChange> changes, List<SnapshotUnobserved> unobserved,
        SnapshotSlot before, SnapshotSlot after, bool compareMembers)
    {
        // A reference-only Slot lists no components; comparing its empty list would read every component as created or removed.
        if (!before.ComponentsObserved || !after.ComponentsObserved || before.IsReferenceOnly || after.IsReferenceOnly)
        {
            unobserved.Add(new(after.Path, null, null, "components-not-observed"));
            return;
        }
        var beforeComponents = before.Components.ToDictionary(component => (component.Type, component.Ordinal));
        var afterComponents = after.Components.ToDictionary(component => (component.Type, component.Ordinal));
        foreach (var (key, component) in afterComponents)
        {
            if (!beforeComponents.TryGetValue(key, out var original))
            {
                changes.Add(new("component.created", after.Path, component.Type));
                continue;
            }
            if (!StringComparer.Ordinal.Equals(original.SessionId, component.SessionId))
                changes.Add(new("component.replaced", after.Path, component.Type,
                    Before: JsonValue.Create(original.SessionId), After: JsonValue.Create(component.SessionId)));
            if (!compareMembers) continue;
            if (original.MembersObserved && component.MembersObserved)
                DiffMembers(changes, unobserved, after.Path, component.Type, original.Members, component.Members,
                    original.UnreadableMembers, component.UnreadableMembers);
            else unobserved.Add(new(after.Path, component.Type, null, "members-not-observed"));
        }
        foreach (var (key, component) in beforeComponents)
            if (!afterComponents.ContainsKey(key))
                changes.Add(new("component.removed", after.Path, component.Type));
    }

    private static void DiffMembers(List<SnapshotChange> changes, List<SnapshotUnobserved> unobserved, string path, string? componentType,
        IReadOnlyDictionary<string, JsonNode?> before, IReadOnlyDictionary<string, JsonNode?> after,
        IReadOnlyList<string>? beforeUnreadable, IReadOnlyList<string>? afterUnreadable)
    {
        // A member that could not be read on either side says nothing about its value or existence.
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in beforeUnreadable ?? []) unreadable.Add(name);
        foreach (var name in afterUnreadable ?? []) unreadable.Add(name);
        foreach (var name in unreadable.Order(StringComparer.Ordinal))
            unobserved.Add(new(path, componentType, name, "member-unreadable"));

        foreach (var (name, value) in after)
        {
            if (unreadable.Contains(name)) continue;
            if (!before.TryGetValue(name, out var original)) { changes.Add(new("member.added", path, componentType, name, After: value?.DeepClone())); continue; }
            if (JsonEquivalent(original, value)) continue;
            var kind = IsReference(original) || IsReference(value) ? "reference.changed" : "member.changed";
            changes.Add(new(kind, path, componentType, name, original?.DeepClone(), value?.DeepClone()));
        }
        foreach (var (name, value) in before)
            if (!after.ContainsKey(name) && !unreadable.Contains(name))
                changes.Add(new("member.removed", path, componentType, name, Before: value?.DeepClone()));
    }

    private static bool IsReference(JsonNode? node) => node is JsonObject obj && obj.ContainsKey("targetId");

    private async Task<Observation> ObserveAsync(string selector, string resolvedId, int maxDepth, int maxSlots,
        bool includeMembers, bool excludeUserRoots, CancellationToken cancellationToken)
    {
        var root = await client.GetSlotAsync(resolvedId, maxDepth, includeMembers, cancellationToken);
        var rootPath = root.Path ?? root.Name;
        var nodes = new List<ObservedNode>();
        var queue = new Queue<(SlotInfo Slot, string Path, string? ParentPath, int Depth)>();
        queue.Enqueue((root, rootPath, null, 0));
        var budgetReached = false;
        var depthBoundary = false;
        var excludedPaths = new List<string>();
        var excludedParents = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            if (nodes.Count >= maxSlots) { budgetReached = true; break; }
            var (slot, path, parentPath, depth) = queue.Dequeue();
            // The transport cannot distinguish "no children" from "cut off at the requested depth",
            // so a Slot sitting exactly on the boundary makes completeness unprovable.
            var atBoundary = depth >= maxDepth;
            if (atBoundary) depthBoundary = true;
            nodes.Add(new ObservedNode(slot, path, parentPath, depth, !atBoundary));
            if (atBoundary) continue;
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in slot.Children)
            {
                var ordinal = ordinals.GetValueOrDefault(child.Name);
                ordinals[child.Name] = ordinal + 1;
                var childPath = path + "/" + Segment(child.Name, ordinal);
                // Only an explicit option drops a user's subtree; it is reported as excluded (unobserved), never as absent.
                if (excludeUserRoots && IsUserRoot(child))
                {
                    excludedPaths.Add(childPath);
                    excludedParents.Add(path);
                    continue;
                }
                queue.Enqueue((child, childPath, path, depth + 1));
            }
        }

        // A parent whose children are still queued was never descended into; claiming otherwise
        // would let a later diff read their absence as a removal.
        if (queue.Count > 0)
        {
            var pending = queue.Select(item => item.ParentPath).OfType<string>().ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < nodes.Count; index++)
                if (nodes[index].ChildrenObserved && pending.Contains(nodes[index].Path))
                    nodes[index] = nodes[index] with { ChildrenObserved = false };
        }

        if (excludedParents.Count > 0)
            for (var index = 0; index < nodes.Count; index++)
                if (nodes[index].ChildrenObserved && excludedParents.Contains(nodes[index].Path))
                    nodes[index] = nodes[index] with { ChildrenObserved = false };

        var reason = budgetReached ? "max-slots" : depthBoundary ? "depth-boundary" : excludedPaths.Count > 0 ? "user-root-excluded" : null;
        var truncation = reason is null ? null : new ObservationTruncation(reason, null, nodes.Count,
            excludedPaths.Count == 0 ? null : excludedPaths.Count, excludedPaths.Count == 0 ? null : excludedPaths.Take(MaxListedUnobserved).ToArray());
        return new Observation(nodes, rootPath, selector, resolvedId, truncation);
    }

    private static bool IsUserRoot(SlotInfo slot) =>
        slot.Components.Any(component => NormalizeType(component.Type) is "UserRoot" or "FrooxEngine.UserRoot");

    private ObservationEnvelope<T> Envelope<T>(SessionInfo session, Observation observation, int depth, int maxSlots,
        IReadOnlyList<string> fieldMask, T data, Stopwatch timer)
    {
        var metrics = client is IResoniteClientDiagnostics diagnostics ? diagnostics.SnapshotMetrics() : new ClientMetrics(0, 0, 0, []);
        return new ObservationEnvelope<T>(ObservationEnvelope<T>.CurrentSchemaVersion, session.ResoniteVersion,
            session.ResoniteLinkVersion, session.UniqueSessionId, DateTimeOffset.UtcNow,
            new ObservationRoot(observation.Selector, observation.ResolvedId, observation.RootPath), depth, maxSlots,
            fieldMask, observation.Truncation is null, observation.Truncation, metrics.Requests,
            timer.Elapsed.TotalMilliseconds, JsonSerializer.SerializeToUtf8Bytes(data, SnapshotJson).LongLength, data);
    }

    private static HierarchyQueryMatch Project(ObservedNode node, Projection projection)
    {
        var components = projection.NeedsComponents
            ? node.Slot.Components.Select(component => new HierarchyQueryComponent(
                projection.Has("component.id") ? component.Id : null,
                projection.Has("component.type") ? NormalizeType(component.Type) : null,
                projection.Members.Count == 0 ? null : projection.Members
                    .Where(name => component.Members?.ContainsKey(name) == true)
                    .ToDictionary(name => name, name => MemberNode(component.Members![name]), StringComparer.Ordinal)))
                .Where(component => component.Id is not null || component.Type is not null || component.Members?.Count > 0)
                .ToArray()
            : null;
        return new HierarchyQueryMatch(node.Slot.Id, node.Path,
            projection.Has("slot.name") ? node.Slot.Name : null,
            components is { Length: 0 } ? null : components);
    }

    private static bool Matches(SlotInfo slot, HierarchyQueryFilter filter, Regex? regex)
    {
        if (filter.Name is not null && !slot.Name.Contains(filter.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (regex is not null && !regex.IsMatch(slot.Name)) return false;
        if (filter.ComponentType is not null &&
            !slot.Components.Any(component => NormalizeType(component.Type).Contains(filter.ComponentType, StringComparison.OrdinalIgnoreCase))) return false;
        if (filter.MemberName is not null &&
            !slot.Components.Any(component => component.Members?.ContainsKey(filter.MemberName) == true)) return false;
        if (filter.ReferenceToId is not null && !ReferencesTarget(slot, filter.ReferenceToId)) return false;
        return true;
    }

    private static bool ReferencesTarget(SlotInfo slot, string targetId) =>
        slot.Components.Any(component => component.Members?.Values.Any(member => TargetsId(member, targetId)) == true) ||
        slot.Members?.Values.Any(member => TargetsId(member, targetId)) == true;

    private static bool TargetsId(MemberValue member, string targetId) =>
        StringComparer.Ordinal.Equals(member.TargetId, targetId) ||
        member.Members?.Values.Any(child => TargetsId(child, targetId)) == true ||
        member.Elements?.Any(element => TargetsId(element, targetId)) == true;

    private static IReadOnlyDictionary<string, JsonNode?> CaptureMembers(IReadOnlyDictionary<string, MemberValue>? members,
        string scope, IReadOnlyList<string>? selected, SortedSet<string> excluded, out List<string> unreadable)
    {
        var captured = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        unreadable = [];
        if (members is null) return captured;
        foreach (var (name, member) in members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (VolatileSlotMembers.Contains(name, StringComparer.Ordinal)) { excluded.Add(name); continue; }
            // A listed member with no readable value is unknown; recording it as null would diff as a value change.
            if (member is null) { unreadable.Add(name); continue; }
            var keep = scope switch
            {
                "all" => true,
                "selected" => selected!.Contains(name, StringComparer.Ordinal),
                _ => member.TargetId is not null
            };
            if (keep) captured[name] = MemberNode(member);
            else excluded.Add(name);
        }
        return captured;
    }

    private static JsonNode? MemberNode(MemberValue member)
    {
        if (member.TargetId is not null)
            return new JsonObject { ["targetId"] = member.TargetId, ["targetType"] = member.TargetType };
        if (member.Members is { Count: > 0 })
        {
            var nested = new JsonObject();
            foreach (var (name, value) in member.Members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                nested[name] = MemberNode(value);
            return nested;
        }
        if (member.Elements is { Count: > 0 })
            return new JsonArray(member.Elements.Select(MemberNode).ToArray());
        return Canonical(member.Value);
    }

    /// <summary>Property order and float formatting differ between observations of identical state.</summary>
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Aggregate(new JsonObject(), (result, pair) => { result[pair.Key] = Canonical(pair.Value); return result; }),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        JsonValue value => CanonicalValue(value),
        _ => null
    };

    private static JsonNode? CanonicalValue(JsonValue value) =>
        value.TryGetValue<double>(out var number) && double.IsFinite(number)
            ? JsonValue.Create(Math.Round(number, 5))
            : value.DeepClone();

    private static bool JsonEquivalent(JsonNode? left, JsonNode? right) =>
        (left is null && right is null) || (left is not null && right is not null &&
            left.ToJsonString(SnapshotJson) == right.ToJsonString(SnapshotJson));

    private static SnapshotDocument LoadSnapshot(string path)
    {
        if (!File.Exists(path))
            throw new RLoopException("SNAPSHOT_NOT_FOUND", $"Snapshot file '{path}' was not found.", ExitCodes.NotFound);
        SnapshotDocument? document;
        try { document = JsonSerializer.Deserialize<SnapshotDocument>(CheckpointFiles.Read(path), SnapshotJson); }
        catch (JsonException ex)
        {
            throw new RLoopException("SNAPSHOT_INVALID", $"Snapshot file '{path}' is not valid snapshot JSON.",
                ExitCodes.ValidationFailed, innerException: ex);
        }
        if (document is null)
            throw new RLoopException("SNAPSHOT_INVALID", $"Snapshot file '{path}' is empty.", ExitCodes.ValidationFailed);
        if (document.SchemaVersion != SnapshotDocument.CurrentSchemaVersion)
            throw new RLoopException("SNAPSHOT_SCHEMA_UNSUPPORTED",
                $"Snapshot '{path}' uses schema {document.SchemaVersion}; this build reads schema {SnapshotDocument.CurrentSchemaVersion}.",
                ExitCodes.ValidationFailed, suggestions: ["Recreate the snapshot with resoloop snapshot create."]);
        return document;
    }

    private static Projection ParseSelect(IReadOnlyList<string> select)
    {
        var fields = select.Count == 0 ? ["slot.id", "slot.path"] : select;
        var members = new List<string>();
        foreach (var field in fields)
        {
            if (field.StartsWith("member.", StringComparison.OrdinalIgnoreCase)) { members.Add(field["member.".Length..]); continue; }
            if (field is not ("slot.id" or "slot.path" or "slot.name" or "component.id" or "component.type"))
                throw new RLoopException("INVALID_OPTION",
                    $"--select does not support '{field}'.", ExitCodes.InvalidArguments,
                    suggestions: ["Use slot.id, slot.path, slot.name, component.id, component.type, or member.NAME."]);
        }
        return new Projection(fields, members);
    }

    private static Regex? CompileRegex(string? pattern)
    {
        if (pattern is null) return null;
        try { return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex)
        {
            throw new RLoopException("INVALID_OPTION", $"--name-regex is not a valid regular expression: {ex.Message}",
                ExitCodes.InvalidArguments, innerException: ex);
        }
    }

    private static void ValidateBudget(int maxDepth, int maxSlots)
    {
        if (maxDepth is < 0 or > 64)
            throw new RLoopException("INVALID_OPTION", "--max-depth must be between 0 and 64.", ExitCodes.InvalidArguments);
        if (maxSlots is < 1 or > 100000)
            throw new RLoopException("INVALID_OPTION", "--max-slots must be between 1 and 100000.", ExitCodes.InvalidArguments);
    }

    private static HierarchyGroupCount[] Ranked(Dictionary<string, int> counts) => counts
        .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new HierarchyGroupCount(pair.Key, pair.Value)).ToArray();

    private void ResetMetrics()
    {
        if (client is IResoniteClientDiagnostics diagnostics) diagnostics.ResetMetrics();
    }

    private bool CacheUsable() => client is IResoniteClientDiagnostics diagnostics && diagnostics.SnapshotMetrics().CacheHits > 0;

    internal static string Fingerprint(string resolvedId, HierarchyQueryFilter filter, IReadOnlyList<string> select,
        int maxDepth, int maxSlots)
    {
        var material = string.Join('\u001f', resolvedId, filter.Name, filter.NameRegex, filter.ComponentType,
            filter.MemberName, filter.ReferenceToId, filter.DirectChildren, filter.ExcludeUserRoots, string.Join(',', select), maxDepth, maxSlots);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
    }

    private sealed record CursorState(int Offset, string PrefixDigest);

    private static string EncodeCursor(string fingerprint, int offset, string prefixDigest) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"v2\u001f{fingerprint}\u001f{offset}\u001f{prefixDigest}"));

    private static CursorState DecodeCursor(string cursor, string fingerprint)
    {
        string[] parts;
        try { parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\u001f'); }
        catch (FormatException ex)
        {
            throw new RLoopException("CURSOR_INVALID", "--cursor is not a cursor issued by this command.",
                ExitCodes.InvalidArguments, innerException: ex);
        }
        if (parts.Length != 4 || parts[0] != "v2" || !int.TryParse(parts[2], out var offset) || offset < 0 || parts[3].Length == 0)
            throw new RLoopException("CURSOR_INVALID", "--cursor is not a cursor issued by this command.", ExitCodes.InvalidArguments);
        if (!StringComparer.Ordinal.Equals(parts[1], fingerprint))
            throw new RLoopException("CURSOR_QUERY_MISMATCH",
                "This cursor was issued for a different query; filters, projection, and budgets must match to continue.",
                ExitCodes.ValidationFailed, suggestions: ["Re-run the query without --cursor, or restore the original options."]);
        return new CursorState(offset, parts[3]);
    }

    private static string NormalizeType(string value)
    {
        var bracket = value.IndexOf(']');
        return bracket >= 0 ? value[(bracket + 1)..] : value;
    }

    /// <summary>Slot names may repeat between siblings and may contain separators, so a display path alone
    /// cannot identify a Slot across two observations. Escaping plus a sibling index keeps the key unique.</summary>
    private static string Segment(string name, int ordinal)
    {
        var escaped = name.Contains('%') || name.Contains('/') || name.Contains('[')
            ? name.Replace("%", "%25", StringComparison.Ordinal)
                .Replace("/", "%2F", StringComparison.Ordinal)
                .Replace("[", "%5B", StringComparison.Ordinal)
            : name;
        return ordinal == 0 ? escaped : $"{escaped}[{ordinal}]";
    }

    private static Dictionary<string, SnapshotSlot> IndexByPath(SnapshotDocument document, string path)
    {
        var index = new Dictionary<string, SnapshotSlot>(StringComparer.Ordinal);
        foreach (var slot in document.Slots)
            if (!index.TryAdd(slot.Path, slot))
                throw new RLoopException("SNAPSHOT_PATH_AMBIGUOUS",
                    $"Snapshot '{path}' contains more than one Slot at '{slot.Path}', so changes cannot be attributed.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["path"] = slot.Path },
                    ["Recreate the snapshot with resoloop snapshot create."]);
        return index;
    }

    private sealed record ObservedNode(SlotInfo Slot, string Path, string? ParentPath, int Depth, bool ChildrenObserved);

    private sealed record Observation(IReadOnlyList<ObservedNode> Nodes, string RootPath, string Selector,
        string ResolvedId, ObservationTruncation? Truncation);

    private sealed record Projection(IReadOnlyList<string> Fields, IReadOnlyList<string> Members)
    {
        public bool Has(string field) => Fields.Contains(field, StringComparer.OrdinalIgnoreCase);
        public bool NeedsComponents => Has("component.id") || Has("component.type") || Members.Count > 0;
        public bool NeedsMembers => Members.Count > 0;
    }
}

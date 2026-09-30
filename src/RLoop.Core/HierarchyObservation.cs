using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record ObservationRoot(string Selector, string ResolvedId, string Path);

/// <summary>Carries the freshness and completeness of one bounded observation. Truncated results never report complete.</summary>
public sealed record ObservationEnvelope<T>(
    int SchemaVersion,
    string? ResoniteVersion,
    string? ResoniteLinkVersion,
    [property: JsonIgnore] string? SessionId,
    DateTimeOffset ObservedAtUtc,
    ObservationRoot Root,
    int Depth,
    int MaxSlots,
    IReadOnlyList<string> FieldMask,
    bool Complete,
    ObservationTruncation? Truncation,
    int Requests,
    double ElapsedMs,
    long ResponseByteEstimate,
    T Data)
{
    public const int CurrentSchemaVersion = 1;

    public string? ConnectionId => SessionId;
    public string ConnectionIdScope => "ResoniteLink connection; stable keys and paths are used across connections";
}

/// <summary>Why an observation is not the whole requested subtree. <c>ExcludedUserRoots</c> counts Slots left out by the
/// explicit --exclude-user-roots option; an excluded Slot is unobserved, never absent. <c>ReferenceOnlySlots</c> counts Slots
/// the transport returned without components or children: their children (and whether they hold a UserRoot) are unknown.</summary>
public sealed record ObservationTruncation(string Reason, string? Continuation, int Observed,
    int? ExcludedUserRoots = null, IReadOnlyList<string>? ExcludedPaths = null,
    int? ReferenceOnlySlots = null, IReadOnlyList<string>? ReferenceOnlyPaths = null);

public sealed record HierarchyProfile(
    int TotalSlots,
    int TotalComponents,
    int MaxObservedDepth,
    IReadOnlyList<HierarchyDepthCount> SlotsPerDepth,
    IReadOnlyList<HierarchyGroupCount> Groups,
    string GroupBy,
    int MaxSiblingCount,
    string? MaxSiblingParentId,
    string? MaxSiblingParentPath,
    int ReferenceOnlySlots,
    int EmptySlots);

public sealed record HierarchyDepthCount(int Depth, int Slots);
public sealed record HierarchyGroupCount(string Key, int Count);

public sealed record HierarchyQueryFilter(
    string? Name = null,
    string? NameRegex = null,
    string? ComponentType = null,
    string? MemberName = null,
    string? ReferenceToId = null,
    bool DirectChildren = false,
    bool ExcludeUserRoots = false);

public sealed record HierarchyQueryMatch(
    string SlotId,
    string SlotPath,
    string? SlotName,
    IReadOnlyList<HierarchyQueryComponent>? Components);

public sealed record HierarchyQueryComponent(
    string? Id,
    string? Type,
    IReadOnlyDictionary<string, JsonNode?>? Members);

public sealed record HierarchyQueryResult(
    IReadOnlyList<HierarchyQueryMatch> Matches,
    int Returned,
    int Matched,
    int Traversed,
    bool CacheUsable);

/// <summary>Normalized comparison IR. Identity is the stable path; session ids are recorded but never compared.</summary>
public sealed record SnapshotDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    string? ResoniteVersion,
    string? ResoniteLinkVersion,
    string? ConnectionId,
    ObservationRoot Root,
    int Depth,
    int MaxSlots,
    bool Complete,
    ObservationTruncation? Truncation,
    string MemberScope,
    IReadOnlyList<string> ExcludedMembers,
    IReadOnlyList<SnapshotSlot> Slots,
    IReadOnlyList<string>? SelectedMembers = null)
{
    public const int CurrentSchemaVersion = 1;

    public string ConnectionIdScope => "informational only: the session id is a per-connection sequence number, not proof that two snapshots share a connection; stable keys and paths are used across connections";
}

public sealed record SnapshotSlot(
    string Path,
    string Name,
    string? ParentPath,
    string SessionId,
    bool IsReferenceOnly,
    bool ChildrenObserved,
    IReadOnlyList<SnapshotComponent> Components,
    IReadOnlyDictionary<string, JsonNode?> Members,
    bool ComponentsObserved = false,
    bool MembersObserved = false,
    IReadOnlyList<string>? UnreadableMembers = null);

/// <summary>MembersObserved=false means member data was not returned, which is different from a component that has no members.
/// UnreadableMembers lists names that were present but could not be read; neither is comparable in a diff.
/// The observed flags default to false so a snapshot written before they existed reads as unknown, never as observed.</summary>
public sealed record SnapshotComponent(
    string Type,
    int Ordinal,
    string SessionId,
    IReadOnlyDictionary<string, JsonNode?> Members,
    bool MembersObserved = false,
    IReadOnlyList<string>? UnreadableMembers = null);

public sealed record SnapshotChange(
    string Kind,
    string Path,
    string? ComponentType = null,
    string? Member = null,
    JsonNode? Before = null,
    JsonNode? After = null);

public sealed record SnapshotDiffIssue(string Code, string Severity, string Message);

/// <summary>A comparison the diff could not make. Reported instead of guessing a removal, addition, or value change.</summary>
public sealed record SnapshotUnobserved(string Path, string? ComponentType, string? Member, string Reason);

public sealed record SnapshotDiffResult(
    bool Complete,
    string GroupBy,
    int Changes,
    IReadOnlyList<SnapshotChangeGroup> Groups,
    IReadOnlyList<SnapshotDiffIssue> Issues,
    int UnobservedCount = 0,
    IReadOnlyList<SnapshotUnobserved>? Unobserved = null);

public sealed record SnapshotChangeGroup(string Key, IReadOnlyList<SnapshotChange> Changes);

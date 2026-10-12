using System.Text.Json.Nodes;

namespace RLoop.Core;

public enum CreationEvidenceCompleteness { Complete, Partial, Unknown }

public sealed record CreationOwnershipEvidence(
    CreationEvidenceCompleteness Completeness,
    string SessionStatus,
    string ExactIdStatus,
    string ParentStatus,
    string StampStatus,
    string SiblingUniquenessStatus,
    string DescendantBoundaryStatus,
    IReadOnlyList<string> ObservedSlotIds);

public sealed record CreationOwnershipOptions(
    int MaxSlots = 10_000,
    int MaxDepth = 64,
    int MaxComponents = 10_000,
    int MaxQueuedSlots = 10_000,
    TimeSpan? MaxDuration = null);

/// <summary>Read-only, bounded verification of the current creation root's ownership stamp.</summary>
public sealed class CreationOwnershipVerifier
{
    public const string VariableName = "_resoloop_creation_stamp";
    public const string VariableComponentType = "[FrooxEngine]FrooxEngine.DynamicValueVariable<string>";
    internal static readonly TimeSpan ProductionDuration = TimeSpan.FromSeconds(10);

    private readonly IResoniteClient _client;
    private readonly IApplyDeletionObservation? _observer;
    private readonly CreationOwnershipOptions _options;

    public CreationOwnershipVerifier(IResoniteClient client, CreationOwnershipOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _observer = client as IApplyDeletionObservation;
        var requested = options ?? new CreationOwnershipOptions();
        if (requested.MaxSlots < 0 || requested.MaxDepth < 0 || requested.MaxComponents < 0 ||
            requested.MaxQueuedSlots < 0 || requested.MaxDuration is { } duration && duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Ownership observation budgets cannot be negative.");
        _options = new CreationOwnershipOptions(
            Math.Min(requested.MaxSlots, 10_000),
            Math.Min(requested.MaxDepth, 64),
            Math.Min(requested.MaxComponents, 10_000),
            Math.Min(requested.MaxQueuedSlots, 10_000),
            requested.MaxDuration is { } maxDuration && maxDuration < ProductionDuration ? maxDuration : ProductionDuration);
    }

    public Task<CreationOwnershipEvidence> VerifyAsync(CreationState state, CancellationToken cancellationToken = default) =>
        VerifyAsync(state, CreateContext(cancellationToken));

    internal async Task<CreationOwnershipEvidence> VerifyAsync(CreationState state, CreationObservationContext run)
    {
        if (state?.ActiveRoot is not { } root)
            throw StateRequired("Creation state must contain an activeRoot before ownership can be verified.");
        if (_options.MaxQueuedSlots == 0)
            throw ObservationIncomplete("Creation ownership queue limit is too small to schedule the active root.");
        ValidateStateSession(state);
        var expectedStamp = ValidateStateStamp(state, root);
        var initialSession = await VerifySessionAsync(state.Session, run, expectedSession: null);
        await VerifyStampMetadataAsync(run);

        if (root.Id.Equals("Root", StringComparison.OrdinalIgnoreCase) || root.Id == root.ParentId)
            throw OwnerUnproven("activeRoot.id is Root or forms a parent cycle.");

        var parent = await run.ReadSlotAsync(root.ParentId, expectedRoot: false);
        var siblingIds = DirectChildIds(parent, root.ParentId, _options.MaxQueuedSlots);
        if (siblingIds.Count(id => id == root.Id) != 1)
            throw OwnerUnproven("activeRoot.id was not observed exactly once as a direct child of its recorded parent.");
        run.EnsureSlotReadCapacity(siblingIds.Count);

        var active = await run.ReadSlotAsync(root.Id, expectedRoot: true);
        if (active.Id != root.Id || active.ParentId != root.ParentId)
            throw OwnerUnproven("The exact active root ID or parent does not match creation state.");
        if (active.IsReferenceOnly) throw ObservationIncomplete("The active root observation is reference-only.");

        var activeMarkers = await ReadMarkersAsync(active, run);
        if (activeMarkers.Count != 1) throw OwnerUnproven("The active root must contain exactly one creation stamp component.");
        VerifyStampValue(activeMarkers[0], root.Stamp, expectedStamp, "active root");

        var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        foreach (var siblingId in siblingIds)
        {
            run.CheckBudget();
            if (siblingId.Equals("Root", StringComparison.OrdinalIgnoreCase))
                throw OwnerUnproven("Root appeared as a child in the recorded parent observation.");
            if (siblingId == root.Id) continue;
            var sibling = await run.ReadSlotAsync(siblingId, expectedRoot: false);
            if (sibling.Id != siblingId || sibling.ParentId != root.ParentId)
                throw ObservationIncomplete("A parent child could not be confirmed by exact ID and ParentId.");
            var markers = await ReadMarkersAsync(sibling, run);
            foreach (var marker in markers)
                if (marker.Stamp.CreationId == state.CreationId && marker.Stamp.GenerationId == root.GenerationId)
                    throw OwnerAmbiguous("A second root with the same creationId and generationId exists below the recorded parent.");
        }

        var queue = new Queue<(SlotInfo Slot, int Depth)>();
        queue.Enqueue((active, 0));
        while (queue.TryDequeue(out var item))
        {
            run.CheckBudget();
            if (item.Depth > _options.MaxDepth)
                throw ObservationIncomplete("Creation ownership depth limit was reached.");

            if (item.Depth > 0)
            {
                var markers = await ReadMarkersAsync(item.Slot, run);
                foreach (var marker in markers)
                {
                    if (marker.Stamp.CreationId == state.CreationId && marker.Stamp.GenerationId == root.GenerationId)
                        throw OwnerAmbiguous("A matching generation stamp also exists below the active root.");
                    if (marker.Stamp.CreationId != state.CreationId)
                        throw OwnerUnproven("A different creation owns a stamp below the active root.");
                }
            }

            var children = DirectChildIds(item.Slot, item.Slot.Id, _options.MaxQueuedSlots);
            run.EnsureQueueCapacity(queue.Count, children.Count);
            for (var index = 0; index < children.Count; index++)
            {
                var childId = children[index];
                if (childId.Equals("Root", StringComparison.OrdinalIgnoreCase))
                    throw OwnerUnproven("Root appeared inside the active root subtree.");
                if (!seen.Add(childId))
                    throw OwnerUnproven("The active root subtree contains a duplicate Slot ID or cycle.");
                var childDepth = item.Depth + 1;
                if (childDepth > _options.MaxDepth)
                    throw ObservationIncomplete("Creation ownership depth limit was reached.");
                var child = await run.ReadSlotAsync(childId, expectedRoot: false);
                if (child.Id != childId || child.ParentId != item.Slot.Id)
                    throw ObservationIncomplete("A descendant could not be confirmed by exact ID and ParentId.");
                queue.Enqueue((child, childDepth));
            }
        }

        await VerifySessionAsync(state.Session, run, initialSession);
        return new(CreationEvidenceCompleteness.Complete, "verified", "exact", "verified", "verified",
            "unique", "bounded-and-closed", run.ReadSlotIds.ToArray());
    }

    public Task<CreationOwnershipEvidence> ObserveInitialParentAsync(string parentId, string creationId,
        CancellationToken cancellationToken = default) =>
        ObserveInitialParentAsync(parentId, creationId, CreateContext(cancellationToken));

    internal async Task<CreationOwnershipEvidence> ObserveInitialParentAsync(string parentId, string creationId,
        CreationObservationContext run)
    {
        ValidateCreationId(creationId);
        if (string.IsNullOrWhiteSpace(parentId)) throw ObservationIncomplete("parentId is required for initial ownership observation.");
        var initialSession = await VerifySessionAsync(null, run, null);
        await VerifyStampMetadataAsync(run);
        var parent = await run.ReadSlotAsync(parentId, expectedRoot: false);
        if (parent.Id != parentId) throw ObservationIncomplete("The requested parent was not returned by exact ID.");
        var childIds = DirectChildIds(parent, parentId, _options.MaxQueuedSlots);
        run.EnsureSlotReadCapacity(childIds.Count);
        foreach (var childId in childIds)
        {
            if (childId.Equals("Root", StringComparison.OrdinalIgnoreCase))
                throw OwnerUnproven("Root appeared as a child in the parent observation.");
            var child = await run.ReadSlotAsync(childId, expectedRoot: false);
            if (child.Id != childId || child.ParentId != parentId)
                throw ObservationIncomplete("A parent child could not be confirmed by exact ID and ParentId.");
            foreach (var marker in await ReadMarkersAsync(child, run))
                if (marker.Stamp.CreationId == creationId)
                    throw StateRequired("An existing direct child carries this creationId; load its creation state before continuing.");
        }

        await VerifySessionAsync(null, run, initialSession);
        return new(CreationEvidenceCompleteness.Complete, "verified", "not-applicable", "verified",
            "complete-no-match", "no-matching-stamp", "not-observed", run.ReadSlotIds.ToArray());
    }

    private static void ValidateStateSession(CreationState state)
    {
        if (state.Session is null || state.Session.IdentityStatus != "matched" ||
            !IsSessionId(state.Session.DiscoverSessionId))
            throw SessionUnproven("Creation state does not contain a matched, valid discovered session identity.");
    }

    private static CreationStamp ValidateStateStamp(CreationState state, CreationActiveRoot root)
    {
        if (!IsLowerHex(state.OwnerNonce) || !IsLowerHex(root.GenerationId))
            throw OwnerUnproven("Creation state does not contain valid ownerNonce or generation evidence.");
        if (!CreationStamp.TryParse(root.Stamp, out var stamp) || stamp is null ||
            stamp.CreationId != state.CreationId || stamp.OwnerNonce != state.OwnerNonce || stamp.GenerationId != root.GenerationId)
            throw OwnerUnproven("activeRoot.stamp does not match the creation state ownership fields.");
        return stamp;
    }

    internal async Task<CreationSessionProof> VerifySessionAsync(CreationSession? expectedState, CreationObservationContext run, CreationSessionProof? expectedSession)
    {
        run.CheckBudget();
        SessionInfo session;
        try { session = await run.AwaitAsync(token => _client.GetSessionInfoAsync(token)); }
        catch (OperationCanceledException) { throw; }
        catch (RLoopException ex) when (ex.ExitCode == ExitCodes.Timeout) { throw; }
        catch (RLoopException ex) when (ex.Code == "CREATION_OBSERVATION_INCOMPLETE") { throw; }
        catch (TimeoutException) { throw; }
        catch (Exception ex) { throw SessionUnproven("Could not read the connected session: " + ex.Message); }
        run.CheckBudget();

        if (!session.Connected || _client is not IApplySessionObservation observer)
            throw SessionUnproven("Connected session identity is unavailable.");
        ApplySessionObservation identity;
        try { identity = observer.ObserveApplySession(); }
        catch (OperationCanceledException) { throw; }
        catch (RLoopException ex) when (ex.ExitCode == ExitCodes.Timeout) { throw; }
        catch (TimeoutException) { throw; }
        catch (Exception ex) { throw SessionUnproven("Could not read the connection's discovered session identity: " + ex.Message); }
        string normalizedUrl;
        try { normalizedUrl = ApplySessionObservation.NormalizeUrl(session.Url); }
        catch (Exception ex) { throw SessionUnproven("Connected session URL is invalid: " + ex.Message); }
        if (identity.IdentityStatus != "matched" || !IsSessionId(identity.DiscoverSessionId) ||
            identity.NormalizedUrl != normalizedUrl || expectedState is { } state &&
            (state.IdentityStatus != "matched" || state.NormalizedUrl != normalizedUrl || state.DiscoverSessionId != identity.DiscoverSessionId))
            throw SessionUnproven("The announced session does not exactly match the connected session and creation state.");

        ApplyConnectionObservation? connection = null;
        if (_client is IApplyConnectionGuard guard)
        {
            try { connection = guard.ObserveApplyConnection(); }
            catch (OperationCanceledException) { throw; }
            catch (RLoopException ex) when (ex.ExitCode == ExitCodes.Timeout) { throw; }
            catch (TimeoutException) { throw; }
            catch (Exception ex) { throw SessionUnproven("Could not read the connection generation: " + ex.Message); }
            if (!connection.Connected) throw SessionUnproven("The guarded Resonite connection is not connected.");
        }
        run.CheckBudget();
        var proof = new CreationSessionProof(normalizedUrl, identity.DiscoverSessionId!, connection?.Generation);
        if (expectedSession is not null && proof != expectedSession)
            throw SessionUnproven("Session identity or connection generation changed during ownership observation.");
        return proof;
    }

    internal static async Task VerifyStampMetadataAsync(CreationObservationContext run)
    {
        run.CheckBudget();
        ComponentTypeInfo metadata;
        try { metadata = await run.AwaitAsync(token => run.Client.DescribeComponentTypeAsync(VariableComponentType, token)); }
        catch (OperationCanceledException) { throw; }
        catch (RLoopException ex) when (ex.ExitCode == ExitCodes.Timeout) { throw; }
        catch (TimeoutException) { throw; }
        catch (Exception ex) { throw ObservationIncomplete("Could not read runtime Reflection metadata for the creation stamp type: " + ex.Message); }
        run.CheckBudget();
        if (metadata is null || metadata.Members is null || metadata.FullTypeName != VariableComponentType ||
            !metadata.Members.Any(member => member is not null && member.Name == "VariableName") ||
            !metadata.Members.Any(member => member is not null && member.Name == "Value"))
            throw ObservationIncomplete("Runtime Reflection did not confirm the exact creation stamp component type and VariableName/Value members.");
    }

    internal static async Task<IReadOnlyList<CreationObservedStamp>> ReadMarkersAsync(SlotInfo slot, CreationObservationContext run)
    {
        if (slot.Components is null) throw ObservationIncomplete($"Component coverage for Slot '{slot.Id}' is unknown.");
        if (slot.Components.Count > run.Options.MaxComponents)
            throw ObservationIncomplete("Creation ownership component limit was reached.");
        run.EnsureComponentReadCapacity(slot.Components.Count);
        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        var markers = new List<CreationObservedStamp>();
        foreach (var summary in slot.Components)
        {
            run.CheckBudget();
            if (summary is null || string.IsNullOrWhiteSpace(summary.Id) || !componentIds.Add(summary.Id))
                throw ObservationIncomplete("A Slot has an unknown or duplicate Component ID.");
            var full = await run.ReadComponentAsync(summary.Id);
            if (full.Id != summary.Id || full.Type != summary.Type)
                throw ObservationIncomplete("A Component summary and full read do not have the same exact ID and type.");
            if (full.Members is null)
                throw ObservationIncomplete("A Component's member set is unknown.");
            var variableNameExists = HasMember(full, "VariableName");
            if (!TryFindStringMember(full, "VariableName", out var variableName))
            {
                if (full.Type == VariableComponentType || variableNameExists)
                    throw ObservationIncomplete("Runtime Component data did not expose the stamp type's VariableName value.");
                continue;
            }
            if (variableName != VariableName) continue;
            if (full.Type != VariableComponentType)
                throw OwnerUnproven("The reserved VariableName is present on a Component with a different exact type.");
            if (!TryFindStringMember(full, "Value", out var rawStamp))
                throw ObservationIncomplete("The creation stamp Value is unreadable.");
            if (!CreationStamp.TryParse(rawStamp, out var parsed) || parsed is null)
                throw OwnerUnproven("The creation stamp value is not a valid version-1 stamp.");
            markers.Add(new(rawStamp!, parsed));
        }
        return markers;
    }

    private static bool HasMember(ComponentInfo component, string name) =>
        component.Members?.Keys.Any(key => key == name) == true;

    private static bool TryFindStringMember(ComponentInfo component, string name, out string? value)
    {
        value = null;
        if (component.Members is null) return false;
        var member = component.Members.FirstOrDefault(pair => pair.Key == name).Value;
        if (member is null) return false;
        if (member.Value is JsonValue json && json.TryGetValue<string>(out var memberValue))
        {
            value = memberValue;
            return true;
        }
        return false;
    }

    internal static void VerifyStampValue(CreationObservedStamp observed, string expectedValue, CreationStamp expectedStamp, string target)
    {
        if (!string.Equals(expectedValue, observed.Raw, StringComparison.Ordinal))
            throw OwnerUnproven($"The {target} stamp differs from creation state, including JSON whitespace.");
        if (observed.Stamp != expectedStamp)
            throw OwnerUnproven($"The {target} stamp fields do not match creation state.");
    }

    internal static IReadOnlyList<string> DirectChildIds(SlotInfo slot, string expectedParent, int maxQueuedSlots)
    {
        if (slot.Children is null) throw ObservationIncomplete($"Child coverage for Slot '{slot.Id}' is unknown.");
        if (slot.Children.Count > maxQueuedSlots)
            throw ObservationIncomplete("Creation ownership queue limit was reached before scheduling children.");
        var ids = new List<string>(slot.Children.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in slot.Children)
        {
            if (child is null || child.IsReferenceOnly || string.IsNullOrWhiteSpace(child.Id) || child.ParentId != expectedParent)
                throw ObservationIncomplete($"A child of Slot '{slot.Id}' is reference-only, unidentified, or has an unexpected ParentId.");
            if (!unique.Add(child.Id)) throw OwnerUnproven($"Slot '{slot.Id}' listed the same child ID more than once.");
            ids.Add(child.Id);
        }
        return ids;
    }

    private static void ValidateCreationId(string creationId)
    {
        if (creationId is null || creationId.Length is < 1 or > 128 || creationId.Any(char.IsControl))
            throw ObservationIncomplete("creationId must be 1..128 non-control characters.");
    }

    private static bool IsSessionId(string? value) => value is { Length: > 2 } && value.StartsWith("S-", StringComparison.Ordinal);
    private static bool IsLowerHex(string? value) => value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static RLoopException StateRequired(string message) => new(
        "CREATION_STATE_REQUIRED", message, ExitCodes.NotFound);
    internal static RLoopException SessionUnproven(string message) => new(
        "CREATION_SESSION_UNPROVEN", message, ExitCodes.ValidationFailed);
    internal static RLoopException OwnerUnproven(string message) => new(
        "CREATION_OWNER_UNPROVEN", message, ExitCodes.ValidationFailed);
    internal static RLoopException OwnerAmbiguous(string message) => new(
        "CREATION_OWNER_AMBIGUOUS", message, ExitCodes.ValidationFailed);
    internal static RLoopException ObservationIncomplete(string message) => new(
        "CREATION_OBSERVATION_INCOMPLETE", message, ExitCodes.ValidationFailed);

    internal CreationObservationContext CreateContext(CancellationToken cancellationToken = default) =>
        new(_client, _observer, _options, cancellationToken);

}

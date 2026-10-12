using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record CreationPlanOptions(string? StateFile = null, bool RequireState = false,
    string? Parent = null, string ReplaceOrder = "delete-first", string? RequestedSessionId = null);

public sealed record CreationPlanCounts(int Slots, int Components);
public sealed record CreationPlanEvidence(string Overall, string Session, string ExactId, string Parent,
    string RootStamp, string SiblingUniqueness, string DescendantBoundary);
public sealed record CreationPlanResult(string ResultVersion, string Mode, string CreationId, string StateFile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BuildId, string InputHash, string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PreviousRootId, string ReplaceOrder,
    bool DefinitionsReset, CreationPlanCounts Counts, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] object? Pending, CreationPlanEvidence Evidence,
    IReadOnlyList<object?> Diagnostics, bool Atomic);

/// <summary>Read-only planning for creation regenerate. This service deliberately has no apply operation.</summary>
public sealed class CreationApplyService(IResoniteClient client)
{
    public async Task<CreationPlanResult> PlanAsync(CreationDocument document, CreationPlanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (document is null)
            throw new RLoopException("CREATION_DOCUMENT_INVALID", "Creation document is required.", ExitCodes.ValidationFailed);
        var hash = document.ValidatePlanInput();
        var validation = await document.ValidateAsync(cancellationToken);
        if (!validation.Valid)
            throw new RLoopException("CREATION_DOCUMENT_INVALID", validation.Issues.FirstOrDefault()?.Message ?? "Creation document is invalid.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["issues"] = validation.Issues });
        var definition = document.Definition;
        if (definition is null || definition.Slot is not { } definitionRootSlot)
            throw new RLoopException("CREATION_DOCUMENT_INVALID", "Creation document has no validated root definition Slot.", ExitCodes.ValidationFailed);
        if (options.ReplaceOrder != "delete-first")
            throw ModeMismatch($"--replace-order {options.ReplaceOrder} is not available in this version; use delete-first.");

        var statePath = CreationStateStore.ResolvePath(document, options.StateFile);
        var state = CreationStateStore.Load(statePath, document.CreationId, options.RequireState);
        if (state?.Pending is { } pending)
            throw new RLoopException("APPLY_WRITE_UNVERIFIED", "Creation state contains a pending operation that must be reconciled before planning.",
                ExitCodes.OperationFailed, new Dictionary<string, object?>
                { ["stateFile"] = statePath, ["operationId"] = pending.OperationId, ["pending"] = pending });
        var verifier = new CreationOwnershipVerifier(client);
        var context = verifier.CreateContext(cancellationToken);
        var proof = await verifier.VerifySessionAsync(state?.Session, context, expectedSession: null);
        if (options.RequestedSessionId is { } requested && requested != proof.DiscoverSessionId)
            throw CreationOwnershipVerifier.SessionUnproven("The requested --session does not match the connected discovered session.");

        var action = state?.ActiveRoot is null ? "create" : "replace";
        CreationPlanEvidence evidence;
        string? previousRootId = null;
        if (state?.ActiveRoot is { } activeRoot)
        {
            if (options.Parent is not null)
            {
                var requestedParent = await ResolveParentAsync(options.Parent, context);
                if (requestedParent != activeRoot.ParentId)
                    throw CreationOwnershipVerifier.OwnerUnproven("The requested parent differs from the recorded activeRoot parent.");
            }
            await verifier.VerifyAsync(state, context);
            previousRootId = activeRoot.Id;
            evidence = new("complete", "complete", "complete", "complete", "complete", "complete", "complete");
        }
        else
        {
            var selector = options.Parent ?? definitionRootSlot.Parent;
            selector = string.IsNullOrWhiteSpace(selector) ? "Root" : selector;
            var parentId = await ResolveParentAsync(selector, context);
            await verifier.ObserveInitialParentAsync(parentId, document.CreationId, context);
            await VerifyAncestorBoundaryAsync(parentId, document.CreationId, context);
            evidence = new("complete", "complete", "unknown", "complete", "unknown", "complete", "unknown");
        }

        await verifier.VerifySessionAsync(state?.Session, context, proof);
        var counts = CountDefinitions(definition, definitionRootSlot);
        return new("1", "regenerate", document.CreationId, statePath, null, hash, action, previousRootId,
            "delete-first", true, counts, null, evidence, [], false);
    }

    private static async Task<string> ResolveParentAsync(string selector, CreationObservationContext context)
    {
        context.CheckBudget();
        if (string.IsNullOrWhiteSpace(selector)) throw CreationOwnershipVerifier.ObservationIncomplete("Creation parent selector is empty.");
        if (selector.Equals("Root", StringComparison.OrdinalIgnoreCase) || selector is "/" or "/Root")
        {
            var root = await context.ReadSlotAsync("Root");
            if (root.Id != "Root") throw CreationOwnershipVerifier.ObservationIncomplete("The Root selector did not resolve to exact ID Root.");
            return root.Id;
        }
        var segments = selector.StartsWith("path:", StringComparison.Ordinal) || selector.Contains('/')
            ? SlotPaths.ParseSelector(selector)
            : null;
        if (segments is null)
        {
            var exact = await context.ReadSlotAsync(selector);
            if (exact.Id != selector) throw CreationOwnershipVerifier.ObservationIncomplete("Parent exact-ID observation did not return the requested ID.");
            return exact.Id;
        }
        if (segments.Length - 1 > context.Options.MaxDepth)
            throw CreationOwnershipVerifier.ObservationIncomplete("Creation parent path exceeds the bounded depth.");
        var current = await context.ReadSlotAsync("Root");
        if (current.Id != "Root" || segments[0] != "Root")
            throw CreationOwnershipVerifier.ObservationIncomplete("A creation parent path must begin at exact Root.");
        for (var index = 1; index < segments.Length; index++)
        {
            var ids = CreationOwnershipVerifier.DirectChildIds(current, current.Id, context.Options.MaxQueuedSlots);
            context.EnsureSlotReadCapacity(ids.Count);
            var matches = new List<SlotInfo>();
            foreach (var id in ids)
            {
                var child = await context.ReadSlotAsync(id);
                if (child.ParentId != current.Id)
                    throw CreationOwnershipVerifier.ObservationIncomplete("A full child read did not confirm the exact observed parent ID.");
                if (child.Name == segments[index]) matches.Add(child);
            }
            if (matches.Count != 1)
                throw CreationOwnershipVerifier.OwnerUnproven(matches.Count == 0
                    ? $"Parent path segment '{segments[index]}' was not observed below its exact parent."
                    : $"Parent path segment '{segments[index]}' is ambiguous below its exact parent.");
            current = matches[0];
        }
        return current.Id;
    }

    private static async Task VerifyAncestorBoundaryAsync(string parentId, string creationId, CreationObservationContext context)
    {
        await CreationOwnershipVerifier.VerifyStampMetadataAsync(context);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var currentId = parentId;
        for (var depth = 0; ; depth++)
        {
            context.CheckBudget();
            if (depth > context.Options.MaxDepth)
                throw CreationOwnershipVerifier.ObservationIncomplete("The creation parent ancestry exceeded the bounded depth.");
            if (!seen.Add(currentId))
                throw CreationOwnershipVerifier.OwnerUnproven("The creation parent ancestry contains a confirmed cycle.");
            var current = await context.ReadSlotAsync(currentId);
            if ((await CreationOwnershipVerifier.ReadMarkersAsync(current, context)).Count > 0)
                throw CreationOwnershipVerifier.OwnerUnproven("A creation stamp exists on the selected parent ancestry.");
            if (current.Id == "Root")
            {
                if (current.ParentId is not null && current.ParentId.Length > 0)
                    throw CreationOwnershipVerifier.OwnerUnproven("The exact Root observation unexpectedly has a parent.");
                return;
            }
            if (string.IsNullOrWhiteSpace(current.ParentId))
                throw CreationOwnershipVerifier.OwnerUnproven("The selected parent ancestry did not terminate at exact Root.");
            currentId = current.ParentId;
        }
    }

    private static CreationPlanCounts CountDefinitions(ApplyDocument definition, ApplySlotSpec rootSlot)
    {
        var slots = 0;
        var components = 1; // Reserved creation stamp Component.
        void Walk(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? values, IReadOnlyList<ApplyNodeSpec>? children)
        {
            slots++;
            components += values?.Count ?? 0;
            foreach (var child in children ?? []) Walk(child.Slot, child.Components, child.Children);
        }
        Walk(rootSlot, definition.Components, definition.Children);
        return new(slots, components);
    }

    private static RLoopException ModeMismatch(string message) => new("CREATION_MODE_MISMATCH", message, ExitCodes.InvalidArguments);
}

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed partial class WorldService(IResoniteClient client, string? generatedContentSource = null)
{
    public async Task<string> ResolveSlotIdAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new RLoopException("SLOT_SELECTOR_MISSING", "A Slot ID or path is required.", ExitCodes.InvalidArguments);
        if (selector.Equals("Root", StringComparison.OrdinalIgnoreCase) || selector is "/" or "/Root") return "Root";

        if (!selector.StartsWith("path:", StringComparison.Ordinal) && !selector.Contains('/') && !selector.StartsWith("Root", StringComparison.OrdinalIgnoreCase))
        {
            try { return (await client.GetSlotAsync(selector, 0, false, cancellationToken)).Id; }
            catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
            {
                var matches = await FindAsync(selector, true, null, 8, cancellationToken);
                return matches.Count switch
                {
                    1 => matches[0].Id,
                    0 => throw new RLoopException("SLOT_NOT_FOUND", $"Slot '{selector}' was not found as an ID or exact name.", ExitCodes.NotFound),
                    _ => throw new RLoopException("SLOT_AMBIGUOUS", $"Slot name '{selector}' matched {matches.Count} Slots; use an ID or path.", ExitCodes.ValidationFailed,
                        new Dictionary<string, object?> { ["matches"] = matches.Select(x => new { x.Id, x.Path }).ToArray() })
                };
            }
        }

        var parts = SlotPaths.ParseSelector(selector).Skip(1);
        var currentId = "Root";
        var currentPath = "Root";
        foreach (var part in parts)
        {
            var current = await client.GetSlotAsync(currentId, 1, false, cancellationToken);
            var matches = current.Children.Where(x => x.Name.Equals(part, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
                throw new RLoopException("SLOT_PATH_NOT_FOUND", $"Path segment '{part}' was not found below '{currentPath}'.", ExitCodes.NotFound,
                    new Dictionary<string, object?> { ["path"] = selector, ["resolvedPrefix"] = currentPath });
            if (matches.Length > 1)
                throw new RLoopException("SLOT_PATH_AMBIGUOUS", $"Path segment '{part}' matched multiple Slots below '{currentPath}'. Use a Slot ID.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["ids"] = matches.Select(x => x.Id).ToArray() });
            currentId = matches[0].Id;
            currentPath += "/" + part;
        }
        return currentId;
    }

    public async Task<string> ResolveSlotSelectorAsync(string selector, string? stateFile = null,
        CancellationToken cancellationToken = default)
    {
        if (!selector.StartsWith('$')) return await ResolveSlotIdAsync(selector, cancellationToken);
        var syntax = StableSelectorSyntax.Parse(selector);
        if (syntax.Kind != "slot")
            throw new RLoopException("STABLE_SELECTOR_KIND_MISMATCH",
                $"Selector '{selector}' does not identify a Slot.", ExitCodes.InvalidArguments);
        var state = RequireStateFile(stateFile, selector);
        var session = await client.GetSessionInfoAsync(cancellationToken);
        return (await ResolveStableReferenceAsync(state, selector, session.UniqueSessionId, cancellationToken)).Id;
    }

    public async Task<string> ResolveComponentSelectorAsync(string selector, string? stateFile = null,
        CancellationToken cancellationToken = default)
    {
        if (!selector.StartsWith('$'))
            return (await client.GetComponentAsync(selector, cancellationToken)).Id;
        var syntax = StableSelectorSyntax.Parse(selector);
        if (syntax.Kind != "component")
            throw new RLoopException("STABLE_SELECTOR_KIND_MISMATCH",
                $"Selector '{selector}' does not identify a Component.", ExitCodes.InvalidArguments);
        var state = RequireStateFile(stateFile, selector);
        var session = await client.GetSessionInfoAsync(cancellationToken);
        return (await ResolveStableReferenceAsync(state, selector, session.UniqueSessionId, cancellationToken)).Id;
    }

    public async Task<SlotInfo> InspectAsync(string selector, int depth, bool includeComponentData,
        CancellationToken cancellationToken = default, bool excludeReferenceOnly = false)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, depth, includeComponentData, cancellationToken);
        var withPaths = AddPaths(slot, selector.Contains('/') ? NormalizePath(selector) : slot.Name);
        return excludeReferenceOnly ? RemoveReferenceOnlyChildren(withPaths) : withPaths;
    }

    public async Task<IReadOnlyList<SlotMatch>> FindAsync(string? name, bool exact, string? componentType,
        int depth, CancellationToken cancellationToken = default, FindOptions? options = null)
    {
        options ??= new FindOptions();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(componentType))
            throw new RLoopException("FIND_FILTER_MISSING", "find requires --name or --component.", ExitCodes.InvalidArguments);
        var rootId = string.IsNullOrWhiteSpace(options.Under)
            ? "Root"
            : await ResolveSlotIdAsync(options.Under, cancellationToken);
        var requestedDepth = options.DirectChildren ? 1 : depth;
        var root = await client.GetSlotAsync(rootId, requestedDepth, false, cancellationToken);
        var rootPath = string.IsNullOrWhiteSpace(options.Under)
            ? "Root"
            : options.Under!.Contains('/') ? NormalizePath(options.Under) : root.Name;
        var results = new List<SlotMatch>();
        void Collect(SlotInfo slot)
        {
            if (options.DirectChildren && slot.Id == root.Id) return;
            if (options.ExcludeReferenceOnly && slot.IsReferenceOnly) return;
            var nameMatches = string.IsNullOrWhiteSpace(name) || (exact
                ? slot.Name.Equals(name, StringComparison.Ordinal)
                : slot.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            var componentMatches = string.IsNullOrWhiteSpace(componentType) || slot.Components.Any(c =>
                ObservationTypeNames.Contains(c.Type, componentType));
            if (nameMatches && componentMatches)
                results.Add(new SlotMatch(slot.Id, slot.Name, slot.Path!, slot.Components));
        }
        // --direct-children evaluates only the root's direct children: backends may mark
        // grandchildren as reference-only stubs, but they are never direct children.
        if (options.DirectChildren)
            foreach (var child in root.Children)
                Collect(child with { Path = rootPath + "/" + child.Name });
        else
            Visit(root, rootPath, Collect);
        return results;
    }

    public async Task<IReadOnlyList<InspectedComponent>> InspectComponentsAsync(string selector, int depth,
        string? componentType = null, string? memberName = null, bool excludeReferenceOnly = false,
        CancellationToken cancellationToken = default)
    {
        var slot = await InspectAsync(selector, depth, true, cancellationToken);
        var results = new List<InspectedComponent>();
        Visit(slot, slot.Path ?? slot.Name, current =>
        {
            if (excludeReferenceOnly && current.IsReferenceOnly) return;
            foreach (var summary in current.Components)
            {
                if (!string.IsNullOrWhiteSpace(componentType) &&
                    !ObservationTypeNames.Contains(summary.Type, componentType)) continue;
                var members = summary.Members ?? new Dictionary<string, MemberValue>();
                if (!string.IsNullOrWhiteSpace(memberName))
                {
                    var matches = members.Where(pair => pair.Key.Equals(memberName, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    if (matches.Count == 0) continue;
                    members = matches;
                }
                results.Add(new InspectedComponent(current.Id, current.Path ?? current.Name,
                    new ComponentInfo(summary.Id, summary.Type, members)));
            }
        });
        return results;
    }

    public async Task<IReadOnlyList<ComponentInfo>> ListComponentsAsync(string selector, CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var slot = await client.GetSlotAsync(id, 0, false, cancellationToken);
        var result = new List<ComponentInfo>();
        foreach (var component in slot.Components) result.Add(await client.GetComponentAsync(component.Id, cancellationToken));
        return result;
    }

    // Within one call a verified Slot and its Component list are read once; the values are sequential observations.
    private sealed class ResolutionCache : Dictionary<string, ComponentInfo>
    {
        public ResolutionCache() : base(StringComparer.Ordinal) { }
        public Dictionary<string, string> SlotIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SlotInfo> SlotsWithComponents { get; } = new(StringComparer.Ordinal);
    }

    public Task<ResolvedWorldReference> ResolveStableReferenceAsync(string stateFile, string selector,
        string? currentConnectionId, CancellationToken cancellationToken = default) =>
        ResolveStableReferenceCoreAsync(stateFile, selector, currentConnectionId, new HashSet<string>(StringComparer.Ordinal), cancellationToken);

    private async Task<ResolvedWorldReference> ResolveStableReferenceCoreAsync(string stateFile, string selector,
        string? currentConnectionId, HashSet<string> resolvingComponents, CancellationToken cancellationToken,
        ResolutionCache? observedComponents = null)
    {
        async Task<ComponentInfo> ReadComponent(string id)
        {
            if (observedComponents is not null && observedComponents.TryGetValue(id, out var observed)) return observed;
            var result = await client.GetComponentAsync(id, cancellationToken);
            if (observedComponents is not null) observedComponents[id] = result;
            return result;
        }
        var syntax = StableSelectorSyntax.Parse(selector);
        if (syntax.Kind == "slot-member")
        {
            var target = await ResolveStableReferenceCoreAsync(stateFile, "$slot:" + syntax.Key,
                currentConnectionId, resolvingComponents, cancellationToken, observedComponents);
            var slot = await client.GetSlotAsync(target.Id, 0, false, cancellationToken);
            var member = RequireSlotMember(slot, syntax.MemberName!, selector);
            return new ResolvedWorldReference(selector, member.Id!, "member", member.Type ?? member.TargetType, target.Path);
        }
        if (syntax.Kind == "slot")
        {
            var stable = StableReferenceResolver.ResolveSlot(stateFile, selector);
            if (observedComponents is not null && observedComponents.SlotIds.TryGetValue(stable.Key, out var knownId))
                return new ResolvedWorldReference(selector, knownId, "slot", "[FrooxEngine]FrooxEngine.Slot", stable.Path);
            // UniqueSessionId is a per-connection counter, not a world identity: the stored ID is only a
            // hint and is reused after a live re-read proves it still is the owned Slot. A stored ID that is alive
            // but cannot be verified never falls through to "not found"; it stops with APPLY_STORED_ID_UNVERIFIED.
            var check = await VerifyStoredSlotAsync(stateFile, stable, cancellationToken);
            var id = check.VerifiedId;
            if (id is null)
            {
                try
                {
                    id = stable.RuntimeRelocatable
                        ? (await ResolveRelocatableSlotAsync(stateFile, stable, cancellationToken)).Id
                        : await ResolveSlotIdAsync(SlotPaths.Selector(stable.Path, stable.PathSegments), cancellationToken);
                }
                catch (RLoopException ex) when (check.Live && ex.Code is "SLOT_NOT_FOUND" or "SLOT_PATH_NOT_FOUND" or "STABLE_RELOCATABLE_SLOT_NOT_FOUND")
                {
                    throw StoredIdUnverified(stable.Key, stable.Id, stable.Path, check, ex);
                }
            }
            if (observedComponents is not null) observedComponents.SlotIds[stable.Key] = id;
            return new ResolvedWorldReference(selector, id, "slot", "[FrooxEngine]FrooxEngine.Slot", stable.Path);
        }

        var componentSelector = "$component:" + syntax.Key;
        var memberName = syntax.MemberName;

        var stableComponent = StableReferenceResolver.ResolveComponent(stateFile, componentSelector);
        var firstVisit = resolvingComponents.Add(stableComponent.Key);
        try
        {
            ComponentInfo? component = null;
            if (component is null)
            {
                var stableSlot = StableReferenceResolver.ResolveSlot(stateFile, "$slot:" + stableComponent.SlotKey);
                var slotId = (await ResolveStableReferenceCoreAsync(stateFile, "$slot:" + stableComponent.SlotKey,
                    currentConnectionId, resolvingComponents, cancellationToken, observedComponents)).Id;
                SlotInfo slot;
                if (observedComponents is null || !observedComponents.SlotsWithComponents.TryGetValue(slotId, out slot!))
                {
                    slot = await client.GetSlotAsync(slotId, 0, true, cancellationToken);
                    if (observedComponents is not null) observedComponents.SlotsWithComponents[slotId] = slot;
                }
                Dictionary<string, string>? referenceTargets = null;
                if (firstVisit && stableComponent.ReferenceSelectors is { Count: > 0 })
                {
                    referenceTargets = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var reference in stableComponent.ReferenceSelectors)
                    {
                        var targetSyntax = StableSelectorSyntax.Parse(reference.Value);
                        if (targetSyntax.Kind is not ("slot" or "slot-member") && resolvingComponents.Contains(targetSyntax.Key)) continue;
                        var target = await ResolveStableReferenceCoreAsync(stateFile, reference.Value, currentConnectionId,
                            resolvingComponents, cancellationToken, observedComponents);
                        referenceTargets[reference.Key] = target.Id;
                    }
                }
                // The owning Slot is resolved first and only its own Component list is searched, so a stored ID
                // can never pull in a Component of another Slot. componentIndex is not ownership evidence.
                var resolution = ResolveComponentOnSlot(slot.Components, stableComponent.Type, stableComponent.Id,
                    stableComponent.MemberNames, stableComponent.IdentityValues, referenceTargets);
                var matching = resolution.Match is null ? resolution.Candidates.ToArray() : [resolution.Match];
                if (matching.Length == 0)
                    throw new RLoopException("FLUX_BINDING_COMPONENT_NOT_FOUND",
                        $"Stable component '{stableComponent.Key}' could not be re-resolved on '{stableSlot.Path}'.", ExitCodes.NotFound,
                        new Dictionary<string, object?> { ["selector"] = selector, ["slotPath"] = stableSlot.Path,
                            ["type"] = stableComponent.Type, ["typeOrdinal"] = stableComponent.TypeOrdinal });
                if (matching.Length > 1)
                    throw new RLoopException("STABLE_COMPONENT_AMBIGUOUS",
                        $"Stable component '{stableComponent.Key}' matches multiple Components on '{stableSlot.Path}'.",
                        ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["selector"] = selector,
                            ["slotPath"] = stableSlot.Path, ["candidateIds"] = matching.Select(candidate => candidate.Id).ToArray() },
                        ["Inspect candidateIds and preserve the existing state. Adding identityFields to a manifest does not populate an older checkpoint's identity values.",
                         "For new content use one named provider Slot per Component or initialize immutable identityFields at creation. Recover existing content only after verifying ownership and exact candidates; do not select by ordinal or discard state blindly."]);
                component = await ReadComponent(matching[0].Id);
            }

            if (memberName is null)
                return new ResolvedWorldReference(selector, component.Id, "component", component.Type);
            var member = component.Members.FirstOrDefault(pair => pair.Key.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(member.Key) || string.IsNullOrWhiteSpace(member.Value.Id))
                throw new RLoopException("FLUX_BINDING_MEMBER_NOT_FOUND",
                    $"Member '{memberName}' was not found on stable component '{stableComponent.Key}'.", ExitCodes.NotFound,
                    suggestions: component.Members.Keys.Take(30).ToArray());
            return new ResolvedWorldReference(selector, member.Value.Id!, "member", member.Value.Type ?? member.Value.TargetType);
        }
        finally
        {
            if (firstVisit) resolvingComponents.Remove(stableComponent.Key);
        }
    }

    // The stored Slot ID is a hint. Live is true when something answers to the ID; Reason says why it was not accepted.
    private sealed record StoredSlotCheck(string? VerifiedId, bool Live, string? Reason = null,
        string? ObservedName = null, string? ObservedPath = null);

    private async Task<StoredSlotCheck> VerifyStoredSlotAsync(string stateFile, StableSlotReference stable,
        CancellationToken cancellationToken, ApplyState? loadedState = null)
    {
        if (string.IsNullOrWhiteSpace(stable.Id)) return new StoredSlotCheck(null, false);
        var segments = stable.PathSegments ?? SlotPaths.LegacySegments(stable.Path);
        SlotInfo slot;
        try { slot = await client.GetSlotAsync(stable.Id, 0, stable.RuntimeRelocatable, cancellationToken); }
        catch (RLoopException ex) when (ex.Code == "SLOT_NOT_FOUND")
        {
            return new StoredSlotCheck(null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw StoredIdUnverified(stable.Key, stable.Id, stable.Path,
                new StoredSlotCheck(null, false, "storedIdReadFailed"), ex);
        }
        if (slot.IsReferenceOnly)
            return new StoredSlotCheck(null, true, "the stored ID answered with a reference-only placeholder, which proves nothing about ownership");
        if (!slot.Name.Equals(segments.LastOrDefault(), StringComparison.Ordinal))
            return new StoredSlotCheck(null, true,
                $"the live Slot is named '{slot.Name}', the recorded name is '{segments.LastOrDefault()}'", slot.Name);
        if (stable.RuntimeRelocatable)
        {
            var state = loadedState ?? ApplyStateStore.Load(Path.GetFullPath(stateFile), stable.OwnershipKey);
            var evidence = state.Components.Values.Where(component => component.SlotKey == stable.Key).ToArray();
            return RelocatableEvidenceMatches(slot.Components, evidence)
                ? new StoredSlotCheck(slot.Id, true)
                : new StoredSlotCheck(null, true, "the recorded Component evidence does not match the live Slot one-to-one", slot.Name);
        }
        try
        {
            var live = await ObserveAbsoluteSegmentsAsync(slot, cancellationToken, requireUniqueNames: true);
            return live.SequenceEqual(segments, StringComparer.Ordinal)
                ? new StoredSlotCheck(slot.Id, true)
                : new StoredSlotCheck(null, true,
                    $"the live path '{string.Join('/', live)}' differs from the recorded path '{string.Join('/', segments)}'",
                    slot.Name, string.Join('/', live));
        }
        catch (RLoopException ex) when (ex.Code is "SLOT_PATH_UNRESOLVED" or "SLOT_PATH_AMBIGUOUS")
        {
            return new StoredSlotCheck(null, true, ex.Message, slot.Name);
        }
        catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "RESONITE_OPERATION_FAILED")
        {
            return new StoredSlotCheck(null, true, "an ancestor could not be read while verifying the path: " + ex.Message, slot.Name);
        }
    }

    private static RLoopException StoredIdUnverified(string key, string storedId, string recordedPath,
        StoredSlotCheck check, Exception? inner = null) =>
        new("APPLY_STORED_ID_UNVERIFIED",
            $"The stored ID '{storedId}' of managed Slot '{key}' cannot be verified. " +
            $"Nothing was changed. Reason: {check.Reason}.",
            ExitCodes.ValidationFailed, new Dictionary<string, object?>
            {
                ["key"] = key, ["storedId"] = storedId, ["recordedPath"] = recordedPath,
                ["observedName"] = check.ObservedName, ["observedPath"] = check.ObservedPath, ["reason"] = check.Reason
            },
            ["Inspect the object that holds this ID. If it is the owned Slot (renamed or moved by hand), restore its recorded name and parent and run again.",
             "If the state file belongs to another world or the ID was reused by an unrelated object, preserve the checkpoint and repair or replace the state explicitly; ResoLoop will not guess."],
            inner);

    private async Task<SlotInfo> ResolveRelocatableSlotAsync(string stateFile, StableSlotReference stable,
        CancellationToken cancellationToken)
    {
        var state = ApplyStateStore.Load(Path.GetFullPath(stateFile), stable.OwnershipKey);
        var evidence = state.Components.Values.Where(component => component.SlotKey == stable.Key).ToArray();
        if (evidence.Length == 0)
            throw new RLoopException("STABLE_RELOCATABLE_EVIDENCE_MISSING",
                $"Runtime-relocatable Slot '{stable.Key}' has no managed Component evidence for a safe world-wide search.",
                ExitCodes.ValidationFailed, suggestions:
                ["Declare at least one keyed Component on the runtimeRelocatable Slot and apply it before moving the item."]);
        var name = (stable.PathSegments ?? SlotPaths.LegacySegments(stable.Path)).LastOrDefault() ?? string.Empty;
        var world = AddPaths(await client.GetSlotAsync("Root", 64, true, cancellationToken), "Root");
        var candidates = new List<SlotInfo>();
        Visit(world, "Root", slot =>
        {
            if (slot.IsReferenceOnly || !slot.Name.Equals(name, StringComparison.Ordinal)) return;
            if (RelocatableEvidenceMatches(slot.Components, evidence)) candidates.Add(slot);
        });
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0)
            throw new RLoopException("STABLE_RELOCATABLE_SLOT_NOT_FOUND",
                $"Runtime-relocatable Slot '{stable.Key}' could not be uniquely re-resolved outside its saved path.",
                ExitCodes.NotFound, new Dictionary<string, object?> { ["savedPath"] = stable.Path, ["name"] = name });
        throw new RLoopException("STABLE_RELOCATABLE_SLOT_AMBIGUOUS",
            $"Runtime-relocatable Slot '{stable.Key}' matches multiple Slots outside its saved path.",
            ExitCodes.ValidationFailed, new Dictionary<string, object?>
            {
                ["savedPath"] = stable.Path,
                ["candidateIds"] = candidates.Select(candidate => candidate.Id).ToArray(),
                ["candidatePaths"] = candidates.Select(candidate => candidate.Path).ToArray()
            }, ["Add identityFields with immutable values to a managed Component on the relocatable Slot."]);
    }

    private static string RequireStateFile(string? stateFile, string selector) =>
        !string.IsNullOrWhiteSpace(stateFile) ? stateFile : throw new RLoopException(
            "WORLD_STATE_REQUIRED", $"Selector '{selector}' requires --state WORLD_STATE.", ExitCodes.InvalidArguments,
            suggestions: ["Pass the apply world-state file that contains this stable key."]);

    public async Task<ItemAuditReport> AuditItemAsync(string selector, IReadOnlyCollection<string>? allowedExternalIds = null,
        bool strict = false, IReadOnlyCollection<string>? allowedExternalRoles = null,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var root = AddPaths(RemoveReferenceOnlyChildren(await client.GetSlotAsync(id, 64, true, cancellationToken)),
            selector.StartsWith("Root", StringComparison.OrdinalIgnoreCase) ? NormalizePath(selector) : selector);
        return ItemAuditService.Audit(root, allowedExternalIds, strict, allowedExternalRoles);
    }

    public async Task<ToolAuditReport> AuditToolAsync(string selector, int depth = 16,
        float minimumAlignmentDot = 0.8f, CancellationToken cancellationToken = default)
    {
        var id = await ResolveSlotIdAsync(selector, cancellationToken);
        var root = AddPaths(await client.GetSlotAsync(id, Math.Clamp(depth, 0, 64), true, cancellationToken), selector);
        return ToolAuditService.Audit(root, minimumAlignmentDot);
    }

    public Task<ApplyValidationResult> ValidateApplyAsync(ApplyDocument document, bool strict,
        CancellationToken cancellationToken = default) =>
        ApplyDocumentValidator.ValidateAsync(GeneratedContentMetadata.AddToGeneratedRoots(document, generatedContentSource),
            strict ? client : null, cancellationToken);

    public async Task<string?> EnsureGeneratedContentTagAsync(string slotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(generatedContentSource)) return null;
        var slot = await client.GetSlotAsync(slotId, 0, true, cancellationToken);
        var marker = slot.Components.FirstOrDefault(component =>
            TypeNamesEquivalent(component.Type, GeneratedContentMetadata.ComponentType));
        if (marker is null)
        {
            return (await client.AddComponentAsync(slotId, GeneratedContentMetadata.ComponentType,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GeneratedContentMetadata.SourceMember] = generatedContentSource
                }, cancellationToken)).Id;
        }

        var members = marker.Members ?? (await client.GetComponentAsync(marker.Id, cancellationToken)).Members;
        if (!members.TryGetValue(GeneratedContentMetadata.SourceMember, out var source) ||
            !MemberMatchesRaw(source, generatedContentSource))
            await client.SetComponentMemberAsync(marker.Id, GeneratedContentMetadata.SourceMember,
                generatedContentSource, cancellationToken);
        return marker.Id;
    }

    public async Task<ApplyPlanResult> PlanApplyAsync(ApplyDocument document, ApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        options.Progress?.Invoke(new ApplyProgress("validate", 0, 1, document.SourcePath, "Validating the complete document."));
        var prepared = await PrepareAsync(document, options, cancellationToken);
        options.Progress?.Invoke(new ApplyProgress("plan", prepared.Entries.Count, prepared.Entries.Count, null, "Plan is ready; no world changes were made."));
        return new ApplyPlanResult(true, document.SchemaVersion!, document.Ownership!.Key, prepared.StatePath,
            prepared.Session.UniqueSessionId, prepared.Entries,
            prepared.Entries.Count(x => x.Action == "create"),
            prepared.Entries.Count(x => x.Action is "update" or "rename" or "relocate"),
            prepared.Entries.Count(x => x.Action == "no-op"),
            prepared.Entries.Count(x => x.Action == "rename"),
            prepared.Entries.Count(x => x.Action == "delete"), false,
            $"Non-atomic preview. State checkpoint: {prepared.StatePath}. Apply reconciles proven pending results or stops before new writes; deletion requires --prune --yes.")
            { Warnings = ComponentIdentityDiagnostics.Analyze(document) };
    }

    public async Task<ApplyResult> ApplyAsync(ApplyDocument document, ApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        var stopwatch = Stopwatch.StartNew();
        document = document with { ResolvedProjectContext = document.ProjectContext };
        var statePath = document.ResolveStatePath(options.StateFile);
        if (options.RequireState && !File.Exists(statePath))
            throw new RLoopException("APPLY_STATE_NOT_FOUND", $"World state file '{statePath}' does not exist.", ExitCodes.NotFound);
        using var writer = CheckpointFiles.AcquireWriter(statePath);
        if (client is IResoniteClientDiagnostics diagnostics) diagnostics.ResetMetrics();
        options.Progress?.Invoke(new ApplyProgress("validate", 0, 1, document.SourcePath, "Validating and planning before mutation."));
        var prepared = await PrepareAsync(document, options, cancellationToken, statePath, reconcilePending: true);
        if (options.Prune && !options.ConfirmDeletes)
            throw new RLoopException("CONFIRMATION_REQUIRED", "apply --prune is destructive and requires --yes.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["deleteCandidates"] = prepared.Deletions.Count, ["stateFile"] = prepared.StatePath });
        document.BuildBundle?.VerifyInputs();
        var firstWriteChecked = false;
        void BeforeFirstWrite()
        {
            if (firstWriteChecked) return;
            document.BuildBundle?.VerifyInputs();
            firstWriteChecked = true;
        }
        using var connectionGuard = (client as IApplyConnectionGuard)?.GuardApplyWrites(prepared.Session.ConnectionGeneration);
        var safety = prepared.Safety!;
        var counts = new ApplyCounts();
        var updatedComponents = new HashSet<string>(StringComparer.Ordinal);
        var completed = 0;
        var total = prepared.Nodes.Count + prepared.Components.Count * 2;
        try
        {
            foreach (var asset in prepared.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset.DirectUrl is not null) asset.Url = asset.DirectUrl;
                else if (asset.Action == "no-op" && prepared.State.Assets.TryGetValue(asset.Key, out var saved)) asset.Url = saved.Url;
                else
                {
                    BeforeFirstWrite();
                    var pending = Pending(prepared, "importAsset", asset.Key);
                    await ExecutePendingAsync(prepared, pending, () => safety.CheckConnectionAsync(asset.Key, cancellationToken), async () =>
                    {
                        asset.Url = await client.ImportAssetAsync(asset.Spec, asset.ResolvedSource, cancellationToken);
                        pending.AssetBinding = new(asset.Spec.Kind, asset.SourceHash, asset.Url);
                    }, cancellationToken);
                }
                if (asset.Action == "create") counts.AssetsImported++;
                else counts.AssetsUnchanged++;
                if (asset.DirectUrl is not null || asset.Action == "no-op")
                {
                    var next = ApplyStateStore.Copy(prepared.State);
                    next.Assets[asset.Key] = new ApplyStateAsset(asset.Spec.Kind, asset.SourceHash, asset.Url!);
                    Checkpoint(prepared, next);
                }
                options.Progress?.Invoke(new ApplyProgress("assets", counts.AssetsImported + counts.AssetsUnchanged,
                    prepared.Assets.Count, "$assets/" + asset.Key, asset.Action == "create" ? "imported asset" : "reused asset"));
            }
            var assetUrls = prepared.Assets.ToDictionary(x => x.Key, x => x.Url!, StringComparer.Ordinal);
            foreach (var existingNode in prepared.Nodes.Where(node => node.Existing is not null))
                existingNode.Id = existingNode.Existing!.Id;
            foreach (var node in prepared.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // UIX children must not be reparented into an empty Slot before its RectTransform
                // and layout are attached. Keep the old checkpoint path until relocation commits.
                if (node.SlotAction == "relocate") continue;
                var parentId = node.Parent?.Id ?? prepared.ParentId;
                switch (node.SlotAction)
                {
                    case "create":
                        BeforeFirstWrite();
                        var request = new SlotCreateRequest(parentId, node.Spec.Name,
                            node.Spec.Position?.ToVector3("position"), node.Spec.Rotation?.ToQuaternion("rotation"),
                            node.Spec.Scale?.ToVector3("scale"));
                        var creation = Pending(prepared, "createSlot", node.StableKey, parentId: parentId, type: "Slot");
                        creation.SlotValues = new("", request.Name, request.Position, request.Rotation, request.Scale, parentId);
                        creation.SlotBinding = new("", node.Path, node.Spec.RuntimeRelocatable, node.PathSegments);
                        await ExecutePendingAsync(prepared, creation, () => safety.CheckConnectionAsync(node.StableKey, cancellationToken), async () =>
                        {
                            node.Id = await client.CreateSlotAsync(request, cancellationToken);
                            creation.Id = node.Id;
                        }, cancellationToken);
                        counts.SlotsCreated++;
                        break;
                    case "update":
                        node.Id = node.Existing!.Id;
                        BeforeFirstWrite();
                        var update = CreateSlotUpdate(node, prepared.ParentId);
                        var slotWrite = Pending(prepared, "updateSlot", node.StableKey, node.Id, parentId, "Slot");
                        slotWrite.SlotValues = update;
                        slotWrite.SlotBinding = new(node.Id, node.Path, node.Spec.RuntimeRelocatable, node.PathSegments);
                        await ExecutePendingAsync(prepared, slotWrite, () => safety.CheckSlotAsync(node, update, cancellationToken),
                            () => client.UpdateSlotAsync(update, cancellationToken), cancellationToken);
                        counts.SlotsUpdated++;
                        break;
                    default:
                        node.Id = node.Existing!.Id;
                        counts.SlotsUnchanged++;
                        break;
                }
                if (node.SlotAction == "no-op")
                {
                    var next = ApplyStateStore.Copy(prepared.State);
                    next.Slots[node.StableKey] = new ApplyStateSlot(node.Id!, node.Path, node.Spec.RuntimeRelocatable, node.PathSegments);
                    Checkpoint(prepared, next);
                }
                completed++;
                options.Progress?.Invoke(new ApplyProgress("slots", completed, total, node.Path, $"{node.SlotAction} Slot"));
            }

            var byKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null)
                .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
            var slotsByKey = prepared.Nodes.ToDictionary(x => x.StableKey, StringComparer.Ordinal);
            foreach (var component in prepared.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Existing is not null)
                {
                    component.Id = component.Existing.Id;
                    component.ResolvedType = component.Existing.Type;
                }
                else
                {
                    IReadOnlyDictionary<string, string> initialFields = new Dictionary<string, string>();
                    var createFields = MergeCreateFields(component.Spec);
                    if (CanResolveAll(createFields, byKey, slotsByKey))
                    {
                        initialFields = await ResolveFieldsAsync(createFields, byKey, slotsByKey, assetUrls, cancellationToken);
                        component.AppliedOnCreate = initialFields;
                    }
                    BeforeFirstWrite();
                    var creation = Pending(prepared, "addComponent", component.StableKey, parentId: component.Node.Id, type: component.Spec.Type);
                    creation.Members = initialFields.ToDictionary(StringComparer.Ordinal);
                    creation.ComponentBinding = CreateComponentState(component, string.Empty);
                    await ExecutePendingAsync(prepared, creation, () => safety.CheckConnectionAsync(component.StableKey, cancellationToken), async () =>
                    {
                        var created = await client.AddComponentAsync(component.Node.Id!, component.Spec.Type, initialFields, cancellationToken);
                        component.Id = creation.Id = created.Id;
                        component.ResolvedType = creation.Type = created.Type;
                    }, cancellationToken);
                    counts.ComponentsAdded++;
                }
                if (!string.IsNullOrWhiteSpace(component.Spec.Key)) byKey[component.Spec.Key!] = component;
                if (component.Existing is not null)
                {
                    var next = ApplyStateStore.Copy(prepared.State);
                    next.Components[component.StableKey] = CreateComponentState(component, component.Id!, safety.ObservedFields(component.Id!));
                    Checkpoint(prepared, next);
                }
                completed++;
                options.Progress?.Invoke(new ApplyProgress("components", completed, total, component.Path,
                    component.Existing is null ? "created Component" : "resolved Component"));
            }

            foreach (var component in prepared.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var desiredFields = component.Existing is null ? MergeCreateFields(component.Spec) : ManagedFields(component.Spec);
                var fields = await ResolveFieldsAsync(desiredFields, byKey, slotsByKey, assetUrls, cancellationToken);
                var changed = fields.Where(field => component.AppliedOnCreate is null ||
                                                    !component.AppliedOnCreate.TryGetValue(field.Key, out var applied) || applied != field.Value)
                    .Where(field => component.Existing?.Members is null ||
                                    !component.Existing.Members.TryGetValue(field.Key, out var current) ||
                                    !MemberMatchesRaw(current, field.Value)).ToDictionary(StringComparer.Ordinal);
                if (changed.Count > 0)
                {
                    BeforeFirstWrite();
                    var memberWrite = Pending(prepared, "setMembers", component.StableKey, component.Id, component.Node.Id,
                        component.ResolvedType ?? component.Spec.Type);
                    memberWrite.Members = changed;
                    memberWrite.ComponentBinding = CreateComponentState(component, component.Id!, fields);
                    await ExecutePendingAsync(prepared, memberWrite, () => safety.CheckComponentAsync(component, changed.Keys, cancellationToken),
                        () => client.SetComponentMembersAsync(component.Id!, component.ResolvedType ?? component.Spec.Type, changed, cancellationToken), cancellationToken);
                    if (component.Existing is not null) { counts.ComponentsUpdated++; updatedComponents.Add(component.Id!); }
                }
                else if (component.Existing is not null)
                {
                    counts.ComponentsUnchanged++;
                }
                if (component.RelocationSource is not null && component.RelocationSource.Id != component.Id)
                {
                    BeforeFirstWrite();
                    var removal = Pending(prepared, "removeComponent", component.StableKey, component.RelocationSource.Id,
                        type: component.RelocationSource.Type);
                    await ExecutePendingAsync(prepared, removal, () => safety.CheckConnectionAsync(component.StableKey, cancellationToken),
                        () => client.RemoveComponentAsync(component.RelocationSource.Id, cancellationToken), cancellationToken);
                    counts.ComponentsDeleted++;
                    component.RelocationSource = null;
                }
                if (changed.Count == 0)
                {
                    var next = ApplyStateStore.Copy(prepared.State);
                    next.Components[component.StableKey] = CreateComponentState(component, component.Id!, safety.ObservedFields(component.Id!));
                    Checkpoint(prepared, next);
                }
                completed++;
                options.Progress?.Invoke(new ApplyProgress("fields", completed, total, component.Path,
                    changed.Count == 0 ? "no field changes" : $"updated {changed.Count} field(s)"));
            }

            // All new parents and their field/reference configuration now exist. Preserve the
            // parent-first relocation order, local/world transform policy, and existing Slot IDs.
            foreach (var node in prepared.Nodes.Where(node => node.SlotAction == "relocate"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BeforeFirstWrite();
                var update = CreateSlotUpdate(node, prepared.ParentId);
                var relocation = Pending(prepared, "updateSlot", node.StableKey, node.Id, update.ParentId, "Slot");
                relocation.SlotValues = update;
                relocation.SlotBinding = new(node.Id!, node.Path, node.Spec.RuntimeRelocatable, node.PathSegments);
                await ExecutePendingAsync(prepared, relocation, () => safety.CheckSlotAsync(node, update, cancellationToken),
                    () => client.UpdateSlotAsync(update, cancellationToken), cancellationToken);
                counts.SlotsUpdated++;
                completed++;
                options.Progress?.Invoke(new ApplyProgress("slots", completed, total, node.Path, "relocated Slot after parent preparation"));
            }

            if (options.Prune)
            {
                foreach (var deletion in prepared.Deletions.Where(x => x.Kind == "component"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BeforeFirstWrite();
                    var removal = Pending(prepared, "removeComponent", deletion.Key, deletion.Id);
                    removal.RemoveComponents.Add(deletion.Key);
                    await ExecutePendingAsync(prepared, removal, () => safety.CheckConnectionAsync(deletion.Key, cancellationToken),
                        () => client.RemoveComponentAsync(deletion.Id, cancellationToken), cancellationToken);
                    counts.ComponentsDeleted++;
                    options.Progress?.Invoke(new ApplyProgress("prune", counts.ComponentsDeleted + counts.SlotsDeleted,
                        prepared.Deletions.Count, deletion.Path, "deleted owned Component"));
                }
                foreach (var deletion in prepared.Deletions.Where(x => x.Kind == "slot").OrderByDescending(x => x.Path.Count(ch => ch == '/')))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deletion.Id.Equals("Root", StringComparison.OrdinalIgnoreCase))
                        throw new RLoopException("DELETE_ROOT_FORBIDDEN", "The Root Slot can never be pruned.", ExitCodes.ValidationFailed);
                    BeforeFirstWrite();
                    var removal = Pending(prepared, "deleteSlot", deletion.Key, deletion.Id);
                    removal.RemoveComponents = [.. deletion.CoveredComponentKeys ?? []];
                    removal.RemoveSlots = [.. deletion.CoveredSlotKeys ?? [deletion.Key]];
                    await ExecutePendingAsync(prepared, removal, () => safety.CheckConnectionAsync(deletion.Key, cancellationToken),
                        () => client.DeleteSlotAsync(deletion.Id, cancellationToken), cancellationToken);
                    counts.SlotsDeleted++;
                    options.Progress?.Invoke(new ApplyProgress("prune", counts.ComponentsDeleted + counts.SlotsDeleted,
                        prepared.Deletions.Count, deletion.Path, "deleted owned Slot"));
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new RLoopException("APPLY_CANCELLED", "Apply was cancelled; inspect confirmed results and pending evidence before continuing.",
                ExitCodes.OperationFailed, new Dictionary<string, object?>
                {
                    ["stateFile"] = prepared.StatePath,
                    ["completed"] = completed,
                    ["total"] = total,
                    ["remaining"] = Math.Max(0, total - completed),
                    ["slotsCreated"] = counts.SlotsCreated,
                    ["componentsAdded"] = counts.ComponentsAdded,
                    ["atomic"] = false,
                    ["recovery"] = $"Reconcile pending evidence before new writes; unresolved results stop apply. State: {prepared.StatePath}"
                }, ["Inspect pending evidence and the exact IDs after resolving the cancellation cause."], ex);
        }
        catch (RLoopException ex)
        {
            var detailed = safety.DecorateBoundaryFailure(ex);
            var context = new Dictionary<string, object?>(detailed.Context, StringComparer.Ordinal)
            {
                ["stateFile"] = prepared.StatePath,
                ["completed"] = completed,
                ["total"] = total,
                ["remaining"] = Math.Max(0, total - completed),
                ["slotsCreated"] = counts.SlotsCreated,
                ["slotsUpdated"] = counts.SlotsUpdated,
                ["componentsAdded"] = counts.ComponentsAdded,
                ["componentsUpdated"] = counts.ComponentsUpdated,
                ["componentsDeleted"] = counts.ComponentsDeleted,
                ["slotsDeleted"] = counts.SlotsDeleted,
                ["assetsImported"] = counts.AssetsImported,
                ["atomic"] = false,
                ["recovery"] = $"Reconcile pending evidence before new writes; unresolved results stop apply. State: {prepared.StatePath}"
            };
            var suggestions = ex.Suggestions.Concat(["Inspect confirmed results and pending evidence; a subsequent apply reconciles proven results or stops."])
                .Distinct(StringComparer.Ordinal).ToArray();
            var failure = new RLoopException(ex.Code, ex.Message, ex.ExitCode, context, suggestions,
                detailed.Code == "APPLY_CANCELLED" ? detailed.InnerException : detailed);
            ApplyDiagnostics.AttachRuntime(failure, safety.Diagnostics
                .Concat(ApplyDiagnostics.ForException(detailed, "apply").Diagnostics).Distinct().ToArray());
            throw failure;
        }

        stopwatch.Stop();
        var metrics = client is IResoniteClientDiagnostics profiled ? profiled.SnapshotMetrics() :
            new ClientMetrics(0, 0, 0, []);
        var mutations = counts.SlotsCreated + counts.SlotsUpdated + counts.ComponentsAdded + counts.ComponentsUpdated +
                        counts.ComponentsDeleted + counts.SlotsDeleted + counts.AssetsImported;
        var noOps = counts.SlotsUnchanged + counts.ComponentsUnchanged + counts.AssetsUnchanged;
        var profile = options.Profile ? new ApplyProfile(stopwatch.Elapsed.TotalMilliseconds, metrics,
            prepared.Entries.Count, mutations, noOps) : null;
        var root = prepared.Nodes[0];
        var result = new ApplyResult(root.Id!, root.SlotAction == "create", counts.ComponentsAdded, counts.ComponentsUpdated,
            counts.SlotsCreated, counts.SlotsUpdated, counts.SlotsUnchanged, counts.ComponentsUnchanged,
            prepared.StatePath, prepared.Session.UniqueSessionId, profile, counts.ComponentsDeleted, counts.SlotsDeleted,
            false, $"Operations are non-atomic. A subsequent apply reconciles proven results or stops at unresolved evidence in {prepared.StatePath}.",
            counts.AssetsImported, counts.AssetsUnchanged);
        ApplyDiagnostics.AttachRuntime(result, safety.Diagnostics);
        return result;
    }

    public async Task<ApplyTestReport> TestAsync(ApplyDocument document, ApplyOptions? options = null,
        bool allowProbe = false, CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        if (document.Tests is null || document.Tests.Count == 0)
            throw new RLoopException("APPLY_TESTS_MISSING", "The apply document does not declare any tests.", ExitCodes.ValidationFailed);
        var prepared = await PrepareAsync(document, options, cancellationToken);
        foreach (var node in prepared.Nodes) node.Id = node.Existing?.Id;
        foreach (var component in prepared.Components)
        {
            component.Id = component.Existing?.Id;
            component.ResolvedType = component.Existing?.Type;
        }
        var byKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null)
            .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
        var slotsByKey = prepared.Nodes.ToDictionary(x => x.StableKey, StringComparer.Ordinal);
        var assetUrls = prepared.Assets.Where(x => x.DirectUrl is not null || prepared.State.Assets.ContainsKey(x.Key))
            .ToDictionary(x => x.Key, x => x.DirectUrl ?? prepared.State.Assets[x.Key].Url, StringComparer.Ordinal);
        var results = new List<ApplyTestCaseResult>();
        foreach (var test in document.Tests ?? [])
        {
            var assertions = new List<ApplyAssertionResult>();
            var childCountBaselines = new Dictionary<ApplyAssertionSpec, int>();
            foreach (var assertion in test.Assertions ?? [])
                if (assertion.Kind.Equals("child-count", StringComparison.OrdinalIgnoreCase) && assertion.Delta is not null)
                    childCountBaselines[assertion] = await CountChildren(assertion, slotsByKey, cancellationToken, refresh: true);
            foreach (var assertion in (test.Assertions ?? []).Where(x => !string.Equals(x.Phase, "after", StringComparison.OrdinalIgnoreCase)))
                assertions.Add(await EvaluateAssertion(test.Name, assertion, "before", byKey, slotsByKey, assetUrls, cancellationToken));

            var probeExecuted = false;
            var structuralOnly = test.Probe is null;
            var capability = test.Probe is null ? "No interaction probe declared; field/reference structure was verified." : string.Empty;
            Func<Task>? restoreProbe = null;
            try
            {
                if (test.Probe is { } probe)
                {
                    if (!probe.Safe)
                        throw new RLoopException("UNSAFE_PROBE_REJECTED", $"Test '{test.Name}' probe must declare safe=true.", ExitCodes.ValidationFailed);
                    if (!allowProbe)
                    {
                        structuralOnly = true;
                        capability = "Probe was not executed. Re-run with --probe --yes after confirming the operation is safe.";
                    }
                    else
                    {
                        var key = SymbolKey(probe.Target);
                        byKey.TryGetValue(key, out var target);
                        if (!string.Equals(probe.Kind, "set-members", StringComparison.OrdinalIgnoreCase) && target?.Existing is null)
                            throw UnknownApplyReference(probe.Target, byKey.Keys);
                        switch (probe.Kind?.ToLowerInvariant())
                        {
                            case "method":
                            {
                                if (string.IsNullOrWhiteSpace(probe.Method))
                                    throw new RLoopException("PROBE_METHOD_MISSING", $"Test '{test.Name}' method probe requires method.", ExitCodes.ValidationFailed);
                                var definition = await client.DescribeComponentTypeAsync(target!.Existing!.Type, cancellationToken);
                                if (definition.Methods?.Any(x => x.Name == probe.Method && !x.IsStatic) != true)
                                {
                                    structuralOnly = true;
                                    capability = $"Runtime method '{probe.Method}' is not exposed by public Reflection; structural assertions only.";
                                }
                                else
                                {
                                    var call = await client.CallComponentMethodAsync(target.Existing.Id, probe.Method, probe.Arguments, cancellationToken);
                                    if (!call.Success)
                                        throw new RLoopException("PROBE_FAILED", call.Error ?? $"Probe '{probe.Method}' failed.", ExitCodes.OperationFailed);
                                    probeExecuted = true;
                                    capability = "Probe executed through the public ResoniteLink SyncMethod API; after assertions were polled.";
                                }
                                break;
                            }
                            case "set-member":
                            case "set-members":
                            {
                                if (!probe.Restore)
                                    throw new RLoopException("PROBE_RESTORE_REQUIRED", $"Test '{test.Name}' set-member probe requires restore=true.", ExitCodes.ValidationFailed);
                                var values = probe.Kind.Equals("set-members", StringComparison.OrdinalIgnoreCase)
                                    ? probe.Values! : new Dictionary<string, JsonElement> { [probe.Target] = probe.Value!.Value };
                                var changes = new List<(string Selector, string Id, string Member, string Original, string Temporary)>();
                                foreach (var pair in values)
                                {
                                    var selector = pair.Key[(pair.Key.IndexOf(':') + 1)..];
                                    var separator = selector.LastIndexOf('.');
                                    if (!byKey.TryGetValue(selector[..separator], out var currentTarget) || currentTarget.Existing is null)
                                        throw UnknownApplyReference(pair.Key, byKey.Keys);
                                    var memberName = selector[(separator + 1)..];
                                    var current = await client.GetComponentAsync(currentTarget.Existing.Id, cancellationToken);
                                    if (!current.Members.TryGetValue(memberName, out var original))
                                        throw new RLoopException("PROBE_MEMBER_NOT_FOUND", $"Probe member '{pair.Key}' was not found.", ExitCodes.NotFound);
                                    if (original.Kind != "field")
                                        throw new RLoopException("PROBE_MEMBER_KIND_UNSUPPORTED", $"Transactional probes support field members; '{pair.Key}' is '{original.Kind}'.", ExitCodes.ValidationFailed);
                                    if (changes.Any(change => change.Id == current.Id && change.Member == memberName))
                                        throw new RLoopException("PROBE_DUPLICATE_TARGET", "Probe aliases refer to the same member.", ExitCodes.ValidationFailed);
                                    var temporary = await ResolveValueAsync(pair.Value, byKey, slotsByKey, assetUrls, cancellationToken);
                                    await client.ValidateComponentMemberAsync(current.Type, memberName, temporary, cancellationToken);
                                    changes.Add((pair.Key, current.Id, memberName, MemberRaw(original), temporary));
                                }
                                var attempted = 0;
                                restoreProbe = async () =>
                                {
                                    var failures = new List<string>();
                                    foreach (var change in changes.Take(attempted).Reverse())
                                    {
                                        try
                                        {
                                            await client.SetComponentMemberAsync(change.Id, change.Member, change.Original, CancellationToken.None);
                                            var restored = await client.GetComponentAsync(change.Id, CancellationToken.None);
                                            if (!restored.Members.TryGetValue(change.Member, out var actual) || !MemberMatchesRaw(actual, change.Original))
                                                failures.Add(change.Selector);
                                        }
                                        catch (Exception) { failures.Add(change.Selector); }
                                    }
                                    if (failures.Count > 0)
                                        throw new RLoopException("PROBE_RESTORE_FAILED", "One or more temporary probe values could not be restored.", ExitCodes.OperationFailed,
                                            new Dictionary<string, object?> { ["targets"] = failures });
                                };
                                foreach (var change in changes)
                                {
                                    attempted++;
                                    await client.SetComponentMemberAsync(change.Id, change.Member, change.Temporary, cancellationToken);
                                }
                                probeExecuted = true;
                                capability = "Transactional field probe executed; after assertions were polled and the original value was restored.";
                                break;
                            }
                            default:
                                throw new RLoopException("PROBE_KIND_UNSUPPORTED", $"Probe kind '{probe.Kind}' is not supported.", ExitCodes.ValidationFailed,
                                    suggestions: ["Use kind 'method', 'set-member', or 'set-members'."]);
                        }
                    }
                }

                foreach (var assertion in (test.Assertions ?? []).Where(x => string.Equals(x.Phase, "after", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!probeExecuted)
                    {
                        assertions.Add(new ApplyAssertionResult(test.Name, assertion.Target, "after", true,
                            assertion.Expected is { } skippedExpected ? JsonNode.Parse(skippedExpected.GetRawText()) : null, null,
                            "After assertion was not evaluated because the runtime probe was unavailable or not authorized.", false));
                        continue;
                    }
                    ApplyAssertionResult evaluated;
                    var deadline = Stopwatch.StartNew();
                    do
                    {
                        evaluated = await EvaluateAssertion(test.Name, assertion, "after", byKey, slotsByKey, assetUrls, cancellationToken, refresh: true,
                            childCountBaselines.GetValueOrDefault(assertion));
                        if (evaluated.Passed) break;
                        await Task.Delay(Math.Clamp(test.PollMs, 10, 5000), cancellationToken);
                    } while (deadline.ElapsedMilliseconds < Math.Clamp(test.TimeoutMs, 10, 60_000));
                    assertions.Add(evaluated);
                }
            }
            finally
            {
                if (restoreProbe is not null) await restoreProbe();
            }
            results.Add(new ApplyTestCaseResult(test.Name, assertions.All(x => x.Passed), structuralOnly,
                probeExecuted, capability, assertions));
        }
        return new ApplyTestReport(results.All(x => x.Passed), results.Any(x => x.StructuralOnly), results.Count,
            results.Count(x => x.Passed), results);
    }

    private async Task<ApplyAssertionResult> EvaluateAssertion(string testName, ApplyAssertionSpec assertion, string phase,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken, bool refresh = false, int? childCountBaseline = null)
    {
        if (assertion.Kind.Equals("child-count", StringComparison.OrdinalIgnoreCase))
        {
            var actualCount = await CountChildren(assertion, slots, cancellationToken, refresh);
            var expectedCount = assertion.Delta is { } delta && childCountBaseline is { } baseline ? baseline + delta :
                assertion.Count ?? (assertion.Expected is { } countElement && countElement.ValueKind == JsonValueKind.Number ? countElement.GetInt32() : actualCount);
            return new ApplyAssertionResult(testName, assertion.Target, phase, actualCount == expectedCount,
                JsonValue.Create(expectedCount), JsonValue.Create(actualCount), actualCount == expectedCount ? "Child count matched." : "Child count differed.");
        }
        if (assertion.Target.StartsWith("$slot:", StringComparison.Ordinal))
        {
            var exists = slots.TryGetValue(assertion.Target[6..], out var slot) && (slot.Existing is not null || slot.Id is not null);
            var expectedExists = assertion.Exists ?? true;
            return new ApplyAssertionResult(testName, assertion.Target, phase, exists == expectedExists,
                JsonValue.Create(expectedExists), JsonValue.Create(exists), exists == expectedExists ? "Slot existence matched." : "Slot existence differed.");
        }
        var selector = assertion.Target.StartsWith("$member:", StringComparison.Ordinal) ? assertion.Target[8..] :
            assertion.Target.StartsWith("$component:", StringComparison.Ordinal) ? assertion.Target[11..] : assertion.Target;
        var separator = selector.LastIndexOf('.');
        if (separator <= 0)
        {
            var componentExists = components.TryGetValue(selector, out var componentRuntime) && componentRuntime.Id is not null;
            var expectedExists = assertion.Exists ?? true;
            return new ApplyAssertionResult(testName, assertion.Target, phase, componentExists == expectedExists,
                JsonValue.Create(expectedExists), JsonValue.Create(componentExists), componentExists == expectedExists ? "Component existence matched." : "Component existence differed.");
        }
        if (!components.TryGetValue(selector[..separator], out var runtime) || runtime.Id is null)
            return new ApplyAssertionResult(testName, assertion.Target, phase, false,
                assertion.Expected is { } missingExpected ? JsonNode.Parse(missingExpected.GetRawText()) : null, null, "Component/member target was not found.");
        var memberName = selector[(separator + 1)..];
        var component = refresh ? await client.GetComponentAsync(runtime.Id, cancellationToken) :
            new ComponentInfo(runtime.Id, runtime.ResolvedType ?? runtime.Spec.Type,
                runtime.Existing?.Members ?? new Dictionary<string, MemberValue>());
        if (!component.Members.TryGetValue(memberName, out var member))
            return new ApplyAssertionResult(testName, assertion.Target, phase, assertion.Exists == false,
                assertion.Expected is { } absentExpected ? JsonNode.Parse(absentExpected.GetRawText()) : JsonValue.Create(assertion.Exists), null, "Member does not exist.");
        if (assertion.Exists is not null)
            return new ApplyAssertionResult(testName, assertion.Target, phase, assertion.Exists.Value,
                JsonValue.Create(assertion.Exists), JsonValue.Create(true), assertion.Exists.Value ? "Member exists." : "Member unexpectedly exists.");
        if (assertion.Expected is not { } expected)
            return new ApplyAssertionResult(testName, assertion.Target, phase, true, null, MemberActual(member), "Member was readable.");
        var raw = await ResolveValueAsync(expected, components, slots, assets, cancellationToken);
        return new ApplyAssertionResult(testName, assertion.Target, phase, MemberMatchesRaw(member, raw),
            JsonNode.Parse(expected.GetRawText()), MemberActual(member), MemberMatchesRaw(member, raw) ? "Value matched." : "Value differed.");
    }

    private async Task<int> CountChildren(ApplyAssertionSpec assertion, IReadOnlyDictionary<string, NodeRuntime> slots,
        CancellationToken cancellationToken, bool refresh)
    {
        if (!assertion.Target.StartsWith("$slot:", StringComparison.Ordinal) ||
            !slots.TryGetValue(assertion.Target[6..], out var runtime) || runtime.Id is null)
            return 0;
        var slot = refresh ? await client.GetSlotAsync(runtime.Id, 1, true, cancellationToken) : runtime.Existing;
        if (slot is null) return 0;
        return slot.Children.Count(child =>
            (assertion.Name is null || child.Name.Equals(assertion.Name, StringComparison.Ordinal)) &&
            (assertion.ComponentType is null || child.Components.Any(component => TypeNamesEquivalent(component.Type, assertion.ComponentType))));
    }

    private static JsonNode? MemberActual(MemberValue member) => member.Kind switch
    {
        "reference" => JsonValue.Create(member.TargetId),
        "list" => new JsonArray((member.Elements ?? []).Select(MemberActual).ToArray()),
        "syncObject" or "dictionary" => new JsonObject((member.Members ?? new Dictionary<string, MemberValue>())
            .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, MemberActual(pair.Value)))),
        "field" => NormalizeNode(member.Value),
        "empty" => null,
        _ => NormalizeNode(member.Value)
    };

    private static string MemberRaw(MemberValue member)
    {
        if (member.Kind == "reference") return member.TargetId ?? "null";
        if (member.Value is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (member.Value is JsonObject obj)
        {
            var coordinates = obj.ContainsKey("x") ? new[] { "x", "y", "z", "w" } :
                obj.ContainsKey("r") ? new[] { "r", "g", "b", "a" } : [];
            var present = coordinates.TakeWhile(obj.ContainsKey).ToArray();
            if (present.Length is >= 2 and <= 4)
                return string.Join(',', present.Select(key => obj[key]?.ToJsonString() ?? "0"));
        }
        return member.Value?.ToJsonString() ?? "null";
    }

    private static string SymbolKey(string value) => value[(value.IndexOf(':') + 1)..].Split('.')[0];

    private async Task<PreparedApply> PrepareAsync(ApplyDocument document, ApplyOptions options, CancellationToken cancellationToken, string? resolvedStatePath = null, bool reconcilePending = false)
    {
        document = document with { ResolvedProjectContext = document.ProjectContext };
        document = GeneratedContentMetadata.AddToGeneratedRoots(document, generatedContentSource);
        var offlineValidation = await ApplyDocumentValidator.ValidateAsync(document, cancellationToken: cancellationToken);
        ApplyDocumentValidator.ThrowIfInvalid(offlineValidation);
        var statePath = resolvedStatePath ?? document.ResolveStatePath(options.StateFile);
        var state = ApplyStateStore.Load(statePath, document.Ownership!.Key, options.RequireState);
        var pendingDiagnostics = new List<ApplyDiagnostic>();
        var session = await client.GetSessionInfoAsync(cancellationToken);
        document.BuildBundle?.VerifySession(session);
        if (state.Pending.Count > 0 || state.Slots.Values.Any(s => string.IsNullOrWhiteSpace(s.Id)) ||
            state.Components.Values.Any(c => string.IsNullOrWhiteSpace(c.Id)))
        {
            if (reconcilePending) await ReconcilePendingAsync(statePath, state, session, pendingDiagnostics, cancellationToken);
            else throw WriteUnverified(statePath, state.Pending.FirstOrDefault() ?? ApplyPendingDiscard.LegacyPending(state).First(), "pendingUnresolved");
        }
        try
        {
            // SessionId is kept in state for compatibility only. UniqueSessionId is a per-connection counter,
            // so a match never authorizes reusing a stored live ID; every reuse is proven against the live world.
            state.SessionId = session.UniqueSessionId;
            var migrations = ApplyStateMigrations(document, state);
            var parentSelector = string.IsNullOrWhiteSpace(document.Slot!.Parent) ? "Root" : document.Slot.Parent;
            var parentId = await ResolveSlotIdAsync(parentSelector, cancellationToken);
            var stateDepth = state.Slots.Values.Select(x => x.PathSegments?.Count - 1 ?? x.Path.Count(ch => ch == '/')).DefaultIfEmpty(0).Max();
            var parent = await client.GetSlotAsync(parentId, Math.Clamp(Math.Max(MaxDepth(document.Children) + 1, stateDepth), 0, 64), true, cancellationToken);
            var parentPath = await ObserveAbsolutePathAsync(parent, cancellationToken);
            var parentSegments = await ObserveAbsoluteSegmentsAsync(parent, cancellationToken);
            var snapshots = new List<(SlotInfo Slot, string Path)> { (parent, parentPath) };
            var rootKey = document.Slot!.Key!;
            if (state.Slots.TryGetValue(rootKey, out var rootState) &&
                (!ContainsSlot(parent, rootState.Id) ||
                 !(rootState.PathSegments ?? SlotPaths.LegacySegments(rootState.Path)).SkipLast(1)
                     .SequenceEqual(parentSegments, StringComparer.Ordinal)))
            {
                if (rootState.RuntimeRelocatable)
                {
                    var stable = new StableSlotReference(rootKey, rootState.Id, rootState.Path, state.SessionId,
                        state.OwnershipKey, true, rootState.PathSegments);
                    var check = await VerifyStoredSlotAsync(statePath, stable, cancellationToken, state);
                    if (check.VerifiedId is not null)
                        snapshots.Add((await client.GetSlotAsync(check.VerifiedId, Math.Clamp(stateDepth, 0, 64), true, cancellationToken), rootState.Path));
                    else
                    {
                        SlotInfo relocated;
                        try { relocated = await ResolveRelocatableSlotAsync(statePath, stable, cancellationToken); }
                        catch (RLoopException ex) when (check.Live && ex.Code == "STABLE_RELOCATABLE_SLOT_NOT_FOUND")
                        {
                            throw StoredIdUnverified(rootKey, rootState.Id, rootState.Path, check, ex);
                        }
                        state.Slots[rootKey] = rootState = rootState with { Id = relocated.Id };
                        snapshots.Add((relocated, relocated.Path ?? rootState.Path));
                    }
                }
                else
                try
                {
                    var oldRootId = await ResolveSlotIdAsync(SlotPaths.Selector(rootState.Path, rootState.PathSegments), cancellationToken);
                    snapshots.Add((await client.GetSlotAsync(oldRootId, Math.Clamp(stateDepth, 0, 64), true, cancellationToken), rootState.Path));
                }
                catch (RLoopException ex) when (ex.Code is "SLOT_NOT_FOUND" or "SLOT_PATH_NOT_FOUND")
                {
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    throw StoredIdUnverified(rootKey, rootState.Id, rootState.Path,
                        new StoredSlotCheck(null, false, "recordedPathReadFailed"), ex);
                }
            }
            var prepared = new PreparedApply(document, options, state, statePath, session, parentId, snapshots,
                migrations.Slots, migrations.Components, parentSegments);
            var rootSpec = new ApplyNodeSpec(document.Slot, document.Components, document.Children);
            BuildNode(prepared, rootSpec, null, parent, parentPath, true);
            await RequireStoredSlotIdsAbsentBeforeCreateAsync(prepared, cancellationToken);
            await PrepareRelocationTransformsAsync(prepared, parentPath, cancellationToken);
            BuildAssetPlans(prepared);
            BuildComponentPlans(prepared);
            BuildDeletionPlans(prepared);
            ValidateSlotOwnership(prepared);
            ValidateComponentOwnership(prepared);
            var resolvedTypes = prepared.Components.Where(x => x.Existing is not null)
                .GroupBy(x => x.Spec.Type, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Existing!.Type, StringComparer.Ordinal);
            prepared.Safety = new ApplySafety(client, prepared);
            prepared.Safety.Diagnostics.AddRange(pendingDiagnostics);
            var strictValidation = await ApplyDocumentValidator.ValidateAsync(document, client, cancellationToken, resolvedTypes);
            ApplyDocumentValidator.ThrowIfInvalid(strictValidation);
            return prepared;
        }
        catch (RLoopException error) when (pendingDiagnostics.Count > 0)
        {
            var failure = new RLoopException(error.Code, error.Message, error.ExitCode, error.Context, error.Suggestions, error);
            ApplyDiagnostics.AttachRuntime(failure, pendingDiagnostics.Concat(ApplyDiagnostics.ForException(error, "apply").Diagnostics).ToArray());
            throw failure;
        }
    }

    private static void BuildNode(PreparedApply prepared, ApplyNodeSpec spec, NodeRuntime? parentRuntime,
        SlotInfo parentSnapshot, string parentPath, bool isRoot)
    {
        var path = parentPath.TrimEnd('/') + "/" + spec.Slot.Name;
        var stableKey = spec.Slot.Key ?? "$path:" + path;
        prepared.State.Slots.TryGetValue(stableKey, out var stateSlot);
        var newManagedSlot = stateSlot is null && parentRuntime is not null && prepared.State.Slots.ContainsKey(parentRuntime.StableKey);
        var parentChanged = stateSlot is not null &&
            !(stateSlot.PathSegments ?? SlotPaths.LegacySegments(stateSlot.Path)).SkipLast(1)
                .SequenceEqual(parentRuntime?.PathSegments ?? prepared.ParentSegments, StringComparer.Ordinal);
        var existing = parentChanged ? FindManagedSlot(prepared, stateSlot) : null;
        var destination = newManagedSlot ? null : MatchSlot(prepared, parentSnapshot, spec.Slot.Name, stableKey, stateSlot, path);
        if (parentChanged && destination is not null && destination.Id != existing?.Id)
            throw new RLoopException("APPLY_TARGET_AMBIGUOUS",
                $"Relocation destination '{path}' is occupied by another Slot; the recorded owner was not adopted from the destination.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?>
                {
                    ["key"] = stableKey, ["recordedPath"] = stateSlot!.Path, ["path"] = path,
                    ["sourceId"] = existing?.Id, ["ids"] = new[] { destination.Id }, ["reason"] = "relocationDestinationOccupied"
                });
        existing ??= parentChanged ? null : destination;
        existing ??= FindManagedSlot(prepared, stateSlot);
        if (isRoot && existing is not null && stateSlot is null && !prepared.Options.Adopt)
            throw new RLoopException("APPLY_OWNERSHIP_UNVERIFIED",
                $"Slot '{path}' already exists but is not bound to ownership '{prepared.Document.Ownership!.Key}'.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["path"] = path, ["stateFile"] = prepared.StatePath },
                ["Inspect the target, then re-run with --adopt to bind this exact root Slot without deleting it."]);
        var desiredParentId = parentRuntime?.Existing?.Id ?? (parentRuntime is null ? prepared.ParentId : null);
        var relocating = existing is not null && (desiredParentId is null || existing.ParentId != desiredParentId);
        if (relocating && spec.Slot.RuntimeRelocatable && stateSlot is not null &&
            NormalizePath(stateSlot.Path) == NormalizePath(path))
            throw new RLoopException("APPLY_RUNTIME_RELOCATABLE_ACTIVE",
                $"Managed Slot '{stableKey}' is currently outside its declared parent, which is expected for a runtime-relocatable item.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?>
                {
                    ["key"] = stableKey, ["declaredPath"] = path, ["currentParentId"] = existing!.ParentId
                }, ["Drop or return the item to its declared parent before apply; the command stopped before mutation."]);
        var action = existing is null ? "create" : relocating ? "relocate" : SlotNeedsUpdate(existing, spec.Slot) ? "update" : "no-op";
        var node = new NodeRuntime(spec.Slot, spec.Components ?? [], parentRuntime, existing, stableKey, path, action);
        node.PathSegments = [.. parentRuntime?.PathSegments ?? prepared.ParentSegments, spec.Slot.Name];
        prepared.Nodes.Add(node);
        var planAction = action == "update" && existing?.Name != spec.Slot.Name ? "rename" : action;
        var migratedFrom = prepared.SlotMigrations.GetValueOrDefault(stableKey);
        prepared.Entries.Add(new ApplyPlanEntry(planAction, "slot", path, stableKey,
            Reason: action == "create" ? "managed Slot does not exist" : action == "relocate" ?
                $"stable key '{stableKey}' preserves identity while the parent changes; relocationTransform={spec.Slot.RelocationTransform}" : planAction == "rename" ?
                $"stable key '{stableKey}' preserves identity while the name changes from '{existing!.Name}' to '{spec.Slot.Name}'" :
                action == "update" ? "one or more managed transforms differ" : migratedFrom is not null ?
                $"stable key migrated from '{migratedFrom}' without recreating the Slot" : "Slot already matches"));

        var childParent = existing ?? new SlotInfo("", spec.Slot.Name, null, null, null, null, null, null, null, false, [], []);
        for (var i = 0; i < (spec.Children?.Count ?? 0); i++)
            BuildNode(prepared, spec.Children![i], node, childParent, path, false);
    }

    private static void BuildComponentPlans(PreparedApply prepared)
    {
        foreach (var node in prepared.Nodes)
        {
            var typeOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var spec in node.ComponentSpecs)
            {
                var normalizedType = NormalizeType(spec.Type);
                var ordinal = typeOrdinals.GetValueOrDefault(normalizedType);
                typeOrdinals[normalizedType] = ordinal + 1;
                var stableKey = spec.Key ?? StableEffectiveKeys.Component(node.StableKey, normalizedType, ordinal);
                prepared.State.Components.TryGetValue(stableKey, out var stateComponent);
                var relocating = stateComponent is not null && stateComponent.SlotKey != node.StableKey;
                var topologyTargets = stateComponent is null ? null :
                    ResolveStateTopologyTargets(prepared, stateComponent, new HashSet<string>(StringComparer.Ordinal));
                // A new key on an already managed Slot is a new object, not an ordinal rename.
                // Otherwise it can alias a retained key or a stale key scheduled for pruning.
                var newManagedComponent = stateComponent is null && prepared.State.Slots.ContainsKey(node.StableKey);
                var existing = relocating || newManagedComponent ? null :
                    MatchComponent(prepared, node.Existing?.Components ?? [], spec.Type, ordinal, stateComponent,
                        topologyTargets);
                var componentIndex = existing is null
                    ? (node.Existing?.Components.Count ?? 0) + prepared.Components.Count(candidate => candidate.Node == node && candidate.Existing is null)
                    : node.Existing!.Components.ToList().FindIndex(candidate => candidate.Id == existing.Id);
                var runtime = new ComponentRuntime(spec, node, existing, stableKey, ordinal,
                    node.Path + "/@" + (spec.Key ?? normalizedType + "[" + ordinal + "]"), componentIndex);
                if (relocating)
                {
                    var sourceSlot = FindStateSlot(prepared, stateComponent!.SlotKey);
                    runtime.RelocationSource = sourceSlot is null ? null :
                        MatchComponent(prepared, sourceSlot.Components, spec.Type, ordinal, stateComponent,
                            topologyTargets);
                    if (runtime.RelocationSource?.Id == existing?.Id) runtime.RelocationSource = null;
                }
                prepared.Components.Add(runtime);
            }
        }

        var existingByKey = prepared.Components.Where(x => !string.IsNullOrWhiteSpace(x.Spec.Key) && x.Existing is not null)
            .ToDictionary(x => x.Spec.Key!, x => x, StringComparer.Ordinal);
        var slotsByKey = prepared.Nodes.ToDictionary(x => x.StableKey, StringComparer.Ordinal);
        var assetUrls = prepared.Assets.Where(x => x.DirectUrl is not null || prepared.State.Assets.ContainsKey(x.Key))
            .ToDictionary(x => x.Key, x => x.DirectUrl ?? prepared.State.Assets[x.Key].Url, StringComparer.Ordinal);
        foreach (var component in prepared.Components)
        {
            var action = component.RelocationSource is not null ? "relocate" : component.Existing is null ? "create" : "no-op";
            var reason = component.RelocationSource is not null
                ? $"stable key '{component.StableKey}' moves the Component to Slot '{component.Node.StableKey}'"
                : component.Existing is null ? "managed Component does not exist" : "Component and fields already match";
            var diffs = new List<ApplyMemberDiff>();
            foreach (var mode in component.Spec.PropertyModes ?? new Dictionary<string, string>())
                if (mode.Value is "runtime" or "driver-owned")
                    diffs.Add(new ApplyMemberDiff(mode.Key, "preserve", Reason: mode.Value + ": ordinary apply does not write this member, including creation"));
            if (component.Existing is not null)
            {
                foreach (var field in ManagedFields(component.Spec))
                {
                    var resolvable = TryResolveRawForPlan(field.Value, existingByKey, slotsByKey, assetUrls, out var raw);
                    MemberValue? current = null;
                    var found = component.Existing.Members?.TryGetValue(field.Key, out current) == true;
                    if (!resolvable || !found || !MemberMatchesRaw(current!, raw))
                    {
                        action = "update";
                        reason = "one or more fields differ or depend on a new target";
                        diffs.Add(found && current!.Kind == "list" && resolvable
                            ? ListDiff(field.Key, current, raw)
                            : new ApplyMemberDiff(field.Key, "replace", Reason: !resolvable ? "depends on a target created by this apply" : !found ? "member is absent from snapshot" : "value differs"));
                    }
                }
            }
            var migratedFrom = prepared.ComponentMigrations.GetValueOrDefault(component.StableKey);
            prepared.Entries.Add(new ApplyPlanEntry(action, "component", component.Path, component.StableKey,
                component.Spec.Type, component.Spec.Fields?.Keys.ToArray(), reason, diffs));
            if (action == "no-op" && migratedFrom is not null)
                prepared.Entries[^1] = prepared.Entries[^1] with
                { Reason = $"stable key migrated from '{migratedFrom}' without recreating the Component" };
        }
    }

    private static ApplyMemberDiff ListDiff(string member, MemberValue current, string desiredRaw)
    {
        var desired = JsonNode.Parse(desiredRaw) as JsonArray ?? [];
        var actual = new JsonArray((current.Elements ?? []).Select(MemberActual).ToArray());
        var desiredCounts = desired.GroupBy(x => x?.ToJsonString() ?? "null").ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var actualCounts = actual.GroupBy(x => x?.ToJsonString() ?? "null").ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var added = desired.Where(item => actualCounts.GetValueOrDefault(item?.ToJsonString() ?? "null") <
            desired.TakeWhile(candidate => !ReferenceEquals(candidate, item)).Count(candidate => (candidate?.ToJsonString() ?? "null") == (item?.ToJsonString() ?? "null")) + 1).Select(x => x?.DeepClone()).ToArray();
        var removed = actual.Where(item => desiredCounts.GetValueOrDefault(item?.ToJsonString() ?? "null") <
            actual.TakeWhile(candidate => !ReferenceEquals(candidate, item)).Count(candidate => (candidate?.ToJsonString() ?? "null") == (item?.ToJsonString() ?? "null")) + 1).Select(x => x?.DeepClone()).ToArray();
        return new ApplyMemberDiff(member, "list", added, removed,
            "ResoniteLink 0.13.1 applies the list atomically as one member; the preview exposes element additions/removals.");
    }

    private static void BuildAssetPlans(PreparedApply prepared)
    {
        foreach (var pair in prepared.Document.Assets ?? new Dictionary<string, ApplyAssetSpec>())
        {
            string resolved;
            string hash;
            string? direct = null;
            var hasAbsoluteUri = Uri.TryCreate(pair.Value.Source, UriKind.Absolute, out var uri);
            if (!Path.IsPathFullyQualified(pair.Value.Source) && hasAbsoluteUri && uri!.Scheme != Uri.UriSchemeFile)
            {
                direct = uri.ToString();
                resolved = direct;
                hash = "uri:" + direct;
            }
            else
            {
                var sourceDirectory = prepared.Document.ResourceDirectory;
                resolved = uri?.Scheme == Uri.UriSchemeFile ? uri.LocalPath : Path.GetFullPath(pair.Value.Source, sourceDirectory);
                if (!File.Exists(resolved))
                    throw new RLoopException("ASSET_SOURCE_NOT_FOUND", $"Asset '{pair.Key}' source '{resolved}' does not exist.", ExitCodes.NotFound);
                using var stream = File.OpenRead(resolved);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            var unchanged = direct is not null || prepared.State.Assets.TryGetValue(pair.Key, out var state) &&
                state.SourceHash == hash && state.Kind.Equals(pair.Value.Kind, StringComparison.OrdinalIgnoreCase);
            var runtime = new AssetRuntime(pair.Key, pair.Value, resolved, hash, direct, unchanged ? "no-op" : "create");
            prepared.Assets.Add(runtime);
            prepared.Entries.Insert(0, new ApplyPlanEntry(runtime.Action, "asset", "$assets/" + pair.Key, pair.Key, pair.Value.Kind,
                Reason: direct is not null ? "asset URI is already addressable" : unchanged ?
                    "source hash and imported URL match state" : "source is new or changed and must be imported"));
        }
    }

    private static void BuildDeletionPlans(PreparedApply prepared)
    {
        var liveSlotKeys = prepared.Nodes.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var liveComponentKeys = prepared.Components.Select(x => x.StableKey).ToHashSet(StringComparer.Ordinal);
        var rootId = prepared.Nodes[0].Existing?.Id;
        var snapshots = prepared.SnapshotSlots.GroupBy(slot => slot.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        bool IsWithin(string id, string? ancestor, bool includeRoot = true)
        {
            if (ancestor is null) return false;
            if (!includeRoot && id == ancestor) return false;
            for (var depth = 0; depth <= 64 && snapshots.TryGetValue(id, out var current); depth++)
            {
                if (id == ancestor) return true;
                if (current.ParentId is null) break;
                id = current.ParentId;
            }
            return false;
        }
        SlotInfo? ResolveOwned(ApplyStateSlot state) => FindManagedSlot(prepared, state);

        var staleSlots = new List<(string Key, ApplyStateSlot State, SlotInfo Slot, string Path)>();
        foreach (var stateSlot in prepared.State.Slots.Where(x => !liveSlotKeys.Contains(x.Key)).ToArray())
        {
            var slot = ResolveOwned(stateSlot.Value);
            if (slot is null || !IsWithin(slot.Id, rootId, false) || slot.Id == "Root") continue;
            staleSlots.Add((stateSlot.Key, stateSlot.Value, slot, slot.Path ?? stateSlot.Value.Path));
        }
        var parentSlotDeletions = staleSlots.OrderBy(x => x.Path.Count(ch => ch == '/'))
            .Where(candidate => !staleSlots.Any(other => other.Slot.Id != candidate.Slot.Id && IsWithin(candidate.Slot.Id, other.Slot.Id)))
            .ToArray();
        var coveredSlotKeys = staleSlots.Where(stateSlot => parentSlotDeletions.Any(deletion =>
                IsWithin(stateSlot.Slot.Id, deletion.Slot.Id)))
            .Select(stateSlot => stateSlot.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var stateComponent in prepared.State.Components.Where(x => !liveComponentKeys.Contains(x.Key) &&
                     !coveredSlotKeys.Contains(x.Value.SlotKey)).ToArray())
        {
            if (!prepared.State.Slots.TryGetValue(stateComponent.Value.SlotKey, out var stateSlot)) continue;
            var slot = ResolveOwned(stateSlot);
            if (slot is null || !IsWithin(slot.Id, rootId)) continue;
            var topology = ResolveStateTopologyTargets(prepared, stateComponent.Value, new HashSet<string>(StringComparer.Ordinal));
            var component = MatchComponent(prepared, slot.Components, stateComponent.Value.Type,
                stateComponent.Value.TypeOrdinal, stateComponent.Value, topology);
            if (component is null) continue;
            var deletion = new DeletionRuntime("component", stateComponent.Key, component.Id,
                slot.Path + "/@" + stateComponent.Key, "stable key is no longer declared inside the owned boundary");
            prepared.Deletions.Add(deletion);
            prepared.Entries.Add(new ApplyPlanEntry("delete", deletion.Kind, deletion.Path, deletion.Key,
                component.Type, Reason: deletion.Reason));
        }

        foreach (var staleSlot in parentSlotDeletions)
        {
            var removedSlotKeys = staleSlots.Where(stateSlot => IsWithin(stateSlot.Slot.Id, staleSlot.Slot.Id))
                .Select(stateSlot => stateSlot.Key).ToArray();
            var removedComponentKeys = prepared.State.Components.Where(component => !liveComponentKeys.Contains(component.Key) &&
                    removedSlotKeys.Contains(component.Value.SlotKey, StringComparer.Ordinal))
                .Select(component => component.Key).ToArray();
            var deletion = new DeletionRuntime("slot", staleSlot.Key, staleSlot.Slot.Id, staleSlot.Path,
                $"stable parent Slot is no longer declared; one Slot delete covers {removedSlotKeys.Length} managed Slot(s) and {removedComponentKeys.Length} Component(s)",
                removedSlotKeys, removedComponentKeys);
            prepared.Deletions.Add(deletion);
            prepared.Entries.Add(new ApplyPlanEntry("delete", deletion.Kind, deletion.Path, deletion.Key,
                Reason: deletion.Reason));
        }
    }

    private static void ValidateComponentOwnership(PreparedApply prepared)
    {
        var claims = prepared.Components.Where(component => component.Existing is not null)
            .Select(component => (Id: component.Existing!.Id, Key: component.StableKey, Kind: "live"))
            .Concat(prepared.Components.Where(component => component.RelocationSource is not null)
                .Select(component => (Id: component.RelocationSource!.Id, Key: component.StableKey, Kind: "relocation")))
            .Concat(prepared.Deletions.Where(deletion => deletion.Kind == "component")
                .Select(deletion => (deletion.Id, deletion.Key, Kind: "delete")));
        var conflicts = claims.GroupBy(claim => claim.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new { id = group.Key, claims = group.Select(claim => new { claim.Key, claim.Kind }).ToArray() })
            .ToArray();
        if (conflicts.Length > 0)
            throw new RLoopException("APPLY_COMPONENT_OWNERSHIP_CONFLICT",
                "Multiple managed keys or deletion operations claim the same runtime Component. No mutations were performed.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["conflicts"] = conflicts },
                ["Inspect the exact conflicting Components and preserve the checkpoint. Use migrateFrom for an intentional key rename; never repair this by guessing IDs."]);
    }

    private static void ValidateSlotOwnership(PreparedApply prepared)
    {
        var claims = prepared.Nodes.Where(node => node.Existing is not null)
            .Select(node => (Id: node.Existing!.Id, Key: node.StableKey, Kind: "live"))
            .Concat(prepared.Deletions.Where(deletion => deletion.Kind == "slot")
                .Select(deletion => (deletion.Id, deletion.Key, Kind: "delete")));
        var conflicts = claims.GroupBy(claim => claim.Id, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => new { id = group.Key, claims = group.Select(claim => new { claim.Key, claim.Kind }).ToArray() }).ToArray();
        if (conflicts.Length > 0)
            throw new RLoopException("APPLY_SLOT_OWNERSHIP_CONFLICT", "Multiple managed keys or deletion operations claim the same Slot. No mutations were performed.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["conflicts"] = conflicts });
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveFieldsAsync(
        IReadOnlyDictionary<string, JsonElement>? fields,
        IReadOnlyDictionary<string, ComponentRuntime> components,
        IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields ?? new Dictionary<string, JsonElement>())
            result[field.Key] = await ResolveValueAsync(field.Value, components, slots, assets, cancellationToken);
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> MergeCreateFields(ApplyComponentSpec component)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in component.InitialFields ?? new Dictionary<string, JsonElement>()) result[field.Key] = field.Value;
        foreach (var field in component.Fields ?? new Dictionary<string, JsonElement>()) result[field.Key] = field.Value;
        return result.Where(f => WritesProperty(component, f.Key)).ToDictionary(StringComparer.Ordinal);
    }

    private static bool WritesProperty(ApplyComponentSpec spec, string member) =>
        spec.PropertyModes?.GetValueOrDefault(member) is not ("runtime" or "driver-owned");

    private static IReadOnlyDictionary<string, JsonElement> ManagedFields(ApplyComponentSpec spec) =>
        (spec.Fields ?? new Dictionary<string, JsonElement>()).Where(f => WritesProperty(spec, f.Key)).ToDictionary(StringComparer.Ordinal);

    private static bool ContainsWorldReference(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { } text &&
            (text.StartsWith("$ref:", StringComparison.Ordinal) || text.StartsWith("$component:", StringComparison.Ordinal) ||
             text.StartsWith("$member:", StringComparison.Ordinal) || text.StartsWith("$slot:", StringComparison.Ordinal) ||
             text.StartsWith("$slot-member:", StringComparison.Ordinal)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsWorldReference),
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsWorldReference(property.Value)),
        _ => false
    };

    private async Task<string> ResolveValueAsync(JsonElement element,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken)
    {
        if (element.ValueKind == JsonValueKind.String)
            return await ResolveSymbolAsync(element.GetString() ?? string.Empty, components, slots, assets, cancellationToken);
        if (element.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            var node = await ResolveCompositeAsync(element, components, slots, assets, cancellationToken);
            return node?.ToJsonString() ?? "null";
        }
        return element.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }

    private async Task<JsonNode?> ResolveCompositeAsync(JsonElement element,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets, CancellationToken cancellationToken)
    {
        if (element.ValueKind == JsonValueKind.String)
            return JsonValue.Create(await ResolveSymbolAsync(element.GetString() ?? string.Empty, components, slots, assets, cancellationToken));
        if (element.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var child in element.EnumerateArray()) array.Add(await ResolveCompositeAsync(child, components, slots, assets, cancellationToken));
            return array;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var obj = new JsonObject();
            foreach (var property in element.EnumerateObject()) obj[property.Name] = await ResolveCompositeAsync(property.Value, components, slots, assets, cancellationToken);
            return obj;
        }
        return JsonNode.Parse(element.GetRawText());
    }

    private async Task<string> ResolveSymbolAsync(string value,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets,
        CancellationToken cancellationToken)
    {
        if (StableSelectorSyntax.TryParse(value, out var stable))
        {
            if (stable!.Kind == "slot-member")
            {
                if (!slots.TryGetValue(stable.Key, out var target) || target.Id is null)
                    throw new RLoopException("APPLY_REFERENCE_NOT_FOUND", $"Slot reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
                var observed = await client.GetSlotAsync(target.Id, 0, false, cancellationToken);
                return RequireSlotMember(observed, stable.MemberName!, value).Id!;
            }
            if (stable!.Kind == "component")
                return components.TryGetValue(stable.Key, out var component) && component.Id is not null
                    ? component.Id : throw UnknownApplyReference(value, components.Keys);
            if (stable.Kind == "slot")
                return slots.TryGetValue(stable.Key, out var slot) && slot.Id is not null
                    ? slot.Id : throw new RLoopException("APPLY_REFERENCE_NOT_FOUND", $"Slot reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
            if (!components.TryGetValue(stable.Key, out var memberComponent) || memberComponent.Id is null)
                throw UnknownApplyReference(value, components.Keys);
            var memberName = stable.MemberName!;
            if (memberComponent.MemberIds.TryGetValue(memberName, out var cached)) return cached;
            if (memberComponent.Existing?.Members is not null && memberComponent.Existing.Members.TryGetValue(memberName, out var summaryMember) &&
                !string.IsNullOrWhiteSpace(summaryMember.Id))
            {
                memberComponent.MemberIds[memberName] = summaryMember.Id;
                return summaryMember.Id;
            }
            var inspected = await client.GetComponentAsync(memberComponent.Id, cancellationToken);
            foreach (var member in inspected.Members.Where(x => !string.IsNullOrWhiteSpace(x.Value.Id)))
                memberComponent.MemberIds[member.Key] = member.Value.Id!;
            if (memberComponent.MemberIds.TryGetValue(memberName, out var id)) return id;
            throw new RLoopException("APPLY_MEMBER_REFERENCE_NOT_FOUND", $"Member reference '{value}' was not found.", ExitCodes.ValidationFailed);
        }
        if (value.StartsWith("$asset:", StringComparison.Ordinal))
        {
            var key = value[7..];
            return assets.TryGetValue(key, out var url) ? url : throw new RLoopException(
                "APPLY_REFERENCE_NOT_FOUND", $"Asset reference '{value}' could not be resolved.", ExitCodes.ValidationFailed);
        }
        return value;
    }

    private static MemberValue RequireSlotMember(SlotInfo slot, string name, string selector)
    {
        if (slot.Members?.TryGetValue(name, out var member) == true && !string.IsNullOrWhiteSpace(member.Id)) return member;
        throw new RLoopException("APPLY_MEMBER_REFERENCE_NOT_FOUND", $"Slot member '{selector}' has no observed field ID.", ExitCodes.ValidationFailed);
    }

    private static bool CanResolveAll(IReadOnlyDictionary<string, JsonElement>? fields,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots) =>
        (fields ?? new Dictionary<string, JsonElement>()).Values.All(value => CanResolve(value, components, slots));

    private static bool CanResolve(JsonElement value, IReadOnlyDictionary<string, ComponentRuntime> components,
        IReadOnlyDictionary<string, NodeRuntime> slots)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (StableSelectorSyntax.TryParse(text, out var stable))
                return stable!.Kind is "slot" or "slot-member"
                    ? slots.TryGetValue(stable.Key, out var slot) && slot.Id is not null
                    : components.TryGetValue(stable.Key, out var runtime) && runtime.Id is not null;
            return true;
        }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(x => CanResolve(x, components, slots));
        if (value.ValueKind == JsonValueKind.Object) return value.EnumerateObject().All(x => CanResolve(x.Value, components, slots));
        return true;
    }

    private static bool TryResolveRawForPlan(JsonElement value,
        IReadOnlyDictionary<string, ComponentRuntime> components, IReadOnlyDictionary<string, NodeRuntime> slots,
        IReadOnlyDictionary<string, string> assets, out string raw)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (StableSelectorSyntax.TryParse(text, out var stable))
            {
                if (stable!.Kind == "component")
                {
                    if (components.TryGetValue(stable.Key, out var target) && target.Existing is not null) { raw = target.Existing.Id; return true; }
                    raw = string.Empty; return false;
                }
                if (stable.Kind == "slot")
                {
                    if (slots.TryGetValue(stable.Key, out var target) && target.Existing is not null) { raw = target.Existing.Id; return true; }
                    raw = string.Empty; return false;
                }
                if (stable.Kind == "slot-member")
                {
                    if (slots.TryGetValue(stable.Key, out var target) &&
                        target.Existing?.Members?.TryGetValue(stable.MemberName!, out var slotMember) == true &&
                        !string.IsNullOrWhiteSpace(slotMember.Id)) { raw = slotMember.Id; return true; }
                    raw = string.Empty; return false;
                }
                if (components.TryGetValue(stable.Key, out var memberTarget) &&
                    memberTarget.Existing?.Members is not null && memberTarget.Existing.Members.TryGetValue(stable.MemberName!, out var member) &&
                    !string.IsNullOrWhiteSpace(member.Id)) { raw = member.Id; return true; }
                raw = string.Empty; return false;
            }
            if (text.StartsWith("$asset:", StringComparison.Ordinal))
            {
                if (assets.TryGetValue(text[7..], out var url)) { raw = url; return true; }
                raw = string.Empty; return false;
            }
            raw = text; return true;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var item in value.EnumerateArray())
            {
                if (!TryResolveRawForPlan(item, components, slots, assets, out var itemRaw)) { raw = string.Empty; return false; }
                if (item.ValueKind == JsonValueKind.String) array.Add(itemRaw);
                else array.Add(JsonNode.Parse(itemRaw));
            }
            raw = array.ToJsonString(); return true;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var obj = new JsonObject();
            foreach (var property in value.EnumerateObject())
            {
                if (!TryResolveRawForPlan(property.Value, components, slots, assets, out var propertyRaw)) { raw = string.Empty; return false; }
                obj[property.Name] = property.Value.ValueKind == JsonValueKind.String ? JsonValue.Create(propertyRaw) : JsonNode.Parse(propertyRaw);
            }
            raw = obj.ToJsonString(); return true;
        }
        raw = value.ValueKind switch
        {
            JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "null", _ => value.GetRawText()
        };
        return true;
    }

    private static bool MemberMatchesRaw(MemberValue member, string raw)
    {
        if (member.Kind == "reference") return string.Equals(member.TargetId ?? "null", raw, StringComparison.Ordinal);
        if (member.Kind is "syncObject" or "dictionary")
        {
            JsonNode? desired;
            try { desired = JsonNode.Parse(raw); } catch (JsonException) { return false; }
            if (desired is not JsonObject obj || member.Members is null) return false;
            if (member.Kind == "dictionary" && obj.Count != member.Members.Count) return false;
            return obj.All(pair => member.Members.TryGetValue(pair.Key, out var child) &&
                MemberMatchesRaw(child, pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text : pair.Value?.ToJsonString() ?? "null"));
        }
        if (member.Kind == "list")
        {
            JsonNode? desired;
            try { desired = JsonNode.Parse(raw); } catch (JsonException) { return false; }
            if (desired is not JsonArray desiredArray || member.Elements is null || desiredArray.Count != member.Elements.Count) return false;
            for (var i = 0; i < desiredArray.Count; i++)
            {
                var desiredValue = desiredArray[i];
                if (!MemberMatchesRaw(member.Elements[i], desiredValue is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text : desiredValue?.ToJsonString() ?? "null")) return false;
            }
            return true;
        }
        if (member.Kind != "field") return false;
        var currentNode = NormalizeNode(member.Value);
        JsonNode? desiredNode;
        if (IsStringLike(member.Type)) desiredNode = JsonValue.Create(raw);
        else desiredNode = MemberValueSyntax.NormalizeTupleOrJson(member.Type, raw, currentNode is JsonArray array ? array.Count : null);
        desiredNode = NormalizeNode(desiredNode);
        return JsonEquivalent(currentNode, desiredNode);
    }

    private static JsonNode? NormalizeNode(JsonNode? node)
    {
        if (node is not JsonObject obj) return node?.DeepClone();
        var orderedNames = obj.ContainsKey("x") ? new[] { "x", "y", "z", "w" } :
            obj.ContainsKey("r") ? new[] { "r", "g", "b", "a" } : [];
        if (orderedNames.Length == 0) return node.DeepClone();
        var result = new JsonArray();
        foreach (var name in orderedNames)
            if (obj.TryGetPropertyValue(name, out var value)) result.Add(value?.DeepClone());
        return result;
    }

    private static bool JsonEquivalent(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is JsonArray la && right is JsonArray ra)
            return la.Count == ra.Count && Enumerable.Range(0, la.Count).All(i => JsonEquivalent(la[i], ra[i]));
        if (left is JsonValue lv && right is JsonValue rv)
        {
            if (TryNumeric(lv, out var ld) && TryNumeric(rv, out var rd))
                return double.IsNaN(ld) && double.IsNaN(rd) || Math.Abs(ld - rd) <= 0.00001 * Math.Max(1, Math.Max(Math.Abs(ld), Math.Abs(rd)));
            return left.ToJsonString() == right.ToJsonString();
        }
        return JsonNode.DeepEquals(left, right);
    }

    private static bool TryNumeric(JsonValue value, out double number)
    {
        number = 0;
        return value.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static bool IsStringLike(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        var bare = NormalizeType(type).Split('.').Last();
        return bare.Equals("string", StringComparison.OrdinalIgnoreCase) || bare.Equals("Uri", StringComparison.OrdinalIgnoreCase) ||
               bare.Equals("Type", StringComparison.OrdinalIgnoreCase) || bare.Contains("Enum", StringComparison.OrdinalIgnoreCase);
    }

    // Sibling names are counted before any ID is looked at: several same-named siblings are ambiguous even when one
    // of them carries the stored ID, because the ID alone is not an ownership proof.
    private static SlotInfo? MatchSlot(PreparedApply prepared, SlotInfo parent, string desiredName, string stableKey,
        ApplyStateSlot? state, string desiredPath)
    {
        SlotInfo[] candidates = [];
        var recordedName = state is null ? null : RecordedName(state);
        if (state is { RuntimeRelocatable: true })
        {
            // A relocatable Slot is identified by its Component evidence; the name only narrows the search.
            var evidence = prepared.State.Components.Values.Where(component => component.SlotKey == stableKey).ToArray();
            if (!string.IsNullOrWhiteSpace(recordedName))
                candidates = parent.Children.Where(x => x.Name == recordedName &&
                    RelocatableEvidenceMatches(x.Components, evidence)).ToArray();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(recordedName)) candidates = parent.Children.Where(x => x.Name == recordedName).ToArray();
            if (candidates.Length == 0) candidates = parent.Children.Where(x => x.Name == desiredName).ToArray();
        }
        if (candidates.Length > 1)
            throw new RLoopException("APPLY_TARGET_AMBIGUOUS", $"Multiple Slots match managed target '{desiredPath}'.", ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["ids"] = candidates.Select(x => x.Id).ToArray() });
        return candidates.SingleOrDefault();
    }

    // A Slot about to be created must not leave an older copy behind. If the stored ID still answers in the world
    // (hand-renamed or moved Slot, or an ID reused by an unrelated object) and neither the path nor the evidence found
    // the owned Slot, the identity is undecidable and the apply stops before any write.
    private async Task RequireStoredSlotIdsAbsentBeforeCreateAsync(PreparedApply prepared, CancellationToken cancellationToken)
    {
        foreach (var node in prepared.Nodes.Where(node => node.Existing is null))
        {
            if (!prepared.State.Slots.TryGetValue(node.StableKey, out var stateSlot) || string.IsNullOrWhiteSpace(stateSlot.Id)) continue;
            var inSnapshot = prepared.SnapshotSlots.FirstOrDefault(slot => slot.Id == stateSlot.Id);
            if (inSnapshot is not null)
                throw StoredIdUnverified(node.StableKey, stateSlot.Id, stateSlot.Path, new StoredSlotCheck(null, true,
                    $"a Slot named '{inSnapshot.Name}' holds the stored ID but is not the recorded Slot, and no Slot named '{RecordedName(stateSlot)}' exists to re-resolve to",
                    inSnapshot.Name, inSnapshot.Path));
            SlotInfo probe;
            try { probe = await client.GetSlotAsync(stateSlot.Id, 0, false, cancellationToken); }
            catch (RLoopException ex) when (ex.Code == "SLOT_NOT_FOUND")
            {
                // A failed ID lookup alone does not prove that the recorded path is absent (IDs may have changed).
                string oldId;
                try { oldId = await ResolveSlotIdAsync(SlotPaths.Selector(stateSlot.Path, stateSlot.PathSegments), cancellationToken); }
                catch (RLoopException pathError) when (pathError.Code is "SLOT_NOT_FOUND" or "SLOT_PATH_NOT_FOUND") { continue; }
                catch (Exception pathError) when (pathError is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    throw StoredIdUnverified(node.StableKey, stateSlot.Id, stateSlot.Path,
                        new StoredSlotCheck(null, false, "recordedPathReadFailed"), pathError);
                }
                if (stateSlot.RuntimeRelocatable)
                {
                    SlotInfo oldSlot;
                    try { oldSlot = await client.GetSlotAsync(oldId, 0, true, cancellationToken); }
                    catch (Exception pathError) when (pathError is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        throw StoredIdUnverified(node.StableKey, stateSlot.Id, stateSlot.Path,
                            new StoredSlotCheck(null, false, "recordedPathReadFailed"), pathError);
                    }
                    var evidence = prepared.State.Components.Values.Where(component => component.SlotKey == node.StableKey).ToArray();
                    if (!oldSlot.IsReferenceOnly && !RelocatableEvidenceMatches(oldSlot.Components, evidence)) continue;
                }
                throw StoredIdUnverified(node.StableKey, stateSlot.Id, stateSlot.Path,
                    new StoredSlotCheck(null, true, "recordedPathStillPresent", ObservedPath: stateSlot.Path));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw StoredIdUnverified(node.StableKey, stateSlot.Id, stateSlot.Path,
                    new StoredSlotCheck(null, false, "storedIdReadFailed"), ex);
            }
            string? observedPath = null;
            try { observedPath = probe.IsReferenceOnly ? null : await ObserveAbsolutePathAsync(probe, cancellationToken); }
            catch (RLoopException ex) when (ex.Code is "SLOT_PATH_UNRESOLVED" or "SLOT_NOT_FOUND" or "RESONITE_OPERATION_FAILED") { }
            throw StoredIdUnverified(node.StableKey, stateSlot.Id, stateSlot.Path, new StoredSlotCheck(null, true,
                probe.IsReferenceOnly ? "the stored ID answered with a reference-only placeholder"
                    : $"a Slot named '{probe.Name}' holds the stored ID outside the recorded path, and no Slot at the recorded path exists to re-resolve to",
                probe.IsReferenceOnly ? null : probe.Name, observedPath));
        }
    }

    private static string? RecordedName(ApplyStateSlot state) =>
        (state.PathSegments ?? SlotPaths.LegacySegments(state.Path)).LastOrDefault();

    private static SlotInfo? FindManagedSlot(PreparedApply prepared, ApplyStateSlot? state)
    {
        if (state is null) return null;
        IReadOnlyList<ApplyStateComponent>? evidence = null;
        if (state.RuntimeRelocatable)
        {
            // A relocatable Slot may have left its declared parent, so its path proves nothing. Both the stored ID
            // candidate and the path candidate need the same one-to-one Component evidence.
            var slotKey = prepared.State.Slots.FirstOrDefault(pair => ReferenceEquals(pair.Value, state)).Key;
            evidence = slotKey is null ? [] : prepared.State.Components.Values.Where(component => component.SlotKey == slotKey).ToArray();
            if (!string.IsNullOrWhiteSpace(state.Id))
            {
                var byId = prepared.SnapshotSlots.Where(slot => slot.Id == state.Id && slot.Name == RecordedName(state) &&
                    RelocatableEvidenceMatches(slot.Components, evidence)).ToArray();
                if (byId.Length == 1) return byId[0];
            }
        }
        var normalizedPath = NormalizePath(state.Path);
        var byPath = prepared.SnapshotSlots.Where(slot => (state.PathSegments is not null
            ? prepared.SnapshotSegments[slot.Id].SequenceEqual(state.PathSegments, StringComparer.Ordinal)
            : NormalizePath(slot.Path ?? string.Empty) == normalizedPath) &&
            (evidence is null || RelocatableEvidenceMatches(slot.Components, evidence))).ToArray();
        if (byPath.Length > 1)
            throw new RLoopException("APPLY_TARGET_AMBIGUOUS", $"Multiple Slots match managed state path '{state.Path}'.",
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["ids"] = byPath.Select(slot => slot.Id).ToArray() });
        if (byPath.Length == 1) return byPath[0];
        return null;
    }

    private static SlotInfo? FindStateSlot(PreparedApply prepared, string slotKey) =>
        prepared.State.Slots.TryGetValue(slotKey, out var stateSlot) ? FindManagedSlot(prepared, stateSlot) : null;

    private static ComponentSummary? MatchComponent(PreparedApply prepared, IReadOnlyList<ComponentSummary> components, string type, int ordinal,
        ApplyStateComponent? state, IReadOnlyDictionary<string, string>? referenceTargets = null)
    {
        if (state is not null)
        {
            // components is the Component list of the already proven owner Slot, so a stored ID cannot reach another
            // Slot. Several equal candidates require individual identity or verified reference evidence.
            var resolution = ResolveComponentOnSlot(components, state.Type, state.Id, state.MemberNames, state.IdentityValues,
                referenceTargets);
            if (resolution.Match is not null) return resolution.Match;
            if (resolution.Candidates.Count > 1)
                throw new RLoopException("STABLE_COMPONENT_AMBIGUOUS",
                    $"Stable Component on Slot '{state.SlotKey}' matches multiple runtime Components.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["candidateIds"] = resolution.Candidates.Select(candidate => candidate.Id).ToArray(),
                        ["type"] = state.Type },
                    ["Inspect candidateIds and preserve the existing state. Adding identityFields to a manifest does not populate an older checkpoint's identity values.",
                     "Prefer named provider Slots for new content. For existing content, verify ownership and each candidate before an explicit recovery; never guess by ordinal, componentIndex or stored ID."]);
            return null;
        }
        // No record yet (first apply or explicit adopt): the declared ordinal among same-type Components.
        var matches = StableComponentCandidates(components, type, null, null, referenceTargets);
        if (matches.Length == 1) return matches[0];
        matches = components.Where(x => TypeNamesEquivalent(x.Type, type)).ToArray();
        return ordinal >= 0 && ordinal < matches.Length ? matches[ordinal] : null;
    }

    private static IReadOnlyDictionary<string, string>? ResolveStateTopologyTargets(PreparedApply prepared,
        ApplyStateComponent state, HashSet<string> resolving)
    {
        if (state.ReferenceSelectors is not { Count: > 0 } || !resolving.Add(state.SlotKey + "\n" + state.Id)) return null;
        try
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var reference in state.ReferenceSelectors)
            {
                var target = ResolveStateReferenceId(prepared, reference.Value, resolving);
                if (target is not null) result[reference.Key] = target;
            }
            return result.Count == 0 ? null : result;
        }
        finally
        {
            resolving.Remove(state.SlotKey + "\n" + state.Id);
        }
    }

    private static string? ResolveStateReferenceId(PreparedApply prepared, string selector, HashSet<string> resolving)
    {
        if (!StableSelectorSyntax.TryParse(selector, out var syntax)) return null;
        if (syntax!.Kind == "slot")
            return prepared.State.Slots.TryGetValue(syntax.Key, out var slotState)
                ? FindManagedSlot(prepared, slotState)?.Id : null;
        if (syntax.Kind == "slot-member")
            return FindStateSlot(prepared, syntax.Key)?.Members?.GetValueOrDefault(syntax.MemberName!)?.Id;
        if (!prepared.State.Components.TryGetValue(syntax.Key, out var componentState)) return null;
        var slot = FindStateSlot(prepared, componentState.SlotKey);
        if (slot is null) return null;
        var referenceTargets = ResolveStateTopologyTargets(prepared, componentState, resolving);
        var match = ResolveComponentOnSlot(slot.Components, componentState.Type, componentState.Id, componentState.MemberNames,
            componentState.IdentityValues, referenceTargets).Match;
        if (match is null) return null;
        if (syntax.Kind == "component") return match.Id;
        return match.Members?.FirstOrDefault(member =>
            member.Key.Equals(syntax.MemberName, StringComparison.OrdinalIgnoreCase)).Value?.Id;
    }

    private static ComponentSummary[] StableComponentCandidates(IReadOnlyList<ComponentSummary> components, string type,
        IReadOnlyList<string>? memberNames, IReadOnlyDictionary<string, string>? identityValues,
        IReadOnlyDictionary<string, string>? referenceTargets = null)
    {
        var candidates = components.Where(component => TypeNamesEquivalent(component.Type, type)).ToArray();
        if (memberNames is not null)
            candidates = candidates.Where(component => memberNames.All(name => component.Members?.ContainsKey(name) == true)).ToArray();
        if (identityValues is not null)
            candidates = candidates.Where(component => identityValues.All(identity =>
                component.Members?.TryGetValue(identity.Key, out var value) == true && MemberMatchesRaw(value, identity.Value))).ToArray();
        if (referenceTargets is not null)
            candidates = candidates.Where(component => referenceTargets.All(reference =>
                component.Members?.TryGetValue(reference.Key, out var value) == true &&
                value.Kind == "reference" && value.TargetId == reference.Value)).ToArray();
        return candidates;
    }

    private sealed record ComponentResolution(ComponentSummary? Match, IReadOnlyList<ComponentSummary> Candidates);

    // Resolves one recorded Component among the Components of its already proven owner Slot.
    // Type, member names, identity values and verified reference targets are the evidence. Neither componentIndex
    // nor a complete set of stored IDs proves the correspondence of keys to Components across worlds.
    private static ComponentResolution ResolveComponentOnSlot(IReadOnlyList<ComponentSummary> live, string type, string? storedId,
        IReadOnlyList<string>? memberNames, IReadOnlyDictionary<string, string>? identityValues,
        IReadOnlyDictionary<string, string>? referenceTargets)
    {
        var candidates = StableComponentCandidates(live, type, memberNames, identityValues, referenceTargets);
        var stored = live.FirstOrDefault(component => component.Id == storedId);
        if (stored is not null)
        {
            var required = (memberNames ?? []).Concat(identityValues?.Keys ?? [])
                .Concat(referenceTargets?.Keys ?? []).Distinct(StringComparer.Ordinal);
            var unread = TypeNamesEquivalent(stored.Type, type) &&
                (stored.Members is null || required.Any(name => !stored.Members.ContainsKey(name)));
            var identityMismatch = StableComponentCandidates([stored], type, memberNames, identityValues).Length == 0;
            if (unread || identityMismatch || candidates.Length == 0)
                throw new RLoopException("APPLY_STORED_ID_UNVERIFIED",
                    $"Stored Component '{storedId}' is alive on the verified owner Slot but its evidence cannot be verified.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?>
                    {
                        ["storedId"] = storedId, ["type"] = type,
                        ["reason"] = unread ? "componentEvidenceUnread" : "componentEvidenceMismatch"
                    }, ["Inspect the stored Component and preserve the checkpoint. Restore the recorded evidence or explicitly repair state after verifying ownership."]);
        }
        return new ComponentResolution(candidates.Length == 1 ? candidates[0] : null, candidates);
    }

    // A runtime-relocatable Slot is identified by its Components alone: every recorded Component must resolve to
    // exactly one live Component and no two records may resolve to the same one. Empty evidence proves nothing.
    private static bool RelocatableEvidenceMatches(IReadOnlyList<ComponentSummary> live, IReadOnlyList<ApplyStateComponent> evidence)
    {
        if (evidence.Count == 0) return false;
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in evidence)
        {
            var resolution = ResolveComponentOnSlot(live, component.Type, component.Id, component.MemberNames,
                component.IdentityValues, null);
            if (resolution.Candidates.Count > 1)
                throw new RLoopException("STABLE_COMPONENT_AMBIGUOUS",
                    "Runtime-relocatable Slot evidence matches multiple Components; stored IDs cannot distinguish them.",
                    ExitCodes.ValidationFailed, new Dictionary<string, object?>
                    { ["candidateIds"] = resolution.Candidates.Select(candidate => candidate.Id).ToArray(), ["type"] = component.Type });
            if (resolution.Match is null || !matched.Add(resolution.Match.Id)) return false;
        }
        return true;
    }

    private static ApplyStateComponent CreateComponentState(ComponentRuntime component, string id,
        IReadOnlyDictionary<string, string>? resolvedFields = null)
    {
        var memberNames = (component.Spec.Fields?.Keys ?? [])
            .Concat(component.Spec.InitialFields?.Keys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Dictionary<string, string>? identityValues = null;
        if (component.Spec.IdentityFields is { Count: > 0 })
        {
            identityValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in component.Spec.IdentityFields)
            {
                if (TryGetReferenceSelector(component.Spec, name, out _)) continue;
                if (resolvedFields?.TryGetValue(name, out var resolved) == true) identityValues[name] = resolved;
                else if (component.AppliedOnCreate?.TryGetValue(name, out var initial) == true) identityValues[name] = initial;
                else if (component.Existing?.Members?.TryGetValue(name, out var current) == true) identityValues[name] = MemberRaw(current);
            }
        }
        var referenceSelectors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in memberNames)
            if (TryGetReferenceSelector(component.Spec, name, out var selector)) referenceSelectors[name] = selector;
        return new ApplyStateComponent(id, component.Node.StableKey, component.ResolvedType ?? component.Spec.Type,
            component.TypeOrdinal, component.ComponentIndex, memberNames, identityValues,
            referenceSelectors.Count == 0 ? null : referenceSelectors);
    }

    private static bool TryGetReferenceSelector(ApplyComponentSpec component, string memberName, out string selector)
    {
        selector = string.Empty;
        JsonElement value;
        if (component.Fields?.TryGetValue(memberName, out value) != true &&
            component.InitialFields?.TryGetValue(memberName, out value) != true) return false;
        if (value.ValueKind != JsonValueKind.String) return false;
        var raw = value.GetString() ?? string.Empty;
        if (!StableSelectorSyntax.TryParse(raw, out var parsed) || parsed!.Kind == "member" && parsed.MemberName is null) return false;
        selector = raw;
        return true;
    }

    private static bool SlotNeedsUpdate(SlotInfo existing, ApplySlotSpec desired) =>
        existing.Name != desired.Name ||
        ManagesTransform(desired, "position") && desired.Position is not null && !VectorEquals(existing.Position, desired.Position) ||
        ManagesTransform(desired, "rotation") && desired.Rotation is not null && !QuaternionEquals(existing.Rotation, desired.Rotation) ||
        ManagesTransform(desired, "scale") && desired.Scale is not null && !VectorEquals(existing.Scale, desired.Scale);

    private async Task PrepareRelocationTransformsAsync(PreparedApply prepared, string parentPath,
        CancellationToken cancellationToken)
    {
        if (!prepared.Nodes.Any(node => node.SlotAction == "relocate" && node.Spec.RelocationTransform == "world")) return;
        var externalParentWorld = await WorldTransformAtPathAsync(SlotPaths.Selector(parentPath, prepared.ParentSegments), cancellationToken);
        var finalWorld = new Dictionary<NodeRuntime, Matrix4x4>();
        foreach (var node in prepared.Nodes)
        {
            var parentWorld = node.Parent is null ? externalParentWorld : finalWorld[node.Parent];
            Matrix4x4 local;
            if (node.SlotAction == "relocate" && node.Spec.RelocationTransform == "world")
            {
                if (!prepared.State.Slots.TryGetValue(node.StableKey, out var previous))
                    throw new RLoopException("APPLY_RELOCATION_STATE_MISSING",
                        $"World-transform relocation for '{node.StableKey}' requires its previous stable path.",
                        ExitCodes.ValidationFailed);
                var oldWorld = await WorldTransformAtPathAsync(SlotPaths.Selector(previous.Path, previous.PathSegments), cancellationToken);
                if (!Matrix4x4.Invert(parentWorld, out var inverseParent))
                    throw new RLoopException("APPLY_RELOCATION_PARENT_NONINVERTIBLE",
                        $"Cannot preserve world transform for '{node.StableKey}' because the new parent transform is non-invertible.",
                        ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["parentPath"] = parentPath });
                local = oldWorld * inverseParent;
                if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var position))
                    throw new RLoopException("APPLY_RELOCATION_TRANSFORM_DECOMPOSE_FAILED",
                        $"Cannot decompose the preserved local transform for '{node.StableKey}'.", ExitCodes.ValidationFailed);
                rotation = Quaternion.Normalize(rotation);
                node.RelocationPosition = new Vector3Value(position.X, position.Y, position.Z);
                node.RelocationRotation = new QuaternionValue(rotation.X, rotation.Y, rotation.Z, rotation.W);
                node.RelocationScale = new Vector3Value(scale.X, scale.Y, scale.Z);
            }
            else
            {
                local = EffectiveLocalTransform(node);
            }
            finalWorld[node] = local * parentWorld;
        }
    }

    private async Task<Matrix4x4> WorldTransformAtPathAsync(string path, CancellationToken cancellationToken)
    {
        var parts = SlotPaths.ParseSelector(path).Skip(1).ToArray();
        var currentId = "Root";
        var currentPath = "Root";
        var world = Matrix4x4.Identity;
        foreach (var part in parts)
        {
            var current = await client.GetSlotAsync(currentId, 1, false, cancellationToken);
            var matches = current.Children.Where(child => child.Name.Equals(part, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new RLoopException(matches.Length == 0 ? "SLOT_PATH_NOT_FOUND" : "SLOT_PATH_AMBIGUOUS",
                    $"Cannot resolve transform path segment '{part}' below '{currentPath}'.", ExitCodes.ValidationFailed,
                    new Dictionary<string, object?> { ["path"] = path, ["candidateIds"] = matches.Select(match => match.Id).ToArray() });
            var child = matches[0];
            world = LocalTransform(child.Position, child.Rotation, child.Scale) * world;
            currentId = child.Id;
            currentPath += "/" + part;
        }
        return world;
    }

    private static Matrix4x4 EffectiveLocalTransform(NodeRuntime node)
    {
        var position = node.Existing?.Position ?? new Vector3Value(0, 0, 0);
        var rotation = node.Existing?.Rotation ?? new QuaternionValue(0, 0, 0, 1);
        var scale = node.Existing?.Scale ?? new Vector3Value(1, 1, 1);
        if (node.Existing is null || ManagesTransform(node.Spec, "position") && node.Spec.Position is not null)
            position = node.Spec.Position?.ToVector3("position") ?? position;
        if (node.Existing is null || ManagesTransform(node.Spec, "rotation") && node.Spec.Rotation is not null)
            rotation = node.Spec.Rotation?.ToQuaternion("rotation") ?? rotation;
        if (node.Existing is null || ManagesTransform(node.Spec, "scale") && node.Spec.Scale is not null)
            scale = node.Spec.Scale?.ToVector3("scale") ?? scale;
        return LocalTransform(position, rotation, scale);
    }

    private static Matrix4x4 LocalTransform(Vector3Value? position, QuaternionValue? rotation, Vector3Value? scale)
    {
        position ??= new Vector3Value(0, 0, 0);
        rotation ??= new QuaternionValue(0, 0, 0, 1);
        scale ??= new Vector3Value(1, 1, 1);
        return Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z) *
               Matrix4x4.CreateFromQuaternion(new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W)) *
               Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
    }

    private static SlotUpdateRequest CreateSlotUpdate(NodeRuntime node, string rootParentId)
    {
        var existing = node.Existing!;
        return new SlotUpdateRequest(existing.Id,
            existing.Name == node.Spec.Name ? null : node.Spec.Name,
            node.RelocationPosition ?? (ManagesTransform(node.Spec, "position") && node.Spec.Position is not null && !VectorEquals(existing.Position, node.Spec.Position) ? node.Spec.Position.ToVector3("position") : null),
            node.RelocationRotation ?? (ManagesTransform(node.Spec, "rotation") && node.Spec.Rotation is not null && !QuaternionEquals(existing.Rotation, node.Spec.Rotation) ? node.Spec.Rotation.ToQuaternion("rotation") : null),
            node.RelocationScale ?? (ManagesTransform(node.Spec, "scale") && node.Spec.Scale is not null && !VectorEquals(existing.Scale, node.Spec.Scale) ? node.Spec.Scale.ToVector3("scale") : null),
            node.SlotAction == "relocate" ? node.Parent?.Id ?? rootParentId : null);
    }

    private static bool ManagesTransform(ApplySlotSpec slot, string field) =>
        !slot.PreserveWorldTransform && slot.RelocationTransform != "world" &&
        (slot.ManagedFields is null || slot.ManagedFields.Contains(field, StringComparer.Ordinal));

    private static StateMigrations ApplyStateMigrations(ApplyDocument document, ApplyState state)
    {
        var slotMigrations = new Dictionary<string, string>(StringComparer.Ordinal);
        var componentMigrations = new Dictionary<string, string>(StringComparer.Ordinal);

        void MigrateSlot(ApplySlotSpec slot)
        {
            if (!string.IsNullOrWhiteSpace(slot.Key) && !string.IsNullOrWhiteSpace(slot.MigrateFrom))
            {
                if (state.Slots.ContainsKey(slot.Key) && state.Slots.ContainsKey(slot.MigrateFrom))
                    throw new RLoopException("APPLY_STATE_MIGRATION_CONFLICT",
                        $"State contains both Slot keys '{slot.MigrateFrom}' and '{slot.Key}'.",
                        ExitCodes.ValidationFailed);
                if (!state.Slots.ContainsKey(slot.Key) && state.Slots.Remove(slot.MigrateFrom, out var migrated))
                {
                    state.Slots[slot.Key] = migrated;
                    foreach (var component in state.Components.Where(component => component.Value.SlotKey == slot.MigrateFrom).ToArray())
                        state.Components[component.Key] = component.Value with { SlotKey = slot.Key };
                    slotMigrations[slot.Key] = slot.MigrateFrom;
                }
            }
        }

        void Visit(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? components, IReadOnlyList<ApplyNodeSpec>? children)
        {
            MigrateSlot(slot);
            foreach (var component in components ?? [])
            {
                if (string.IsNullOrWhiteSpace(component.Key) || string.IsNullOrWhiteSpace(component.MigrateFrom)) continue;
                if (state.Components.ContainsKey(component.Key) && state.Components.ContainsKey(component.MigrateFrom))
                    throw new RLoopException("APPLY_STATE_MIGRATION_CONFLICT",
                        $"State contains both Component keys '{component.MigrateFrom}' and '{component.Key}'.",
                        ExitCodes.ValidationFailed);
                if (!state.Components.ContainsKey(component.Key) && state.Components.Remove(component.MigrateFrom, out var migrated))
                {
                    state.Components[component.Key] = migrated;
                    componentMigrations[component.Key] = component.MigrateFrom;
                }
            }
            foreach (var child in children ?? []) Visit(child.Slot, child.Components, child.Children);
        }

        Visit(document.Slot!, document.Components, document.Children);
        return new StateMigrations(slotMigrations, componentMigrations);
    }

    private static bool VectorEquals(Vector3Value? current, float[] desired) => current is not null &&
        NearlyEqual(current.X, desired[0]) && NearlyEqual(current.Y, desired[1]) && NearlyEqual(current.Z, desired[2]);
    private static bool QuaternionEquals(QuaternionValue? current, float[] desired) => current is not null &&
        NearlyEqual(current.X, desired[0]) && NearlyEqual(current.Y, desired[1]) && NearlyEqual(current.Z, desired[2]) && NearlyEqual(current.W, desired[3]);
    private static bool NearlyEqual(float left, float right) => Math.Abs(left - right) <= 0.00001f * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    private static void Checkpoint(PreparedApply prepared, ApplyState next)
    {
        next.SessionId = prepared.Session.UniqueSessionId;
        ApplyStateStore.Save(prepared.StatePath, next);
        prepared.State = next;
    }

    private static int MaxDepth(IReadOnlyList<ApplyNodeSpec>? children) => children is null || children.Count == 0
        ? 0 : 1 + children.Max(x => MaxDepth(x.Children));
    private async Task<string> ObserveAbsolutePathAsync(SlotInfo slot, CancellationToken cancellationToken) =>
        string.Join('/', await ObserveAbsoluteSegmentsAsync(slot, cancellationToken));

    // Same bound as the other hierarchy walks (Root depth 64). When requireUniqueNames is set every ancestor step
    // reads the parent one level deep and refuses a name shared by several siblings, because a name that is not unique
    // cannot prove which sibling the stored ID belongs to.
    private const int MaxAncestorDepth = 64;

    private async Task<IReadOnlyList<string>> ObserveAbsoluteSegmentsAsync(SlotInfo slot, CancellationToken cancellationToken,
        bool requireUniqueNames = false)
    {
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var start = slot.Id;
        RLoopException Unresolved(string reason, string detail) => new("SLOT_PATH_UNRESOLVED",
            $"Cannot observe a bounded parent chain to Root for Slot '{start}': {detail}.", ExitCodes.ValidationFailed,
            new Dictionary<string, object?> { ["reason"] = reason, ["slotId"] = start, ["maxDepth"] = MaxAncestorDepth });
        while (slot.Id != "Root")
        {
            if (!visited.Add(slot.Id)) throw Unresolved("cycle", $"the parent chain loops at '{slot.Id}'");
            if (visited.Count > MaxAncestorDepth) throw Unresolved("depth-exceeded", $"the chain is deeper than {MaxAncestorDepth} levels");
            if (string.IsNullOrWhiteSpace(slot.ParentId)) throw Unresolved("parent-id-missing", $"Slot '{slot.Id}' reports no parent ID");
            names.Add(slot.Name);
            var parent = await client.GetSlotAsync(slot.ParentId, requireUniqueNames ? 1 : 0, false, cancellationToken);
            if (requireUniqueNames)
            {
                var siblings = parent.Children.Where(child => child.Name.Equals(slot.Name, StringComparison.Ordinal)).ToArray();
                if (siblings.Length > 1)
                    throw new RLoopException("SLOT_PATH_AMBIGUOUS",
                        $"Name '{slot.Name}' matched {siblings.Length} sibling Slots below '{parent.Id}', so the stored ID cannot be proven to be the owned Slot.",
                        ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["ids"] = siblings.Select(x => x.Id).ToArray() });
            }
            slot = parent;
        }
        names.Reverse();
        return ["Root", .. names];
    }
    private static string NormalizePath(string path) => string.Join('/', SlotPaths.LegacySegments(path));
    private static string MemberKey(string selector) { var separator = selector.LastIndexOf('.'); return separator > 0 ? selector[..separator] : selector; }
    private static string NormalizeType(string value) => StableEffectiveKeys.NormalizeType(value);
    private static bool TypeNamesEquivalent(string left, string right) => NormalizeType(left).Equals(NormalizeType(right), StringComparison.Ordinal) ||
        NormalizeType(left).EndsWith('.' + NormalizeType(right), StringComparison.Ordinal) || NormalizeType(right).EndsWith('.' + NormalizeType(left), StringComparison.Ordinal);
    private static RLoopException UnknownApplyReference(string value, IEnumerable<string> keys) => new("APPLY_REFERENCE_NOT_FOUND",
        $"Symbolic reference '{value}' could not be resolved.", ExitCodes.ValidationFailed, suggestions: keys.Take(30).Select(x => $"$ref:{x}").ToArray());

    private static SlotInfo AddPaths(SlotInfo slot, string path)
    {
        var children = slot.Children.Select(c => AddPaths(c, path.TrimEnd('/') + "/" + c.Name)).ToArray();
        return slot with { Path = path, Children = children };
    }
    private static SlotInfo RemoveReferenceOnlyChildren(SlotInfo slot) => slot with
    {
        Children = slot.Children.Where(child => !child.IsReferenceOnly).Select(RemoveReferenceOnlyChildren).ToArray()
    };
    private static void Visit(SlotInfo slot, string path, Action<SlotInfo> visitor)
    {
        var withPath = slot with { Path = path };
        visitor(withPath);
        foreach (var child in slot.Children) Visit(child, path + "/" + child.Name, visitor);
    }

    private static bool ContainsSlot(SlotInfo root, string id) => root.Id == id || root.Children.Any(child => ContainsSlot(child, id));

    private sealed class PreparedApply(ApplyDocument document, ApplyOptions options, ApplyState state,
        string statePath, SessionInfo session, string parentId, IReadOnlyList<(SlotInfo Slot, string Path)> snapshots,
        IReadOnlyDictionary<string, string> slotMigrations, IReadOnlyDictionary<string, string> componentMigrations, IReadOnlyList<string> parentSegments)
    {
        public ApplyDocument Document { get; } = document;
        public ApplyOptions Options { get; } = options;
        public ApplyState State { get; set; } = state;
        public string StatePath { get; } = statePath;
        public SessionInfo Session { get; } = session;
        public string ParentId { get; } = parentId;
        public IReadOnlyList<string> ParentSegments { get; } = parentSegments;
        public IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotSegments { get; } = BuildSegments(snapshots, state, parentId, parentSegments);
        public IReadOnlyDictionary<string, string> SlotMigrations { get; } = slotMigrations;
        public IReadOnlyDictionary<string, string> ComponentMigrations { get; } = componentMigrations;
        public IReadOnlyList<SlotInfo> SnapshotSlots { get; } = snapshots.SelectMany(snapshot => Flatten(snapshot.Slot, snapshot.Path)).ToArray();
        public ApplySafety? Safety { get; set; }
        public List<NodeRuntime> Nodes { get; } = [];
        public List<ComponentRuntime> Components { get; } = [];
        public List<ApplyPlanEntry> Entries { get; } = [];
        public List<DeletionRuntime> Deletions { get; } = [];
        public List<AssetRuntime> Assets { get; } = [];

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildSegments(IReadOnlyList<(SlotInfo Slot, string Path)> roots,
            ApplyState state, string parentId, IReadOnlyList<string> parentSegments)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            void VisitSegments(SlotInfo slot, IReadOnlyList<string> names)
            {
                result[slot.Id] = names;
                foreach (var child in slot.Children) VisitSegments(child, [.. names, child.Name]);
            }
            foreach (var root in roots)
                VisitSegments(root.Slot, root.Slot.Id == parentId ? parentSegments :
                    state.Slots.Values.FirstOrDefault(slot => slot.Id == root.Slot.Id)?.PathSegments ?? SlotPaths.LegacySegments(root.Path));
            return result;
        }

        private static IReadOnlyList<SlotInfo> Flatten(SlotInfo root, string rootPath)
        {
            var result = new List<SlotInfo>();
            Visit(root, rootPath, result.Add);
            return result;
        }
    }

    private sealed class NodeRuntime(ApplySlotSpec spec, IReadOnlyList<ApplyComponentSpec> componentSpecs,
        NodeRuntime? parent, SlotInfo? existing, string stableKey, string path, string slotAction)
    {
        public ApplySlotSpec Spec { get; } = spec;
        public IReadOnlyList<ApplyComponentSpec> ComponentSpecs { get; } = componentSpecs;
        public NodeRuntime? Parent { get; } = parent;
        public SlotInfo? Existing { get; } = existing;
        public string StableKey { get; } = stableKey;
        public string Path { get; } = path;
        public IReadOnlyList<string> PathSegments { get; set; } = [];
        public string SlotAction { get; } = slotAction;
        public string? Id { get; set; }
        public Vector3Value? RelocationPosition { get; set; }
        public QuaternionValue? RelocationRotation { get; set; }
        public Vector3Value? RelocationScale { get; set; }
    }

    private sealed class ComponentRuntime(ApplyComponentSpec spec, NodeRuntime node, ComponentSummary? existing,
        string stableKey, int typeOrdinal, string path, int componentIndex)
    {
        public ApplyComponentSpec Spec { get; } = spec;
        public NodeRuntime Node { get; } = node;
        public ComponentSummary? Existing { get; } = existing;
        public string StableKey { get; } = stableKey;
        public int TypeOrdinal { get; } = typeOrdinal;
        public int ComponentIndex { get; } = componentIndex;
        public string Path { get; } = path;
        public string? Id { get; set; }
        public string? ResolvedType { get; set; }
        public IReadOnlyDictionary<string, string>? AppliedOnCreate { get; set; }
        public ComponentSummary? RelocationSource { get; set; }
        public Dictionary<string, string> MemberIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ApplyCounts
    {
        public int SlotsCreated { get; set; }
        public int SlotsUpdated { get; set; }
        public int SlotsUnchanged { get; set; }
        public int ComponentsAdded { get; set; }
        public int ComponentsUpdated { get; set; }
        public int ComponentsUnchanged { get; set; }
        public int ComponentsDeleted { get; set; }
        public int SlotsDeleted { get; set; }
        public int AssetsImported { get; set; }
        public int AssetsUnchanged { get; set; }
    }

    private sealed record StateMigrations(IReadOnlyDictionary<string, string> Slots,
        IReadOnlyDictionary<string, string> Components);

    private sealed record DeletionRuntime(string Kind, string Key, string Id, string Path, string Reason,
        IReadOnlyList<string>? CoveredSlotKeys = null, IReadOnlyList<string>? CoveredComponentKeys = null);

    private sealed class AssetRuntime(string key, ApplyAssetSpec spec, string resolvedSource,
        string sourceHash, string? directUrl, string action)
    {
        public string Key { get; } = key;
        public ApplyAssetSpec Spec { get; } = spec;
        public string ResolvedSource { get; } = resolvedSource;
        public string SourceHash { get; } = sourceHash;
        public string? DirectUrl { get; } = directUrl;
        public string Action { get; } = action;
        public string? Url { get; set; }
    }
}

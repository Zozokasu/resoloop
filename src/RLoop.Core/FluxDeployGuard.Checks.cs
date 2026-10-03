namespace RLoop.Core;

// The deploy guard, unit 3b (R9-W3b): the output-writer check before the deployer and the binding readback after
// it. The rules are ported from the Workbench's FluxDeploymentService (binding table) and matched to apply's F1
// (writer rule). Nothing here references Flux-SDK or ResoniteLink types, and nothing here writes to the world.

public static class FluxDeployBindingStatus
{
    /// <summary>The carrier was read and its binding member targets the requested ID.</summary>
    public const string Verified = "verified";
    /// <summary>What was read contradicts the request.</summary>
    public const string Failed = "failed";
    /// <summary>Something needed for the comparison could not be read or is not known.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Whether a binding target still exists after the deployment (<see cref="FluxDeployBindingReadback.TargetExistence"/>).</summary>
public static class FluxDeployTargetExistence
{
    /// <summary>The target's owner was found before the deployment and still holds the target.</summary>
    public const string Exists = "exists";
    /// <summary>The target's owner was found before the deployment and is now proven gone, or no longer holds the target.</summary>
    public const string Missing = "missing";
    /// <summary>The target's owner was found before the deployment but could not be read again.</summary>
    public const string Unread = "unread";
    /// <summary>
    /// The target's owner was not found before the deployment (an input, or an output whose owner lies outside the
    /// writer check's range), so its existence was not checked. Not a reason to stop, and not proof that it exists.
    /// </summary>
    public const string NotObserved = "notObserved";
}

/// <summary>What the readback found for one declared binding.</summary>
/// <param name="Mode"><c>source</c> (input port) or <c>drive</c> (output port).</param>
/// <param name="TargetId">The requested target.</param>
/// <param name="Status">One of <see cref="FluxDeployBindingStatus"/>.</param>
/// <param name="Code">The <c>FLUX_BINDING_*</c> code of a failed or unknown binding; null when verified.</param>
/// <param name="Reason">Short machine-readable reason under <paramref name="Code"/>; null when verified.</param>
/// <param name="PortSlotIdMatchesSent">
/// Whether the only direct child named like the port Slot has the ID the deployer sent for it. The real machine keeps
/// the IDs the SDK sends (live U2), so <c>false</c> fails the binding (FLUX_BINDING_PORT_SLOT_MISSING,
/// <c>sentPortSlotIdMismatch</c>). Null when no single port Slot was found or the deployer reported no ID.
/// </param>
/// <param name="ObservedTargetId">The ID the carrier's binding member targets, when it was read.</param>
/// <param name="TargetExistence">One of <see cref="FluxDeployTargetExistence"/>.</param>
public sealed record FluxDeployBindingReadback(
    string Port,
    string Mode,
    string TargetId,
    string Status,
    string? Code,
    string? Reason,
    string Detail,
    string? PortSlotName = null,
    string? PortSlotId = null,
    bool? PortSlotIdMatchesSent = null,
    string? ComponentId = null,
    string? ComponentType = null,
    string? ExpectedCarrierType = null,
    string? MemberId = null,
    string? ObservedTargetId = null,
    string TargetExistence = FluxDeployTargetExistence.NotObserved);

/// <summary>A component that references an output target as a field: a possible writer (same rule as apply, F1).</summary>
/// <param name="UnderPreviousModule">True when the component sits on the recorded previous module root, which is about to be removed.</param>
public sealed record FluxDeployWriterHit(
    string Port,
    string TargetId,
    string SlotId,
    string ComponentId,
    string ComponentType,
    string Member,
    string? TargetType,
    bool UnderPreviousModule);

/// <summary>The evidence behind <see cref="FluxDeployGuardPreconditions.WriterCheck"/>.</summary>
/// <param name="Status">Same value as <see cref="FluxDeployGuardPreconditions.WriterCheck"/>.</param>
/// <param name="Targets">The output (drive) targets that were checked.</param>
/// <param name="ObservedSlotIds">Slots whose components were read: the parent and its direct children.</param>
/// <param name="Unobserved">What inside that range could not be read; empty when the range was read in full.</param>
/// <param name="OutsideObservedRange">
/// Always <c>unknown</c> when there are outputs: the rest of the world is not searched, so a writer there is neither
/// found nor excluded. <c>notApplicable</c> when there are no outputs.
/// </param>
/// <param name="TargetOwners">Target ID to the ID of the observed component that owns that member; absent when the owner is outside the range.</param>
/// <param name="AllowedWriters">Possible writers on the recorded previous module, which do not stop the deployment.</param>
public sealed record FluxDeployWriterObservation(
    string Status,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> ObservedSlotIds,
    IReadOnlyList<string> Unobserved,
    string OutsideObservedRange,
    IReadOnlyDictionary<string, string> TargetOwners,
    IReadOnlyList<FluxDeployWriterHit> AllowedWriters);

/// <summary>Status of <see cref="FluxDeployBindingPortsCheck"/>.</summary>
public static class FluxDeployBindingPortsStatus
{
    /// <summary>Only in the context of FLUX_BINDING_PORTS_MISMATCH: such a run never deploys.</summary>
    public const string Mismatch = "mismatch";
    /// <summary>Every binding names a compiled port of its mode, and every compiled port is bound.</summary>
    public const string Matched = "matched";
    /// <summary>
    /// Every binding names a compiled port of its mode; some compiled ports have no binding and are created unwired.
    /// A warning. Only when the request does not require every port to be bound (the single deploy).
    /// </summary>
    public const string UnboundPortsAllowed = "unboundPortsAllowed";
    /// <summary>
    /// The deployer could not report the compiled ports, so the keys were not compared. A warning: the deployment
    /// continues and the binding readback after it remains the final check.
    /// </summary>
    public const string PortsUnknown = "portsUnknown";
}

/// <summary>A port, or a binding key, with its mode (<c>source</c> for an input, <c>drive</c> for an output).</summary>
public sealed record FluxDeployPortKey(string Port, string Mode);

/// <summary>
/// The binding keys (<c>InputMap</c>, <c>OutputMap</c> and the stored bindings) compared with the ports of the
/// deployer's compile, before the session lock and before anything is written (ROADMAP-9 unit 5).
/// </summary>
/// <param name="Status">One of <see cref="FluxDeployBindingPortsStatus"/>.</param>
/// <param name="CompiledPorts">The ports the preparation reported; null when it reported none (<c>portsUnknown</c>).</param>
/// <param name="BoundPorts">The binding keys, in the order source then drive.</param>
/// <param name="UnknownKeys">Binding keys for which the compile has no port of any mode.</param>
/// <param name="ModeMismatches">Binding keys for which the compile has a port of that name only with the other mode.</param>
/// <param name="UnboundPorts">Compiled ports without a binding.</param>
/// <param name="RequireAllPortsBound">Same value as <see cref="FluxDeployGuardRequest.RequireAllPortsBound"/>.</param>
public sealed record FluxDeployBindingPortsCheck(
    string Status,
    IReadOnlyList<FluxDeployPortKey>? CompiledPorts,
    IReadOnlyList<FluxDeployPortKey> BoundPorts,
    IReadOnlyList<FluxDeployPortKey> UnknownKeys,
    IReadOnlyList<FluxDeployPortKey> ModeMismatches,
    IReadOnlyList<FluxDeployPortKey> UnboundPorts,
    bool RequireAllPortsBound,
    string Detail);

public sealed partial class FluxDeployGuard
{
    private const string SourceMode = "source";
    private const string DriveMode = "drive";

    // ---- Binding keys and compiled ports, before the lock (unit 5) -----------------------------------------

    /// <summary>
    /// Compares the binding keys with the ports of the preparation's compile (the same compile the guard just checked).
    /// A key that names no compiled port of its mode stops with FLUX_BINDING_PORTS_MISMATCH: Flux-SDK ignores such a
    /// key, so the binding would silently not exist. A compiled port without a binding stops too when the request
    /// requires every port to be bound (a manifest); otherwise it is reported and created unwired. When the deployer
    /// could not report the ports, a request with bindings or requiring completeness refuses; a single unbound
    /// deployment records unknown ports. Writes nothing and takes no lock.
    /// </summary>
    private static FluxDeployBindingPortsCheck CheckBindingPorts(Run run, IReadOnlyList<FluxModulePortInfo>? ports)
    {
        var request = run.Request;
        var keys = DeclaredBindings(request).Select(binding => new FluxDeployPortKey(binding.Port, binding.Mode))
            .Distinct().OrderBy(key => key.Mode == SourceMode ? 0 : 1).ThenBy(key => key.Port, StringComparer.Ordinal).ToArray();
        if (ports is null)
        {
            if (keys.Length > 0 || request.RequireAllPortsBound)
                throw new RLoopException("FLUX_BINDING_PORTS_MISMATCH",
                    "The compiled ports are unknown; binding preflight cannot be established. Nothing was written.",
                    ExitCodes.ValidationFailed, Context(run, "none", "none", new() { ["reason"] = "compiledPortsUnknown" }));
            return new(FluxDeployBindingPortsStatus.PortsUnknown, null, keys, [], [], [], request.RequireAllPortsBound,
                "The deployer did not report the compiled ports; the module's ports were not compared (there is no binding).");
        }

        var compiled = ports.Select(port => new FluxDeployPortKey(port.Name, port.Direction)).Distinct()
            .OrderBy(key => key.Mode == SourceMode ? 0 : 1).ThenBy(key => key.Port, StringComparer.Ordinal).ToArray();
        var compiledSet = compiled.ToHashSet();
        var unknown = keys.Where(key => !compiledSet.Contains(key) && !compiled.Any(port => port.Port == key.Port)).ToArray();
        var modeMismatches = keys.Where(key => !compiledSet.Contains(key) && compiled.Any(port => port.Port == key.Port)).ToArray();
        var unbound = compiled.Where(port => !keys.Contains(port)).ToArray();
        var refuseUnbound = request.RequireAllPortsBound && unbound.Length > 0;
        if (unknown.Length > 0 || modeMismatches.Length > 0 || refuseUnbound)
        {
            var check = new FluxDeployBindingPortsCheck(FluxDeployBindingPortsStatus.Mismatch, compiled, keys, unknown, modeMismatches, unbound,
                request.RequireAllPortsBound, Describe(unknown, modeMismatches, refuseUnbound ? unbound : []));
            throw new RLoopException("FLUX_BINDING_PORTS_MISMATCH",
                $"The bindings of module '{run.ModuleName}' do not match the ports of its compile: {check.Detail} Nothing was written.",
                ExitCodes.ValidationFailed, Context(run, "none", "none", new()
                {
                    ["modulePath"] = request.Module, ["bindingPorts"] = check, ["unknownKeys"] = unknown,
                    ["modeMismatches"] = modeMismatches, ["unboundPorts"] = unbound,
                    ["compiledPorts"] = compiled, ["requireAllPortsBound"] = request.RequireAllPortsBound
                }),
                ["Bind exactly the module's 'in' ports with mode 'source' and its 'out' ports with mode 'drive', using the port names the module declares, then deploy again."]);
        }
        if (FluxDeployClassifier.ValidatePreparedPortTypes(request.DeclaredPorts, ports, keys) is { } typeFailure)
            throw new RLoopException("FLUX_BINDING_PORTS_MISMATCH", typeFailure + " Nothing was written.",
                ExitCodes.ValidationFailed, Context(run, "none", "none", new()
                {
                    ["reason"] = "preparedPortEvidenceMismatch", ["declaredPorts"] = request.DeclaredPorts,
                    ["preparedPorts"] = ports
                }));
        return unbound.Length == 0
            ? new(FluxDeployBindingPortsStatus.Matched, compiled, keys, [], [], [], request.RequireAllPortsBound,
                compiled.Length == 0 ? "The module has no port and the request has no binding." : $"All {compiled.Length} compiled port(s) are bound.")
            : new(FluxDeployBindingPortsStatus.UnboundPortsAllowed, compiled, keys, [], [], unbound, request.RequireAllPortsBound,
                $"{unbound.Length} compiled port(s) have no binding and are created unwired: {string.Join(", ", unbound.Select(Label))}.");

        static string Label(FluxDeployPortKey key) => $"{key.Port} ({key.Mode})";
        static string Describe(IReadOnlyList<FluxDeployPortKey> unknown, IReadOnlyList<FluxDeployPortKey> modes, IReadOnlyList<FluxDeployPortKey> unbound)
        {
            var parts = new List<string>();
            if (unknown.Count > 0) parts.Add($"no compiled port for binding key(s) {string.Join(", ", unknown.Select(Label))}");
            if (modes.Count > 0) parts.Add($"binding key(s) {string.Join(", ", modes.Select(Label))} name a port of the other mode");
            if (unbound.Count > 0) parts.Add($"compiled port(s) {string.Join(", ", unbound.Select(Label))} have no binding");
            return string.Join("; ", parts) + ".";
        }
    }

    /// <summary>
    /// Every binding the request declares: what is sent to the deployer (the maps) and what is stored with the
    /// record (<see cref="FluxDeployGuardRequest.Bindings"/>). Both are checked, so a stored binding that was never
    /// sent reads back as a failure instead of being recorded unverified.
    /// </summary>
    private static IReadOnlyList<FluxDeployBindingRecord> DeclaredBindings(FluxDeployGuardRequest request) =>
        BindingsFromMaps(request).Concat(request.Bindings ?? []).Distinct().ToArray();

    // ---- Output writers, before the deployer (F1) ----------------------------------------------------------

    /// <summary>
    /// Refuses when a component other than the recorded previous module references an output (drive) target as a
    /// field inside the observed range. The range is one read of the parent with component data: the parent's own
    /// components and the components of its direct children. The world is not searched beyond that; what lies
    /// outside is recorded as unknown and does not stop the deployment (apply's F1: only a found conflict refuses).
    /// Runs after the record is reconciled and before the pending record is saved, so a refusal writes nothing and
    /// leaves the state unchanged.
    /// </summary>
    private async Task<string> CheckWritersBeforeDeployAsync(Run run, CancellationToken cancellationToken)
    {
        var outputs = DeclaredBindings(run.Request).Where(binding => binding.Mode == DriveMode).ToArray();
        var targets = outputs.Select(binding => binding.TargetId).Distinct(StringComparer.Ordinal).ToArray();
        if (outputs.Length == 0)
        {
            run.WriterObservation = new(FluxDeployCheckStatus.NoOutputs, [], [], [], "notApplicable",
                new Dictionary<string, string>(), []);
            return FluxDeployCheckStatus.NoOutputs;
        }

        var parentId = run.Request.ParentSlotId;
        var unobserved = new List<string>();
        var observedSlots = new List<string>();
        var components = new List<(string SlotId, ComponentSummary Component, bool UnderPrevious)>();
        SlotInfo? parent = null;
        try { parent = await client.GetSlotAsync(parentId, 1, true, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        { unobserved.Add($"parent '{parentId}': read failed ({error.Message})"); }
        if (parent is not null && (parent.IsReferenceOnly || !string.Equals(parent.Id, parentId, StringComparison.Ordinal)))
        {
            unobserved.Add($"parent '{parentId}': not returned in full");
            parent = null;
        }
        if (parent is not null)
        {
            Observe(parent);
            // Direct children only. Deeper Slots are outside the range by design, not a gap in it.
            foreach (var child in parent.Children ?? [])
            {
                if (child is null || child.IsReferenceOnly || string.IsNullOrWhiteSpace(child.Id))
                    unobserved.Add($"child '{child?.Id}' of '{parentId}': not returned in full");
                else Observe(child);
            }
        }

        void Observe(SlotInfo slot)
        {
            observedSlots.Add(slot.Id);
            var underPrevious = run.PreviousRootSlotId is { } previous && string.Equals(slot.Id, previous, StringComparison.Ordinal);
            foreach (var component in slot.Components ?? [])
            {
                if (component.Members is null) unobserved.Add($"component '{component.Id}' on '{slot.Id}': members not read");
                else components.Add((slot.Id, component, underPrevious));
            }
        }

        // The component that holds the target member is its owner, not its writer (as in apply).
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var ownerSlots = new Dictionary<string, (string SlotId, string ComponentId)>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var owner = components.FirstOrDefault(entry => entry.Component.Members!.Values
                .SelectMany(Descendants).Any(member => string.Equals(member.Id, target, StringComparison.Ordinal)));
            if (owner.Component is null) continue;
            owners[target] = owner.Component.Id;
            ownerSlots[target] = (owner.SlotId, owner.Component.Id);
        }
        run.TargetOwners = ownerSlots;

        var hits = new List<FluxDeployWriterHit>();
        foreach (var output in outputs)
            foreach (var (slotId, component, underPrevious) in components)
            {
                if (owners.TryGetValue(output.TargetId, out var owner) && owner == component.Id) continue;
                foreach (var (name, root) in component.Members!)
                    foreach (var reference in Descendants(root))
                        if (IsFieldReferenceTo(reference, output.TargetId))
                            hits.Add(new(output.Port, output.TargetId, slotId, component.Id, component.Type, name,
                                reference.TargetType, underPrevious));
            }

        var allowed = hits.Where(hit => hit.UnderPreviousModule).ToArray();
        var conflicts = hits.Where(hit => !hit.UnderPreviousModule).ToArray();
        var status = conflicts.Length > 0 ? FluxDeployCheckStatus.Conflict
            : unobserved.Count == 0 ? FluxDeployCheckStatus.ObservedRangeClear : FluxDeployCheckStatus.Unknown;
        run.WriterObservation = new(status, targets, observedSlots, unobserved, FluxDeployCheckStatus.Unknown, owners, allowed);
        if (conflicts.Length > 0)
            throw new RLoopException("FLUX_OUTPUT_WRITER_CONFLICT",
                $"{conflicts.Length} reference(s) outside the recorded previous module point at an output target of module '{run.ModuleName}' as a field, so another component may write it. Nothing was written.",
                ExitCodes.ValidationFailed, Context(run, "none", "none", new()
                {
                    ["reason"] = "writerDetected", ["writers"] = conflicts, ["allowedWriters"] = allowed,
                    ["previousRootSlotId"] = run.PreviousRootSlotId, ["writerObservation"] = run.WriterObservation
                }),
                ["Inspect each reported component by exact ID. Remove the other driver or bind the output to another field, then deploy again. Only the parent's own components and the components of its direct children are searched; writers elsewhere are not found by this check."]);
        return status;
    }

    /// <summary>
    /// The exact rule of apply's writer check (WorldService.ApplySafety.CheckWriter, which is private to apply): a
    /// reference whose target is the field and whose target type is <c>IField&lt;…&gt;</c>.
    /// </summary>
    private static bool IsFieldReferenceTo(MemberValue reference, string fieldId) =>
        reference.Kind == "reference" && string.Equals(reference.TargetId, fieldId, StringComparison.Ordinal) &&
        reference.TargetType?.StartsWith("[FrooxEngine]FrooxEngine.IField<", StringComparison.Ordinal) == true &&
        reference.TargetType.EndsWith('>');

    private static IEnumerable<MemberValue> Descendants(MemberValue member)
    {
        yield return member;
        foreach (var child in (member.Members?.Values ?? []).Concat(member.Elements ?? []))
            foreach (var nested in Descendants(child)) yield return nested;
    }

    // ---- Bindings, after the deployer ----------------------------------------------------------------------

    /// <summary>
    /// Reads the new module root with its direct children and their component data, finds each declared binding's
    /// port Slot and carrier, and compares the carrier's binding member (<c>Reference</c> of an input,
    /// <c>Drive</c> of an output) with the requested target. The port Slot must be the exact ID the deployer sent.
    /// Output targets whose owner the writer check observed are read again to prove they still exist. Read and
    /// different is a failure; not readable is unknown. Either way the deployment stays pending: the module is in
    /// the world and is not settled.
    /// Runs after <see cref="VerifyAfterDeployAsync"/>, which already proved that the root is new, is the only child
    /// with the declared name, and that the previous module is gone.
    /// </summary>
    private async Task<string> VerifyBindingsAfterDeployAsync(Run run, string newRootSlotId, CancellationToken cancellationToken)
    {
        var declared = DeclaredBindings(run.Request);
        if (declared.Count == 0) return FluxDeployCheckStatus.NoBindings;

        RLoopException Unread(string reason, string message, Exception? inner = null) =>
            PendingKept(run, "FLUX_READBACK_FAILED", message, reason, inner);
        SlotInfo root;
        try { root = await client.GetSlotAsync(newRootSlotId, 1, true, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw Unread("moduleUnread",
                $"The new module root '{newRootSlotId}' could not be read, so its bindings are unknown: {error.Message}", error);
        }
        if (root is null || root.IsReferenceOnly || !string.Equals(root.Id, newRootSlotId, StringComparison.Ordinal))
            throw Unread("moduleIncomplete", $"The read of the new module root '{newRootSlotId}' did not return that Slot in full.");
        if (!string.Equals(root.ParentId, run.Request.ParentSlotId, StringComparison.Ordinal))
            throw Unread("moduleParentMismatch",
                $"The new module root '{newRootSlotId}' is below '{root.ParentId}', not '{run.Request.ParentSlotId}'.");
        var children = root.Children ?? [];
        // A reference-only child has no name, so a port Slot could hide behind it: missing and duplicate are unprovable.
        if (children.Any(child => child is null || child.IsReferenceOnly || string.IsNullOrWhiteSpace(child.Id)))
            throw Unread("moduleChildrenIncomplete", $"A direct child of the new module root '{newRootSlotId}' was not returned in full.");

        var existence = await ReadTargetExistenceAsync(run, cancellationToken);
        var evidence = declared.Select(binding => WithExistence(ReadBinding(binding, run.Execution!.Ports, children),
            existence.TryGetValue(binding.TargetId, out var found) ? found : null)).ToArray();
        run.BindingEvidence = evidence;
        var failed = evidence.Where(entry => entry.Status == FluxDeployBindingStatus.Failed).ToArray();
        var unknown = evidence.Where(entry => entry.Status == FluxDeployBindingStatus.Unknown).ToArray();
        if (failed.Length == 0 && unknown.Length == 0) return FluxDeployCheckStatus.Verified;

        // Read-and-different outranks not-readable: it is the stronger statement about the world.
        var first = failed.Length > 0 ? failed[0] : unknown[0];
        var summary = failed.Length > 0
            ? $"{failed.Length} of {evidence.Length} binding(s) of module '{run.ModuleName}' read back differently from the request"
            : $"{unknown.Length} of {evidence.Length} binding(s) of module '{run.ModuleName}' could not be read back";
        throw PendingKept(run, first.Code!,
            $"The module was created, but {summary} (port '{first.Port}': {first.Detail}). The deployment is left pending and is not settled automatically.",
            first.Reason!, extra: new()
            {
                ["bindingReadback"] = failed.Length > 0 ? FluxDeployBindingStatus.Failed : FluxDeployBindingStatus.Unknown,
                ["bindings"] = evidence,
                ["failedPorts"] = failed.Select(entry => entry.Port).ToArray(),
                ["unknownPorts"] = unknown.Select(entry => entry.Port).ToArray()
            });
    }

    private static FluxDeployBindingReadback ReadBinding(FluxDeployBindingRecord binding,
        IReadOnlyList<FluxModulePortInfo>? ports, IReadOnlyList<SlotInfo> moduleChildren)
    {
        FluxDeployBindingReadback Verdict(string status, string? code, string? reason, string detail) =>
            new(binding.Port, binding.Mode, binding.TargetId, status, code, reason, detail);
        FluxDeployBindingReadback Failed(string code, string reason, string detail) => Verdict(FluxDeployBindingStatus.Failed, code, reason, detail);
        FluxDeployBindingReadback Unknown(string code, string reason, string detail) => Verdict(FluxDeployBindingStatus.Unknown, code, reason, detail);

        // The port Slot's name and the carrier type come from the operations the deployer sent, never from a guess.
        if (ports is null)
            return Unknown("FLUX_BINDING_EXPECTATION_UNKNOWN", "portsNotReported",
                "the deployer reported no ports, so the port Slot and its carrier type are not known");
        var declaredPorts = ports.Where(port => string.Equals(port.Name, binding.Port, StringComparison.Ordinal) &&
            string.Equals(port.Direction, binding.Mode, StringComparison.Ordinal)).ToArray();
        if (declaredPorts.Length == 0)
            return Failed("FLUX_BINDING_PORT_SLOT_MISSING", "portNotInSentOperations",
                $"the operations the deployer sent contain no '{binding.Mode}' port named '{binding.Port}'");
        if (declaredPorts.Length > 1)
            return Failed("FLUX_BINDING_PORT_SLOT_DUPLICATE", "portRepeatedInSentOperations",
                $"the operations the deployer sent contain {declaredPorts.Length} '{binding.Mode}' ports named '{binding.Port}'");
        var port = declaredPorts[0];

        var named = moduleChildren.Where(child => string.Equals(child.Name, port.SlotName, StringComparison.Ordinal)).ToArray();
        if (named.Length == 0)
            return Failed("FLUX_BINDING_PORT_SLOT_MISSING", "portSlotMissing",
                $"no direct child of the module is named '{port.SlotName}'") with { PortSlotName = port.SlotName, ExpectedCarrierType = port.ExpectedCarrierType };
        if (named.Length > 1)
            return Failed("FLUX_BINDING_PORT_SLOT_DUPLICATE", "portSlotDuplicate",
                $"{named.Length} direct children of the module are named '{port.SlotName}' ({string.Join(", ", named.Select(slot => slot.Id))})")
                with { PortSlotName = port.SlotName, ExpectedCarrierType = port.ExpectedCarrierType };
        var slot = named[0];
        bool? idMatches = string.IsNullOrWhiteSpace(port.SlotId) ? null : string.Equals(port.SlotId, slot.Id, StringComparison.Ordinal);
        // The port Slot is confirmed by the exact ID the deployer sent, not by its name alone.
        if (idMatches == false)
            return Failed("FLUX_BINDING_PORT_SLOT_MISSING", "sentPortSlotIdMismatch",
                $"the only direct child named '{port.SlotName}' is '{slot.Id}', not the port Slot '{port.SlotId}' the deployer sent")
                with { PortSlotName = port.SlotName, PortSlotId = slot.Id, PortSlotIdMatchesSent = false, ExpectedCarrierType = port.ExpectedCarrierType };
        if (idMatches is null)
            return Unknown("FLUX_BINDING_EXPECTATION_UNKNOWN", "portSlotIdNotReported",
                $"the deployer reported no ID for the port Slot '{port.SlotName}', so '{slot.Id}' is not confirmed as the Slot it sent")
                with { PortSlotName = port.SlotName, PortSlotId = slot.Id, ExpectedCarrierType = port.ExpectedCarrierType };
        FluxDeployBindingReadback At(FluxDeployBindingReadback verdict, ComponentSummary? component = null, MemberValue? member = null) =>
            verdict with
            {
                PortSlotName = port.SlotName, PortSlotId = slot.Id, PortSlotIdMatchesSent = idMatches,
                ExpectedCarrierType = port.ExpectedCarrierType, ComponentId = component?.Id, ComponentType = component?.Type,
                MemberId = member?.Id, ObservedTargetId = member?.TargetId
            };

        var kind = binding.Mode == SourceMode ? "GlobalReference" : "FieldDriveBase proxy";
        var memberName = binding.Mode == SourceMode ? "Reference" : "Drive";
        var slotComponents = slot.Components ?? [];
        var carriers = slotComponents.Where(component => IsCarrierType(component.Type, binding.Mode)).ToArray();
        if (carriers.Length == 0)
        {
            // An unread component may be the carrier: its absence is not proven.
            return slotComponents.Any(component => component.Members is null)
                ? At(Unknown("FLUX_BINDING_MEMBER_UNREAD", "componentsUnread",
                    $"the components of '{port.SlotName}' ({slot.Id}) were not read"))
                : At(Failed("FLUX_BINDING_CARRIER_MISSING", "carrierMissing",
                    $"'{port.SlotName}' ({slot.Id}) has no {kind} component"));
        }

        ComponentSummary? wired = null, unread = null;
        MemberValue? wiredMember = null;
        foreach (var carrier in carriers)
        {
            if (port.ExpectedCarrierType is { } expected && !string.Equals(carrier.Type, expected, StringComparison.Ordinal))
                return At(Failed("FLUX_BINDING_CARRIER_TYPE_MISMATCH", "carrierTypeMismatch",
                    $"the {kind} component {carrier.Id} on '{port.SlotName}' is '{carrier.Type}', but the deployer sent '{expected}'"), carrier);
            if (carrier.Members is null) { unread ??= carrier; continue; }
            if (!carrier.Members.TryGetValue(memberName, out var member))
                return At(Failed("FLUX_BINDING_CARRIER_MISSING", "carrierMemberMissing",
                    $"the {kind} component {carrier.Id} on '{port.SlotName}' has no '{memberName}' member"), carrier);
            if (member.Kind != "reference") { unread ??= carrier; continue; }
            if (string.IsNullOrWhiteSpace(member.TargetId))
                return At(Failed("FLUX_BINDING_TARGET_UNBOUND", "targetUnbound",
                    $"'{memberName}' of component {carrier.Id} on '{port.SlotName}' targets nothing"), carrier, member);
            if (!string.Equals(member.TargetId, binding.TargetId, StringComparison.Ordinal))
                return At(Failed("FLUX_BINDING_TARGET_MISMATCH", "targetMismatch",
                    $"'{memberName}' of component {carrier.Id} on '{port.SlotName}' targets '{member.TargetId}', not the requested '{binding.TargetId}'"),
                    carrier, member);
            wired ??= carrier;
            wiredMember ??= member;
        }

        if (unread is not null)
            return At(Unknown("FLUX_BINDING_MEMBER_UNREAD", "memberUnread",
                $"'{memberName}' of component {unread.Id} on '{port.SlotName}' was not read as a reference"), unread);
        if (port.ExpectedCarrierType is null)
            return At(Unknown("FLUX_BINDING_EXPECTATION_UNKNOWN", "carrierTypeNotEstablished",
                $"the operations the deployer sent do not name exactly one {kind} type for '{port.SlotName}'"), wired, wiredMember);
        return At(Verdict(FluxDeployBindingStatus.Verified, null, null,
            $"'{memberName}' of component {wired!.Id} on '{port.SlotName}' targets the requested '{binding.TargetId}'"), wired, wiredMember);
    }

    /// <summary>
    /// Re-reads, after the deployment, the owner of each output target that the writer check found in its range
    /// (Workbench's TargetExistsAsync, narrowed to owners the guard actually observed: no world snapshot, no
    /// member-owner hints). One read of the owner's Slot with component data per owner. A Slot that is gone, a
    /// component that is gone, or a fully read component without the target member is <c>missing</c>; a read that
    /// fails or comes back incomplete is <c>unread</c>. Targets without an observed owner are not read here.
    /// </summary>
    private async Task<Dictionary<string, (string Status, string Detail)>> ReadTargetExistenceAsync(Run run, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, (string Status, string Detail)>(StringComparer.Ordinal);
        var bySlot = new Dictionary<string, (SlotInfo? Slot, string? Failure)>(StringComparer.Ordinal);
        foreach (var (target, (slotId, componentId)) in run.TargetOwners)
        {
            if (!bySlot.TryGetValue(slotId, out var read))
            {
                try
                {
                    var slot = await client.GetSlotAsync(slotId, 0, true, cancellationToken);
                    read = slot is null || slot.IsReferenceOnly || !string.Equals(slot.Id, slotId, StringComparison.Ordinal)
                        ? (null, $"the read of Slot '{slotId}' did not return that Slot in full")
                        : (slot, null);
                }
                catch (RLoopException error) when (error.Code == "SLOT_NOT_FOUND") { read = (null, null); }
                catch (Exception error) when (error is not OperationCanceledException)
                { read = (null, $"Slot '{slotId}' could not be read: {error.Message}"); }
                bySlot[slotId] = read;
            }

            var owner = $"component {componentId} on Slot '{slotId}', which held it before the deployment";
            if (read.Failure is not null) { result[target] = (FluxDeployTargetExistence.Unread, $"{owner}: {read.Failure}"); continue; }
            if (read.Slot is null) { result[target] = (FluxDeployTargetExistence.Missing, $"Slot '{slotId}' of its owner {componentId} no longer exists"); continue; }
            var component = (read.Slot.Components ?? []).FirstOrDefault(entry => string.Equals(entry.Id, componentId, StringComparison.Ordinal));
            result[target] = component is null
                ? (FluxDeployTargetExistence.Missing, $"its owner {owner}, is no longer on that Slot")
                : component.Members is null
                    ? (FluxDeployTargetExistence.Unread, $"the members of its owner {owner}, were not read")
                    : component.Members.Values.SelectMany(Descendants).Any(member => string.Equals(member.Id, target, StringComparison.Ordinal))
                        ? (FluxDeployTargetExistence.Exists, $"its owner {owner}, still holds it")
                        : (FluxDeployTargetExistence.Missing, $"its owner {owner}, no longer holds it");
        }
        return result;
    }

    /// <summary>
    /// Folds the target's existence into the carrier verdict. A carrier that read back differently stays the
    /// reported failure. A target proven gone fails the binding even when the carrier still points at its ID
    /// (Workbench: BINDING_TARGET_MISSING). A target whose observed owner could not be read again leaves a wired
    /// binding unknown.
    /// </summary>
    private static FluxDeployBindingReadback WithExistence(FluxDeployBindingReadback verdict, (string Status, string Detail)? existence)
    {
        if (existence is not { } found) return verdict;
        var marked = verdict with { TargetExistence = found.Status };
        if (verdict.Status == FluxDeployBindingStatus.Failed) return marked;
        if (found.Status == FluxDeployTargetExistence.Missing)
            return marked with
            {
                Status = FluxDeployBindingStatus.Failed, Code = "FLUX_BINDING_TARGET_MISSING", Reason = "targetMissing",
                Detail = $"the target '{verdict.TargetId}' no longer exists: {found.Detail}"
            };
        if (found.Status == FluxDeployTargetExistence.Unread && verdict.Status == FluxDeployBindingStatus.Verified)
            return marked with
            {
                Status = FluxDeployBindingStatus.Unknown, Code = "FLUX_BINDING_MEMBER_UNREAD", Reason = "targetOwnerUnread",
                Detail = $"the carrier targets '{verdict.TargetId}', but whether it still exists is unknown: {found.Detail}"
            };
        return marked;
    }

    /// <summary>Same test <see cref="FluxDeployClassifier.ReadPorts"/> uses to name a port's carrier type.</summary>
    private static bool IsCarrierType(string? type, string mode) => type is not null && (mode == SourceMode
        ? type.Contains("GlobalReference<", StringComparison.Ordinal)
        : type.Contains("FieldDriveBase<", StringComparison.Ordinal) && type.EndsWith("+Proxy", StringComparison.Ordinal));
}

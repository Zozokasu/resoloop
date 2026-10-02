using System.Text.Json.Nodes;

namespace RLoop.Core;

public sealed partial class WorldService
{
    // Local optimistic checks only: an external change can occur between a read and send.
    private sealed class ApplySafety
    {
        private readonly IResoniteClient client;
        private readonly PreparedApply prepared;
        private readonly Dictionary<string, ComponentInfo> components = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SlotInfo> slots = new(StringComparer.Ordinal);
        private readonly HashSet<(string Id, string Member)> declaredReferences = [];
        private readonly ApplySessionObservation identity;
        private readonly HashSet<string> evidenceKeys = new(StringComparer.Ordinal);
        private string? activeKey;
        private string? activeMember;
        private readonly Dictionary<string, ApplyIssuePath> paths = new(StringComparer.Ordinal);
        public List<ApplyDiagnostic> Diagnostics { get; } = [];

        public ApplySafety(IResoniteClient client, PreparedApply prepared)
        {
            this.client = client;
            this.prepared = prepared;
            void Locate(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? specs, IReadOnlyList<ApplyNodeSpec>? children, ApplyIssuePath path)
            {
                if (slot.Key is not null) paths["slot:" + slot.Key] = path.Property("slot");
                for (var i = 0; i < (specs?.Count ?? 0); i++)
                    if (specs![i].Key is { } key) paths["component:" + key] = path.Property("components").Index(i);
                for (var i = 0; i < (children?.Count ?? 0); i++)
                    Locate(children![i].Slot, children[i].Components, children[i].Children, path.Property("children").Index(i));
            }
            Locate(prepared.Document.Slot!, prepared.Document.Components, prepared.Document.Children, ApplyIssuePath.Root);
            identity = (client as IApplySessionObservation)?.ObserveApplySession() ??
                ApplySessionObservation.Observe(prepared.Session.Url);
            void Observe(SlotInfo slot)
            {
                if (!slots.TryAdd(slot.Id, slot with { Members = CloneMembers(slot.Members) })) return;
                foreach (var c in slot.Components)
                    if (c.Members is not null) components[c.Id] = new(c.Id, c.Type, CloneMembers(c.Members)!);
                foreach (var child in slot.Children) Observe(child);
            }
            foreach (var node in prepared.Nodes) if (node.Existing is not null) Observe(node.Existing);
            foreach (var c in prepared.Components.Where(c => c.Existing is not null))
                foreach (var field in ManagedFields(c.Spec))
                    if (components.TryGetValue(c.Existing!.Id, out var observed) &&
                        observed.Members.TryGetValue(field.Key, out var member) &&
                        Descendants(member).Any(m => m.Kind == "reference"))
                        declaredReferences.Add((observed.Id, field.Key));
        }

        public async Task CheckConnectionAsync(string key, CancellationToken ct)
        {
            activeKey = key;
            ct.ThrowIfCancellationRequested();
            ApplyConnectionObservation observed;
            if (client is IApplyConnectionGuard guard) observed = guard.ObserveApplyConnection();
            else
            {
                var session = await client.GetSessionInfoAsync(ct);
                observed = new(session.Connected, session.ConnectionGeneration);
            }
            if (!observed.Connected || observed.Generation != prepared.Session.ConnectionGeneration)
                Fail("generationChanged", "connection", key, null,
                    new ApplyConnectionObservation(true, prepared.Session.ConnectionGeneration), observed,
                    observed.Generation != prepared.Session.ConnectionGeneration ? "CONNECTION_GENERATION_CHANGED" : "APPLY_PRECONDITION_FAILED");
            Evidence("connection", key);
        }

        public JsonNode? Expected(string? id) => id is null ? null :
            components.TryGetValue(id, out var c) ? EvidenceNode(c) :
            slots.TryGetValue(id, out var s) ? EvidenceNode(s) : null;
        public SlotInfo? ObservedSlot(string id) => slots.GetValueOrDefault(id);
        public IReadOnlyDictionary<string, string>? ObservedFields(string id) =>
            components.TryGetValue(id, out var component) ? component.Members.ToDictionary(m => m.Key, m => MemberRaw(m.Value)) : null;

        public async Task CheckComponentAsync(ComponentRuntime component, IEnumerable<string> memberNames, CancellationToken ct)
        {
            await CheckConnectionAsync(component.StableKey, ct);
            var current = await client.GetComponentAsync(component.Id!, ct);
            var expected = components[component.Id!];
            if (expected.Id != current.Id || expected.Type != current.Type)
                Fail("typeChanged", "component", component.StableKey, null, expected.Type, current.Type);
            foreach (var name in memberNames)
            {
                activeMember = name;
                expected.Members.TryGetValue(name, out var before);
                current.Members.TryGetValue(name, out var now);
                CheckMember("component", component.StableKey, name, before, now);
                CheckWriter("component", component.StableKey, name, component.Id, before?.Id);
            }
            Evidence("component", component.StableKey);
            await CheckConnectionAsync(component.StableKey, ct);
        }

        public async Task CheckSlotAsync(NodeRuntime node, SlotUpdateRequest request, CancellationToken ct)
        {
            await CheckConnectionAsync(node.StableKey, ct);
            var current = await client.GetSlotAsync(request.Id, 0, false, ct);
            var expected = slots[request.Id];
            if (expected.Id != current.Id) Fail("typeChanged", "slot", node.StableKey, null, expected.Id, current.Id);
            void Check(string name, object? before, object? now)
            {
                activeMember = name;
                if (!Equals(before, now)) Fail("valueChanged", "slot", node.StableKey, name, before, now);
                var oldMember = FindSlotMember(expected, name);
                var newMember = FindSlotMember(current, name);
                if (!SameShape(oldMember, newMember)) Fail("typeChanged", "slot", node.StableKey, name, oldMember, newMember);
                CheckWriter("slot", node.StableKey, name, null, oldMember?.Id);
            }
            if (request.Name is not null) Check("name", expected.Name, current.Name);
            if (request.ParentId is not null) Check("parent", expected.ParentId, current.ParentId);
            if (request.Position is not null) Check("position", expected.Position, current.Position);
            if (request.Rotation is not null) Check("rotation", expected.Rotation, current.Rotation);
            if (request.Scale is not null) Check("scale", expected.Scale, current.Scale);
            Evidence("slot", node.StableKey);
            await CheckConnectionAsync(node.StableKey, ct);
        }

        public void AcceptReadback(SlotInfo? slot, ComponentInfo? component, IEnumerable<string> names, SlotUpdateRequest? sent)
        {
            if (slot is not null)
            {
                if (slots.TryGetValue(slot.Id, out var old) && sent is not null)
                    slots[slot.Id] = old with { Name = sent.Name is null ? old.Name : slot.Name,
                        ParentId = sent.ParentId is null ? old.ParentId : slot.ParentId,
                        Position = sent.Position is null ? old.Position : slot.Position,
                        Rotation = sent.Rotation is null ? old.Rotation : slot.Rotation,
                        Scale = sent.Scale is null ? old.Scale : slot.Scale };
                else slots[slot.Id] = slot with { Members = CloneMembers(slot.Members) };
            }
            if (component is null) return;
            var next = components.TryGetValue(component.Id, out var prior) ? prior.Members.ToDictionary(StringComparer.Ordinal) :
                CloneMembers(component.Members)!.ToDictionary(StringComparer.Ordinal);
            foreach (var name in names)
                if (component.Members.TryGetValue(name, out var member))
                {
                    next[name] = CloneMember(member);
                    if (Descendants(member).Any(m => m.Kind == "reference")) declaredReferences.Add((component.Id, name));
                }
            components[component.Id] = component with { Members = next };
        }

        private void CheckMember(string kind, string key, string name, MemberValue? before, MemberValue? now)
        {
            if (!SameShape(before, now)) Fail("typeChanged", kind, key, name, before, now);
            if (!SameValue(before, now)) Fail("valueChanged", kind, key, name, before, now);
        }

        private void CheckWriter(string kind, string key, string member, string? owner, string? fieldId)
        {
            if (fieldId is null) return;
            foreach (var c in components.Values)
            {
                if (c.Id == owner) continue;
                foreach (var root in c.Members)
                {
                    if (declaredReferences.Contains((c.Id, root.Key))) continue;
                    foreach (var reference in Descendants(root.Value))
                        // Exact public targetType form observed in s3-0-live spinner.json / bvd.json.
                        if (reference.Kind == "reference" && reference.TargetId == fieldId &&
                            reference.TargetType?.StartsWith("[FrooxEngine]FrooxEngine.IField<", StringComparison.Ordinal) == true &&
                            reference.TargetType.EndsWith('>'))
                            Fail("writerDetected", kind, key, member, fieldId, new { componentId = c.Id, c.Type, member = root.Key, reference });
                }
            }
        }

        private static IEnumerable<MemberValue> Descendants(MemberValue member)
        {
            yield return member;
            foreach (var child in (member.Members?.Values ?? []).Concat(member.Elements ?? []))
                foreach (var nested in Descendants(child)) yield return nested;
        }

        private static MemberValue? FindSlotMember(SlotInfo slot, string name) =>
            slot.Members?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        private static IReadOnlyDictionary<string, MemberValue>? CloneMembers(IReadOnlyDictionary<string, MemberValue>? members) =>
            members?.ToDictionary(p => p.Key, p => CloneMember(p.Value), StringComparer.Ordinal);
        private static MemberValue CloneMember(MemberValue m) => m with { Value = m.Value?.DeepClone(),
            Members = CloneMembers(m.Members), Elements = m.Elements?.Select(CloneMember).ToArray() };
        private static bool SameShape(MemberValue? a, MemberValue? b) => a is null || b is null ? a is null && b is null :
            a.Kind == b.Kind && a.Type == b.Type && a.TargetType == b.TargetType && a.Id == b.Id &&
            SameChildren(a, b, SameShape);
        private static bool SameValue(MemberValue? a, MemberValue? b) => a is null || b is null ? a is null && b is null :
            a.TargetId == b.TargetId && JsonNode.DeepEquals(a.Value, b.Value) && SameChildren(a, b, SameValue);
        private static bool SameChildren(MemberValue a, MemberValue b, Func<MemberValue?, MemberValue?, bool> equal) =>
            (a.Members is null || b.Members is null ? a.Members is null && b.Members is null :
                a.Members.Count == b.Members.Count && a.Members.All(p => b.Members.TryGetValue(p.Key, out var value) && equal(p.Value, value))) &&
            (a.Elements is null || b.Elements is null ? a.Elements is null && b.Elements is null :
                a.Elements.Count == b.Elements.Count && a.Elements.Zip(b.Elements).All(p => equal(p.First, p.Second)));
        private Dictionary<string, string> Completeness() => new()
        { ["identity"] = identity.IdentityStatus, ["connection"] = prepared.Session.ConnectionGeneration is null ? "unknown" : "complete",
            ["writer"] = "partial", ["writerOutsideObservation"] = "unknown", ["runtime"] = "partial" };
        private void Evidence(string kind, string key)
        {
            if (!evidenceKeys.Add(kind + ":" + key)) return;
            Diagnostics.Add(ApplyDiagnostics.Unknown("APPLY_EVIDENCE_INCOMPLETE",
                "Session identity or writer evidence is incomplete; unknown evidence does not block this apply.", "apply", "warning") with
                { EntityKind = kind, Key = key, Completeness = Completeness() });
        }
        public RLoopException DecorateBoundaryFailure(RLoopException error)
        {
            if (error.Code is not ("CONNECTION_GENERATION_CHANGED" or "APPLY_PRECONDITION_FAILED") || error.Context.ContainsKey("completeness")) return error;
            var observed = (client as IApplyConnectionGuard)?.ObserveApplyConnection();
            var expected = new ApplyConnectionObservation(true, prepared.Session.ConnectionGeneration);
            var enriched = new RLoopException(error.Code, error.Message, error.ExitCode,
                new Dictionary<string, object?>(error.Context) { ["reason"] = "generationChanged", ["key"] = activeKey,
                    ["member"] = activeMember, ["expected"] = expected, ["observed"] = observed, ["completeness"] = Completeness() }, error.Suggestions, error);
            ApplyDiagnostics.AttachRuntime(enriched, [.. Diagnostics, ApplyDiagnostics.Unknown(error.Code, error.Message, "apply") with
                { Key = activeKey, Member = activeMember, Expected = ApplyDiagnosticValue.Known(expected),
                    Observed = observed is null ? ApplyDiagnosticValue.Unknown : ApplyDiagnosticValue.Known(observed), Completeness = Completeness() }]);
            return enriched;
        }
        private void Fail(string reason, string kind, string key, string? member, object? expected, object? observed,
            string code = "APPLY_PRECONDITION_FAILED")
        {
            var failure = new RLoopException(code, $"Apply precondition failed: {reason} at '{key}'{(member is null ? "" : "." + member)}.",
                code == "CONNECTION_GENERATION_CHANGED" ? ExitCodes.OperationFailed : ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["reason"] = reason, ["key"] = key, ["member"] = member,
                    ["expected"] = expected, ["observed"] = observed, ["completeness"] = Completeness() });
            var diagnostic = ApplyDiagnostics.Unknown(code, failure.Message, "apply") with
            { EntityKind = kind, Key = key, Member = member, Expected = ApplyDiagnosticValue.Known(expected),
                Observed = ApplyDiagnosticValue.Known(observed), Completeness = Completeness() };
            if (paths.TryGetValue(kind + ":" + key, out var path))
            {
                if (member is not null)
                {
                    if (kind == "component")
                    {
                        var spec = prepared.Components.First(c => c.StableKey == key).Spec;
                        path = path.Property(spec.Fields?.ContainsKey(member) == true ? "fields" : "initialFields");
                    }
                    path = path.Property(member);
                }
                diagnostic = ApplyDiagnostics.LocateRuntime(prepared.Document, diagnostic, path);
            }
            ApplyDiagnostics.AttachRuntime(failure, [.. Diagnostics, diagnostic]);
            throw failure;
        }
    }
}

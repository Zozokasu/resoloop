namespace RLoop.Core;

public sealed record UixAuditIssue(string Code, string Severity, string Message, string SlotId,
    string? ComponentId = null, string? Member = null);
public sealed record UixComponentObservation(string Id, string Type, IReadOnlyDictionary<string, MemberValue> Members);
public sealed record UixSlotObservation(string Id, string Name, string? ParentId, string Path,
    string ActualSizeStatus, IReadOnlyList<UixComponentObservation> Components);
public sealed record UixAuditReport(string RootId, bool Valid, bool StructuralOnly, string Verification,
    bool Truncated, int ObservedSlots, IReadOnlyList<UixSlotObservation> Slots, IReadOnlyList<UixAuditIssue> Issues);

/// <summary>Read-only diagnostics from public member observations; never substitutes declared size for computed bounds.</summary>
public static class UixAuditService
{
    private static readonly HashSet<string> Layouts = ["VerticalLayout", "HorizontalLayout", "GridLayout", "OverlappingLayout"];
    private static readonly HashSet<string> Members =
    [
        "Enabled", "Size", "AnchorMin", "AnchorMax", "OffsetMin", "OffsetMax", "Pivot",
        "MinWidth", "MinHeight", "PreferredWidth", "PreferredHeight", "FlexibleWidth", "FlexibleHeight",
        "UseZeroMetrics", "Priority", "HorizontalFit", "VerticalFit", "ForceExpandWidth", "ForceExpandHeight",
        "PaddingTop", "PaddingRight", "PaddingBottom", "PaddingLeft", "Spacing", "CellSize",
        "ExpandWidthToFit", "PreserveAspectOnExpand", "HorizontalAlign", "VerticalAlign",
        "HorizontalAutoSize", "VerticalAutoSize", "ViewportOverride", "NormalizedPosition", "ShowMaskGraphic",
        "TargetField", "Source", "Target", "WriteBack", "TargetState", "TargetValue", "CheckVisual", "InteractionTarget",
        "Sprite", "Texture", "Material", "Materials", "URL", "Uncompressed", "CrunchCompressed", "DirectLoad",
        "PreferredFormat", "PreferredProfile", "MipMaps", "RenderQueue", "ZTest", "ZWrite",
        "ColorDrivers", "NormalColor", "HighlightColor", "PressColor", "DisabledColor", "Editor", "__text",
        "HandleAnchorMinDrive", "HandleAnchorMaxDrive"
    ];

    public static async Task<UixAuditReport> InspectAsync(IResoniteClient client, string rootId, int depth = 6,
        int maxSlots = 256, CancellationToken cancellationToken = default)
    {
        if (depth is < 0 or > 32 || maxSlots is < 1 or > 4096)
            throw new RLoopException("INVALID_OPTION", "UIX audit requires depth 0..32 and max-slots 1..4096.", ExitCodes.InvalidArguments);
        var queue = new Queue<(string Id, string? Path, int Depth)>();
        queue.Enqueue((rootId, null, 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observed = new List<SlotInfo>();
        var truncated = false;
        while (queue.Count > 0 && observed.Count < maxSlots)
        {
            var next = queue.Dequeue();
            if (!seen.Add(next.Id)) continue;
            // Only request members for the current Slot, not the whole descendant hierarchy.
            var slot = await client.GetSlotAsync(next.Id, 0, true, cancellationToken);
            var path = next.Path ?? slot.Path ?? slot.Name;
            observed.Add(slot with { Path = path, Children = [] });
            var children = (await client.GetSlotAsync(next.Id, 1, false, cancellationToken)).Children
                .Where(child => !child.IsReferenceOnly).ToArray();
            if (next.Depth == depth) { truncated |= children.Length > 0; continue; }
            foreach (var child in children)
            {
                if (observed.Count + queue.Count >= maxSlots) { truncated = true; break; }
                queue.Enqueue((child.Id, path + "/" + child.Name, next.Depth + 1));
            }
        }
        return Audit(rootId, observed, truncated || queue.Count > 0);
    }

    public static UixAuditReport Audit(string rootId, IReadOnlyList<SlotInfo> slots, bool truncated = false)
    {
        var issues = new List<UixAuditIssue>();
        var observations = new List<UixSlotObservation>();
        foreach (var slot in slots)
        {
            var uix = slot.Components.Where(component => IsUix(component.Type)).ToArray();
            if (uix.Length == 0) continue;
            var active = uix.Where(component => !IsFalse(component, "Enabled")).ToArray();
            var graphics = active.Where(component => Kind(component.Type) is "Image" or "Text").ToArray();
            if (graphics.Length > 1)
                issues.Add(new("UIX_GRAPHIC_CONFLICT", "error", "Competing Image/Text Graphics share one Slot; separate their rendering layers.", slot.Id));
            if (active.Length > 0 && active.All(component => Kind(component.Type) != "RectTransform"))
                issues.Add(new("UIX_RECT_MISSING", "warning", "No enabled RectTransform was observed on this UIX Slot.", slot.Id));
            foreach (var fitter in active.Where(component => Kind(component.Type) == "ContentSizeFitter"))
                if (!active.Any(component => Layouts.Contains(Kind(component.Type)) || Kind(component.Type) is "Text" or "Image" or "LayoutElement"))
                    issues.Add(new("UIX_FITTER_METRICS_MISSING", "warning", "ContentSizeFitter has no observed layout metric provider on the same Slot.", slot.Id, fitter.Id));
            foreach (var component in uix.Where(component => component.Members is null))
                issues.Add(new("UIX_MEMBERS_UNAVAILABLE", "warning", "Member evidence is missing; repeat the observation with component data.", slot.Id, component.Id));
            var relevant = slot.Components.Where(component => IsUix(component.Type) || IsAssetProvider(component.Type) ||
                component.Members?.ContainsKey("TargetField") == true || component.Members?.ContainsKey("Source") == true);
            var summary = relevant.Select(component => new UixComponentObservation(component.Id, component.Type,
                (component.Members ?? new Dictionary<string, MemberValue>()).Where(pair => Members.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))).ToArray();
            observations.Add(new(slot.Id, slot.Name, slot.ParentId, slot.Path ?? slot.Name, "unknown", summary));
        }
        // Include logic-only Slots as well: drivers are often deliberately kept outside the UI layout.
        foreach (var slot in slots.Where(slot => slot.Components.All(component => !IsUix(component.Type))))
        {
            var drivers = slot.Components.Where(component => IsAssetProvider(component.Type) || component.Members?.ContainsKey("TargetField") == true ||
                component.Members?.ContainsKey("Source") == true).Select(component => new UixComponentObservation(component.Id,
                component.Type, (component.Members ?? new Dictionary<string, MemberValue>()).Where(pair => Members.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value))).ToArray();
            if (drivers.Length > 0) observations.Add(new(slot.Id, slot.Name, slot.ParentId, slot.Path ?? slot.Name, "not-applicable", drivers));
        }
        if (truncated) issues.Add(new("UIX_OBSERVATION_TRUNCATED", "warning", "The depth or Slot budget was reached; audit a narrower subtree for complete evidence.", rootId));
        if (observations.Count == 0) issues.Add(new("UIX_NOT_OBSERVED", "warning", "No UIX, provider, or related driver evidence was found within this observation.", rootId));
        return new(rootId, !issues.Any(issue => issue.Severity == "error"), true, "partial", truncated,
            slots.Count, observations, issues);
    }

    private static bool IsFalse(ComponentSummary component, string member) =>
        component.Members?.GetValueOrDefault(member)?.Value is System.Text.Json.Nodes.JsonValue value &&
        value.TryGetValue<bool>(out var result) && !result;
    private static string WithoutAssembly(string type) => type.StartsWith('[') && type.IndexOf(']') is var end && end >= 0 ? type[(end + 1)..] : type;
    private static bool IsUix(string type) => WithoutAssembly(type).StartsWith("FrooxEngine.UIX.", StringComparison.Ordinal);
    private static bool IsAssetProvider(string type) => WithoutAssembly(type) is "FrooxEngine.StaticTexture2D" or
        "FrooxEngine.SpriteProvider" or "FrooxEngine.StaticFont" or "FrooxEngine.FontChain" or "FrooxEngine.UI_TextUnlitMaterial";
    private static string Kind(string type) => WithoutAssembly(type)["FrooxEngine.UIX.".Length..];
}

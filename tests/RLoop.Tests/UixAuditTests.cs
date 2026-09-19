using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class UixAuditTests
{
    [Fact]
    public void DetectsConflictingGraphicsAndDoesNotExposeUserText()
    {
        var report = UixAuditService.Audit("root", [Slot("root", Component("Image"), Component("Text", "Content", "private input"), Component("RectTransform"))]);
        Assert.False(report.Valid);
        Assert.Contains(report.Issues, issue => issue.Code == "UIX_GRAPHIC_CONFLICT");
        Assert.DoesNotContain("private input", System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Equal("unknown", Assert.Single(report.Slots).ActualSizeStatus);
        Assert.Equal("partial", report.Verification);
    }

    [Fact]
    public void FitterRequiresMetricsOnItsOwnSlotAndReportsIncompleteTraversal()
    {
        var report = UixAuditService.Audit("root", [Slot("root", Component("ContentSizeFitter"), Component("RectTransform")),
            Slot("child", Component("VerticalLayout"), Component("RectTransform"))], true);
        Assert.Contains(report.Issues, issue => issue.Code == "UIX_FITTER_METRICS_MISSING" && issue.SlotId == "root");
        Assert.True(report.Truncated);
        var corrected = UixAuditService.Audit("root", [Slot("root", Component("ContentSizeFitter"), Component("VerticalLayout"), Component("RectTransform"))]);
        Assert.Empty(corrected.Issues);
        Assert.True(corrected.StructuralOnly);
    }

    [Fact]
    public void IncludesLogicSlotDriverTargetsWithoutAssumingTheirRuntimeSize()
    {
        var target = new MemberValue("reference", TargetId: "offset");
        var driver = new ComponentSummary("driver", "FrooxEngine.BooleanValueDriver<float2>", new Dictionary<string, MemberValue> { ["TargetField"] = target });
        var report = UixAuditService.Audit("root", [Slot("logic", driver)]);
        Assert.Equal("offset", Assert.Single(Assert.Single(report.Slots).Components).Members["TargetField"].TargetId);
        Assert.Equal("not-applicable", report.Slots[0].ActualSizeStatus);
    }

    private static ComponentSummary Component(string type, string? member = null, string? value = null) =>
        new(type, "[FrooxEngine]FrooxEngine.UIX." + type, member is null ? new Dictionary<string, MemberValue>() :
            new Dictionary<string, MemberValue> { [member] = new("field", Value: JsonValue.Create(value)) });

    [Fact]
    public void IncludesTextureSettingsWithoutClaimingTheAssetLoaded()
    {
        var texture = new ComponentSummary("texture", "[FrooxEngine]FrooxEngine.StaticTexture2D", new Dictionary<string, MemberValue>
        {
            ["URL"] = new("field", Value: JsonValue.Create("local://example/image.png")),
            ["CrunchCompressed"] = new("field", Value: JsonValue.Create(true))
        });
        var report = UixAuditService.Audit("root", [Slot("assets", texture)]);
        Assert.True(report.StructuralOnly);
        Assert.Equal("partial", report.Verification);
        var observed = Assert.Single(Assert.Single(report.Slots).Components);
        Assert.True(observed.Members["CrunchCompressed"].Value!.GetValue<bool>());
    }
    private static SlotInfo Slot(string id, params ComponentSummary[] components) =>
        new(id, id, null, null, null, null, true, true, null, false, components, []);
}

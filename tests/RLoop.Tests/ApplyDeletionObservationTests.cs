using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

/// <summary>
/// Fixtures are hand-written after the live record
/// TestResults/S3-LIVE/executor-v3b/ResoLoop_Test_DriverSwap_TwoStage_e8c680baa2bf4fe2ba1e8aa231072dcc/driver-swap-raw-getslot.json
/// (Resonite 2026.9.18.82 / ResoniteLink 0.13.1.0): SlotData.Depth is 0 whatever depth was requested;
/// children are reference-only only when the requested depth did not reach them; components are
/// reference-only only when IncludeComponentData was not requested; a leaf's Children is null.
/// </summary>
public sealed class ApplyDeletionObservationTests
{
    private const int LiveReportedDepth = 0;

    private static Link.Slot Child(string id, bool referenceOnly = false) => new()
    {
        ID = id, IsReferenceOnly = referenceOnly, Parent = new Link.Reference { TargetID = "exact" },
        Name = referenceOnly ? null : new Link.Field_string { Value = id }
    };

    private static Link.Component Component(string id, bool referenceOnly = false) =>
        new() { ID = id, ComponentType = "Test.Target", IsReferenceOnly = referenceOnly };

    private static async Task<ApplyDeletionObservation> ObserveAsync(Link.Slot? raw, int reportedDepth = LiveReportedDepth)
    {
        var link = new ScriptedMetadataLink { Connected = false };
        await using var adapter = new ResoniteLinkClientAdapter(link, reflectionCache: new("off"), getSlotData: request =>
        {
            Assert.Equal(1, request.Depth);
            Assert.True(request.IncludeComponentData);
            Assert.Equal("exact", request.SlotID);
            return Task.FromResult(new Link.SlotData { Success = true, Depth = reportedDepth, Data = raw! });
        });
        await adapter.ConnectAsync(new("ws://localhost:47610"), TimeSpan.FromSeconds(2));
        return await adapter.ObserveDeletionSlotAsync("exact");
    }

    [Fact]
    public async Task LiveShapedResponseWithReportedDepthZeroAndFullChildrenAndComponentsIsCompleteCoverage()
    {
        var observed = await ObserveAsync(new Link.Slot
        {
            ID = "exact", Children = [Child("A"), Child("B")],
            Components = [Component("C1"), Component("C2"), Component("C3"), Component("C4")]
        });
        Assert.True(observed.ChildrenObserved);
        Assert.True(observed.ComponentsObserved);
        Assert.Equal(["A", "B"], observed.Slot.Children.Select(c => c.Id));
        Assert.Equal(["C1", "C2", "C3", "C4"], observed.Slot.Components.Select(c => c.Id));
    }

    [Theory]
    [InlineData("leaf", true, true)]
    [InlineData("emptyChildList", true, true)]
    [InlineData("referenceOnly", false, false)]
    [InlineData("wrongId", false, false)]
    [InlineData("componentsMissing", true, false)]
    [InlineData("componentReferenceOnly", true, false)]
    [InlineData("componentIdEmpty", true, false)]
    public async Task LeafWithNullChildrenIsObservedOnlyWhenTheSlotBodyIsFullAndExact(string scenario, bool children, bool components)
    {
        var raw = new Link.Slot { ID = scenario == "wrongId" ? "other" : "exact", IsReferenceOnly = scenario == "referenceOnly",
            Children = scenario == "emptyChildList" ? [] : null, Components = scenario == "componentsMissing" ? null :
                [Component("C0"), Component(scenario == "componentIdEmpty" ? " " : "C", scenario == "componentReferenceOnly")] };
        var observed = await ObserveAsync(raw);
        Assert.Equal(children, observed.ChildrenObserved);
        Assert.Equal(components, observed.ComponentsObserved);
        Assert.Empty(observed.Slot.Children);
    }

    [Theory]
    [InlineData("oneChildReferenceOnly", true)]
    [InlineData("allChildrenReferenceOnly", true)]
    [InlineData("childIdEmpty", true)]
    [InlineData("childIdMissing", true)]
    [InlineData("depthZeroRequestShape", false)]
    public async Task AnyReferenceOnlyOrUnidentifiedChildLeavesChildrenUnobserved(string scenario, bool components)
    {
        var raw = new Link.Slot
        {
            ID = "exact",
            Children = scenario switch
            {
                "oneChildReferenceOnly" => [Child("A"), Child("B", referenceOnly: true)],
                "childIdEmpty" => [Child("A"), Child(" ")],
                "childIdMissing" => [Child("A"), Child(null!)],
                _ => [Child("A", referenceOnly: true), Child("B", referenceOnly: true)]
            },
            // A depth-0 request without component data returns every child and component as a reference.
            Components = [Component("C1", referenceOnly: scenario == "depthZeroRequestShape")]
        };
        var observed = await ObserveAsync(raw);
        Assert.False(observed.ChildrenObserved);
        Assert.Equal(components, observed.ComponentsObserved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReportedDepthIsNeverUsedAsCoverageEvidence(int reportedDepth)
    {
        var complete = await ObserveAsync(new Link.Slot { ID = "exact", Children = [Child("A")], Components = [Component("C")] }, reportedDepth);
        Assert.True(complete.ChildrenObserved);
        Assert.True(complete.ComponentsObserved);

        var truncated = await ObserveAsync(new Link.Slot
        {
            ID = "exact", Children = [Child("A", referenceOnly: true)], Components = [Component("C", referenceOnly: true)]
        }, reportedDepth);
        Assert.False(truncated.ChildrenObserved);
        Assert.False(truncated.ComponentsObserved);
    }

    [Fact]
    public async Task SuccessfulResponseWithoutSlotDataFailsInsteadOfReportingCoverage()
    {
        var error = await Assert.ThrowsAsync<RLoopException>(() => ObserveAsync(null));
        Assert.Equal("RESONITE_OPERATION_FAILED", error.Code);
    }
}

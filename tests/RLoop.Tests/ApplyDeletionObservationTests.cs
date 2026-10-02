using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class ApplyDeletionObservationTests
{
    [Theory]
    [InlineData("leaf", true, true)]
    [InlineData("referenceOnly", false, false)]
    [InlineData("depth", false, false)]
    [InlineData("wrongId", false, false)]
    [InlineData("componentsMissing", true, false)]
    [InlineData("componentReferenceOnly", true, false)]
    public async Task S3RawCoverageDistinguishesNullLeafChildrenFromUnknown(string scenario, bool children, bool components)
    {
        var raw = new Link.Slot { ID = scenario == "wrongId" ? "other" : "exact", IsReferenceOnly = scenario == "referenceOnly",
            Children = null, Components = scenario == "componentsMissing" ? null :
                [new Link.Component { ID = "C", ComponentType = "Test.Target", IsReferenceOnly = scenario == "componentReferenceOnly" }] };
        var link = new ScriptedMetadataLink { Connected = false };
        await using var adapter = new ResoniteLinkClientAdapter(link, reflectionCache: new("off"), getSlotData: request =>
        {
            Assert.Equal(1, request.Depth);
            Assert.True(request.IncludeComponentData);
            Assert.Equal("exact", request.SlotID);
            return Task.FromResult(new Link.SlotData { Success = true, Depth = scenario == "depth" ? 0 : 1, Data = raw });
        });
        await adapter.ConnectAsync(new("ws://localhost:47610"), TimeSpan.FromSeconds(2));
        var observed = await adapter.ObserveDeletionSlotAsync("exact");
        Assert.Equal(children, observed.ChildrenObserved);
        Assert.Equal(components, observed.ComponentsObserved);
        Assert.Empty(observed.Slot.Children);
    }
}

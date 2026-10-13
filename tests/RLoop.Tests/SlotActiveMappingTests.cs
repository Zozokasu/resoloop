using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Link = global::ResoniteLink;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

public sealed class SlotActiveMappingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateAndUpdateMapBooleanWrappersAndJson(bool active)
    {
        var create = new SlotCreateRequest("Root", "New", IsActive: active);
        var update = new SlotUpdateRequest("S1", IsActive: active);
        Assert.Equal(active, JsonSerializer.SerializeToNode(create)!["IsActive"]!.GetValue<bool>());
        Assert.Equal(active, JsonSerializer.SerializeToNode(update)!["IsActive"]!.GetValue<bool>());

        var mappedCreate = ResoniteLinkClientAdapter.MapSlotCreate(create);
        var mappedUpdate = ResoniteLinkClientAdapter.MapSlotUpdate(update);
        Assert.Equal(active, mappedCreate.IsActive?.Value);
        Assert.Equal(active, mappedUpdate.IsActive?.Value);

        Link.Message[] envelopes =
        [
            new Link.AddSlot { Data = mappedCreate },
            new Link.UpdateSlot { Data = mappedUpdate }
        ];
        foreach (var envelope in envelopes)
        {
            var wire = JsonSerializer.SerializeToNode<Link.Message>(envelope, Link.LinkInterface.SerializationOptions)!;
            Assert.Equal(active, wire["data"]!["isActive"]!["value"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void OmittedActiveIsNullAndKeepsOldWirePayload()
    {
        var create = new SlotCreateRequest("Root", "New", RequestedId: "new-id");
        var update = new SlotUpdateRequest("S1");
        Assert.False(JsonSerializer.SerializeToNode(create)!.AsObject().ContainsKey("IsActive"));
        Assert.False(JsonSerializer.SerializeToNode(update)!.AsObject().ContainsKey("IsActive"));
        var mappedCreate = ResoniteLinkClientAdapter.MapSlotCreate(create);
        var mappedUpdate = ResoniteLinkClientAdapter.MapSlotUpdate(update);
        Assert.Null(mappedCreate.IsActive);
        Assert.Null(mappedUpdate.IsActive);

        // Independent pre-change fixtures omit IsActive entirely.
        Link.Message[] baseline =
        [
            new Link.AddSlot { Data = new Link.Slot
            {
                ID = "new-id", Parent = new Link.Reference { TargetID = "Root" },
                Name = new Link.Field_string { Value = "New" }, Position = null, Rotation = null, Scale = null
            } },
            new Link.UpdateSlot { Data = new Link.Slot
            {
                ID = "S1", Parent = null, Name = null, Position = null, Rotation = null, Scale = null
            } }
        ];
        Link.Message[] actual =
        [
            new Link.AddSlot { Data = mappedCreate },
            new Link.UpdateSlot { Data = mappedUpdate }
        ];
        for (var i = 0; i < actual.Length; i++)
        {
            var before = JsonSerializer.SerializeToUtf8Bytes<Link.Message>(baseline[i], Link.LinkInterface.SerializationOptions);
            var after = JsonSerializer.SerializeToUtf8Bytes<Link.Message>(actual[i], Link.LinkInterface.SerializationOptions);
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public void InstalledSdkExposesWritableBoolSlotField()
    {
        var active = typeof(Link.Slot).GetProperty("IsActive");
        Assert.NotNull(active);
        Assert.Equal(typeof(Link.Field_bool), active!.PropertyType);
        Assert.True(active.CanWrite);
        var value = typeof(Link.Field_bool).GetProperty("Value");
        Assert.NotNull(value);
        Assert.Equal(typeof(bool), value!.PropertyType);
        Assert.True(value.CanWrite);
    }
}

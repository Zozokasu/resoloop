using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class SlotReadFailureTests
{
    private static ResoniteLinkClientAdapter Adapter(ScriptedMetadataLink link) =>
        new(link, TimeSpan.FromSeconds(5), new ReflectionCacheOptions("off"), link.GetSlotData);

    [Theory]
    [InlineData("Reso_FFFFFF0", "Slot with ID 'Reso_FFFFFF0' not found.")]
    [InlineData("not-a-slot-id", "Slot with ID 'not-a-slot-id' not found.")]
    [InlineData("Reso_FFFFFF0", " \tSlot with ID 'Reso_FFFFFF0' not found.\r\n")]
    public async Task ExactRequestedSlotAbsenceIsNotFound(string id, string errorInfo)
    {
        var link = new ScriptedMetadataLink { SlotResponse = new Link.SlotData { Success = false, ErrorInfo = errorInfo } };
        await using var client = Adapter(link);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSlotAsync(id, 2, true));

        Assert.Equal("SLOT_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Equal(errorInfo, ex.Message);
        Assert.Equal(id, ex.Context["slotId"]);
        Assert.Equal(errorInfo, ex.Context["errorInfo"]);
        var request = Assert.Single(link.RequestedSlots);
        Assert.Equal(id, request.SlotID);
        Assert.Equal(2, request.Depth);
        Assert.True(request.IncludeComponentData);
    }

    [Theory]
    [InlineData("Reso_FFFFFF0", "Slot with ID 'Reso_FFFFFF1' not found.")]
    [InlineData("", "Slot ID must be provided.")]
    [InlineData("Reso_FFFFFF0", null)]
    [InlineData("Reso_FFFFFF0", "")]
    [InlineData("Reso_FFFFFF0", " \t\r\n")]
    [InlineData("Reso_FFFFFF0", "Object reference not set to an instance of an object.")]
    [InlineData("Reso_FFFFFF0", "Slot with ID 'reso_ffffff0' not found.")]
    [InlineData("Reso_FFFFFF0", "slot with ID 'Reso_FFFFFF0' not found.")]
    [InlineData("Reso_FFFFFF0", "Slot with ID 'Reso_FFFFFF0' not found")]
    [InlineData("Reso_FFFFFF0", "Slot with ID ' Reso_FFFFFF0' not found.")]
    [InlineData("Reso_FFFFFF0", "Error: Slot with ID 'Reso_FFFFFF0' not found.")]
    [InlineData("Reso_FFFFFF0", "Slot with ID 'Reso_FFFFFF0' not found. Retry.")]
    public async Task OtherSlotFailuresAreOperationFailed(string id, string? errorInfo)
    {
        var link = new ScriptedMetadataLink { SlotResponse = new Link.SlotData { Success = false, ErrorInfo = errorInfo! } };
        await using var client = Adapter(link);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSlotAsync(id, 0, false));

        Assert.Equal("RESONITE_OPERATION_FAILED", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal(string.IsNullOrWhiteSpace(errorInfo) ? "ResoniteLink operation failed." : errorInfo, ex.Message);
        Assert.Equal(id, ex.Context["slotId"]);
        Assert.True(ex.Context.ContainsKey("errorInfo"));
        Assert.Equal(errorInfo, ex.Context["errorInfo"]);
        Assert.Equal(id, Assert.Single(link.RequestedSlots).SlotID);
    }

    [Fact]
    public async Task SuccessfulSlotReadStillMapsData()
    {
        var link = new ScriptedMetadataLink
        {
            SlotResponse = new Link.SlotData
            {
                Success = true,
                Data = new Link.Slot { ID = "Root", Name = new Link.Field_string { Value = "World" } }
            }
        };
        await using var client = Adapter(link);

        var slot = await client.GetSlotAsync("Root", 0, false);

        Assert.Equal("Root", slot.Id);
        Assert.Equal("World", slot.Name);
        Assert.Single(link.RequestedSlots);
    }
}

using RLoop.Core;
using RLoop.Workbench;

namespace RLoop.Workbench.Tests;

public sealed class SlotTagUnsupportedTests
{
    [Theory]
    [InlineData("")]
    [InlineData("custom")]
    public async Task DeclaredTagUsesFrozenBackendUnsupportedPattern(string tag)
    {
        await using var client = new WorkbenchResoniteClient();
        var create = await Assert.ThrowsAsync<RLoopException>(() => client.CreateSlotAsync(new("Root", "Managed", Tag: tag)));
        var update = await Assert.ThrowsAsync<RLoopException>(() => client.UpdateSlotAsync(new("S1", Tag: tag)));
        Assert.Equal("BACKEND_UNSUPPORTED", create.Code);
        Assert.Equal("BACKEND_UNSUPPORTED", update.Code);
    }
}

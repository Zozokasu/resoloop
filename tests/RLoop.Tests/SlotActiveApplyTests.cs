using RLoop.Core;

namespace RLoop.Tests;

public sealed class SlotActiveApplyTests
{
    [Fact]
    public async Task ApplyCreateAndUpdateLeaveDirectActiveFieldUnspecified()
    {
        var directory = Path.Combine(Path.GetTempPath(), "resoloop-slot-active-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var statePath = Path.Combine(directory, "state.json");
            var client = new SlotActiveTestClient();
            var service = new WorldService(client);
            var initial = Document("Managed");
            await service.ApplyAsync(initial, new(statePath));
            Assert.Null(Assert.Single(client.Creates).IsActive);

            await service.ApplyAsync(Document("Renamed"), new(statePath));
            Assert.Equal("Renamed", Assert.Single(client.Updates).Name);
            Assert.All(client.Creates, request => Assert.Null(request.IsActive));
            Assert.All(client.Updates, request => Assert.Null(request.IsActive));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ApplyDocument Document(string name) => new("1", new("slot-active-apply"),
        new(name, "Root", null, null, null, Key: "root"), []);
}

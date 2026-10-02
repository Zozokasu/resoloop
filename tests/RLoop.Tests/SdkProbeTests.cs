using System.Text.Json;
using RLoop.CatalogExport;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class SdkProbeTests
{
    private sealed class FakeConnection(List<FakeConnection> all) : SdkProbe.IReadConnection
    {
        public List<Link.Message> Requests { get; } = [];
        public bool Disposed { get; private set; }
        public bool Stall { get; init; }
        public Task Connect(Uri url, CancellationToken ct)
        {
            Assert.Equal("ws://localhost:1234/", url.AbsoluteUri);
            Assert.All(all, connection => Assert.True(connection.Disposed));
            all.Add(this);
            return Task.CompletedTask;
        }
        public Task<Link.SessionData> Session()
        {
            Requests.Add(new Link.RequestSessionData());
            return Task.FromResult(new Link.SessionData { Success = true, UniqueSessionId = all.Count.ToString(), ResoniteVersion = "test", ResoniteLinkVersion = "0.13.1" });
        }
        public Task<Link.SlotData> Slot(Link.GetSlot request)
        {
            Requests.Add(request);
            Assert.Equal(0, request.Depth);
            Assert.False(request.IncludeComponentData);
            if (Stall) return new TaskCompletionSource<Link.SlotData>().Task;
            return Task.FromResult(new Link.SlotData { Success = true, Data = new Link.Slot
            {
                ID = request.SlotID, Position = new Link.Field_float3 { ID = "position", Value = new Link.float3 { x = 1, y = 2, z = 3 } },
                OrderOffset = new Link.Field_long { ID = "order", Value = 19 },
                Children = [new() { ID = request.SlotID + "/child", IsReferenceOnly = true }],
                Components = [new() { ID = request.SlotID + "/component", ComponentType = "Synthetic.Driver", IsReferenceOnly = true }]
            } });
        }
        public Task<Link.ComponentData> Component(Link.GetComponent request)
        {
            Requests.Add(request);
            return Task.FromResult(new Link.ComponentData { Success = true, Data = new Link.Component
            {
                ID = request.ComponentID, ComponentType = "Synthetic.Driver", Members = new()
                {
                    ["Target"] = new Link.Reference { ID = "target", TargetID = "position", TargetType = "Synthetic.Field" },
                    ["Value"] = new Link.Field_float { ID = "value", Value = 7 },
                    ["Nested"] = new Link.SyncObject { ID = "nested", Members = new() { ["Flag"] = new Link.Field_bool { ID = "flag", Value = true } } }
                }
            } });
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task TwoConnectionsSendOnlyReadsAndRetainSdkPolymorphicData()
    {
        var connections = new List<FakeConnection>();
        var result = await SdkProbe.ReadAsync(new("ws://localhost:1234"), "Root", 0, factory: () => new FakeConnection(connections));
        Assert.Equal(2, connections.Count);
        Assert.All(connections, c =>
        {
            Assert.True(c.Disposed);
            Assert.Equal(4, c.Requests.Count);
            Assert.All(c.Requests, request => Assert.True(request is Link.RequestSessionData or Link.GetSlot or Link.GetComponent));
        });
        Assert.Equal("1", result.Connections[0].SessionBefore!.UniqueSessionId);
        Assert.Equal("2", result.Connections[1].SessionBefore!.UniqueSessionId);
        Assert.All(result.Connections, p => Assert.True(p.CompleteWithinRequestedDepth));
        using var json = JsonDocument.Parse(SdkProbe.Serialize(result));
        var pass = json.RootElement.GetProperty("Connections")[0];
        var slot = pass.GetProperty("Slots")[0].GetProperty("Response").GetProperty("data");
        Assert.Equal(1, slot.GetProperty("position").GetProperty("value").GetProperty("x").GetSingle());
        Assert.Equal(19, slot.GetProperty("orderOffset").GetProperty("value").GetInt64());
        var members = pass.GetProperty("Components")[0].GetProperty("Response").GetProperty("data").GetProperty("members");
        Assert.Equal("reference", members.GetProperty("Target").GetProperty("$type").GetString());
        Assert.Equal("position", members.GetProperty("Target").GetProperty("targetId").GetString());
        Assert.Equal(7, members.GetProperty("Value").GetProperty("value").GetSingle());
        Assert.True(members.GetProperty("Nested").GetProperty("members").GetProperty("Flag").GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task SlotComponentDepthAndTimeBudgetsStopReadsAndMarkPartialEvidence()
    {
        var connections = new List<FakeConnection>();
        var result = await SdkProbe.ReadAsync(new("ws://localhost:1234"), "Root", 8,
            factory: () => new FakeConnection(connections), maxSlots: 2, maxComponents: 1);
        Assert.All(result.Connections, pass =>
        {
            Assert.Equal(2, pass.Slots.Count); Assert.Single(pass.Components);
            Assert.Contains(pass.Diagnostics, d => d.StartsWith("slot-limit:"));
            Assert.Contains(pass.Diagnostics, d => d.StartsWith("component-limit:"));
            Assert.False(pass.CompleteWithinRequestedDepth);
        });
        var timedConnections = new List<FakeConnection>();
        var timed = await SdkProbe.ReadAsync(new("ws://localhost:1234"), "Root", 0,
            factory: () => new FakeConnection(timedConnections) { Stall = true }, timeLimit: TimeSpan.FromMilliseconds(50));
        Assert.Single(timedConnections); Assert.True(timedConnections[0].Disposed);
        Assert.Equal(2, timed.Connections.Count);
        Assert.All(timed.Connections, pass => Assert.Contains(pass.Diagnostics, d => d.StartsWith("time-limit:")));
        Assert.All(timedConnections[0].Requests, request => Assert.True(request is Link.RequestSessionData or Link.GetSlot));
        await Assert.ThrowsAsync<ArgumentException>(() => SdkProbe.ReadAsync(new("ws://localhost:1234"), "Root", -1));
    }
}

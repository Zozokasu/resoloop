using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = global::ResoniteLink;

namespace RLoop.Tests;

public sealed class SlotTagApplyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-tag-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(directory, "state.json");
    public SlotTagApplyTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static ApplyDocument Document(string? tag = null) => new("1", new("tag-owner"),
        new("Managed", "Root", null, null, null, Key: "root", Tag: tag), []);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("custom")]
    public void SlotTagRequestAndAdapterPayloadPreserveOmissionAndEmpty(string? tag)
    {
        var create = new SlotCreateRequest("Root", "Managed", Tag: tag);
        var update = new SlotUpdateRequest("S1", Tag: tag);
        var createJson = JsonSerializer.SerializeToNode(create)!.AsObject();
        var updateJson = JsonSerializer.SerializeToNode(update)!.AsObject();
        Assert.Equal(tag is not null, createJson.ContainsKey("Tag"));
        Assert.Equal(tag is not null, updateJson.ContainsKey("Tag"));
        var mappedCreate = ResoniteLinkClientAdapter.MapSlotCreate(create);
        var mappedUpdate = ResoniteLinkClientAdapter.MapSlotUpdate(update);
        Assert.Equal(tag, mappedCreate.Tag?.Value);
        Assert.Equal(tag, mappedUpdate.Tag?.Value);
        Assert.Equal(tag is null, mappedCreate.Tag is null);
        Assert.Equal(tag is null, mappedUpdate.Tag is null);
        // Independent pre-change adapter fixture: Tag was never assigned, but the SDK
        // still serializes its inherited null property. Preserve these exact wire bytes.
        Link.Message[] baselineMessages =
        [
            new Link.AddSlot { Data = new Link.Slot
            {
                ID = create.RequestedId,
                Parent = new Link.Reference { TargetID = "Root" },
                Name = new Link.Field_string { Value = "Managed" },
                Position = null, Rotation = null, Scale = null
            } },
            new Link.UpdateSlot { Data = new Link.Slot
            {
                ID = "S1", Parent = null, Name = null,
                Position = null, Rotation = null, Scale = null
            } }
        ];
        Link.Message[] messages = [new Link.AddSlot { Data = mappedCreate }, new Link.UpdateSlot { Data = mappedUpdate }];
        for (var index = 0; index < messages.Length; index++)
        {
            // Match the installed SDK SendMessage path: Message envelope plus its public options.
            var wireBytes = JsonSerializer.SerializeToUtf8Bytes<Link.Message>(messages[index],
                Link.LinkInterface.SerializationOptions);
            var payload = JsonNode.Parse(wireBytes)!["data"]!.AsObject();
            if (tag is null)
            {
                var baselineBytes = JsonSerializer.SerializeToUtf8Bytes<Link.Message>(baselineMessages[index],
                    Link.LinkInterface.SerializationOptions);
                Assert.Equal(baselineBytes, wireBytes);
                Assert.Null(payload["tag"]); // No Field_string wrapper or Tag write value.
            }
            else Assert.Equal(tag, payload["tag"]!["value"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("custom")]
    public async Task SlotTagCreateAndUpdateReadExactlyOnceAndConverge(string tag)
    {
        var client = new TagClient();
        var service = new WorldService(client);
        var document = Document(tag);
        var result = await service.ApplyAsync(document, new(StatePath));
        Assert.Equal(tag, Assert.Single(client.Creates).Tag);
        Assert.Equal(tag, client.Slots[result.SlotId].Tag);
        Assert.Equal(1, client.Readbacks);
        Assert.Empty(ApplyStateStore.Load(StatePath, "tag-owner").Pending);
        var plan = await service.PlanApplyAsync(document, new(StatePath));
        Assert.Equal("no-op", Assert.Single(plan.Operations).Action);
        Assert.Null(Assert.Single(plan.Operations).Diffs);
        var desired = document with { Slot = document.Slot! with { Tag = "changed" } };
        var changedPlan = await service.PlanApplyAsync(desired, new(StatePath));
        Assert.Equal("update", Assert.Single(changedPlan.Operations).Action);
        Assert.Equal("tag", Assert.Single(Assert.Single(changedPlan.Operations).Diffs!).Member);
        client.Readbacks = 0;
        await service.ApplyAsync(desired, new(StatePath));
        Assert.Equal("changed", Assert.Single(client.Updates).Tag);
        Assert.Equal(1, client.Readbacks);
        client.Updates.Clear();
        await service.ApplyAsync(desired, new(StatePath));
        Assert.Empty(client.Updates);
    }

    [Fact]
    public async Task SlotTagOmissionPreservesLiveValueAndOldPlanAndRequestShape()
    {
        var client = new TagClient();
        var service = new WorldService(client);
        var document = Document();
        var result = await service.ApplyAsync(document, new(StatePath));
        Assert.Null(Assert.Single(client.Creates).Tag);
        client.Slots[result.SlotId] = client.Slots[result.SlotId] with { Tag = "external" };
        var plan = await service.PlanApplyAsync(document, new(StatePath));
        Assert.Equal("no-op", Assert.Single(plan.Operations).Action);
        Assert.Null(Assert.Single(plan.Operations).Diffs);
        var renamed = document with { Slot = document.Slot! with { Name = "Renamed", Position = [1, 2, 3] } };
        var previous = ApplyStateStore.SaveFault.Value;
        var sawSlotIntent = false;
        try
        {
            ApplyStateStore.SaveFault.Value = (_, state) =>
            {
                foreach (var pending in state.Pending.Where(p => p.Kind == "updateSlot"))
                {
                    sawSlotIntent = true;
                    Assert.False(JsonSerializer.SerializeToNode(pending.SlotValues)!.AsObject().ContainsKey("Tag"));
                    Assert.DoesNotContain("tag", pending.Confirmed);
                    if (pending.Observed?["attributes"] is JsonObject attributes) Assert.False(attributes.ContainsKey("tag"));
                }
            };
            await service.ApplyAsync(renamed, new(StatePath));
        }
        finally { ApplyStateStore.SaveFault.Value = previous; }
        Assert.True(sawSlotIntent);
        var request = Assert.Single(client.Updates);
        Assert.Equal(result.SlotId, request.Id);
        Assert.Null(request.Tag);
        Assert.Equal("external", client.Slots[result.SlotId].Tag);
        Assert.Equal("Renamed", client.Slots[result.SlotId].Name);
        Assert.Equal(new Vector3Value(1, 2, 3), client.Slots[result.SlotId].Position);
        Assert.Equal("{\"Id\":\"S1\",\"Name\":\"Renamed\",\"Position\":{\"X\":1,\"Y\":2,\"Z\":3},\"Rotation\":null,\"Scale\":null,\"ParentId\":null}", JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task SlotTagExplicitEmptyClearsLiveValue()
    {
        var client = new TagClient();
        var service = new WorldService(client);
        var document = Document("old");
        var result = await service.ApplyAsync(document, new(StatePath));
        await service.ApplyAsync(document with { Slot = document.Slot! with { Tag = "" } }, new(StatePath));
        Assert.Equal("", Assert.Single(client.Updates).Tag);
        Assert.Equal("", client.Slots[result.SlotId].Tag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlotTagReadbackMismatchJournalsValuesAndNextApplyReplans(bool create)
    {
        var client = new TagClient();
        var service = new WorldService(client);
        if (!create) await service.ApplyAsync(Document("old"), new(StatePath));
        client.MismatchTag = true;
        client.Readbacks = 0;
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(Document("desired"), new(StatePath)));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("readbackMismatch", error.Context["reason"]);
        Assert.Equal(1, client.Readbacks);
        var state = ApplyStateStore.Load(StatePath, "tag-owner");
        Assert.Equal(3, state.SchemaVersion);
        var pending = Assert.Single(state.Pending);
        Assert.Equal("desired", pending.SlotValues!.Tag);
        Assert.DoesNotContain("tag", pending.Confirmed);
        Assert.Equal("external", pending.Observed!["attributes"]!["tag"]!.GetValue<string>());
        client.MismatchTag = false;
        var resumed = await service.ApplyAsync(Document("desired"), new(StatePath));
        Assert.Empty(ApplyStateStore.Load(StatePath, "tag-owner").Pending);
        Assert.Equal("desired", client.Slots[resumed.SlotId].Tag);
        Assert.Single(client.Creates);
        Assert.Contains(ApplyDiagnostics.ForRuntime(resumed).Diagnostics, d => d.Code == "APPLY_PENDING_RESOLVED_NOT_APPLIED" && d.Member == "tag");
    }

    [Theory]
    [InlineData("value")]
    [InlineData("fieldId")]
    [InlineData("generation")]
    public async Task SlotTagPlanDriftBlocksSendAndClearsUnsentIntent(string drift)
    {
        var client = new TagClient();
        var service = new WorldService(client);
        var result = await service.ApplyAsync(Document("old"), new(StatePath));
        var previous = ApplyStateStore.SaveFault.Value;
        try
        {
            ApplyStateStore.SaveFault.Value = (_, state) =>
            {
                if (state.Pending.Count == 0) return;
                if (drift == "generation") client.Generation = "reconnected";
                else if (drift == "fieldId") client.TagFieldId = "replacement-field";
                else client.Slots[result.SlotId] = client.Slots[result.SlotId] with { Tag = "external" };
            };
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(Document("desired"), new(StatePath)));
            Assert.Equal(drift == "generation" ? "CONNECTION_GENERATION_CHANGED" : "APPLY_PRECONDITION_FAILED", error.Code);
            if (drift != "generation") Assert.Equal("tag", error.Context["member"]);
            Assert.Empty(client.Updates);
            Assert.Empty(ApplyStateStore.Load(StatePath, "tag-owner").Pending);
        }
        finally { ApplyStateStore.SaveFault.Value = previous; }
    }

    [Fact]
    public async Task SlotTagObservedIFieldWriterBlocksOnlyDeclaredTagWrite()
    {
        var client = new TagClient();
        var service = new WorldService(client);
        var result = await service.ApplyAsync(Document("old"), new(StatePath));
        client.WriterSlotId = result.SlotId;
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(Document("desired"), new(StatePath)));
        Assert.Equal("writerDetected", error.Context["reason"]);
        Assert.Equal("tag", error.Context["member"]);
        Assert.Empty(client.Updates);
        await service.ApplyAsync(Document() with { Slot = Document().Slot! with { Name = "Renamed" } }, new(StatePath));
        Assert.Null(Assert.Single(client.Updates).Tag);
        Assert.Equal("old", client.Slots[result.SlotId].Tag);
    }

    [Fact]
    public async Task SlotTagAcknowledgedUpdateRejectionClearsPendingAndDoesOneReadback()
    {
        var client = new TagClient();
        var service = new WorldService(client);
        await service.ApplyAsync(Document("old"), new(StatePath));
        client.RejectUpdate = true;
        client.Readbacks = 0;
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(Document("desired"), new(StatePath)));
        Assert.Equal("SLOT_UPDATE_FAILED", error.Code);
        Assert.Equal(1, client.Readbacks);
        Assert.Empty(ApplyStateStore.Load(StatePath, "tag-owner").Pending);
        client.RejectUpdate = false;
        await service.ApplyAsync(Document("desired"), new(StatePath));
        Assert.Single(client.Updates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("new-tag")]
    public async Task SlotTagRelocationAndRenamePreserveStableKeyIdentity(string? tag)
    {
        var client = new TagClient();
        var service = new WorldService(client);
        var child = new ApplyNodeSpec(new("Child", null, null, null, null, Key: "child", Tag: "old-tag"), []);
        var initial = Document() with { Children = [child] };
        await service.ApplyAsync(initial, new(StatePath));
        var childId = ApplyStateStore.Load(StatePath, "tag-owner").Slots["child"].Id;
        var moved = child with { Slot = child.Slot with { Name = "Moved", Tag = tag, Position = [4, 5, 6] } };
        var target = initial with { Children = [new(new("Parent", null, null, null, null, Key: "parent"), [], [moved])] };
        await service.ApplyAsync(target, new(StatePath));
        var request = Assert.Single(client.Updates);
        Assert.Equal(childId, request.Id);
        Assert.Equal(tag, request.Tag);
        Assert.Equal("Moved", client.Slots[childId].Name);
        Assert.Equal(tag ?? "old-tag", client.Slots[childId].Tag);
        Assert.Equal(new Vector3Value(4, 5, 6), client.Slots[childId].Position);
        Assert.Equal(ApplyStateStore.Load(StatePath, "tag-owner").Slots["parent"].Id, client.Slots[childId].ParentId);
        Assert.Equal(childId, ApplyStateStore.Load(StatePath, "tag-owner").Slots["child"].Id);
    }

    // This fake independently models optional Slot writes and returns fresh observations.
    private sealed class TagClient : IResoniteClient, IApplySessionObservation, IApplySendEvidence
    {
        public Dictionary<string, SlotInfo> Slots { get; } = new() { ["Root"] = new("Root", "Root", null, null, null, null, true, true, "", false, [], []) };
        public List<SlotCreateRequest> Creates { get; } = [];
        public List<SlotUpdateRequest> Updates { get; } = [];
        public bool MismatchTag { get; set; }
        public bool RejectUpdate { get; set; }
        public string? WriterSlotId { get; set; }
        public string? TagFieldId { get; set; }
        public string Generation { get; set; } = "generation-1";
        public int Readbacks { get; set; }
        private bool readbackExpected;
        public bool ApplySendStarted { get; private set; }
        public bool ApplyResponseReceived { get; private set; }
        public bool ApplyResponseAccepted { get; private set; }
        public void BeginApplySend() { ApplySendStarted = false; ApplyResponseReceived = false; ApplyResponseAccepted = false; }
        public ApplySessionObservation ObserveApplySession() => new("ws://tag-fake", "S-tag-test", "matched");
        public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SessionInfo("ws://tag-fake", true, "test", "test", "unused", Generation));
        public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
        {
            if (readbackExpected) { Readbacks++; readbackExpected = false; Assert.Equal(0, depth); Assert.False(includeComponentData); }
            if (!Slots.ContainsKey(id)) throw new RLoopException("SLOT_NOT_FOUND", id, ExitCodes.NotFound);
            SlotInfo Observe(string slotId, int remaining)
            {
                var slot = Slots[slotId];
                IReadOnlyList<ComponentSummary> components = slot.Id == WriterSlotId ? [new("writer", "Test.Writer", includeComponentData ?
                    new Dictionary<string, MemberValue> { ["Target"] = new("reference", "writer-target", TargetId: slot.Id + ":Tag", TargetType: "[FrooxEngine]FrooxEngine.IField<string>") } : null)] : [];
                return slot with { Children = remaining == 0 ? [] : Slots.Values.Where(s => s.ParentId == slot.Id).Select(s => Observe(s.Id, remaining < 0 ? -1 : remaining - 1)).ToArray(),
                    Components = components, Members = new Dictionary<string, MemberValue> { ["Tag"] = new("field", TagFieldId ?? slot.Id + ":Tag", "string", JsonValue.Create(slot.Tag)) } };
            }
            return Task.FromResult(Observe(id, depth));
        }
        public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default)
        {
            ApplySendStarted = true;
            Creates.Add(request);
            var id = "S" + Creates.Count;
            Slots[id] = new(id, request.Name, request.ParentId, request.Position, request.Rotation, request.Scale, true, true,
                MismatchTag ? "external" : request.Tag ?? "", false, [], []);
            readbackExpected = true;
            return Task.FromResult(id);
        }
        public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default)
        {
            ApplySendStarted = true;
            readbackExpected = true;
            if (RejectUpdate) { ApplyResponseReceived = true; ApplyResponseAccepted = false; throw new RLoopException("SLOT_UPDATE_FAILED", "rejected", ExitCodes.OperationFailed); }
            Updates.Add(request);
            var prior = Slots[request.Id];
            Slots[request.Id] = prior with { Name = request.Name ?? prior.Name, ParentId = request.ParentId ?? prior.ParentId,
                Position = request.Position ?? prior.Position, Rotation = request.Rotation ?? prior.Rotation, Scale = request.Scale ?? prior.Scale,
                Tag = request.Tag is null ? prior.Tag : MismatchTag ? "external" : request.Tag };
            return Task.CompletedTask;
        }
        public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetComponentMembersAsync(string componentId, string componentType, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RLoop.Core.TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

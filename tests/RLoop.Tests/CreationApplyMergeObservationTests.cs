using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyMergeObservationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-merge-observation-" + Guid.NewGuid().ToString("N"));

    public CreationApplyMergeObservationTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialOwnerObservationDoesNotAdoptTheVisibleCandidate(bool referenceOnly)
    {
        var client = new CreationMutationTestClient();
        client.RegisterType("X");
        client.RegisterType("B", "Value:System.String");
        client.EngineAutoAdds["X"] = "B";
        var candidateCanBePartial = false;
        client.AfterAddComponent = (_, type, _) =>
        {
            if (type == "X") candidateCanBePartial = true;
        };
        client.DeletionObservationOverride = id =>
        {
            var slot = client.Slots[id];
            if (!candidateCanBePartial || id != "slot-1")
                return new ApplyDeletionObservation(slot, true, true);
            if (referenceOnly) slot = slot with { IsReferenceOnly = true };
            return new ApplyDeletionObservation(slot, true, referenceOnly);
        };
        var statePath = Path.Combine(root, "state.json");
        var document = CreationDocument.Parse("""
            {"schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"room",
             "definition":{"schemaVersion":"1","ownership":{"key":"room"},"slot":{"key":"root","name":"Room","parent":"Root"},
             "components":[{"type":"X","key":"x"},{"type":"B","key":"b","fields":{"Value":"written"}}],"children":[]},"diagnostics":[]}
            """);

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).ApplyAsync(document,
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test",
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test"))));

        Assert.Equal("COMPONENT_NOT_FOUND", error.Code);
        Assert.Empty(client.SetRequests);
        Assert.Equal("creatingTree", CreationStateStore.Load(statePath, "room", true)!.Pending!.Stage);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

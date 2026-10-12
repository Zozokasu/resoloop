using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationPreviousRootAbsentTests : IDisposable
{
    private const string Session = "S-creation-test";
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationId\":\"room\",\"creationVersion\":\"1\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-absent-" + Guid.NewGuid().ToString("N"));

    public CreationPreviousRootAbsentTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task PlanTreatsExactlyAbsentRecordedRootAsCreate()
    {
        var (client, path) = Arrange("plan", present: false);

        var plan = await new CreationApplyService(client).PlanAsync(CreationDocument.Parse(Envelope),
            new CreationPlanOptions(path, RequestedSessionId: Session));

        Assert.Equal("create", plan.Action);
        Assert.Null(plan.PreviousRootId);
        AssertAbsentDiagnostic(plan.Diagnostics);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task ApplySendsNoDeleteAndReplacesTheStaleActiveRoot()
    {
        var (client, path) = Arrange("apply", present: false);

        var result = await Apply(client, path);

        Assert.DoesNotContain(client.RequestHistory, item => item.StartsWith("delete-slot", StringComparison.Ordinal));
        Assert.False(result.Replaced);
        Assert.Null(result.PreviousRootId);
        AssertAbsentDiagnostic(result.Diagnostics);
        var state = CreationStateStore.Load(path, "room", true)!;
        Assert.Equal("slot-1", state.ActiveRoot!.Id);
        Assert.Null(state.Pending);
    }

    public enum Unproven { Partial, OtherId, Timeout, SessionChanged }

    [Theory]
    [InlineData(Unproven.Partial)]
    [InlineData(Unproven.OtherId)]
    [InlineData(Unproven.Timeout)]
    [InlineData(Unproven.SessionChanged)]
    public async Task UnprovenAbsenceStopsWithoutWrites(Unproven kind)
    {
        var (client, path) = Arrange("unproven-" + kind, present: true);
        var expectedCode = kind == Unproven.SessionChanged
            ? CreationOwnershipVerifier.SessionUnproven("x").Code
            : CreationOwnershipVerifier.ObservationIncomplete("x").Code;
        switch (kind)
        {
            case Unproven.Partial: client.IncompleteSlotId = CreationOwnershipFixture.RootId; break;
            case Unproven.OtherId: client.SlotReadOverride = (slot, _) => slot with { Id = "someone-else" }; break;
            case Unproven.Timeout: client.SlotReadOverride = (_, _) => throw new TimeoutException("slow"); break;
            default:
                client.SlotReadOverride = (_, _) =>
                {
                    client.SessionId = "S-other";
                    throw new RLoopException("SLOT_NOT_FOUND", "gone", ExitCodes.NotFound);
                };
                break;
        }

        var planError = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).PlanAsync(
            CreationDocument.Parse(Envelope), new CreationPlanOptions(path, RequestedSessionId: Session)));
        Assert.Equal(expectedCode, planError.Code);
        client.SessionId = Session;
        var applyError = await Assert.ThrowsAsync<RLoopException>(() => Apply(client, path));
        Assert.Equal(expectedCode, applyError.Code);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public async Task DifferentParentIsAcceptedWhenTheRecordedRootIsAbsent()
    {
        var (client, path) = Arrange("parent", present: false);

        await Apply(client, path, parent: "Root");

        Assert.Equal("create-slot:Root:Room", client.RequestHistory[0]);
    }

    [Fact]
    public async Task RollbackAfterMidTreeFailureRemovesCandidateAndClearsActiveRoot()
    {
        var (client, path) = Arrange("rollback", present: false);
        client.ThrowOnMutation = 3;
        await Assert.ThrowsAsync<RLoopException>(() => Apply(client, path));
        Assert.NotNull(CreationStateStore.Load(path, "room", true)!.Pending);

        await new CreationRecoveryService(client).RecoverAsync(new(path, "rollback", Session, true));

        var state = CreationStateStore.Load(path, "room", true)!;
        Assert.Null(state.ActiveRoot);
        Assert.Null(state.Pending);
        Assert.DoesNotContain("slot-1", client.Slots.Keys);
    }

    private (CreationMutationTestClient Client, string Path) Arrange(string name, bool present)
    {
        var path = Path.Combine(root, name + ".json");
        var prior = CreationOwnershipFixture.State();
        var client = new CreationMutationTestClient();
        client.SeedOwnedRoot(prior, prior.ActiveRoot!.Stamp);
        if (!present)
        {
            var parentId = prior.ActiveRoot.ParentId;
            client.Slots.Remove(prior.ActiveRoot.Id);
            client.Slots[parentId] = client.Slots[parentId] with { Children = [] };
        }
        CreationStateStore.Save(path, prior);
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks-" + name));
        return (client, path);
    }

    private static Task<CreationApplyResult> Apply(CreationMutationTestClient client, string path, string? parent = null) =>
        new CreationApplyService(client).ApplyAsync(CreationDocument.Parse(Envelope),
            new CreationApplyOptions(StateFile: path, Yes: true, RequestedSessionId: Session, Parent: parent,
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test")));

    private static void AssertAbsentDiagnostic(IReadOnlyList<object?> diagnostics)
    {
        var json = JsonSerializer.SerializeToElement(Assert.Single(diagnostics));
        Assert.Equal("CREATION_PREVIOUS_ROOT_ABSENT", json.GetProperty("code").GetString());
        Assert.Equal("warning", json.GetProperty("severity").GetString());
        Assert.Equal(CreationOwnershipFixture.RootId, json.GetProperty("previousRootId").GetString());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

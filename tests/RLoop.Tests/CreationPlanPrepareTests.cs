using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationPlanPrepareTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[{\"type\":\"Fixture.Missing\",\"key\":\"missing\"}],\"children\":[]},\"diagnostics\":[]}";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-plan-prepare-" + Guid.NewGuid().ToString("N"));

    public CreationPlanPrepareTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task PlanFailsWithTheSameCodeAsApplyForAnUnknownTypeAndSendsNothing()
    {
        var client = new CreationMutationTestClient();
        SessionLockTestIsolation.Share(client, Path.Combine(root, "locks"));
        var document = CreationDocument.Parse(Envelope);
        var service = new CreationApplyService(client);

        var apply = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document,
            new CreationApplyOptions(StateFile: Path.Combine(root, "apply.json"), Yes: true, RequestedSessionId: "S-test",
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test"))));
        var plan = await Assert.ThrowsAsync<RLoopException>(() => service.PlanAsync(document,
            new CreationPlanOptions(Path.Combine(root, "plan.json"), RequestedSessionId: "S-test",
                GeneratedContentSource: GeneratedContentMetadata.SourceForVersion("test"))));

        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", apply.Code);
        Assert.Equal(apply.Code, plan.Code);
        Assert.Empty(client.SendLog);
    }

    [Fact]
    public void TimeoutShowsCreationRecoveryGuidanceOnlyForRegenerateAndRecoverCommands()
    {
        var context = new Dictionary<string, object?>
        {
            ["pending"] = new object(), ["operationId"] = "op", ["stateFile"] = "s.json"
        };
        var withPending = new RLoopException("APPLY_CANCELLED", "x", ExitCodes.OperationFailed, context,
            ["resoloop creation recover --state \"s.json\" --action rollback --url auto --session S-ID --yes"]);
        var ordinaryApply = ParsedArguments.Parse(["apply", "file.json"]);
        var regenerateApply = ParsedArguments.Parse(["apply", "file.json", "--mode", "regenerate"]);
        var recover = ParsedArguments.Parse(["creation", "recover", "--state", "s.json"]);

        Assert.Equal("Inspect the state, pending evidence and exact targets before reconciling or explicitly discarding a pending operation. Increase --command-timeout only after checking responsiveness.",
            Assert.Single(Program.TimeoutSuggestions(withPending, ordinaryApply)));
        Assert.Contains(Program.TimeoutSuggestions(withPending, regenerateApply),
            item => item.Contains("creation recover", StringComparison.Ordinal));
        Assert.Contains(Program.TimeoutSuggestions(withPending, recover),
            item => item.Contains("creation recover", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

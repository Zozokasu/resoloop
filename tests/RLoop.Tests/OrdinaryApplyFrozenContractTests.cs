using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task OrdinaryApplyFrozenContractStateV3FromThisVersionPlansNoChanges()
    {
        var fixtureDirectory = Path.Combine(RepoRoot(), "tests", "RLoop.Tests", "fixtures", "ordinary-apply-surface");
        var definitionPath = Path.Combine(_root, "definition.json");
        var statePath = Path.Combine(_root, "state-v3.json");
        File.Copy(Path.Combine(fixtureDirectory, "definition.json"), definitionPath);
        File.Copy(Path.Combine(fixtureDirectory, "state-v3.json"), statePath);
        var document = ApplyDocument.Load(definitionPath);
        var client = new FakeResoniteClient { DiscoverId = "S-frozen-fixture" };
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(Path.Combine(_root, "created.state.json")));
        client.ResetWriteCounts();

        var plan = await service.PlanApplyAsync(document, new ApplyOptions(statePath));

        Assert.True(plan.Valid);
        Assert.Empty(plan.Changes);
        Assert.Equal(0, client.Writes);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResoLoop.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

using RLoop.Cli;

namespace RLoop.Tests;

[CollectionDefinition("Creation regenerate console", DisableParallelization = true)]
public sealed class CreationRegenerateConsoleCollection { }

[Collection("Creation regenerate console")]
public sealed class CreationRegenerateUsageTests
{
    [Fact]
    public async Task HelpSurfacesExperimentalSafetyAndRecoveryCommands()
    {
        var all = await Help();
        var apply = await Help("apply");
        var plan = await Help("plan");
        var diff = await Help("diff");
        var creation = await Help("creation");

        Assert.Contains("--mode regenerate", all);
        Assert.Contains("creation recover", all);
        Assert.Contains("resoloop plan creation.json --mode regenerate --state creation-state.json --url auto --session S-ID --json", plan);
        Assert.Contains("--replace-order delete-first --url auto --session S-ID --yes --json", apply);
        Assert.Contains("experimental", apply);
        Assert.Contains("including manually added content", apply);
        Assert.Contains("--url auto --session S-ID", plan);
        Assert.Contains("live behavior is unverified", plan);
        Assert.Contains("Asset/Flux/Script placement is unsupported", diff);
        Assert.Contains("hand-written JSON envelopes only", diff);
        Assert.Contains("no v3 migration", diff);
        Assert.Contains("does not restore the old root", diff);

        Assert.Contains("action rollback", diff);
        Assert.Contains("action finish", diff);
        Assert.Contains("--discard-pending OP", diff);
        Assert.Contains("unmarked candidate is never deleted automatically", diff);
        Assert.Contains("discard-pending clears only the local pending record", diff);
        Assert.Contains("creation recover --bind-root", creation);
        Assert.Contains("migrate", creation);
        Assert.Contains("inspect", creation);
        Assert.Contains("world", creation);
    }

    private static async Task<string> Help(params string[] args)
    {
        var oldOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await Program.RunAsync(["help", .. args]));
        }
        finally
        {
            Console.SetOut(oldOut);
        }
        return output.ToString();
    }
}

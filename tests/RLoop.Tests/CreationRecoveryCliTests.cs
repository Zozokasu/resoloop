using RLoop.Core;
using RLoop.Cli;

namespace RLoop.Tests;

public sealed class CreationRecoveryCliTests
{
    [Fact]
    public async Task ConfirmationFailureIsReportedBeforeConnection()
    {
        var connects = 0;
        var exit = await Program.RunAsync(["creation", "recover", "--state", "missing.json", "--action", "rollback",
            "--url", "auto", "--session", CreationRecoveryTestData.SessionId], _ => { connects++; return Task.FromResult<IResoniteClient>(new CreationMutationTestClient()); });
        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task MissingPendingStateIsReportedBeforeConnection()
    {
        var connects = 0;
        var path = CreationRecoveryTestData.PathFor(nameof(MissingPendingStateIsReportedBeforeConnection));
        var exit = await Program.RunAsync(["creation", "recover", "--state", path, "--action", "rollback",
            "--url", "auto", "--session", CreationRecoveryTestData.SessionId, "--yes"], _ => { connects++; return Task.FromResult<IResoniteClient>(new CreationMutationTestClient()); });
        Assert.Equal(ExitCodes.NotFound, exit);
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task DirectoryStateIsClassifiedInvalidBeforeConnection()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(DirectoryStateIsClassifiedInvalidBeforeConnection));
        Directory.CreateDirectory(path);
        var connects = 0;
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            var exit = await Program.RunAsync(["creation", "recover", "--state", path, "--action", "rollback",
                "--url", "auto", "--session", CreationRecoveryTestData.SessionId, "--yes", "--json"],
                _ => { connects++; return Task.FromResult<IResoniteClient>(CreationRecoveryTestData.Client()); });
            Assert.Equal(ExitCodes.ValidationFailed, exit);
        }
        finally { Console.SetError(original); }
        Assert.Contains("APPLY_STATE_INVALID", stderr.ToString());
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task LegacyV3StateIsClassifiedMigrationBeforeConnection()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(LegacyV3StateIsClassifiedMigrationBeforeConnection));
        File.WriteAllText(path, "{\"kind\":\"creation\",\"schemaVersion\":3,\"creationId\":\"room\"}");
        var connects = 0;
        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            var exit = await Program.RunAsync(["creation", "recover", "--state", path, "--action", "rollback",
                "--url", "auto", "--session", CreationRecoveryTestData.SessionId, "--yes", "--json"],
                _ => { connects++; return Task.FromResult<IResoniteClient>(CreationRecoveryTestData.Client()); });
            Assert.Equal(ExitCodes.ValidationFailed, exit);
        }
        finally { Console.SetError(original); }
        Assert.Contains("CREATION_STATE_MIGRATION_REQUIRED", stderr.ToString());
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task RollbackConnectHookUsesOfflineRouteAndKeepsExactOldRoot()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(RollbackConnectHookUsesOfflineRouteAndKeepsExactOldRoot));
        var previous = CreationOwnershipFixture.State().ActiveRoot!;
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending("deletingPrevious", previous: previous), previous);
        CreationStateStore.Save(path, state);
        var client = CreationRecoveryTestData.Client();
        client.SeedOwnedRoot(state, previous.Stamp);
        var connects = 0;

        var exit = await Program.RunAsync(["creation", "recover", "--state", path, "--action", "rollback",
            "--url", "auto", "--session", CreationRecoveryTestData.SessionId, "--yes", "--json"],
            _ => { connects++; return Task.FromResult<IResoniteClient>(client); });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, connects);
        Assert.Empty(client.SendLog);
        var final = CreationStateStore.Load(path, state.CreationId, true)!;
        Assert.Null(final.Pending);
        Assert.Equal(previous.Id, final.ActiveRoot!.Id);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("bind-root")]
    public async Task UnsupportedRecoveryModesFailBeforeConnection(string mode)
    {
        var connects = 0;
        string[] args = mode == "bind-root"
            ? ["creation", "recover", "--state", "missing.json", "--action", "rollback", "--url", "auto", "--session", CreationRecoveryTestData.SessionId, "--yes", "--bind-root", "candidate"]
            : ["creation", "recover", "--state", "missing.json", "--action", mode, "--url", "auto", "--session", CreationRecoveryTestData.SessionId, "--yes"];
        var exit = await Program.RunAsync(args, _ => { connects++; return Task.FromResult<IResoniteClient>(new CreationMutationTestClient()); });
        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task DiscardDoesNotRequireUrlOrSessionAndDoesNotConnect()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(DiscardDoesNotRequireUrlOrSessionAndDoesNotConnect));
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending());
        CreationStateStore.Save(path, state);
        var connects = 0;

        var exit = await Program.RunAsync(["creation", "recover", "--state", path, "--discard-pending", "operation-4", "--yes"],
            _ => { connects++; return Task.FromResult<IResoniteClient>(new CreationMutationTestClient()); });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(0, connects);
        Assert.Null(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
    }
}

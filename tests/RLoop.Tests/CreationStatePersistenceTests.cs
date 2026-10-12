using System.Text;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationStatePersistenceTests : IDisposable
{
    private const string Nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-persistence-" + Guid.NewGuid().ToString("N"));

    public CreationStatePersistenceTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OldCreationStateVersionsRequireMigrationBeforeKindValidation(int version)
    {
        var error = LoadError($"{{\"schemaVersion\":{version},\"kind\":\"wrong\"}}", "room");
        Assert.Equal("CREATION_STATE_MIGRATION_REQUIRED", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
    }

    [Fact]
    public void VersionFourIsRejectedByLegacyApplyStore()
    {
        var path = Write("legacy.json", "{\"schemaVersion\":4,\"ownershipKey\":\"room\",\"slots\":{},\"components\":{},\"pending\":[]}");
        var error = Assert.Throws<RLoopException>(() => ApplyStateStore.Load(path, "room"));
        Assert.Equal("APPLY_STATE_VERSION_UNSUPPORTED", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":9}", "APPLY_STATE_VERSION_UNSUPPORTED")]
    [InlineData("{\"schemaVersion\":4,\"kind\":\"creation\",\"creationId\":\"room\",\"session\":{},\"ownerNonce\":\"bad\",\"activeRoot\":null,\"pending\":null,\"unknown\":1}", "APPLY_STATE_INVALID")]
    [InlineData("{\"schemaVersion\":4,\"schemaVersion\":4}", "APPLY_STATE_INVALID")]
    [InlineData("{broken", "APPLY_STATE_INVALID")]
    [InlineData("{\"schemaVersion\":4,\"kind\":\"other\",\"creationId\":\"room\",\"session\":{\"normalizedUrl\":\"ws://x\",\"discoverSessionId\":null,\"identityStatus\":\"unknown\"},\"ownerNonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"activeRoot\":null,\"pending\":null}", "APPLY_STATE_INVALID")]
    public void RejectsUnsupportedMalformedDuplicateAndUnknownState(string json, string expectedCode)
    {
        var error = LoadError(json, "room");
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
    }

    [Fact]
    public void RejectsCreationIdMismatchWithOwnershipDiagnostic()
    {
        var json = StateJson("other");
        var error = LoadError(json, "room");
        Assert.Equal("APPLY_STATE_OWNERSHIP_MISMATCH", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Contains("creationId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionUnknownIsPreservedAndRequiredFieldsAreNotDefaulted()
    {
        var state = CreationStateStore.Load(Write("unknown-session.json", StateJson("room", "unknown")), "room");
        Assert.Equal("unknown", state!.Session.IdentityStatus);
        Assert.Equal("APPLY_STATE_INVALID", LoadError(StateJson("room").Replace("\"ownerNonce\":", "\"ownerNonce\":null,\"ignored\":"), "room").Code);
        Assert.Equal("APPLY_STATE_INVALID", LoadError(StateJson("room").Replace("\"ownerNonce\":", "\"unexpected\":1,\"ownerNonce\":"), "room").Code);
        Assert.Equal("APPLY_STATE_INVALID", LoadError(StateJson("room").Replace(Nonce, "bad"), "room").Code);
        Assert.Equal("APPLY_STATE_INVALID", LoadError(StateJson("room").Replace("\"discoverSessionId\":null,", ""), "room").Code);
    }

    [WindowsCheckpointFact]
    public void ReadOnlyDestinationSaveFailurePreservesBytesAndCleansTemporaryFile()
    {
        var path = Path.Combine(root, "readonly.json");
        const string previous = "{\"old\":true}";
        File.WriteAllText(path, previous, new UTF8Encoding(false));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var error = Assert.Throws<RLoopException>(() => CreationStateStore.Save(path, ValidState()));
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
            Assert.Equal(ExitCodes.OperationFailed, error.ExitCode);
            Assert.Equal(previous, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(root, Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    private RLoopException LoadError(string json, string creationId) =>
        Assert.Throws<RLoopException>(() => CreationStateStore.Load(Write("input-" + Guid.NewGuid().ToString("N") + ".json", json), creationId));

    private string Write(string name, string json)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static string StateJson(string creationId, string identityStatus = "known") =>
        "{\"kind\":\"creation\",\"schemaVersion\":4,\"creationId\":\"" + creationId + "\",\"session\":{\"normalizedUrl\":\"ws://localhost:1234\",\"discoverSessionId\":null,\"identityStatus\":\"" + identityStatus + "\"},\"ownerNonce\":\"" + Nonce + "\",\"activeRoot\":null,\"pending\":null}";

    private static CreationState ValidState() => new("creation", 4, "room",
        new CreationSession("ws://localhost:1234", null, "unknown"), Nonce);

    public void Dispose() => Directory.Delete(root, recursive: true);
}

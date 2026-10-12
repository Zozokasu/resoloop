using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationStateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-state-" + Guid.NewGuid().ToString("N"));
    private const string Nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public CreationStateTests() => Directory.CreateDirectory(root);

    [Fact]
    public void SavesRootOnlyNullStateAsHandwrittenCamelCaseShape()
    {
        var path = Path.Combine(root, "state.json");
        CreationStateStore.Save(path, BasicState());
        using var actual = JsonDocument.Parse(File.ReadAllText(path));
        var names = actual.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "activeRoot", "creationId", "kind", "ownerNonce", "pending", "schemaVersion", "session" }, names);
        Assert.Equal("creation", actual.RootElement.GetProperty("kind").GetString());
        Assert.Equal(4, actual.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, actual.RootElement.GetProperty("activeRoot").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.RootElement.GetProperty("pending").ValueKind);
        Assert.DoesNotContain(actual.RootElement.EnumerateObject(), property =>
            new[] { "slots", "components", "objects", "assets", "scripts", "variables" }.Contains(property.Name, StringComparer.Ordinal));
        Assert.Equal("known", CreationStateStore.Load(path, "room")!.Session.IdentityStatus);
    }

    [Fact]
    public void PendingEvidenceRoundTripsAgainstIndependentHandwrittenJson()
    {
        const string expected = """
        {"kind":"creation","schemaVersion":4,"creationId":"room","session":{"normalizedUrl":"ws://localhost:1234","discoverSessionId":"session-a","identityStatus":"known"},"ownerNonce":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","activeRoot":{"id":"root-a","parentId":"world","generationId":"gen-a","stamp":"stamp-a","inputHash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","buildId":"build-a"},"pending":{"operationId":"op-a","stage":"creatingTree","scope":{"kind":"object","userId":"user-a","previousObjectId":"old-a","candidateObjectId":"new-a","parentId":"root-a"},"replaceOrder":"delete-first","session":{"normalizedUrl":"ws://localhost:1234","discoverSessionId":"session-a","identityStatus":"known"},"connectionGeneration":"conn-a","buildId":"build-a","inputHash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","catalogHash":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","previousRoot":{"id":"root-old","parentId":"world","generationId":"gen-old","stamp":"stamp-old","inputHash":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","buildId":null},"candidateRoot":{"id":null,"parentId":"world","generationId":null,"stamp":null,"inputHash":null,"buildId":"build-a"},"steps":[{"kind":"createObject","targetId":"new-a","parentId":"root-a","type":"Example.Component","sendStatus":"sent","responseReceived":true,"responseAccepted":true,"expected":{"value":1},"observed":{"value":1},"confirmed":true,"completeness":{"complete":true}}],"evidence":{"source":"fixture","sequence":7}}}
        """;
        var path = Path.Combine(root, "pending.json");
        var state = new CreationState("creation", 4, "room", Session(), Nonce,
            new CreationActiveRoot("root-a", "world", "gen-a", "stamp-a", Hash, "build-a"),
            new CreationPending("op-a", "creatingTree",
                new CreationScope("object", "user-a", "old-a", "new-a", "root-a"), "delete-first", Session(),
                "conn-a", "build-a", Hash, new string('c', 64),
                new CreationActiveRoot("root-old", "world", "gen-old", "stamp-old", new string('d', 64), null),
                new CreationPendingRoot(null, "world", null, null, null, "build-a"),
                [new CreationStep("createObject", "new-a", "root-a", "Example.Component", "sent", true, true,
                    Element("{\"value\":1}"), Element("{\"value\":1}"), true, Element("{\"complete\":true}"))],
                Element("{\"source\":\"fixture\",\"sequence\":7}")));

        CreationStateStore.Save(path, state);
        var expectedNode = JsonNode.Parse(expected);
        var actualNode = JsonNode.Parse(File.ReadAllText(path));
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode), "Persisted JSON must match the independent handwritten fixture.");
        var loaded = CreationStateStore.Load(path, "room")!;
        Assert.Equal("delete-first", loaded.Pending!.ReplaceOrder);
        Assert.Equal("creatingTree", loaded.Pending.Stage);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"source":"fixture","sequence":7}"""),
            JsonNode.Parse(loaded.Pending.Evidence!.Value.GetRawText())), "Evidence must match the handwritten JSON value.");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"value":1}"""),
            JsonNode.Parse(loaded.Pending.Steps[0].Observed!.Value.GetRawText())), "Observed must match the handwritten JSON value.");

        CreationStateStore.Save(path, loaded);
        Assert.True(JsonNode.DeepEquals(expectedNode, JsonNode.Parse(File.ReadAllText(path))),
            "A state loaded with every pending field must save back to the handwritten JSON value.");

        CreationStateStore.Save(path, loaded with { Pending = loaded.Pending! with { ReplaceOrder = "stage-inactive" } });
        Assert.Equal("stage-inactive", CreationStateStore.Load(path, "room")!.Pending!.ReplaceOrder);
    }

    [Fact]
    public void ResolvePathUsesExplicitPathThenProjectRootAndKnownUtf8HashWithoutCreatingDirectories()
    {
        var projectRoot = Path.Combine(root, "project");
        var document = CreationDocument.Parse(Envelope(projectRoot));
        var expectedHash = "1f1c5b2fad778434024f1537986346927917f4755a6e7d3fd91f22653b7c3132";
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("room"))).ToLowerInvariant());
        Assert.Equal(Path.Combine(projectRoot, ".resoloop", "creation-state", expectedHash + ".json"), CreationStateStore.ResolvePath(document));
        var explicitPath = Path.Combine(root, "chosen.json");
        Assert.Equal(Path.GetFullPath(explicitPath), CreationStateStore.ResolvePath(document, explicitPath));
        Assert.False(Directory.Exists(Path.Combine(projectRoot, ".resoloop")));

        var overrideRoot = Path.Combine(root, "record-override");
        var noAuthoring = CreationDocument.Parse(Envelope(Path.Combine(root, "unused")));
        var overrideAuthoring = new ApplyAuthoringSpec(overrideRoot, "content/entry.tsx");
        Assert.Equal(Path.Combine(overrideRoot, ".resoloop", "creation-state", expectedHash + ".json"),
            CreationStateStore.ResolvePath(noAuthoring with { Authoring = overrideAuthoring }));
        Assert.False(Directory.Exists(overrideRoot));
    }

    [Fact]
    public void MissingLoadDoesNotCreateAnythingAndRequiredLoadUsesCreationStateCode()
    {
        var path = Path.Combine(root, "missing", "state.json");
        Assert.Null(CreationStateStore.Load(path, "room"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
        var error = Assert.Throws<RLoopException>(() => CreationStateStore.Load(path, "room", requireState: true));
        Assert.Equal("CREATION_STATE_REQUIRED", error.Code);
        Assert.Equal(ExitCodes.NotFound, error.ExitCode);
    }

    private static CreationState BasicState() => new("creation", 4, "room", Session(), Nonce);
    private static CreationSession Session() => new("ws://localhost:1234", "session-a", "known");
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Envelope(string projectRoot) =>
        "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"authoring\":{\"projectRoot\":" + JsonSerializer.Serialize(projectRoot) + ",\"source\":\"content/entry.tsx\"},\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]}}";

    public void Dispose() => Directory.Delete(root, recursive: true);
}

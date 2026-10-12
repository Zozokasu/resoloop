using System.Text.Json;
using System.Text.Json.Serialization;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationPlanTests : IDisposable
{
    private const string Envelope = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"diagnostics\":[]}";
    private const string InputHash = "8d44fcd6c980f46af1b961f08206e2272290ff32115d6a286174dad7fe93e1b0";
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-plan-" + Guid.NewGuid().ToString("N"));

    public CreationPlanTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task PlansFirstCreateWithHandwrittenResultAndNoStateOrWorldWrites()
    {
        var client = InitialClient();
        var statePath = Path.Combine(root, "creation-state.json");
        var document = CreationDocument.Parse(Envelope);
        Assert.Equal(InputHash, document.InputHash);

        var result = await new CreationApplyService(client).PlanAsync(document,
            new CreationPlanOptions(statePath, RequestedSessionId: CreationTestClient.SessionId));

        Assert.Equal("{\"resultVersion\":\"1\",\"mode\":\"regenerate\",\"creationId\":\"room\",\"stateFile\":" +
            JsonSerializer.Serialize(Path.GetFullPath(statePath)) +
            ",\"buildId\":null,\"inputHash\":\"" + InputHash + "\",\"action\":\"create\",\"previousRootId\":null," +
            "\"replaceOrder\":\"delete-first\",\"definitionsReset\":true,\"counts\":{\"slots\":1,\"components\":1}," +
            "\"pending\":null,\"evidence\":{\"overall\":\"complete\",\"session\":\"complete\",\"exactId\":\"unknown\"," +
            "\"parent\":\"complete\",\"rootStamp\":\"unknown\",\"siblingUniqueness\":\"complete\"," +
            "\"descendantBoundary\":\"unknown\"},\"diagnostics\":[],\"atomic\":false}", Serialize(result));
        Assert.False(File.Exists(statePath));
        Assert.Empty(client.Slots[CreationOwnershipFixture.ParentId].Slot.Children);
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(0, client.GuardWriteCalls);
    }

    [Fact]
    public async Task ExistingRootAlwaysPlansReplaceEvenWhenInputHashMatches()
    {
        var client = CreationOwnershipFixture.Client();
        var document = CreationDocument.Parse(Envelope);
        var statePath = Path.Combine(root, "replace-state.json");
        var active = CreationOwnershipFixture.State().ActiveRoot! with { InputHash = document.InputHash! };
        var state = CreationOwnershipFixture.State() with { ActiveRoot = active };
        CreationStateStore.Save(statePath, state);
        var before = File.ReadAllBytes(statePath);

        var result = await new CreationApplyService(client).PlanAsync(document,
            new CreationPlanOptions(statePath, RequestedSessionId: CreationTestClient.SessionId));

        Assert.Equal("{\"resultVersion\":\"1\",\"mode\":\"regenerate\",\"creationId\":\"room\",\"stateFile\":" +
            JsonSerializer.Serialize(Path.GetFullPath(statePath)) + ",\"buildId\":null,\"inputHash\":\"" + InputHash +
            "\",\"action\":\"replace\",\"previousRootId\":\"created-root\",\"replaceOrder\":\"delete-first\"," +
            "\"definitionsReset\":true,\"counts\":{\"slots\":1,\"components\":1},\"pending\":null," +
            "\"evidence\":{\"overall\":\"complete\",\"session\":\"complete\",\"exactId\":\"complete\"," +
            "\"parent\":\"complete\",\"rootStamp\":\"complete\",\"siblingUniqueness\":\"complete\"," +
            "\"descendantBoundary\":\"complete\"},\"diagnostics\":[],\"atomic\":false}", Serialize(result));
        Assert.Equal(before, File.ReadAllBytes(statePath));
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(0, client.GuardWriteCalls);
    }

    [Fact]
    public async Task PlanRequiresIntactRawSourceBeforeAnyObservationButValidateAllowsDirectConstruction()
    {
        var parsed = CreationDocument.Parse(Envelope);
        Assert.True((await (parsed with { SourceText = null, InputHash = null }).ValidateAsync()).Valid);
        var cases = new[]
        {
            parsed with { SourceText = null },
            parsed with { InputHash = new string('a', 64) }
        };
        foreach (var document in cases)
        {
            var client = new CreationTestClient();
            var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).PlanAsync(
                document, new CreationPlanOptions(Path.Combine(root, Guid.NewGuid().ToString("N")))));
            Assert.Equal("CREATION_DOCUMENT_INVALID", error.Code);
            Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
            Assert.Equal(0, client.SessionReads);
            Assert.Empty(client.SlotReads);
            Assert.Empty(client.ComponentReads);
        }
    }

    [Fact]
    public void RawHashIsBOMStrippedAndCoversAuthoringArraysAndDiagnostics()
    {
        const string raw = "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[],\"children\":[]},\"variables\":[],\"authoring\":{\"projectRoot\":\"C:/work\",\"source\":\"main.tsx\"},\"diagnostics\":[{\"code\":\"IGNORED\"}]}";
        const string hash = "af5ee7d1ede56a75261cb2ebfe082a050ecfc474a0f04dad97cd858029822514";
        var document = CreationDocument.Parse("\uFEFF" + raw);

        Assert.Equal(raw, document.SourceText);
        Assert.Equal(hash, document.InputHash);
        Assert.Equal(hash, document.ValidatePlanInput());
        var json = JsonSerializer.Serialize(document);
        Assert.DoesNotContain("sourceText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inputHash", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedReplaceOrderIsAStableModeMismatch()
    {
        var client = InitialClient();
        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).PlanAsync(
            CreationDocument.Parse(Envelope), new CreationPlanOptions(Path.Combine(root, "unused.json"), ReplaceOrder: "stage-inactive")));
        Assert.Equal("CREATION_MODE_MISMATCH", error.Code);
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
        Assert.Contains("not available in this version", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.SessionReads);
    }

    [Fact]
    public async Task ActiveRootNullRemainsCreateAndRequiredStateIsLoadedBeforeObservation()
    {
        var client = InitialClient();
        var document = CreationDocument.Parse(Envelope);
        var statePath = Path.Combine(root, "inactive-state.json");
        CreationStateStore.Save(statePath, CreationOwnershipFixture.State() with { ActiveRoot = null });

        var result = await new CreationApplyService(client).PlanAsync(document,
            new CreationPlanOptions(statePath, RequireState: true, RequestedSessionId: CreationTestClient.SessionId));

        Assert.Equal("create", result.Action);
        Assert.Null(result.PreviousRootId);
        Assert.Equal(0, client.MutationCalls);
    }

    [Fact]
    public async Task MissingDefaultStateDoesNotCreateTheResoloopDirectory()
    {
        var projectRoot = Path.Combine(root, "fresh-project");
        var authoring = "\"authoring\":{" + "\"projectRoot\":" + JsonSerializer.Serialize(projectRoot) +
            ",\"source\":\"content/main.tsx\"},";
        var document = CreationDocument.Parse(Envelope.Replace("\"diagnostics\":[]", authoring + "\"diagnostics\":[]"));
        var client = InitialClient();

        var result = await new CreationApplyService(client).PlanAsync(document,
            new CreationPlanOptions(RequestedSessionId: CreationTestClient.SessionId));

        Assert.False(File.Exists(result.StateFile));
        Assert.False(Directory.Exists(Path.Combine(projectRoot, ".resoloop")));
        Assert.Equal(0, client.MutationCalls);
    }

    private CreationTestClient InitialClient()
    {
        var client = new CreationTestClient();
        client.AddSlot(CreationOwnershipFixture.Slot("Root", null, [],
            [CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "Root")]));
        client.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "Root"));
        return client;
    }

    private static string Serialize(CreationPlanResult result) => JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    public void Dispose() => Directory.Delete(root, recursive: true);
}

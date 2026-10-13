using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationApplyPropertyModeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "resoloop-creation-property-mode-" + Guid.NewGuid().ToString("N"));

    public CreationApplyPropertyModeTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("runtime")]
    [InlineData("driver-owned")]
    public async Task IdentityFieldWithNonManagedPropertyModeAppliesWithoutWritingField(string mode)
    {
        var document = CreationDocument.Parse(Envelope(mode, "true"));
        Assert.True((await document.ValidateAsync()).Valid);

        var statePath = Path.Combine(root, mode + ".json");
        var client = new CreationMutationTestClient();
        client.RegisterType("Fixture.Objects.Lamp", "Enabled:System.Boolean");
        SessionLockTestIsolation.Share(client, Path.Combine(root, mode + "-locks"));

        var result = await new CreationApplyService(client).ApplyAsync(document,
            new CreationApplyOptions(StateFile: statePath, Yes: true, RequestedSessionId: "S-test",
                GeneratedContentSource: "[resoloop test]"));

        var state = CreationStateStore.Load(statePath, "room", true)!;
        var lampRequest = Assert.Single(client.AddRequests, request => request.Type == "Fixture.Objects.Lamp");
        var lamp = Assert.Single(client.Components.Values, component => component.Type == "Fixture.Objects.Lamp");
        Assert.Empty(lampRequest.Fields);
        Assert.False(lamp.Members["Enabled"].Value!.GetValue<bool>());
        Assert.Contains(client.Components.Values, component => component.Type == CreationOwnershipVerifier.VariableComponentType &&
            component.Members["VariableName"].Value!.GetValue<string>() == CreationOwnershipVerifier.VariableName);
        Assert.Contains(client.Components.Values, component => component.Type == GeneratedContentMetadata.ComponentType &&
            component.Members[GeneratedContentMetadata.SourceMember].Value!.GetValue<string>() == "[resoloop test]");
        Assert.Equal(new[] { "create-slot", "add-component", "add-component", "add-component" }, client.SendLog);
        Assert.Equal(1, result.Counts.Slots);
        Assert.Equal(3, result.Counts.Components);
        Assert.Null(state.Pending);
        Assert.Equal(result.RootId, state.ActiveRoot!.Id);
    }

    [Fact]
    public async Task MissingComponentReferenceStillFailsOfflineSchemaValidation()
    {
        var document = CreationDocument.Parse(Envelope("runtime", "\"$component:missing\""));
        var validation = await document.ValidateAsync();
        var client = new CreationMutationTestClient();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationApplyService(client).ApplyAsync(document,
            new CreationApplyOptions(StateFile: Path.Combine(root, "invalid.json"), Yes: true)));

        Assert.False(validation.Valid);
        Assert.Contains(validation.Issues, issue => issue.Code == "CREATION_DOCUMENT_INVALID" &&
            issue.Path == "$.components[0].fields.Enabled");
        Assert.Equal("CREATION_DOCUMENT_INVALID", error.Code);
        Assert.Empty(client.SendLog);
        Assert.Equal(0, client.SessionReadCount);
    }

    private static string Envelope(string mode, string enabledJson) =>
        "{\"schemaVersion\":\"creation-1\",\"kind\":\"resoloop-creation\",\"creationVersion\":\"1\",\"creationId\":\"room\",\"definition\":{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"room\"},\"slot\":{\"key\":\"root\",\"name\":\"Room\",\"parent\":\"Root\"},\"components\":[{\"type\":\"Fixture.Objects.Lamp\",\"fields\":{\"Enabled\":" + enabledJson + "},\"identityFields\":[\"Enabled\"],\"propertyModes\":{\"Enabled\":\"" + mode + "\"}}],\"children\":[]},\"diagnostics\":[]}";

    public void Dispose() => Directory.Delete(root, recursive: true);
}

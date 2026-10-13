using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationUnsetVariableNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NullOrEmptyStringVariableNameIsNotAStamp(string? variableName)
    {
        var client = ClientWithVariableName(new MemberValue("field", Type: "System.String",
            Value: variableName is null ? null : JsonValue.Create(variableName)));

        var evidence = await new CreationOwnershipVerifier(client).ObserveInitialParentAsync(
            CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId);

        Assert.Equal(CreationEvidenceCompleteness.Complete, evidence.Completeness);
        Assert.Equal("complete-no-match", evidence.StampStatus);
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(0, client.ConnectCalls);
    }

    [Fact]
    public async Task MissingNonFieldUnknownTypeAndInvalidVariableNameStayIncomplete()
    {
        var cases = new[]
        {
            MakeClient(null),
            MakeClient(new MemberValue("reference", Type: "System.String", Value: JsonValue.Create(CreationOwnershipVerifier.VariableName))),
            MakeClient(new MemberValue("field", Type: "System.Int32", Value: JsonValue.Create(CreationOwnershipVerifier.VariableName))),
            MakeClient(new MemberValue("field", Type: "System.String", Value: JsonValue.Create(1)))
        };

        foreach (var client in cases)
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationOwnershipVerifier(client)
                .ObserveInitialParentAsync(CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId));
            Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
            Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
            Assert.Equal(0, client.MutationCalls);
        }
    }

    [Fact]
    public async Task MalformedStampValueRemainsOwnerUnproven()
    {
        var client = MakeClient(new MemberValue("field", Type: "System.String",
            Value: JsonValue.Create(CreationOwnershipVerifier.VariableName)), "malformed");

        var error = await Assert.ThrowsAsync<RLoopException>(() => new CreationOwnershipVerifier(client)
            .ObserveInitialParentAsync(CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId));

        Assert.Equal("CREATION_OWNER_UNPROVEN", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal(0, client.MutationCalls);
    }

    private static CreationTestClient ClientWithVariableName(MemberValue? variableName) => MakeClient(variableName);

    private static CreationTestClient MakeClient(MemberValue? variableName, string? stampValue = null)
    {
        var client = new CreationTestClient();
        var members = new Dictionary<string, MemberValue>(StringComparer.Ordinal)
        {
            ["Value"] = new("field", Type: "System.String", Value: JsonValue.Create(stampValue ?? "not-a-stamp"))
        };
        if (variableName is not null) members["VariableName"] = variableName;
        var component = new ComponentInfo("marker", CreationOwnershipVerifier.VariableComponentType, members);
        client.AddComponent(component);
        var child = CreationOwnershipFixture.Slot("child", CreationOwnershipFixture.ParentId,
            [CreationOwnershipFixture.Summary(component)]);
        client.AddSlot(child);
        client.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "world", [], [child]));
        return client;
    }
}

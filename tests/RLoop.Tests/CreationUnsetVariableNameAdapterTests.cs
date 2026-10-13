using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class CreationUnsetVariableNameAdapterTests
{
    [Fact]
    public async Task NullStringFieldMapsToReadableNullAndIsNotAStamp()
    {
        var mappedName = ModelMapper.MapMember(new Link.Field_string { ID = "variable-name", Value = null! });
        Assert.Equal("field", mappedName.Kind);
        Assert.Equal("System.String", mappedName.Type);
        Assert.Null(mappedName.Value);

        var client = new CreationTestClient();
        var marker = new ComponentInfo("marker", CreationOwnershipVerifier.VariableComponentType,
            new Dictionary<string, MemberValue>(StringComparer.Ordinal)
            {
                ["VariableName"] = mappedName,
                ["Value"] = new MemberValue("field", "stamp-value", "System.String", JsonValue.Create("not-a-stamp"))
            });
        client.AddComponent(marker);
        var child = CreationOwnershipFixture.Slot("child", CreationOwnershipFixture.ParentId,
            [CreationOwnershipFixture.Summary(marker)]);
        client.AddSlot(child);
        client.AddSlot(CreationOwnershipFixture.Slot(CreationOwnershipFixture.ParentId, "world", [], [child]));

        var evidence = await new CreationOwnershipVerifier(client).ObserveInitialParentAsync(
            CreationOwnershipFixture.ParentId, CreationOwnershipFixture.CreationId);

        Assert.Equal(CreationEvidenceCompleteness.Complete, evidence.Completeness);
        Assert.Equal("complete-no-match", evidence.StampStatus);
        Assert.Equal(0, client.MutationCalls);
    }
}

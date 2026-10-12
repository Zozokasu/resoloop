using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

internal sealed partial class CreationMutationTestClient
{
    internal void SeedOwnedRoot(CreationState state, string stampText, string? parentId = null)
    {
        SessionUrl = state.Session.NormalizedUrl;
        SessionId = state.Session.DiscoverSessionId ?? SessionId;
        var rootId = state.ActiveRoot!.Id;
        var ownerParent = parentId ?? state.ActiveRoot.ParentId;
        if (!Slots.ContainsKey(ownerParent))
        {
            var newParent = new SlotInfo(ownerParent, "Parent", "Root", null, null, null, true, true, null, false, [], []);
            Slots[ownerParent] = newParent;
            var world = Slots["Root"];
            Slots["Root"] = world with { Children = world.Children.Append(newParent).ToArray() };
        }
        var stampId = "seed-stamp";
        Types[CreationOwnershipVerifier.VariableComponentType] = new ComponentTypeInfo(
            CreationOwnershipVerifier.VariableComponentType, "FrooxEngine", null, false,
            [new MemberDefinitionInfo("VariableName", "field", "System.String", "System.String", null),
             new MemberDefinitionInfo("Value", "field", "System.String", "System.String", null)]);
        var stamp = new ComponentInfo(stampId, CreationOwnershipVerifier.VariableComponentType,
            new Dictionary<string, MemberValue>(StringComparer.Ordinal)
            {
                ["VariableName"] = new MemberValue("field", stampId + ":VariableName", "System.String",
                    JsonValue.Create(CreationOwnershipVerifier.VariableName)),
                ["Value"] = new MemberValue("field", stampId + ":Value", "System.String", JsonValue.Create(stampText))
            });
        Components[stampId] = stamp;
        var root = new SlotInfo(rootId, "Room", ownerParent, null, null, null, true, true,
            null, false, [new ComponentSummary(stampId, CreationOwnershipVerifier.VariableComponentType)], []);
        Slots[rootId] = root;
        var parent = Slots[ownerParent];
        Slots[ownerParent] = parent with { Children = parent.Children.Where(child => child.Id != rootId).Append(root).ToArray() };
    }

    internal void AddChild(string parentId, SlotInfo child)
    {
        Slots[child.Id] = child;
        var parent = Slots[parentId];
        Slots[parentId] = parent with { Children = parent.Children.Append(child).ToArray() };
    }
}

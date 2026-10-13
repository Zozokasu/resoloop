using System.Text.Json;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationVariablesBuildTests
{
    [Fact]
    public async Task HandwrittenCreationEnvelopeBuildsVariableSpaceValueAndBindings()
    {
        const string source = """
        {
          "schemaVersion":"creation-1","kind":"resoloop-creation","creationVersion":"1","creationId":"scene",
          "definition":{
              "schemaVersion":"1","ownership":{"key":"scene"},"slot":{"name":"Lamp","key":"scene"},
            "components":[
              {"type":"Fixture.Objects.Lamp","key":"light","propertyModes":{"Enabled":"driver-owned"}},
              {"type":"Fixture.Objects.Lamp","key":"toggle","propertyModes":{"Enabled":"runtime"}},
              {"type":"[FrooxEngine]FrooxEngine.DynamicVariableSpace","key":"__rlv_5_space_4_lamp_5_scene","fields":{"SpaceName":"lamp","OnlyDirectBinding":false}}
            ],
            "children":[
              {"slot":{"name":"DynamicVariables","key":"__rlv_9_variables_4_lamp_5_scene"},"components":[
                {"type":"[FrooxEngine]FrooxEngine.DynamicValueVariable<bool>","key":"__rlv_8_variable_4_lamp_5_scene_2_on","fields":{"VariableName":"on","Value":true},"identityFields":["VariableName"]}]},
              {"slot":{"name":"__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled","key":"__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled"},"components":[
                {"type":"[FrooxEngine]FrooxEngine.DynamicValueVariableDriver<bool>","key":"__rlv_7_binding_4_lamp_2_on_5_light_7_Enabled","fields":{"VariableName":"on","DefaultValue":true,"Target":"$member:light.Enabled"}}]},
              {"slot":{"name":"__rlv_4_wire_4_lamp_2_on_6_toggle_7_Enabled","key":"__rlv_4_wire_4_lamp_2_on_6_toggle_7_Enabled"},"components":[
                {"type":"[FrooxEngine]FrooxEngine.DynamicField<bool>","key":"__rlv_7_binding_4_lamp_2_on_6_toggle_7_Enabled","fields":{"VariableName":"on","TargetField":"$member:toggle.Enabled"}}]}
            ]
          }
        }
        """;
        var definition = CreationDocument.Parse(source).Definition;
        var client = new CreationMutationTestClient();
        client.RegisterType("Fixture.Objects.Lamp", "Enabled:System.Boolean");
        client.RegisterType("[FrooxEngine]FrooxEngine.DynamicVariableSpace", "SpaceName:System.String", "OnlyDirectBinding:System.Boolean");
        client.RegisterType("[FrooxEngine]FrooxEngine.DynamicValueVariable<bool>", "VariableName:System.String", "Value:System.Boolean");
        client.RegisterType("[FrooxEngine]FrooxEngine.DynamicValueVariableDriver<bool>",
            "VariableName:System.String", "DefaultValue:System.Boolean", "Target:System.String");
        client.RegisterType("[FrooxEngine]FrooxEngine.DynamicField<bool>", "VariableName:System.String", "TargetField:System.String");
        client.RegisterReference("[FrooxEngine]FrooxEngine.DynamicValueVariableDriver<bool>", "Target");
        client.RegisterReference("[FrooxEngine]FrooxEngine.DynamicField<bool>", "TargetField");
        var executor = new CreationTreeExecutor(client);
        var prepared = await executor.PrepareAsync(definition, CancellationToken.None);

        await executor.ExecuteAsync(prepared, "root-created", CreationTreeExecutorTests.Send(client), CancellationToken.None);

        Assert.DoesNotContain(client.FieldLog, item => item.Contains("Enabled", StringComparison.Ordinal));
        Assert.Contains("add:[FrooxEngine]FrooxEngine.DynamicVariableSpace:SpaceName,OnlyDirectBinding", client.FieldLog);
        Assert.Contains("add:[FrooxEngine]FrooxEngine.DynamicValueVariable<bool>:VariableName,Value", client.FieldLog);
        var driver = client.Components.Values.Single(component => component.Type.Contains("DynamicValueVariableDriver", StringComparison.Ordinal));
        var field = client.Components.Values.Single(component => component.Type.Contains("DynamicField", StringComparison.Ordinal));
        Assert.Equal("component-4:Enabled", driver.Members["Target"].TargetId);
        Assert.Equal("component-5:Enabled", field.Members["TargetField"].TargetId);
        Assert.Equal(3, client.SendLog.Count(operation => operation == "create-slot"));
        Assert.Equal(6, client.SendLog.Count(operation => operation == "add-component"));
        Assert.Equal(6, client.AddRequests.Count);
        Assert.Equal(2, client.SetRequests.Count);
        Assert.Equal("lamp", client.AddRequests[2].Fields["SpaceName"]);
        Assert.Equal("false", client.AddRequests[2].Fields["OnlyDirectBinding"]);
        Assert.Equal("on", client.AddRequests[3].Fields["VariableName"]);
        Assert.Equal("true", client.AddRequests[3].Fields["Value"]);
        Assert.Equal("root-created", client.AddRequests[0].SlotId);
        Assert.Equal("root-created", client.AddRequests[1].SlotId);
        Assert.Equal("root-created", client.AddRequests[2].SlotId);
        Assert.Equal("slot-1", client.AddRequests[3].SlotId);
        Assert.Equal("slot-2", client.AddRequests[4].SlotId);
        Assert.Equal("slot-3", client.AddRequests[5].SlotId);
        Assert.Equal("component-4:Enabled", client.SetRequests[0].Fields["Target"]);
        Assert.Equal("component-5:Enabled", client.SetRequests[1].Fields["TargetField"]);
    }
}

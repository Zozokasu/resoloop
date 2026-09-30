using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Workbench;

namespace RLoop.Workbench.Tests;

public sealed class WorkbenchReflectionMapperTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static RLoopException Throws(Action action) => Assert.Throws<RLoopException>(action);

    private const string ComponentResult = """
        {
          "value": {
            "type": {
              "fullTypeName": "[FrooxEngine]FrooxEngine.Slider",
              "assemblyName": "FrooxEngine",
              "namespace": "FrooxEngine",
              "name": "Slider",
              "baseType": { "type": "[FrooxEngine]FrooxEngine.Component", "isGenericParameter": false, "genericArguments": [] },
              "isComponent": true, "isSyncObject": false, "isAbstract": false, "isInterface": false,
              "isValueType": false, "isEnum": false, "isGenericType": false,
              "genericArguments": [], "interfaces": []
            },
            "categoryPath": "Transform/Drivers",
            "flattened": true,
            "members": {
              "Value": {
                "name": "Value", "kind": "Field",
                "wrapperType": { "type": "[FrooxEngine]FrooxEngine.Sync<>", "isGenericParameter": false,
                  "genericArguments": [ { "type": "[mscorlib]System.Single", "isGenericParameter": false, "genericArguments": [] } ] },
                "valueType": { "type": "[mscorlib]System.Single", "isGenericParameter": false, "genericArguments": [] },
                "targetType": null, "element": null
              },
              "Target": {
                "name": "Target", "kind": "Reference",
                "wrapperType": { "type": "[FrooxEngine]FrooxEngine.SyncRef<>", "isGenericParameter": false,
                  "genericArguments": [ { "type": "[FrooxEngine]FrooxEngine.IField", "isGenericParameter": false, "genericArguments": [] } ] },
                "valueType": null,
                "targetType": { "type": "[FrooxEngine]FrooxEngine.IField", "isGenericParameter": false, "genericArguments": [] },
                "element": null
              },
              "Items": {
                "name": "Items", "kind": "List",
                "wrapperType": { "type": "[FrooxEngine]FrooxEngine.SyncList", "isGenericParameter": false, "genericArguments": [] },
                "valueType": null, "targetType": null,
                "element": { "name": "Items", "kind": "Reference", "wrapperType": null, "valueType": null,
                  "targetType": { "type": "[FrooxEngine]FrooxEngine.Slot", "isGenericParameter": false, "genericArguments": [] },
                  "element": null }
              },
              "Mode": {
                "name": "Mode", "kind": "field",
                "wrapperType": { "type": "[FrooxEngine]FrooxEngine.Slider<>+Direction", "isGenericParameter": false,
                  "genericArguments": [ { "type": "float", "isGenericParameter": false, "genericArguments": [] } ] },
                "valueType": { "type": "[FrooxEngine]FrooxEngine.Slider<>+Direction", "isGenericParameter": false,
                  "genericArguments": [ { "type": "float", "isGenericParameter": false, "genericArguments": [] } ] },
                "targetType": null, "element": null
              }
            },
            "methods": [
              {
                "name": "OnChanged", "isStatic": false, "isAsync": true,
                "returnType": { "type": "[mscorlib]System.Void", "isGenericParameter": false, "genericArguments": [] },
                "parameters": {
                  "value": { "type": "[mscorlib]System.Single", "isGenericParameter": false, "genericArguments": [] }
                }
              }
            ]
          },
          "provenance": { "sessionId": "s-1", "connectionId": "c-1", "resoniteVersion": "2025.9.2.1349",
            "observedAt": "2026-09-30T00:00:00+00:00", "source": "Live" },
          "unknownReason": null
        }
        """;

    [Fact]
    public void MapSearch_ReturnsTypesInReportedOrder()
    {
        const string json = """
            { "value": { "types": ["[FrooxEngine]FrooxEngine.Slider", "[FrooxEngine]FrooxEngine.AA", "[FrooxEngine]FrooxEngine.ZZ"],
              "truncated": true }, "provenance": null, "unknownReason": null }
            """;

        IReadOnlyList<string> names = WorkbenchReflectionMapper.MapSearch(Json(json), out bool truncated);

        Assert.Equal(new[]
        {
            "[FrooxEngine]FrooxEngine.Slider", "[FrooxEngine]FrooxEngine.AA", "[FrooxEngine]FrooxEngine.ZZ"
        }, names);
        Assert.True(truncated);
    }

    [Fact]
    public void MapSearch_Unknown_ThrowsUnavailable()
    {
        const string json = """{ "value": null, "provenance": null, "unknownReason": "No session is connected." }""";

        var ex = Throws(() => WorkbenchReflectionMapper.MapSearch(Json(json), out _));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapSearch_NonStringType_ThrowsUnavailable()
    {
        const string json = """{ "value": { "types": ["a", 42], "truncated": false } }""";

        var ex = Throws(() => WorkbenchReflectionMapper.MapSearch(Json(json), out _));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapComponentType_MapsMembersAndMethods()
    {
        ComponentTypeInfo info = WorkbenchReflectionMapper.MapComponentType(
            Json(ComponentResult), "[FrooxEngine]FrooxEngine.Slider");

        Assert.Equal("[FrooxEngine]FrooxEngine.Slider", info.FullTypeName);
        Assert.Equal("Transform/Drivers", info.CategoryPath);
        Assert.Equal("[FrooxEngine]FrooxEngine.Component", info.BaseType);
        Assert.False(info.IsGeneric);

        Assert.Equal(new[] { "Value", "Target", "Items", "Mode" }, info.Members.Select(member => member.Name));

        MemberDefinitionInfo value = info.Members[0];
        Assert.Equal("field", value.Kind);
        Assert.Equal("[FrooxEngine]FrooxEngine.Sync<[mscorlib]System.Single>", value.MemberType);
        Assert.Equal("[mscorlib]System.Single", value.ValueType);
        Assert.Null(value.TargetType);

        MemberDefinitionInfo target = info.Members[1];
        Assert.Equal("reference", target.Kind);
        Assert.Equal("[FrooxEngine]FrooxEngine.SyncRef<[FrooxEngine]FrooxEngine.IField>", target.MemberType);
        Assert.Equal("[FrooxEngine]FrooxEngine.IField", target.TargetType);
        Assert.Null(target.ValueType);

        MemberDefinitionInfo items = info.Members[2];
        Assert.Equal("list", items.Kind);
        Assert.Equal("[FrooxEngine]FrooxEngine.SyncList", items.MemberType);
        Assert.Null(items.ValueType);
        Assert.Null(items.TargetType);

        // A generic argument nested in a declaring type fills the <> placeholder, matching the
        // direct path's ModelMapper.Render rather than the Workbench's own Display.
        MemberDefinitionInfo mode = info.Members[3];
        Assert.Equal("field", mode.Kind);
        Assert.Equal("[FrooxEngine]FrooxEngine.Slider<float>+Direction", mode.MemberType);
        Assert.Equal("[FrooxEngine]FrooxEngine.Slider<float>+Direction", mode.ValueType);

        SyncMethodInfo method = Assert.Single(info.Methods!);
        Assert.Equal("OnChanged", method.Name);
        Assert.False(method.IsStatic);
        Assert.True(method.IsAsync);
        Assert.Equal("[mscorlib]System.Void", method.ReturnType);
        Assert.Equal("[mscorlib]System.Single", method.Parameters["value"]);
    }

    [Fact]
    public void MapComponentType_Unknown_ThrowsComponentTypeNotFound()
    {
        const string json = """{ "value": null, "provenance": null, "unknownReason": "No session is connected." }""";

        var ex = Throws(() => WorkbenchReflectionMapper.MapComponentType(Json(json), "Nope"));

        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Equal("Nope", ex.Context["type"]);
        Assert.Contains("No session is connected.", ex.Message);
    }

    [Fact]
    public void MemberNames_ReturnsDeclaredNamesInOrder()
    {
        IReadOnlyList<string> names = WorkbenchReflectionMapper.MemberNames(
            Json(ComponentResult), "[FrooxEngine]FrooxEngine.Slider");

        Assert.Equal(new[] { "Value", "Target", "Items", "Mode" }, names);
    }

    [Fact]
    public void MemberNames_Unknown_ThrowsComponentTypeNotFound()
    {
        const string json = """{ "value": null, "unknownReason": "gone" }""";

        var ex = Throws(() => WorkbenchReflectionMapper.MemberNames(Json(json), "Nope"));

        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", ex.Code);
    }

    private static string TypeResult(string typeDefinition) => $$"""
        { "value": {{typeDefinition}}, "provenance": null, "unknownReason": null }
        """;

    private const string SliderTypeDefinition = """
        {
          "fullTypeName": "[FrooxEngine]FrooxEngine.Slider",
          "assemblyName": "FrooxEngine", "namespace": "FrooxEngine", "name": "Slider",
          "baseType": { "type": "[FrooxEngine]FrooxEngine.Component", "isGenericParameter": false, "genericArguments": [] },
          "isComponent": true, "isSyncObject": false, "isAbstract": false, "isInterface": false,
          "isValueType": false, "isEnum": false, "isGenericType": false,
          "genericArguments": [],
          "interfaces": [ { "type": "[FrooxEngine]FrooxEngine.IWorldElement", "isGenericParameter": false, "genericArguments": [] } ]
        }
        """;

    [Fact]
    public void MapType_Component_IsWorldElementTrue()
    {
        Core.TypeInfo info = WorkbenchReflectionMapper.MapType(
            Json(TypeResult(SliderTypeDefinition)), null, "[FrooxEngine]FrooxEngine.Slider");

        Assert.Equal("[FrooxEngine]FrooxEngine.Slider", info.FullTypeName);
        Assert.Equal("FrooxEngine", info.AssemblyName);
        Assert.Equal("FrooxEngine", info.Namespace);
        Assert.Equal("Slider", info.Name);
        Assert.Equal("[FrooxEngine]FrooxEngine.Component", info.BaseType);
        Assert.True(info.IsComponent);
        Assert.True(info.IsWorldElement);
        Assert.Equal(new[] { "[FrooxEngine]FrooxEngine.IWorldElement" }, info.Interfaces);
        Assert.Empty(info.GenericParameters);
        Assert.Null(info.EnumValues);
        Assert.Null(info.IsFlags);
    }

    private const string Int32TypeDefinition = """
        {
          "fullTypeName": "[mscorlib]System.Int32",
          "assemblyName": "mscorlib", "namespace": "System", "name": "Int32",
          "baseType": { "type": "[mscorlib]System.ValueType", "isGenericParameter": false, "genericArguments": [] },
          "isComponent": false, "isSyncObject": false, "isAbstract": false, "isInterface": false,
          "isValueType": true, "isEnum": false, "isGenericType": false,
          "genericArguments": [], "interfaces": []
        }
        """;

    [Fact]
    public void MapType_ValueType_IsWorldElementFalse()
    {
        Core.TypeInfo info = WorkbenchReflectionMapper.MapType(
            Json(TypeResult(Int32TypeDefinition)), null, "[mscorlib]System.Int32");

        Assert.Equal("Int32", info.Name);
        Assert.False(info.IsEnum);
        Assert.False(info.IsWorldElement);
    }

    private const string AlignmentTypeDefinition = """
        {
          "fullTypeName": "[FrooxEngine]FrooxEngine.Alignment",
          "assemblyName": "FrooxEngine", "namespace": "FrooxEngine", "name": "Alignment",
          "baseType": { "type": "[mscorlib]System.Enum", "isGenericParameter": false, "genericArguments": [] },
          "isComponent": false, "isSyncObject": false, "isAbstract": false, "isInterface": false,
          "isValueType": true, "isEnum": true, "isGenericType": false,
          "genericArguments": [], "interfaces": []
        }
        """;

    private const string AlignmentEnumResult = """
        {
          "value": {
            "status": "Found", "typeName": "[FrooxEngine]FrooxEngine.Alignment",
            "underlyingType": "[mscorlib]System.Int32", "isFlags": true,
            "values": [ { "name": "Near", "value": 2 }, { "name": "Far", "value": 8 } ],
            "detail": null
          },
          "provenance": null, "unknownReason": null
        }
        """;

    [Fact]
    public void MapType_Enum_ReadsValuesFromEnumResult()
    {
        Core.TypeInfo info = WorkbenchReflectionMapper.MapType(
            Json(TypeResult(AlignmentTypeDefinition)), Json(AlignmentEnumResult), "[FrooxEngine]FrooxEngine.Alignment");

        Assert.True(info.IsEnum);
        Assert.False(info.IsWorldElement);
        Assert.Equal(true, info.IsFlags);
        Assert.NotNull(info.EnumValues);
        Assert.Equal(2, info.EnumValues!["Near"]);
        Assert.Equal(8, info.EnumValues["Far"]);
    }

    [Fact]
    public void MapType_EnumWithoutEnumResult_ThrowsUnsupported()
    {
        var ex = Throws(() => WorkbenchReflectionMapper.MapType(
            Json(TypeResult(AlignmentTypeDefinition)), null, "[FrooxEngine]FrooxEngine.Alignment"));

        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
    }

    [Fact]
    public void MapType_EnumResultUnknown_ThrowsUnavailable()
    {
        const string enumResult = """{ "value": null, "provenance": null, "unknownReason": "no session" }""";

        var ex = Throws(() => WorkbenchReflectionMapper.MapType(
            Json(TypeResult(AlignmentTypeDefinition)), Json(enumResult), "[FrooxEngine]FrooxEngine.Alignment"));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapType_EnumResultNotAnEnum_ThrowsUnavailable()
    {
        const string enumResult = """
            { "value": { "status": "NotAnEnum", "typeName": "x", "underlyingType": null,
              "isFlags": false, "values": [], "detail": "x is not an enum type." },
              "provenance": null, "unknownReason": null }
            """;

        var ex = Throws(() => WorkbenchReflectionMapper.MapType(
            Json(TypeResult(AlignmentTypeDefinition)), Json(enumResult), "[FrooxEngine]FrooxEngine.Alignment"));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    private const string PlainClassDefinition = """
        {
          "fullTypeName": "[FrooxEngine]FrooxEngine.SomeHelper",
          "assemblyName": "FrooxEngine", "namespace": "FrooxEngine", "name": "SomeHelper",
          "baseType": { "type": "[mscorlib]System.Object", "isGenericParameter": false, "genericArguments": [] },
          "isComponent": false, "isSyncObject": false, "isAbstract": false, "isInterface": false,
          "isValueType": false, "isEnum": false, "isGenericType": false,
          "genericArguments": [], "interfaces": []
        }
        """;

    [Fact]
    public void MapType_UndecidableWorldElement_ThrowsUnsupported()
    {
        var ex = Throws(() => WorkbenchReflectionMapper.MapType(
            Json(TypeResult(PlainClassDefinition)), null, "[FrooxEngine]FrooxEngine.SomeHelper"));

        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
        Assert.Contains("isWorldElement", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapType_OpenGeneric_ReportsParameterNames()
    {
        const string definition = """
            {
              "fullTypeName": "[FrooxEngine]FrooxEngine.FieldDrive<>",
              "assemblyName": "FrooxEngine", "namespace": "FrooxEngine", "name": null,
              "baseType": { "type": "[FrooxEngine]FrooxEngine.Component", "isGenericParameter": false, "genericArguments": [] },
              "isComponent": true, "isSyncObject": false, "isAbstract": false, "isInterface": false,
              "isValueType": false, "isEnum": false, "isGenericType": true,
              "genericArguments": [ { "type": "T", "isGenericParameter": true, "genericArguments": [] } ],
              "interfaces": []
            }
            """;

        Core.TypeInfo info = WorkbenchReflectionMapper.MapType(
            Json(TypeResult(definition)), null, "[FrooxEngine]FrooxEngine.FieldDrive<>");

        Assert.Equal("FieldDrive", info.Name);
        Assert.True(info.IsGeneric);
        Assert.Equal(new[] { "T" }, info.GenericParameters);
    }

    [Fact]
    public void MapType_ClosedGeneric_ThrowsUnsupported()
    {
        const string definition = """
            {
              "fullTypeName": "[FrooxEngine]FrooxEngine.Sync<float>",
              "assemblyName": "FrooxEngine", "namespace": "FrooxEngine", "name": "Sync",
              "baseType": null,
              "isComponent": false, "isSyncObject": true, "isAbstract": false, "isInterface": false,
              "isValueType": false, "isEnum": false, "isGenericType": true,
              "genericArguments": [ { "type": "float", "isGenericParameter": false, "genericArguments": [] } ],
              "interfaces": []
            }
            """;

        var ex = Throws(() => WorkbenchReflectionMapper.MapType(
            Json(TypeResult(definition)), null, "[FrooxEngine]FrooxEngine.Sync<float>"));

        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
    }

    [Fact]
    public void MapType_Unknown_ThrowsTypeNotFound()
    {
        const string json = """{ "value": null, "provenance": null, "unknownReason": "session closed" }""";

        var ex = Throws(() => WorkbenchReflectionMapper.MapType(Json(json), null, "FrooxEngine.Missing"));

        Assert.Equal("TYPE_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
        Assert.Equal("FrooxEngine.Missing", ex.Context["type"]);
    }

    [Fact]
    public void MapMember_Field_ParsesNumberValue()
    {
        MemberValue member = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"field","id":"m-1","valueType":"float","valueJson":"1.5"}"""));

        Assert.Equal("field", member.Kind);
        Assert.Equal("m-1", member.Id);
        Assert.Null(member.Type);
        Assert.Equal(1.5, member.Value!.GetValue<double>());
    }

    [Fact]
    public void MapMember_Field_ParsesStringAndFloat3()
    {
        MemberValue text = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"field","id":"m-2","valueType":"string","valueJson":"\"hello\""}"""));
        MemberValue vector = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"field","id":"m-3","valueType":"float3","valueJson":"{\"x\":1,\"y\":2,\"z\":3}"}"""));

        Assert.Equal("hello", text.Value!.GetValue<string>());
        Assert.Equal(1.0, vector.Value!["x"]!.GetValue<double>());
        Assert.Equal(3.0, vector.Value!["z"]!.GetValue<double>());
        Assert.Null(vector.Type);
    }

    [Fact]
    public void MapMember_EnumField_UsesEnumType()
    {
        MemberValue member = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"field","id":"m-4","valueType":"enum","enumType":"[FrooxEngine]FrooxEngine.Alignment","valueJson":"\"Near\""}"""));

        Assert.Equal("field", member.Kind);
        Assert.Equal("[FrooxEngine]FrooxEngine.Alignment", member.Type);
        Assert.Equal("Near", member.Value!.GetValue<string>());
    }

    [Fact]
    public void MapMember_FieldWithoutValueJson_ValueIsNull()
    {
        MemberValue member = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"field","id":"m-5","valueType":"float","valueJson":null}"""));

        Assert.Null(member.Value);
        Assert.Null(member.Type);
    }

    [Fact]
    public void MapMember_Reference_MapsTarget()
    {
        MemberValue member = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"reference","id":"r-1","targetId":"Reso_42","targetType":"[FrooxEngine]FrooxEngine.Slot"}"""));

        Assert.Equal("reference", member.Kind);
        Assert.Equal("Reso_42", member.TargetId);
        Assert.Equal("[FrooxEngine]FrooxEngine.Slot", member.TargetType);
    }

    [Fact]
    public void MapMember_List_MapsElementsRecursively()
    {
        MemberValue member = WorkbenchMemberMapper.MapMember(Json("""
            {"kind":"list","id":"l-1","elements":[
              {"kind":"field","id":"e-1","valueType":"float","valueJson":"1.5"},
              {"kind":"reference","id":"e-2","targetId":"Reso_7","targetType":"[FrooxEngine]FrooxEngine.Slot"}]}
            """));

        Assert.Equal("list", member.Kind);
        Assert.Equal(2, member.Elements!.Count);
        Assert.Equal("field", member.Elements[0].Kind);
        Assert.Equal("reference", member.Elements[1].Kind);
        Assert.Equal("Reso_7", member.Elements[1].TargetId);
    }

    [Fact]
    public void MapMember_SyncObject_MapsMemberDictionary()
    {
        MemberValue member = WorkbenchMemberMapper.MapMember(Json("""
            {"kind":"syncObject","id":"s-1","members":{
              "Weight":{"kind":"field","id":"w-1","valueType":"float","valueJson":"1"},
              "Target":{"kind":"reference","id":"t-1","targetId":null,"targetType":"[FrooxEngine]FrooxEngine.Slot"}}}
            """));

        Assert.Equal("syncObject", member.Kind);
        Assert.Equal(new[] { "Weight", "Target" }, member.Members!.Keys.ToArray());
        Assert.Equal("field", member.Members["Weight"].Kind);
        Assert.Null(member.Members["Target"].TargetId);
    }

    [Fact]
    public void MapMember_Opaque_MapsEmptyPlaybackAndOthers()
    {
        MemberValue empty = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"opaque","id":"o-1","wireType":"empty"}"""));
        MemberValue playback = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"opaque","id":"o-2","wireType":"playback"}"""));
        MemberValue array = WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"opaque","id":"o-3","wireType":"float[]"}"""));

        Assert.Equal("empty", empty.Kind);
        Assert.Equal("SyncPlayback", playback.Kind);
        Assert.Equal("[FrooxEngine]FrooxEngine.SyncPlayback", playback.Type);
        Assert.Null(playback.Value);
        Assert.Equal("opaque", array.Kind);
        Assert.Equal("float[]", array.Type);
        Assert.Null(array.Value);
    }

    [Fact]
    public void MapMember_UnknownKind_ThrowsUnavailable()
    {
        var ex = Throws(() => WorkbenchMemberMapper.MapMember(
            Json("""{"kind":"blob","id":"x-1"}""")));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapMember_MissingKind_ThrowsUnavailable()
    {
        var ex = Throws(() => WorkbenchMemberMapper.MapMember(Json("""{"id":"x-1"}""")));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public void MapReadback_KnownMember_MapsMember()
    {
        MemberReadResult result = WorkbenchMemberMapper.MapReadback(Json("""
            {"sessionId":"s-1","connectionId":"c-1","componentId":"c-9",
             "componentType":"[FrooxEngine]FrooxEngine.Slider","memberName":"Value",
             "member":{"kind":"field","id":"m-1","valueType":"float","valueJson":"0.25"},
             "observedAt":"2026-09-30T00:00:00+00:00","unknownReason":null}
            """));

        Assert.Equal("[FrooxEngine]FrooxEngine.Slider", result.ComponentType);
        Assert.Null(result.UnknownReason);
        Assert.NotNull(result.Member);
        Assert.Equal("field", result.Member!.Kind);
        Assert.Equal(0.25, result.Member.Value!.GetValue<double>());
    }

    [Fact]
    public void MapReadback_UnknownMember_CarriesReason()
    {
        MemberReadResult result = WorkbenchMemberMapper.MapReadback(Json("""
            {"sessionId":"s-1","connectionId":"c-1","componentId":"c-9",
             "componentType":"[FrooxEngine]FrooxEngine.Slider","memberName":"Nope",
             "member":null,"observedAt":"2026-09-30T00:00:00+00:00",
             "unknownReason":"Component c-9 has no member named exactly 'Nope'."}
            """));

        Assert.Equal("[FrooxEngine]FrooxEngine.Slider", result.ComponentType);
        Assert.Null(result.Member);
        Assert.Equal("Component c-9 has no member named exactly 'Nope'.", result.UnknownReason);
    }

    [Fact]
    public void MapReadback_Malformed_ThrowsUnavailable()
    {
        var ex = Throws(() => WorkbenchMemberMapper.MapReadback(Json("""[1,2]""")));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }
}

using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class ReflectionMetadataCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-definition-" + Guid.NewGuid().ToString("N"));
    private readonly SessionInfo session = new("ws://localhost:1/", true, "engine1", "link1", "connection1");
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task SdkDefinitionsRoundTripWithoutLosingNestedConversionMetadata()
    {
        var definition = new Link.ComponentDefinition
        {
            Type = new Link.TypeDefinition { FullTypeName = "[Test]Component" }, Methods = [],
            Members = new()
            {
                ["Values"] = new Link.ListDefinition { ElementDefinition = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "int" } } },
                ["Targets"] = new Link.DictionaryDefinition { KeyType = new Link.TypeReference { Type = "string" }, ElementDefinition = new Link.ReferenceDefinition { TargetType = new Link.TypeReference { Type = "[Test]Target" } } },
                ["Nullable"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "Nullable<>", GenericArguments = [new Link.TypeReference { Type = "float" }] } },
                ["Embedded"] = new Link.SyncObjectMemberDefinition { Type = new Link.TypeReference { Type = "[Test]Embedded" } }
            }
        };
        var store = new ReflectionMetadataCache(session, new("auto", directory));
        store.Write("component-sdk", definition.Type.FullTypeName, definition, DateTimeOffset.UtcNow.AddYears(-1));
        var next = new ReflectionMetadataCache(session with { UniqueSessionId = "restarted", Url = "ws://127.0.0.1:21237/" }, new("auto", directory));
        var stored = next.Read<Link.ComponentDefinition>("component-sdk", definition.Type.FullTypeName, ResoniteLinkClientAdapter.ValidComponentDefinition);
        Assert.NotNull(stored); Assert.False(stored.Live);
        Assert.IsType<Link.SyncObjectMemberDefinition>(stored.Value.Members["Embedded"]);
        var dictionary = Assert.IsType<Link.DictionaryDefinition>(stored.Value.Members["Targets"]);
        Assert.Equal("[Test]Target", Assert.IsType<Link.ReferenceDefinition>(dictionary.ElementDefinition).TargetType.Type);
        var link = new Link.LinkInterface(); // SDK disposal requires an established connection.
        var list = Assert.IsType<Link.SyncList>(await ValueCodec.ParseAsync(link, stored.Value.Members["Values"], "[1,2]"));
        Assert.Equal(2, list.Elements.Count);
        var nullable = Assert.IsType<Link.Field_Nullable_float>(await ValueCodec.ParseAsync(link, stored.Value.Members["Nullable"], "0.5"));
        Assert.Equal(.5f, nullable.Value);
    }

    [Fact]
    public void VersionScopeOffAndMalformedSdkMetadataCannotSupplyDefinitions()
    {
        var value = new Link.ComponentDefinition { Type = new Link.TypeDefinition { FullTypeName = "T" }, Methods = [],
            Members = new() { ["Broken"] = new Link.FieldDefinition() } };
        var store = new ReflectionMetadataCache(session, new("auto", directory));
        store.Write("component-sdk", "T", value, DateTimeOffset.UtcNow);
        Assert.Null(store.Read<Link.ComponentDefinition>("component-sdk", "T", ResoniteLinkClientAdapter.ValidComponentDefinition));
        value.Members.Clear(); store.Write("component-sdk", "T", value, DateTimeOffset.UtcNow);
        var changed = new ReflectionMetadataCache(session with { ResoniteLinkVersion = "link2" }, new("auto", directory));
        Assert.Null(changed.Read<Link.ComponentDefinition>("component-sdk", "T", ResoniteLinkClientAdapter.ValidComponentDefinition));
        var off = new ReflectionMetadataCache(session, new("off", directory));
        off.Write("component-sdk", "Other", value, DateTimeOffset.UtcNow); off.Invalidate("component-sdk", "T");
        Assert.Null(off.Read<Link.ComponentDefinition>("component-sdk", "T", _ => true));
        Assert.Single(Directory.GetFiles(directory));
        Assert.NotNull(store.Read<Link.ComponentDefinition>("component-sdk", "T", ResoniteLinkClientAdapter.ValidComponentDefinition));
    }
}

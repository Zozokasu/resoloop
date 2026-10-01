using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

/// <summary>
/// Scripted fake of the metadata seam used by the category-walk and connection-generation tests.
/// Categories are keyed by the path the adapter requests ("" is the root).
/// </summary>
internal sealed class ScriptedMetadataLink : IMetadataLink
{
    public IReadOnlyList<string> AllTypes = [];
    public readonly Dictionary<string, LinkTypeList> Categories = new(StringComparer.Ordinal);
    public readonly List<string> RequestedCategories = [];
    public readonly Dictionary<string, Link.ComponentDefinition> Definitions = new();
    public readonly Dictionary<string, Link.TypeDefinition> TypeDefinitions = new();
    public readonly Dictionary<string, Func<Task<LinkTypeList>>> CategoryFaults = new(StringComparer.Ordinal);
    public bool Connected = true;
    public Link.SlotData SlotResponse = new();
    public readonly List<Link.GetSlot> RequestedSlots = [];
    public int ConnectCalls;
    public int DefinitionCalls;
    public int GetAllCalls;
    public int SessionCalls;
    public TaskCompletionSource? SessionFailureGate;
    public TaskCompletionSource? SessionStarted;
    public readonly Dictionary<string, string?> TypeDefinitionFailures = new();
    public readonly Dictionary<string, string?> ComponentDefinitionFailures = new();
    public readonly List<string> RequestedTypeDefinitions = [];
    public readonly List<string> RequestedComponentDefinitions = [];
    /// <summary>When set, GetComponentDefinition waits for this task before answering.</summary>
    public TaskCompletionSource? DefinitionGate;
    public TaskCompletionSource? DefinitionStarted;

    public bool IsConnected => Connected;

    public Task<Link.SlotData> GetSlotData(Link.GetSlot request)
    {
        RequestedSlots.Add(request);
        return Task.FromResult(SlotResponse);
    }

    public Task Connect(Uri uri, CancellationToken cancellationToken)
    {
        ConnectCalls++;
        Connected = true;
        return Task.CompletedTask;
    }

    public async Task<LinkSessionData> GetSessionData()
    {
        SessionCalls++;
        if (SessionFailureGate is not null)
        {
            SessionStarted?.TrySetResult();
            await SessionFailureGate.Task;
            return new(false, "old session failed", null, null, null);
        }
        return new(true, null, "2026.1.1.1", "0.13.1", "fake-session");
    }

    /// <summary>When set, GetAllComponentTypes signals AllTypesStarted, waits for this task, then answers a failure.</summary>
    public TaskCompletionSource? AllTypesFailureGate;
    public TaskCompletionSource? AllTypesStarted;
    public TaskCompletionSource? TypeDefinitionFailureGate;
    public TaskCompletionSource? TypeDefinitionStarted;

    public async Task<LinkTypeList> GetAllComponentTypes()
    {
        GetAllCalls++;
        if (AllTypesFailureGate is not null)
        {
            AllTypesStarted?.TrySetResult();
            await AllTypesFailureGate.Task;
            return new LinkTypeList(false, "list failed", null, null);
        }
        return new LinkTypeList(true, null, AllTypes, []);
    }

    public Task<LinkTypeList> GetComponentTypes(string category)
    {
        RequestedCategories.Add(category);
        if (CategoryFaults.TryGetValue(category, out var fault)) return fault();
        return Task.FromResult(Categories.TryGetValue(category, out var list) ? list : new LinkTypeList(false, "Unknown category.", null, null));
    }

    public async Task<LinkComponentDefinition> GetComponentDefinition(string type)
    {
        DefinitionCalls++;
        RequestedComponentDefinitions.Add(type);
        DefinitionStarted?.TrySetResult();
        if (DefinitionGate is not null) await DefinitionGate.Task;
        if (ComponentDefinitionFailures.TryGetValue(type, out var error)) return new(false, error, default!);
        return Definitions.TryGetValue(type, out var definition)
            ? new LinkComponentDefinition(true, null, definition)
            : new LinkComponentDefinition(false, "Component type not found.", default!);
    }

    public async Task<LinkTypeDefinition> GetTypeDefinition(string type)
    {
        RequestedTypeDefinitions.Add(type);
        if (TypeDefinitionFailures.TryGetValue(type, out var error)) return new(false, error, default!);
        if (TypeDefinitionFailureGate is not null)
        {
            TypeDefinitionStarted?.TrySetResult();
            await TypeDefinitionFailureGate.Task;
            return new LinkTypeDefinition(false, "type failed", default!);
        }
        return TypeDefinitions.TryGetValue(type, out var definition)
            ? new LinkTypeDefinition(true, null, definition)
            : new LinkTypeDefinition(false, "Type not found.", default!);
    }
    public Task<LinkEnumDefinition> GetEnumDefinition(string type) => Task.FromResult(new LinkEnumDefinition(false, "Enum not found.", default!, false));
    public void Dispose() { }

    public static LinkTypeList Level(string[]? types, string[]? subs) => new(true, null, types, subs);

    public static Link.ComponentDefinition Definition(string fullName) => new()
    {
        Type = new Link.TypeDefinition { FullTypeName = fullName },
        Methods = [],
        Members = new() { ["Intensity"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "float" } } }
    };
}

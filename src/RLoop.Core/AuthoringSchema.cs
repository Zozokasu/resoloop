using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record AuthoringProperty(string Name, string Type, bool Required, object? Default);
public sealed record AuthoringContract(string Topic, IReadOnlyList<AuthoringProperty> Properties, object Example, string Note);

/// <summary>Small, offline authoring contracts derived from the same DTOs used by the parser.</summary>
public static class AuthoringSchema
{
    private static readonly IReadOnlyDictionary<string, Type> Topics = new Dictionary<string, Type>
    {
        ["authoring"] = typeof(ApplyAuthoringSpec), ["document"] = typeof(ApplyDocument), ["slot"] = typeof(ApplySlotSpec),
        ["node"] = typeof(ApplyNodeSpec), ["component"] = typeof(ApplyComponentSpec),
        ["camera"] = typeof(ApplyCameraSpec), ["test"] = typeof(ApplyTestSpec),
        ["assertion"] = typeof(ApplyAssertionSpec), ["probe"] = typeof(ApplyProbeSpec),
        ["reflection"] = typeof(ReflectionRequest)
    };
    public static IEnumerable<string> List() => Topics.Keys;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ApplyCameraSpec CameraExample() => new([0, 0, -2], [0, 0, 0]);
    public static ApplyDocument Scaffold(string key = "panel")
    {
        CheckKey(key);
        return new("1", new(key), new("ResoLoop_Test_" + key, "Root", null, null, null, key), [],
            Cameras: new Dictionary<string, ApplyCameraSpec> { ["main"] = CameraExample() });
    }

    public static ApplyNodeSpec Provider(string key, string type)
    {
        CheckKey(key);
        if (string.IsNullOrWhiteSpace(type)) throw new RLoopException("ARGUMENT_REQUIRED", "Provider component type is required.", ExitCodes.InvalidArguments);
        return new(new(key + " provider", null, null, null, null, key + "-provider"),
            [new(type, new Dictionary<string, JsonElement>(), key)]);
    }

    private static void CheckKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new RLoopException("INVALID_OPTION", "Scaffold key must contain only ASCII letters, digits, '-' or '_'.", ExitCodes.InvalidArguments);
    }

    public static AuthoringContract Describe(string topic)
    {
        if (!Topics.TryGetValue(topic, out var type))
            throw new RLoopException("SCHEMA_TOPIC_NOT_FOUND", "Unknown schema topic: " + topic, ExitCodes.NotFound,
                suggestions: ["Use schema list --json."]);
        var nullable = new NullabilityInfoContext();
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var properties = constructor.GetParameters().Select(p => new AuthoringProperty(
            JsonNamingPolicy.CamelCase.ConvertName(p.Name!), TypeLabel(p.ParameterType),
            topic == "document" && p.Name is "SchemaVersion" or "Ownership" or "Slot" ||
            !p.HasDefaultValue && nullable.Create(p).ReadState != NullabilityState.Nullable,
            p.HasDefaultValue ? p.DefaultValue : null)).ToArray();
        object example = topic switch
        {
            "authoring" => new ApplyAuthoringSpec(Path.GetFullPath("."), "content/main.tsx", "entry-export"),
            "document" => Scaffold(), "slot" => Scaffold().Slot!,
            "node" => Provider("resource", "CALLER_REFLECTED_TYPE"),
            "component" => Provider("resource", "CALLER_REFLECTED_TYPE").Components![0],
            "camera" => CameraExample(),
            "test" => new ApplyTestSpec("root exists", [new("$slot:panel", Exists: true)]),
            "assertion" => new ApplyAssertionSpec("$slot:panel", Exists: true),
            "probe" => new ApplyProbeSpec(Target: "$member:state.Value", Kind: "set-member", Safe: true, Value: JsonSerializer.SerializeToElement(true)),
            "reflection" => new ReflectionRequest([new("[FrooxEngine]FrooxEngine.UI_UnlitMaterial", ["Sidedness"], ["Sidedness"],
                new Dictionary<string, ReflectionExpectation> { ["Sidedness"] = new(Kind: "field") })]),
            _ => throw new InvalidOperationException()
        };
        var note = topic == "authoring"
            ? "Optional build context. projectRoot must be absolute; source is project-relative. ownershipSource is diagnostic (entry-export or root-key). Rebuild after moving the project. State uses projectRoot; local resources and bookmark outputs use the source directory. Invalid metadata stops with APPLY_PROJECT_CONTEXT_INVALID."
            : topic == "reflection"
            ? "Save example as the --request JSON for type query/check. Select explicit members; enums requests enum candidates only for selected fields. Optional expect maps member names to kind/valueType/targetType/enumValues (required name/value subset). Up to 64 selections and 256 members total. Version-matched disk metadata is trusted by query/check/diff/apply; --refresh or --cache off requests fresh definitions."
            : topic == "camera"
            ? "Place under cameras as an object keyed by bookmark name. position/target are finite 3-number vectors in Root space; they must differ. fieldOfView is vertical degrees (5..170); width/height are 64..8192. Use target, not rotation/lookAt."
            : "Expanded declaration DTO; required marks non-null parameters without defaults plus required document fields. Semantic validation still applies. Component fields/types require runtime Reflection. Source include/prototypes/parameters are expanded before this schema. Examples are structural, not a visible UI.";
        return new(topic, properties, example, note);
    }

    private static string TypeLabel(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return TypeLabel(inner) + "?";
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(float) || type == typeof(double)) return "number";
        if (type == typeof(int)) return "integer";
        if (type == typeof(JsonElement)) return "JSON value";
        if (type.IsArray) return TypeLabel(type.GetElementType()!) + "[]";
        if (type.IsGenericType)
        {
            var args = type.GenericTypeArguments;
            return args.Length == 2 ? "object<string, " + TypeLabel(args[1]) + ">" : TypeLabel(args[0]) + "[]";
        }
        return Topics.FirstOrDefault(x => x.Value == type).Key ?? type.Name;
    }

    public static string WriteNew(object value, string output)
    {
        var path = Path.GetFullPath(output);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, value, value.GetType(), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RLoopException("SCAFFOLD_OUTPUT_FAILED", "Choose a new writable output file: " + path,
                ExitCodes.InvalidArguments, innerException: ex);
        }
        return path;
    }

    public static IReadOnlyList<string>? ErrorHints(string? path, string unknown)
    {
        if (path?.Equals("$.schemaVersion", StringComparison.OrdinalIgnoreCase) == true)
            return ["Use a string: \"schemaVersion\": \"1\". Run schema describe document --json for a valid example."];
        if (path?.StartsWith("$.cameras", StringComparison.OrdinalIgnoreCase) == true)
            return ["cameras is an object keyed by bookmark name: \"cameras\": { \"main\": { \"position\": [0,0,-2], \"target\": [0,0,0] } }. Camera orientation uses target, not rotation/lookAt. Run schema describe camera --json."];
        if (unknown == "key" && path?.Contains(".children[", StringComparison.Ordinal) == true && !path.Contains(".slot", StringComparison.Ordinal))
            return ["Each children[] entry wraps Slot properties: { \"slot\": { \"key\": \"child\", \"name\": \"Child\" } }. Run schema describe node --json."];
        return null;
    }
}

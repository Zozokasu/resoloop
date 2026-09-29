using System.Text.Json;
using System.Text.RegularExpressions;
using RLoop.Core;

namespace RLoop.Workbench;

/// <summary>
/// Maps Workbench reflection.* RPC results onto Core models. Every reflection result is a
/// ReflectionResult&lt;T&gt;: { "value": T|null, "provenance": {...}|null, "unknownReason": string|null }.
/// A null value means the Workbench could not answer; it never proves the type does not exist.
/// Pure conversion: no RPC, no transport.
/// </summary>
internal static class WorkbenchReflectionMapper
{
    /// <summary>reflection.search result -> type names exactly as the Workbench reports them ("[Assembly]Namespace.Name"), in the reported order.</summary>
    public static IReadOnlyList<string> MapSearch(JsonElement reflectionResult, out bool truncated)
    {
        JsonElement value = Value(reflectionResult, "component type search", reason =>
            WorkbenchErrors.Unavailable(
                $"The Workbench could not report component types{Reason(reason)}."));
        truncated = WireJson.Bool(value, "truncated", "component type search result");
        var names = new List<string>();
        foreach (JsonElement element in WireJson.Array(value, "types", "component type search result").EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } name)
                throw WorkbenchErrors.Unavailable(
                    "The Workbench returned a component type search result with a non-string type name.");
            names.Add(name);
        }
        return names;
    }

    /// <summary>reflection.component result -> ComponentTypeInfo. Unknown -> COMPONENT_TYPE_NOT_FOUND (NotFound).</summary>
    public static ComponentTypeInfo MapComponentType(JsonElement reflectionResult, string requestedType)
    {
        JsonElement value = WireJson.Object(ComponentValue(reflectionResult, requestedType), "component type definition");
        JsonElement type = WireJson.ObjectProperty(value, "type", "component type definition");
        var members = WireJson.ObjectProperty(value, "members", "component type definition")
            .EnumerateObject()
            .Select(pair => MapMemberDefinition(pair.Name, pair.Value))
            .ToArray();
        var methods = WireJson.Array(value, "methods", "component type definition")
            .EnumerateArray()
            .Select(MapMethod)
            .ToArray();
        return new ComponentTypeInfo(
            WireJson.String(type, "fullTypeName", "component type definition"),
            WireJson.OptionalString(value, "categoryPath"),
            RenderNullable(WireJson.OptionalObject(type, "baseType", "component type definition")),
            WireJson.Bool(type, "isGenericType", "component type definition"),
            members,
            methods);
    }

    /// <summary>Member names declared by a reflection.component result, in the reported order (used to drive member.read calls).</summary>
    public static IReadOnlyList<string> MemberNames(JsonElement reflectionResult, string requestedType) =>
        WireJson.ObjectProperty(
                WireJson.Object(ComponentValue(reflectionResult, requestedType), "component type definition"),
                "members", "component type definition")
            .EnumerateObject()
            .Select(pair => pair.Name)
            .ToArray();

    /// <summary>reflection.type result (+ the reflection.enum result when the type is an enum) -> Core TypeInfo.</summary>
    public static Core.TypeInfo MapType(JsonElement typeResult, JsonElement? enumResult, string requestedType)
    {
        const string what = "type definition";
        JsonElement value = WireJson.Object(TypeValue(typeResult, requestedType), what);

        string fullTypeName = WireJson.String(value, "fullTypeName", what);
        bool isComponent = WireJson.Bool(value, "isComponent", what);
        bool isSyncObject = WireJson.Bool(value, "isSyncObject", what);
        bool isValueType = WireJson.Bool(value, "isValueType", what);
        bool isEnum = WireJson.Bool(value, "isEnum", what);
        bool isGeneric = WireJson.Bool(value, "isGenericType", what);
        var interfaces = WireJson.Array(value, "interfaces", what)
            .EnumerateArray()
            .Select(Render)
            .ToArray();
        var genericArguments = WireJson.Array(value, "genericArguments", what)
            .EnumerateArray()
            .Select(argument => WireJson.Object(argument, "generic argument"))
            .ToArray();

        // The Workbench does not report IsWorldElement (R5 pending). It is decidable only from
        // the flags it does report; anything else is refused rather than guessed.
        bool isWorldElement = isComponent || isSyncObject ? true
            : isEnum || isValueType ? false
            : throw WorkbenchErrors.Unsupported(
                $"Type '{fullTypeName}': isWorldElement is not reported by the Workbench (R5 pending) " +
                "and cannot be decided for a type that is neither a component, sync object, enum nor value type.");

        IReadOnlyList<string> genericParameters;
        if (!isGeneric)
        {
            genericParameters = [];
        }
        else if (genericArguments.All(argument => WireJson.Bool(argument, "isGenericParameter", "generic argument")))
        {
            genericParameters = genericArguments
                .Select(argument => WireJson.String(argument, "type", "generic parameter"))
                .ToArray();
        }
        else
        {
            throw WorkbenchErrors.Unsupported(
                $"Type '{fullTypeName}': the Workbench reports concrete generic arguments, so the generic " +
                "parameter names cannot be recovered; only open generic definitions are supported.");
        }

        IReadOnlyDictionary<string, long>? enumValues = null;
        bool? isFlags = null;
        if (isEnum)
        {
            if (enumResult is not { } enumElement)
                throw WorkbenchErrors.Unsupported(
                    $"Type '{fullTypeName}' is an enum but no reflection.enum result was provided.");
            (enumValues, isFlags) = EnumDefinition(enumElement, fullTypeName);
        }

        return new Core.TypeInfo(
            fullTypeName,
            WireJson.OptionalString(value, "assemblyName"),
            WireJson.OptionalString(value, "namespace"),
            WireJson.OptionalString(value, "name") ?? ShortName(fullTypeName),
            RenderNullable(WireJson.OptionalObject(value, "baseType", what)),
            WireJson.Bool(value, "isAbstract", what),
            WireJson.Bool(value, "isInterface", what),
            isGeneric, isEnum, isComponent, isSyncObject, isWorldElement,
            genericParameters, interfaces, enumValues, isFlags);
    }

    private static JsonElement ComponentValue(JsonElement reflectionResult, string requestedType) =>
        Value(reflectionResult, "component type definition", reason =>
            WorkbenchErrors.NotFound("COMPONENT_TYPE_NOT_FOUND",
                $"Component type '{requestedType}' was not found{Reason(reason)}",
                new Dictionary<string, object?> { ["type"] = requestedType }));

    private static JsonElement TypeValue(JsonElement reflectionResult, string requestedType) =>
        Value(reflectionResult, "type definition", reason =>
            WorkbenchErrors.NotFound("TYPE_NOT_FOUND",
                $"Runtime type '{requestedType}' was not found{Reason(reason)}",
                new Dictionary<string, object?> { ["type"] = requestedType }));

    private static JsonElement Value(JsonElement reflectionResult, string what, Func<string?, RLoopException> unknown)
    {
        JsonElement result = WireJson.Object(reflectionResult, what);
        if (result.TryGetProperty("value", out JsonElement value) && value.ValueKind != JsonValueKind.Null)
            return value;
        throw unknown(WireJson.OptionalString(result, "unknownReason"));
    }

    private static string Reason(string? unknownReason) =>
        string.IsNullOrEmpty(unknownReason) ? "." : $": {unknownReason}";

    private static (IReadOnlyDictionary<string, long> Values, bool IsFlags) EnumDefinition(
        JsonElement enumResult, string fullTypeName)
    {
        const string what = "enum definition";
        JsonElement result = WireJson.Object(enumResult, what);
        if (!result.TryGetProperty("value", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            throw WorkbenchErrors.Unavailable(
                $"The Workbench could not report the enum values of '{fullTypeName}'" +
                $"{Reason(WireJson.OptionalString(result, "unknownReason"))}");
        WireJson.Object(value, what);
        string status = WireJson.String(value, "status", what);
        if (status.Equals("NotAnEnum", StringComparison.OrdinalIgnoreCase))
            throw WorkbenchErrors.Unavailable(
                $"The Workbench reports '{fullTypeName}' as an enum type but its enum lookup returned NotAnEnum.");
        if (!status.Equals("Found", StringComparison.OrdinalIgnoreCase))
            throw WorkbenchErrors.Unavailable(
                $"The Workbench returned an unknown enum status '{status}' for '{fullTypeName}'.");
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonElement element in WireJson.Array(value, "values", what).EnumerateArray())
            values[WireJson.String(element, "name", "enum value")] = WireJson.Int64(element, "value", "enum value");
        return (values, WireJson.Bool(value, "isFlags", what));
    }

    private static MemberDefinitionInfo MapMemberDefinition(string name, JsonElement element)
    {
        JsonElement definition = WireJson.Object(element, $"member definition '{name}'");
        return new MemberDefinitionInfo(
            name,
            MemberKind(WireJson.String(definition, "kind", $"member definition '{name}'")),
            RenderNullable(WireJson.OptionalObject(definition, "wrapperType", $"member definition '{name}'")),
            RenderNullable(WireJson.OptionalObject(definition, "valueType", $"member definition '{name}'")),
            RenderNullable(WireJson.OptionalObject(definition, "targetType", $"member definition '{name}'")));
    }

    /// <summary>The Core member kind vocabulary is the wire kind lower-camelCased (enum names arrive PascalCase).</summary>
    private static string MemberKind(string wire) => wire.ToLowerInvariant() switch
    {
        "field" => "field",
        "reference" => "reference",
        "list" => "list",
        "array" => "array",
        "dictionary" => "dictionary",
        "syncobject" => "syncObject",
        "playback" => "playback",
        "empty" => "empty",
        // Unrecognised kinds keep their wire spelling, the same way the direct path falls back to
        // the CLR member class name for member kinds it does not special-case.
        _ => wire
    };

    private static SyncMethodInfo MapMethod(JsonElement element)
    {
        JsonElement method = WireJson.Object(element, "sync method");
        var parameters = WireJson.ObjectProperty(method, "parameters", "sync method")
            .EnumerateObject()
            .ToDictionary(pair => pair.Name,
                pair => pair.Value.ValueKind == JsonValueKind.Null ? (string?)null : Render(pair.Value),
                StringComparer.Ordinal);
        return new SyncMethodInfo(
            WireJson.String(method, "name", "sync method"),
            parameters,
            RenderNullable(WireJson.OptionalObject(method, "returnType", "sync method")),
            WireJson.Bool(method, "isStatic", "sync method"),
            WireJson.Bool(method, "isAsync", "sync method"));
    }

    private static string? RenderNullable(JsonElement? reference) =>
        reference is { } element ? Render(element) : null;

    /// <summary>
    /// Renders a TypeReferenceInfo wire object ({type, isGenericParameter, genericArguments}).
    /// Mirrors ModelMapper.Render in the direct path (ResoniteLinkClientAdapter): each &lt;,*&gt;
    /// placeholder is filled left to right (nested declaring types included, e.g.
    /// "Slider&lt;&gt;+Direction" + float -> "Slider&lt;float&gt;+Direction"), and arguments are
    /// appended when the placeholders do not account for every argument. The Workbench's own
    /// TypeReferenceInfo.Display only substitutes a trailing &lt;&gt; and drops arguments
    /// otherwise - deliberately not reproduced.
    /// </summary>
    private static string Render(JsonElement reference)
    {
        JsonElement element = WireJson.Object(reference, "type reference");
        string type = WireJson.String(element, "type", "type reference");
        if (!element.TryGetProperty("genericArguments", out JsonElement arguments) ||
            arguments.ValueKind != JsonValueKind.Array)
            return type;
        var rendered = arguments.EnumerateArray().Select(Render).ToArray();
        if (rendered.Length == 0) return type;
        var placeholders = Regex.Matches(type, @"<,*>");
        if (placeholders.Count > 0 && placeholders.Sum(match => match.Length - 1) == rendered.Length)
        {
            var index = 0;
            return Regex.Replace(type, @"<,*>", match =>
            {
                var count = match.Length - 1;
                var value = "<" + string.Join(',', rendered.Skip(index).Take(count)) + ">";
                index += count;
                return value;
            });
        }
        return $"{type}<{string.Join(',', rendered)}>";
    }

    /// <summary>Derives a short name from "[Assembly]Namespace.Type&lt;...&gt;" when the Workbench reports no name.</summary>
    private static string ShortName(string fullTypeName)
    {
        string text = fullTypeName;
        if (text.StartsWith('[') && text.IndexOf(']') is var bracket && bracket >= 0)
            text = text[(bracket + 1)..];
        var cut = -1;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == '.' && depth == 0) cut = i;
        }
        string name = text[(cut + 1)..];
        if (name.IndexOf('<') is var angle && angle >= 0) name = name[..angle];
        if (name.LastIndexOf('+') is var plus && plus >= 0) name = name[(plus + 1)..];
        return name.Length > 0
            ? name
            : throw WorkbenchErrors.Unsupported(
                $"The Workbench reported no short name for type '{fullTypeName}' and none could be derived.");
    }
}

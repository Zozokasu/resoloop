using System.Text;
using System.Text.Json;
using RLoop.Core;

namespace RLoop.ContractGen;

/// <summary>Development-only C# → TS generation. Never invoked by npm or the product CLI.</summary>
public static class ContractGenerator
{
    private const string Header = "// Generated file; do not edit by hand.\n// Regenerate: dotnet run --project tools/RLoop.ContractGen -- tools/resoloop-jsx/src/generated\n\n";

    public static IReadOnlyDictionary<string, string> Generate()
    {
        var shapes = ApplyShape.All;
        var types = new StringBuilder(Header);
        types.AppendLine("export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };");
        foreach (var p in shapes.SelectMany(s => s.Properties).Where(p => p.Rules.Alias is not null))
            types.AppendLine($"export type {p.Rules.Alias} = {Choices(p.Rules.Choices!)};");
        foreach (var shape in shapes)
        {
            types.AppendLine($"\nexport interface {shape.Type.Name} {{");
            foreach (var p in shape.Properties)
                types.AppendLine($"  {p.Name}{(OutputRequired(p) ? "" : "?")}: {PropertyType(p)};");
            // $scope is consumed during expansion; only the draft guard survives TSX output.
            foreach (var source in shape.SourceProperties.Where(p => p.Name == "$draftKeys"))
                types.AppendLine($"  {source.Name}?: {source.Type};");
            types.AppendLine("}");
        }
        foreach (var (type, name) in new[] { (typeof(ApplySlotSpec), "SlotScalarProps"), (typeof(ApplyComponentSpec), "ComponentScalarProps") })
        {
            types.AppendLine($"\nexport interface {name} {{");
            foreach (var p in shapes.Single(s => s.Type == type).Properties)
                types.AppendLine($"  {p.Name}{(p.Rules.JsxRequired ? "" : "?")}: {PropertyType(p)};");
            types.AppendLine("}");
        }

        var copies = new StringBuilder(Header);
        copies.AppendLine("import type { ApplySlotSpec, ApplyComponentSpec } from \"./apply-types.js\";");
        copies.AppendLine("\nexport interface CopyHelpers {\n  assertFiniteNumbers(value: unknown, path: string): void;\n  scopeValue(value: any, scope: string): any;\n}");
        foreach (var (type, label) in new[] { (typeof(ApplySlotSpec), "Slot"), (typeof(ApplyComponentSpec), "Component") })
        {
            var props = shapes.Single(s => s.Type == type).Properties;
            copies.AppendLine($"\nexport const {label.ToUpperInvariant()}_SCALAR_PROPS = [{string.Join(", ", props.Where(p => p.Rules.Copy is not (ApplyCopyPolicy.Identity or ApplyCopyPolicy.RootOnly)).Select(p => Quote(p.Name)))}] as const;");
            copies.AppendLine($"\nexport function copy{label}(props: Record<string, any>, key: string, path: string, scope: string, isRoot: boolean, helpers: CopyHelpers): {type.Name} {{");
            copies.AppendLine($"  const spec: {type.Name} = {{ {string.Join(", ", props.Where(p => p.Rules.Copy == ApplyCopyPolicy.Identity).Select(p => p.Name == "key" ? "key" : $"{p.Name}: props.{p.Name}"))} }};");
            foreach (var p in props.Where(p => p.Rules.Copy != ApplyCopyPolicy.Identity))
            {
                copies.AppendLine($"  if ({(p.Rules.Copy == ApplyCopyPolicy.RootOnly ? "isRoot && " : "")}props.{p.Name} !== undefined) {{");
                if (p.Rules.CheckFinite) copies.AppendLine($"    helpers.assertFiniteNumbers(props.{p.Name}, `${{path}}.{p.Name}`);");
                var value = p.Rules.Copy switch
                {
                    ApplyCopyPolicy.ScopeValue => $"helpers.scopeValue(props.{p.Name}, scope)",
                    ApplyCopyPolicy.SlotMigration => Migration(p.Name, "$slot:", 6),
                    ApplyCopyPolicy.ComponentMigration => Migration(p.Name, "$component:", 11),
                    _ => $"props.{p.Name}"
                };
                copies.AppendLine($"    spec.{p.Name} = {value};");
                copies.AppendLine("  }");
            }
            copies.AppendLine("  return spec;\n}");
        }
        var manifest = shapes.Select(s => new
        {
            name = s.Type.Name,
            ignored = s.IgnoredProperties,
            sourceOnly = s.SourceProperties.Select(p => new { name = p.Name, type = p.Type }),
            properties = s.Properties.Select(p => new
            {
                name = p.Name, type = PropertyType(p), nullable = p.Nullable,
                constructorRequired = p.ConstructorRequired, jsonRequired = p.Rules.JsonRequired,
                jsxRequired = p.Rules.JsxRequired, outputRequired = OutputRequired(p),
                hasDefault = p.HasDefault, @default = p.Default,
                ignoreCondition = p.IgnoreCondition?.ToString(), length = p.Rules.Length,
                choices = p.Rules.Choices, copy = p.Rules.Copy.ToString(), checkFinite = p.Rules.CheckFinite,
                suggestCandidate = p.Rules.SuggestCandidate
            })
        });
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apply-types.ts"] = Lf(types.ToString()),
            ["apply-copy.ts"] = Lf(copies.ToString()),
            ["apply-shape.json"] = Lf(JsonSerializer.Serialize(new
            {
                generated = "Generated file; do not edit by hand.",
                regenerate = "dotnet run --project tools/RLoop.ContractGen -- tools/resoloop-jsx/src/generated",
                records = manifest
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n")
        };
    }

    // TS authoring output has historically excluded explicit null in its static API.
    // CLR nullability remains independent in the manifest; runtime copies retain null.
    private static bool OutputRequired(ApplyPropertyShape p) => p.Rules.OutputRequired || p.Rules.JsonRequired;
    private static string PropertyType(ApplyPropertyShape p)
    {
        var type = TypeScript(p.Property.PropertyType);
        if (p.Rules.Length > 0)
            type = "[" + string.Join(", ", Enumerable.Repeat(TypeScript(p.Property.PropertyType.GetElementType()!), p.Rules.Length)) + "]";
        if (p.Rules.Choices is { } choices)
        {
            var union = p.Rules.Alias ?? Choices(choices);
            type = p.Property.PropertyType == typeof(string) ? union : $"({union})[]";
        }
        return type;
    }

    public static string TypeScript(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(float) || type == typeof(double) || type == typeof(int)) return "number";
        if (type == typeof(JsonElement)) return "JsonValue";
        if (type.IsArray && type.GetArrayRank() == 1) return TypeScript(type.GetElementType()!) + "[]";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return TypeScript(type.GenericTypeArguments[0]) + "[]";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) && type.GenericTypeArguments[0] == typeof(string))
            return $"Record<string, {TypeScript(type.GenericTypeArguments[1])}>";
        if (ApplyShape.All.Any(s => s.Type == type)) return type.Name;
        throw new InvalidOperationException($"Unsupported Apply CLR shape: {type.FullName}");
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static string Choices(string[] choices) => string.Join(" | ", choices.Select(Quote));
    private static string Migration(string name, string prefix, int length) =>
        $"scope && typeof props.{name} === \"string\" ? helpers.scopeValue(`{prefix}${{props.{name}}}`, scope).slice({length}) : props.{name}";
    private static string Lf(string value) => value.Replace("\r\n", "\n");
}

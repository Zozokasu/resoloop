using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public enum ApplyCopyPolicy { Copy, Identity, RootOnly, ScopeValue, SlotMigration, ComponentMigration }

/// <summary>Authoring conditions that cannot be inferred from CLR nullability or constructor defaults.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ApplyShapeAttribute : Attribute
{
    public bool JsonRequired { get; set; }
    public bool JsxRequired { get; set; }
    public bool OutputRequired { get; set; }
    public int Length { get; set; }
    public string[]? Choices { get; set; }
    public string? Alias { get; set; }
    public ApplyCopyPolicy Copy { get; set; }
    public bool CheckFinite { get; set; }
    // Preserve the legacy typo-hint vocabulary for existing non-JSX fields.
    public bool SuggestCandidate { get; set; } = true;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ApplySourcePropertyAttribute(string name, string type) : Attribute
{
    public string Name { get; } = name;
    public string Type { get; } = type;
}

public sealed record ApplyPropertyShape(string Name, PropertyInfo Property, bool Nullable,
    bool ConstructorRequired, bool HasDefault, object? Default, JsonIgnoreCondition? IgnoreCondition,
    ApplyShapeAttribute Rules);

public sealed record ApplyRecordShape(Type Type, IReadOnlyList<ApplyPropertyShape> Properties,
    IReadOnlyList<string> IgnoredProperties, IReadOnlyList<ApplySourcePropertyAttribute> SourceProperties);

/// <summary>Shared reflection descriptor; contains no code generator or SDK dependencies.</summary>
public static class ApplyShape
{
    private static readonly Lazy<IReadOnlyList<ApplyRecordShape>> Records = new(() => Read(typeof(ApplyDocument)));
    public static IReadOnlyList<ApplyRecordShape> All => Records.Value;

    public static IReadOnlyList<ApplyRecordShape> Read(Type root)
    {
        var shapes = new Dictionary<Type, ApplyRecordShape>();
        var nullability = new NullabilityInfoContext();
        void Visit(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsArray && type.GetArrayRank() == 1) { Visit(type.GetElementType()!); return; }
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ||
                type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            { Visit(type.GenericTypeArguments[^1]); return; }
            if (type == typeof(string) || type == typeof(bool) || type == typeof(float) ||
                type == typeof(double) || type == typeof(int) || type == typeof(JsonElement)) return;
            if (shapes.ContainsKey(type)) return;
            if (type.GetMethod("<Clone>$") is null)
                throw new InvalidOperationException($"Unsupported Apply CLR shape: {type.FullName}");
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = constructor.GetParameters().ToDictionary(p => p.Name!, StringComparer.OrdinalIgnoreCase);
            var properties = new List<ApplyPropertyShape>();
            var ignored = new List<string>();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.MetadataToken))
            {
                var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();
                if (ignore?.Condition == JsonIgnoreCondition.Always) { ignored.Add(property.Name); continue; }
                parameters.TryGetValue(property.Name, out var parameter);
                properties.Add(new(property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                    JsonNamingPolicy.CamelCase.ConvertName(property.Name), property,
                    nullability.Create(property).ReadState == NullabilityState.Nullable,
                    parameter is not null && !parameter.HasDefaultValue, parameter?.HasDefaultValue == true,
                    parameter?.HasDefaultValue == true ? parameter.DefaultValue : null, ignore?.Condition,
                    property.GetCustomAttribute<ApplyShapeAttribute>() ?? new()));
            }
            shapes.Add(type, new(type, properties, ignored, type.GetCustomAttributes<ApplySourcePropertyAttribute>().ToArray()));
            foreach (var property in properties) Visit(property.Property.PropertyType);
        }
        Visit(root);
        return shapes.Values.OrderBy(s => s.Type.Name, StringComparer.Ordinal).ToArray();
    }

    public static ApplyPropertyShape Property<T>(string name) =>
        All.Single(s => s.Type == typeof(T)).Properties.Single(p => p.Property.Name == name);

    public static bool Allows<T>(string property, string? value) =>
        Property<T>(property).Rules.Choices!.Contains(value, StringComparer.Ordinal);

    public static IEnumerable<string> KnownProperties => All.SelectMany(s => s.Properties)
        .Where(p => p.Rules.SuggestCandidate).Select(p => p.Name).Distinct(StringComparer.Ordinal);
}

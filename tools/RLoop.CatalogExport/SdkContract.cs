using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RLoop.CatalogExport;

internal static class SdkContract
{
    internal static void Save(string path)
    {
        var assembly = typeof(global::ResoniteLink.LinkInterface).Assembly;
        var types = assembly.GetExportedTypes().OrderBy(t => t.FullName).Select(t => new
        {
            type = t.FullName, baseType = t.BaseType?.FullName,
            attributes = t.CustomAttributes.Select(a => a.ToString()).ToArray(),
            properties = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(p => new { name = p.Name, type = p.PropertyType.ToString(), attributes = p.CustomAttributes.Select(a => a.ToString()).ToArray() }).ToArray(),
            fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(f => new { name = f.Name, type = f.FieldType.ToString(), attributes = f.CustomAttributes.Select(a => a.ToString()).ToArray() }).ToArray(),
            methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).Select(m => m.ToString()).ToArray()
        }).ToArray();
        var options = global::ResoniteLink.LinkInterface.SerializationOptions;
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            assembly = assembly.FullName, location = assembly.Location,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            serializer = new { options.IncludeFields, options.DefaultIgnoreCondition, options.NumberHandling,
                propertyNamingPolicy = options.PropertyNamingPolicy?.GetType().FullName,
                converters = options.Converters.Select(c => c.GetType().FullName).ToArray() }, types
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

public sealed record ApplyAuthoringSpec([property: ApplyShape(JsonRequired = true)] string ProjectRoot,
    [property: ApplyShape(JsonRequired = true)] string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnershipSource = null);

internal sealed record ApplyProjectContext(string ProjectRoot, string SourceDirectory, ApplyAuthoringSpec? Authoring, string? SourcePath)
{
    internal static ApplyProjectContext Resolve(ApplyDocument document)
    {
        if (document.Authoring is { } authoring)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(authoring.ProjectRoot) || !Path.IsPathFullyQualified(authoring.ProjectRoot) ||
                    string.IsNullOrWhiteSpace(authoring.Source) || Path.IsPathRooted(authoring.Source) ||
                    authoring.OwnershipSource is { } diagnostic && string.IsNullOrWhiteSpace(diagnostic))
                    throw Invalid();
                var root = Path.GetFullPath(authoring.ProjectRoot);
                var source = Path.GetFullPath(authoring.Source, root);
                if (Path.GetFileName(source).Length == 0 || source.Equals(root, StringComparison.OrdinalIgnoreCase))
                    throw Invalid();
                return new(root, Path.GetDirectoryName(source)!, authoring, document.SourcePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw Invalid(ex);
            }
        }
        var directory = document.SourcePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(document.SourcePath)!;
        var config = ConfigResolver.FindProjectConfigPath(directory);
        return new(config is null ? directory : Path.GetDirectoryName(config)!, directory, null, document.SourcePath);
    }

    // Validate before DTO deserialization so malformed metadata always has its own error code.
    internal static void ValidateJson(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        foreach (var property in parsed.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("authoring", StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object) throw Invalid();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in property.Value.EnumerateObject())
            {
                if (!names.Add(member.Name) ||
                    !(member.Name.Equals("projectRoot", StringComparison.OrdinalIgnoreCase) ||
                      member.Name.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                      member.Name.Equals("ownershipSource", StringComparison.OrdinalIgnoreCase)) ||
                    member.Value.ValueKind != JsonValueKind.String)
                    throw Invalid();
            }
            if (!names.Contains("projectRoot") || !names.Contains("source")) throw Invalid();
        }
    }

    private static RLoopException Invalid(Exception? inner = null) => new(
        "APPLY_PROJECT_CONTEXT_INVALID",
        "authoring requires an absolute projectRoot, a project-relative source file, and optional non-empty ownershipSource diagnostic. Rebuild after moving the project.",
        ExitCodes.ValidationFailed, innerException: inner);
}

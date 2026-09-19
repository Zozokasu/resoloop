using System.Text.Json;

namespace RLoop.Core;

internal static class SlotPaths
{
    internal static string[] LegacySegments(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts[0].Equals("Root", StringComparison.OrdinalIgnoreCase)
            ? ["Root", .. parts.Skip(1)] : ["Root", .. parts];
    }

    internal static string Selector(string path, IReadOnlyList<string>? segments) =>
        segments is null ? path : "path:" + JsonSerializer.Serialize(segments);

    internal static string[] ParseSelector(string selector)
    {
        if (!selector.StartsWith("path:", StringComparison.Ordinal)) return LegacySegments(selector);
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(selector[5..]);
            if (names is not { Length: > 0 and <= 65 } || names[0] != "Root" || names.Any(string.IsNullOrEmpty))
                throw new JsonException("Use an array beginning with Root and containing exact nonempty Slot names (maximum depth 64).");
            return names;
        }
        catch (JsonException ex)
        {
            throw new RLoopException("SLOT_PATH_INVALID", "Invalid exact Slot path. Example: path:[\"Root\",\"A/B\",\" Label \"].",
                ExitCodes.InvalidArguments, innerException: ex);
        }
    }
}

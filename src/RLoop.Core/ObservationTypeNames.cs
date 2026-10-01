namespace RLoop.Core;

// Observation filters keep their existing substring semantics. Only an initial
// assembly qualifier is removed; no short-name resolution or generic inference.
internal static class ObservationTypeNames
{
    internal static string WithoutAssembly(string value)
    {
        if (value.StartsWith('[') && value.IndexOf(']') is var end && end > 1)
            return value[(end + 1)..];
        return value;
    }

    internal static bool Contains(string type, string filter) =>
        WithoutAssembly(type).Contains(WithoutAssembly(filter), StringComparison.OrdinalIgnoreCase);
}

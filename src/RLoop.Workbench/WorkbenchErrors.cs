using RLoop.Core;

namespace RLoop.Workbench;

/// <summary>
/// Observation limits of the Workbench RPC (world.observe) and the errors the read path raises.
/// Requests beyond the limits are rejected, never truncated.
/// </summary>
internal static class WorkbenchLimits
{
    public const int MaxObserveDepth = 32;
    public const int MaxObserveSlots = 8192;
    public const string LimitExceededCode = "WORKBENCH_OBSERVE_LIMIT_EXCEEDED";

    /// <summary>Throws WORKBENCH_OBSERVE_LIMIT_EXCEEDED when the requested depth is beyond the Workbench limit (or unbounded, i.e. negative).</summary>
    public static void RequireDepth(int depth, string operation)
    {
        if (depth >= 0 && depth <= MaxObserveDepth) return;
        throw LimitExceeded(
            $"{operation} requested depth {depth}, but the workbench backend observes at most {MaxObserveDepth} levels " +
            "and never truncates silently (a negative depth means unbounded).");
    }

    public static RLoopException LimitExceeded(string message) =>
        new(LimitExceededCode, message, ExitCodes.OperationFailed,
            new Dictionary<string, object?> { ["maxDepth"] = MaxObserveDepth, ["maxSlots"] = MaxObserveSlots },
            [$"Use a depth of {MaxObserveDepth} or less, or narrow the scope with --under, or use --backend link."]);
}

internal static class WorkbenchErrors
{
    public static RLoopException Unavailable(string message, Exception? innerException = null) =>
        new("WORKBENCH_UNAVAILABLE", message, ExitCodes.ConnectionFailed, innerException: innerException);

    public static RLoopException Unsupported(string message) =>
        new("BACKEND_UNSUPPORTED", message, ExitCodes.OperationFailed);

    public static RLoopException NotFound(string code, string message, IReadOnlyDictionary<string, object?>? context = null) =>
        new(code, message, ExitCodes.NotFound, context);
}

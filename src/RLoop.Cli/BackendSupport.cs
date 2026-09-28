using RLoop.Core;

namespace RLoop.Cli;

/// <summary>
/// Per-command support level on the workbench backend, for top-level commands that
/// can reach a Resonite-side connection. <see cref="RequireSupported"/> rejects a
/// listed command before any connection attempt when <c>--backend workbench</c>
/// is in effect.
/// Levels are forward-looking notes for follow-up milestones: "planned-w2" for
/// read/inspection commands, "planned-w4" for writing commands, "link-only" for
/// commands with no workbench support decided yet, "unsupported" for commands
/// never planned. Commands that never open a connection (help, version, init,
/// skills, discover, schema, manifest, uix recipe) are deliberately absent:
/// the backend choice does not apply to them.
/// </summary>
public static class BackendSupport
{
    public const string LinkOnly = "link-only";
    public const string PlannedW2 = "planned-w2";
    public const string PlannedW4 = "planned-w4";
    public const string Unsupported = "unsupported";

    public static readonly IReadOnlyDictionary<string, string> WorkbenchSupport =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = PlannedW2,
            ["ping"] = PlannedW2,
            ["hierarchy"] = PlannedW2,
            ["find"] = PlannedW2,
            ["observe"] = PlannedW2,
            ["inspect"] = PlannedW2,
            ["type"] = PlannedW2,
            ["validate"] = PlannedW2,
            ["diff"] = PlannedW2,
            ["plan"] = PlannedW2,
            ["uix"] = PlannedW2,
            ["item"] = PlannedW2,
            ["tool"] = PlannedW2,
            ["doctor"] = PlannedW2,
            ["capture"] = PlannedW2,
            ["slot"] = PlannedW4,
            ["component"] = PlannedW4,
            ["apply"] = PlannedW4,
            ["test"] = PlannedW4,
            ["flux"] = PlannedW4,
            ["scene"] = LinkOnly,
            ["blender"] = LinkOnly,
            ["logs"] = LinkOnly,
        };

    /// <summary>
    /// Throws BACKEND_UNSUPPORTED when the workbench backend is selected and
    /// <paramref name="commandName"/> is a connecting command not supported there
    /// yet. Unlisted commands never connect, so the backend is irrelevant to them.
    /// </summary>
    public static void RequireSupported(string commandName, string? backend)
    {
        if (!string.Equals(backend, "workbench", StringComparison.OrdinalIgnoreCase)) return;
        if (WorkbenchSupport.ContainsKey(commandName))
            throw new RLoopException("BACKEND_UNSUPPORTED",
                $"'{commandName}' is not supported on the workbench backend yet.",
                ExitCodes.OperationFailed,
                suggestions: ["Use --backend link, or wait for a future ResoLoop release."]);
    }
}

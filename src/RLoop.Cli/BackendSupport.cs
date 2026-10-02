using RLoop.Core;

namespace RLoop.Cli;

/// <summary>
/// Per-command support level on the workbench backend, for top-level commands that
/// can reach a Resonite-side connection. <see cref="RequireSupported"/> rejects a
/// listed command before any connection attempt when <c>--backend workbench</c>
/// is in effect and its level is not <see cref="Supported"/>.
/// Levels: "supported" commands answer through the workbench read path;
/// "planned-w2" read/inspection commands are deferred because they would silently
/// fall back on values the workbench cannot observe; "planned-w4" writing commands
/// wait for the write-path gate; "link-only" commands have no workbench support
/// decided yet; "unsupported" commands are never planned. Commands that never open
/// a connection (help, version, init, skills, discover, schema, manifest,
/// uix recipe) are deliberately absent: the backend choice does not apply to them.
/// </summary>
public static class BackendSupport
{
    public const string Supported = "supported";
    public const string LinkOnly = "link-only";
    public const string PlannedW2 = "planned-w2";
    public const string PlannedW4 = "planned-w4";
    public const string Unsupported = "unsupported";

    public static readonly IReadOnlyDictionary<string, string> WorkbenchSupport =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = Supported,
            ["ping"] = Supported,
            ["hierarchy"] = Supported,
            // Local observation over the direct-link client; the frozen workbench backend gets no implementation.
            // "snapshot diff" reads only stored files and never connects, so it is deliberately unlisted.
            ["hierarchy profile"] = Unsupported,
            ["hierarchy query"] = Unsupported,
            ["snapshot create"] = Unsupported,
            ["catalog capture"] = LinkOnly,
            ["find"] = Supported,
            ["observe"] = Supported,
            ["inspect"] = Supported,
            ["type"] = Supported,
            ["validate"] = Supported,
            ["uix"] = PlannedW2,
            ["item"] = PlannedW2,
            ["tool"] = PlannedW2,
            ["capture"] = PlannedW2,
            ["diff"] = PlannedW4,
            ["plan"] = PlannedW4,
            ["slot"] = PlannedW4,
            ["component"] = PlannedW4,
            ["apply"] = PlannedW4,
            ["test"] = PlannedW4,
            ["flux"] = PlannedW4,
            ["doctor"] = PlannedW2,
            ["scene"] = LinkOnly,
            ["blender"] = LinkOnly,
            ["logs"] = LinkOnly,
        };

    /// <summary>
    /// Throws BACKEND_UNSUPPORTED when the workbench backend is selected and
    /// <paramref name="commandName"/> is a connecting command not supported there
    /// yet. Unlisted commands never connect, so the backend is irrelevant to them.
    /// </summary>
    public static void RequireSupported(string commandName, string? backend, string? subcommand = null)
    {
        if (!string.Equals(backend, "workbench", StringComparison.OrdinalIgnoreCase)) return;
        var key = subcommand is null ? commandName : $"{commandName} {subcommand}";
        if (subcommand is not null && !WorkbenchSupport.ContainsKey(key)) key = commandName;
        commandName = key;
        if (WorkbenchSupport.TryGetValue(commandName, out string? level) && level != Supported)
            throw new RLoopException("BACKEND_UNSUPPORTED",
                $"'{commandName}' is not supported on the workbench backend yet.",
                ExitCodes.OperationFailed,
                new Dictionary<string, object?>
                {
                    ["level"] = level,
                    ["reason"] = DeferralReason(commandName, level),
                },
                suggestions: ["Use --backend link, or wait for a future ResoLoop release."]);
    }

    /// <summary>Why a listed command stays refused on the workbench backend; reported as error context.</summary>
    private static string DeferralReason(string commandName, string level) =>
        commandName.ToLowerInvariant() switch
        {
            "doctor" => "doctor diagnoses the ResoniteLink URL resolution path, which the workbench backend never uses; 'wb status' reports the Workbench transport instead.",
            "uix" => "uix audit treats a missing member value as valid evidence, so workbench reads could silently report wrong results.",
            "item" => "item audit requires a fixed depth-64 subtree read and slot-level Members the workbench backend cannot observe.",
            "tool" => "tool audit silently falls back to identity transforms when position/rotation/scale are missing, so workbench reads could report wrong geometry.",
            "capture" => "live capture creates Components and Slots; the workbench backend is read-only.",
            "diff" or "plan" => "diff/plan share the write path's world read; the workbench write-path gate must land first.",
            _ => level switch
            {
                PlannedW4 => "writing commands are planned for a later milestone on the workbench backend.",
                LinkOnly => "this command only applies to the ResoniteLink backend.",
                _ => "no workbench support is planned for this command.",
            },
        };
}

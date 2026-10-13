using System.Text.Json;
using RLoop.Core;

namespace RLoop.Cli;

internal static class CreationCommands
{
    internal static int? Handle(ParsedArguments args, OutputWriter output)
    {
        if (args.Positionals.Count == 0) return null;
        if (args.Positionals[0].Equals("creation", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Positionals.Count < 2) throw ModeMismatch("creation requires a subcommand.");
            var subcommand = args.Positionals[1].ToLowerInvariant();
            if (subcommand is "migrate" or "inspect" || args.Has("bind-root"))
                throw ModeMismatch("creation migrate, inspect, and --bind-root are not available in this version.");
            if (subcommand != "recover") return null;
            ValidateRecoveryArguments(args);
            if (args.Option("discard-pending") is { } operationId)
            {
                var discarded = CreationRecoveryService.DiscardPending(args.RequireOption("state"), operationId, args.Has("yes"));
                output.Success(discarded, writer =>
                {
                    writer.WriteLine($"discarded creation pending {discarded.OperationId}");
                    writer.WriteLine($"state: {discarded.StateFile}");
                    if (discarded.Diagnostics.FirstOrDefault() is { } diagnostic) writer.WriteLine(JsonSerializer.Serialize(diagnostic));
                });
                return ExitCodes.Success;
            }
            return null;
        }
        var command = args.Positionals[0].ToLowerInvariant();
        if (command is not ("validate" or "plan" or "diff" or "apply"))
        {
            if (args.Has("mode")) throw ModeMismatch("--mode is supported only by validate, plan, diff, and apply.");
            return null;
        }

        var mode = args.Option("mode");
        if (mode is not null && mode != "regenerate")
            throw ModeMismatch($"Unknown mode '{mode}'.");
        var path = args.Positionals.Count > 1 ? args.Positionals[1] : null;
        var isEnvelope = path is not null && IsCreationEnvelope(path);

        if (command == "validate")
        {
            if (mode is not null) throw ModeMismatch("--mode is not supported by validate.");
            if (!isEnvelope) return null;
            if (args.Has("catalog")) throw ModeMismatch("--catalog is not supported by creation validation in this version.");
            var creation = CreationDocument.Load(path!);
            var validation = creation.ValidateAsync().GetAwaiter().GetResult();
            output.Success(validation);
            return validation.Valid ? ExitCodes.Success : ExitCodes.ValidationFailed;
        }

        if (isEnvelope && mode is null)
            throw ModeMismatch("Creation envelopes require --mode regenerate for plan/diff/apply.");

        if (mode == "regenerate")
        {
            if (command is "apply" or "plan" or "diff")
                path = args.Positional(1, "Creation envelope");
            if (new[] { "adopt", "prune", "deletes-only", "changes-only" }.Any(args.Has))
                throw ModeMismatch("--mode regenerate cannot be combined with --adopt, --prune, --deletes-only, or --changes-only.");
            if (command == "apply")
            {
                if (!isEnvelope && path is not null && IsValidJson(path))
                    throw ModeMismatch("Only creation envelopes support --mode regenerate in this version.");
                if (IsCreationBundle(path!))
                    throw ModeMismatch("Creation build bundles are not available in this version.");
                if (args.Has("creates-only") || args.Has("summary") || args.Has("discard-pending") ||
                    new[] { "adopt", "prune", "deletes-only", "changes-only" }.Any(args.Has))
                    throw ModeMismatch("Creation apply does not support legacy output filters or pending discard.");
                return null;
            }
            if (command is "plan" or "diff")
            {
                if (args.Has("creates-only") || args.Has("summary") || args.Has("discard-pending"))
                    throw ModeMismatch("Creation plan/diff does not support legacy output filters or --discard-pending.");
                if (!isEnvelope && path is not null && IsValidJson(path))
                    throw ModeMismatch("Only creation envelopes support --mode regenerate in this version.");
                if (IsCreationBundle(path!))
                    throw ModeMismatch("Creation build bundles are not available in this version.");
                return null;
            }
            throw ModeMismatch("Creation plan/diff is not available in this version.");
        }

        return null;
    }

    internal static bool IsRecovery(ParsedArguments args) => args.Positionals.Count >= 2 &&
        args.Positionals[0].Equals("creation", StringComparison.OrdinalIgnoreCase) &&
        args.Positionals[1].Equals("recover", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRecoveryArguments(ParsedArguments args)
    {
        if (!IsRecovery(args)) return;
        if (args.Positionals.Count != 2) throw ModeMismatch("creation recover accepts no positional arguments after the subcommand.");
        if (args.Has("bind-root") || args.Has("action") && args.Has("discard-pending"))
            throw ModeMismatch("creation recover cannot combine --action, --discard-pending, or --bind-root modes.");
        if (args.Options("action").Count > 1 || args.Options("discard-pending").Count > 1)
            throw ModeMismatch("creation recover accepts exactly one recovery mode.");
        if (args.Option("discard-pending") is not null)
        {
            args.RequireOption("state"); args.RequireOption("discard-pending");
            if (!args.Has("yes")) throw new RLoopException("CONFIRMATION_REQUIRED", "creation recover --discard-pending requires --yes.", ExitCodes.ValidationFailed);
            return;
        }
        if (args.Option("action") is not ("rollback" or "finish"))
            throw ModeMismatch("creation recover requires --action rollback or --action finish.");
        args.RequireOption("state");
        if (!args.Has("yes")) throw new RLoopException("CONFIRMATION_REQUIRED", "creation recover requires --yes after inspecting the retained operation.", ExitCodes.ValidationFailed);
        if (args.Option("url") != "auto" || args.Option("session") is not { } session || !session.StartsWith("S-", StringComparison.Ordinal))
            throw new RLoopException("CREATION_SESSION_UNPROVEN", "Creation recovery requires --url auto and an explicit --session S-ID.", ExitCodes.ValidationFailed);
        CreationRecoveryService.ValidateRecoveryInput(args.RequireOption("state"), args.RequireOption("action"), args.Has("yes"));
    }

    internal static bool IsRegeneratePlan(ParsedArguments args) => args.Positionals.Count > 0 &&
        (args.Positionals[0].ToLowerInvariant() is "plan" or "diff") && args.Option("mode") == "regenerate";

    internal static bool IsRegenerateApply(ParsedArguments args) => args.Positionals.Count > 0 &&
        args.Positionals[0].Equals("apply", StringComparison.OrdinalIgnoreCase) && args.Option("mode") == "regenerate";

    private static bool IsCreationBundle(string path)
    {
        try
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(path));
            return parsed.RootElement.ValueKind == JsonValueKind.Object &&
                parsed.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
                kind.GetString() == "resoloop-creation-build-bundle";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static bool IsValidJson(string path)
    {
        try
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static bool IsCreationEnvelope(string path)
    {
        if (!File.Exists(path)) return false;
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        try
        {
            using var parsed = JsonDocument.Parse(text);
            return CreationDocument.IsEnvelope(parsed.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static RLoopException ModeMismatch(string message) => new(
        "CREATION_MODE_MISMATCH", message, ExitCodes.InvalidArguments);
}

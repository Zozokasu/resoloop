using System.Text.Json;
using RLoop.Core;

namespace RLoop.Cli;

internal static class CreationCommands
{
    internal static int? Handle(ParsedArguments args, OutputWriter output)
    {
        if (args.Positionals.Count == 0) return null;
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
            if (new[] { "adopt", "prune", "deletes-only", "changes-only" }.Any(args.Has))
                throw ModeMismatch("--mode regenerate cannot be combined with --adopt, --prune, --deletes-only, or --changes-only.");
            if (command == "apply")
                throw ModeMismatch("Creation apply is not available in this version.");
            if (command is "plan" or "diff")
            {
                if (args.Has("creates-only") || args.Has("summary") || args.Has("discard-pending"))
                    throw ModeMismatch("Creation plan/diff does not support legacy output filters or --discard-pending.");
                if (!isEnvelope)
                    throw ModeMismatch("Only creation envelopes support --mode regenerate in this version.");
                if (IsCreationBundle(path!))
                    throw ModeMismatch("Creation build bundles are not available in this version.");
                return null;
            }
            throw ModeMismatch("Creation plan/diff is not available in this version.");
        }

        return null;
    }

    internal static bool IsRegeneratePlan(ParsedArguments args) => args.Positionals.Count > 0 &&
        (args.Positionals[0].ToLowerInvariant() is "plan" or "diff") && args.Option("mode") == "regenerate";

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
            return text.Contains("\"creationId\"", StringComparison.Ordinal) ||
                   text.Contains("\"definition\"", StringComparison.Ordinal);
        }
    }

    private static RLoopException ModeMismatch(string message) => new(
        "CREATION_MODE_MISMATCH", message, ExitCodes.InvalidArguments);
}

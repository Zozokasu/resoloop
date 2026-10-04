using System.Text;
using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Cli;

internal static class CatalogCommands
{
    internal static void Types(ParsedArguments args, OutputWriter output)
    {
        if (args.Positionals.Count != 3)
            throw new RLoopException("UNEXPECTED_ARGUMENT", "catalog types accepts one catalog file.", ExitCodes.InvalidArguments);
        var input = args.Positional(2, "Catalog file");
        var configPath = ConfigResolver.FindProjectConfigPath(Environment.CurrentDirectory);
        var project = configPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(configPath)!;
        var destination = Path.GetFullPath(args.Has("output") ? args.RequireOption("output") : Path.Combine(project, ".resoloop", "catalog-types.d.ts"));
        DistinctPaths(input, destination);
        ApplyCatalog catalog;
        try { catalog = ApplyCatalog.Load(input); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", $"Cannot read catalog '{input}': {ex.Message}", ExitCodes.ValidationFailed, innerException: ex); }
        var generated = CatalogTypesGenerator.Generate(catalog);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, generated.Text, new UTF8Encoding(false));
        output.Success(new { output = destination, generated.ContentHash, generated.GeneratorVersion, generated.Fallbacks }, writer =>
        {
            writer.WriteLine($"catalog types: {destination}");
            writer.WriteLine($"catalog hash: {generated.ContentHash}; generator: {generated.GeneratorVersion}");
            foreach (var fallback in generated.Fallbacks)
                writer.WriteLine($"  fallback {fallback.Type}{(fallback.Member is null ? "" : "." + fallback.Member)}: {fallback.Reason}");
        });
    }

    internal static async Task CaptureAsync(ParsedArguments args, OutputWriter output, RLoopConfig config,
        CancellationToken token, Func<Uri, IReadOnlyList<string>, CancellationToken, Task<CatalogSnapshot>>? capture = null)
    {
        if (args.Positionals.Count != 2)
            throw new RLoopException("UNEXPECTED_ARGUMENT", "catalog capture takes no positional arguments.", ExitCodes.InvalidArguments);
        var typesPath = args.RequireOption("types");
        var destination = Path.GetFullPath(args.RequireOption("output"));
        var snapshotPath = args.Has("snapshot") ? Path.GetFullPath(args.RequireOption("snapshot")) : null;
        DistinctPaths(typesPath, destination);
        if (snapshotPath is not null) { DistinctPaths(typesPath, snapshotPath); DistinctPaths(destination, snapshotPath); }
        var names = LoadNames(typesPath);
        var uri = await ResoniteClientFactory.ResolveConnectionUrlAsync(args, config, token);
        var snapshot = await (capture ?? CatalogCapture.ReadAsync)(uri, names, token);
        // Keep bounded partial acquisition evidence reviewable even when export refuses its trust status.
        if (snapshotPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            CatalogMapper.SaveSnapshot(snapshot, snapshotPath);
        }
        var catalog = CatalogMapper.Export(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        catalog.Save(destination);
        output.Success(new { output = destination, snapshot = snapshotPath, catalog.ContentHash, catalog.FormatVersion,
            catalog.Identity, requestedTypes = names.Count, acquisitionFailures = catalog.Content.AcquisitionFailures }, writer =>
        {
            writer.WriteLine($"catalog: {destination}");
            if (snapshotPath is not null) writer.WriteLine($"snapshot: {snapshotPath}");
            writer.WriteLine($"catalog hash: {catalog.ContentHash}; requested types: {names.Count}");
            foreach (var failure in catalog.Content.AcquisitionFailures ?? [])
                writer.WriteLine($"  unverified {failure.Type} ({failure.Request}): {failure.Reason}");
        });
    }

    internal static IReadOnlyList<string> LoadNames(string path)
    {
        try
        {
            var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
            if (names is null || names.Length is 0 or > 512 || names.Any(name => string.IsNullOrWhiteSpace(name) || name != name.Trim()))
                throw new JsonException("Expected a JSON array of 1..512 explicit, nonempty full Component names without surrounding whitespace.");
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        { throw new RLoopException("INVALID_ARGUMENT", $"Cannot read --types '{path}': {ex.Message}", ExitCodes.InvalidArguments, innerException: ex); }
    }

    private static void DistinctPaths(string input, string output)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (comparer.Equals(Path.GetFullPath(input), Path.GetFullPath(output)))
            throw new RLoopException("INVALID_OPTION", "Catalog input and output paths must be different.", ExitCodes.InvalidArguments);
    }
}

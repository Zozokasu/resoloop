using System.Text;
using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Cli;

internal static class CatalogCommands
{
    private const string NonDtsWarning = "Runtime catalog table was not written: --output does not end in .d.ts; componentFields requires a companion table.";

    internal static void Types(ParsedArguments args, OutputWriter output)
    {
        if (args.Positionals.Count != 3)
            throw new RLoopException("UNEXPECTED_ARGUMENT", "catalog types accepts one catalog file.", ExitCodes.InvalidArguments);

        var input = Path.GetFullPath(args.Positional(2, "Catalog file"));
        var configPath = ConfigResolver.FindProjectConfigPath(Environment.CurrentDirectory);
        var project = configPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(configPath)!;
        var destination = Path.GetFullPath(args.Has("output") ? args.RequireOption("output") : Path.Combine(project, ".resoloop", "catalog-types.d.ts"));
        var publishesRuntime = HasDeclarationSuffix(destination);
        var runtimeDestination = publishesRuntime
            ? args.Has("output") ? RuntimePath(destination) : Path.Combine(Path.GetDirectoryName(destination)!, "catalog-fields.ts")
            : null;

        DistinctPaths(input, destination);
        if (runtimeDestination is not null)
        {
            DistinctPaths(input, runtimeDestination);
            DistinctPaths(destination, runtimeDestination);
        }

        ApplyCatalog catalog;
        try { catalog = ApplyCatalog.Load(input); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", $"Cannot read catalog '{input}': {ex.Message}", ExitCodes.ValidationFailed, innerException: ex); }

        var runtimeLeaf = runtimeDestination is null ? "catalog-fields.ts" : Path.GetFileName(runtimeDestination);
        var generated = CatalogTypesGenerator.Generate(catalog, runtimeLeaf);
        var declarationText = publishesRuntime ? generated.Text : WithoutRuntimeHeader(generated);
        Publish(destination, declarationText, runtimeDestination, publishesRuntime ? generated.RuntimeText : null);

        var result = new Dictionary<string, object?>
        {
            ["output"] = destination,
            ["contentHash"] = generated.ContentHash,
            ["generatorVersion"] = generated.GeneratorVersion,
            ["fallbacks"] = generated.Fallbacks,
            ["runtimeOutput"] = runtimeDestination,
        };
        if (!publishesRuntime) Console.Error.WriteLine(NonDtsWarning);
        output.Success(result, writer =>
        {
            writer.WriteLine($"catalog types: {destination}");
            if (runtimeDestination is not null) writer.WriteLine($"runtime catalog: {runtimeDestination}");
            writer.WriteLine($"catalog hash: {generated.ContentHash}; generator: {generated.GeneratorVersion}");
            foreach (var fallback in generated.Fallbacks)
                writer.WriteLine($"  fallback {fallback.Type}{(fallback.Member is null ? "" : "." + fallback.Member)}: {fallback.Reason}");
        });
    }

    private static bool HasDeclarationSuffix(string path) => path.EndsWith(".d.ts",
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string RuntimePath(string declarationPath) => declarationPath[..^5] + ".fields.ts";

    private static string WithoutRuntimeHeader(CatalogTypesDeclaration generated)
    {
        var newline = generated.Text.IndexOf('\n');
        if (newline < 0) throw new InvalidOperationException("Generated catalog declaration has no header line ending.");
        var header = "// resoloop-catalog-types: " + JsonSerializer.Serialize(new
        { formatVersion = "1", contentHash = generated.ContentHash, generatorVersion = generated.GeneratorVersion }, ApplyCatalog.Json) + "\n";
        return header + generated.Text[(newline + 1)..];
    }

    private static void Publish(string declarationPath, string declarationText, string? runtimePath, string? runtimeText)
    {
        var temporaryPaths = new List<string>();
        try
        {
            var declarationTemp = WriteTemporary(declarationPath, declarationText, temporaryPaths);
            string? runtimeTemp = runtimePath is null || runtimeText is null ? null : WriteTemporary(runtimePath, runtimeText, temporaryPaths);
            if (runtimeTemp is not null) File.Move(runtimeTemp, runtimePath!, true);
            File.Move(declarationTemp, declarationPath, true);
        }
        finally
        {
            foreach (var path in temporaryPaths)
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string WriteTemporary(string destination, string text, List<string> temporaryPaths)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        FileStream stream;
        string path;
        for (var attempt = 0; ; attempt++)
        {
            try { stream = CreateTemporary(directory, out path); break; }
            catch (IOException) when (attempt < 10) { }
        }
        temporaryPaths.Add(path);
        using (stream)
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes);
        }
        return path;
    }

    private static FileStream CreateTemporary(string directory, out string path)
    {
        path = Path.Combine(directory, $".resoloop-catalog-{Guid.NewGuid():N}.tmp");
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    private static void DistinctPaths(string input, string output)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (comparer.Equals(Path.GetFullPath(input), Path.GetFullPath(output)))
            throw new RLoopException("INVALID_OPTION", "Catalog input and output paths must be different.", ExitCodes.InvalidArguments);
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
        // Keep the raw bounded acquisition evidence alongside the mapped partial catalog.
        if (snapshotPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            CatalogMapper.SaveSnapshot(snapshot, snapshotPath);
        }
        var catalog = CatalogMapper.Export(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        catalog.Save(destination);
        object acquisitionBudgetUsed = snapshot.AcquisitionBudgetUsed is { } used ? used : "unknown";
        var result = new { output = destination, snapshot = snapshotPath, catalog.ContentHash, catalog.FormatVersion,
            catalog.Identity, requestedTypes = names.Count, acquisitionBudgetUsed,
            acquisitionBudgetLimit = CatalogCapture.AcquisitionBudgetLimit, outputTypes = catalog.Content.Types.Count,
            acquisitionFailures = catalog.Content.AcquisitionFailures };
        var failures = snapshot.Content.AcquisitionFailures ?? [];
        var missing = names.Distinct(StringComparer.Ordinal).Where(name =>
            !snapshot.Content.Components.Any(component => component.Type?.FullTypeName == name) ||
            !snapshot.Content.Types.Any(type => type.FullTypeName == (snapshot.Content.Aliases.GetValueOrDefault(name) ?? name)) ||
            failures.Any(failure => failure.Type == name && failure.Request is "component" or "type")).ToArray();
        // Dependency failures alone remain diagnostic evidence. Requested component/type
        // failures and any budget/deadline truncation make capture incomplete.
        var truncated = snapshot.AcquisitionTruncated || snapshot.AcquisitionBudgetUsed is null && failures.Any(failure =>
            failure.Reason.StartsWith("type-limit:", StringComparison.Ordinal) ||
            failure.Reason.StartsWith("time-limit:", StringComparison.Ordinal));
        void WriteResult(TextWriter writer)
        {
            writer.WriteLine($"catalog: {destination}");
            if (snapshotPath is not null) writer.WriteLine($"snapshot: {snapshotPath}");
            writer.WriteLine($"catalog hash: {catalog.ContentHash}; requested types: {names.Count}");
            writer.WriteLine($"acquisition budget used: {snapshot.AcquisitionBudgetUsed?.ToString() ?? "unknown"}/{CatalogCapture.AcquisitionBudgetLimit} unique attempted names; output types: {catalog.Content.Types.Count}");
            foreach (var failure in catalog.Content.AcquisitionFailures ?? [])
                writer.WriteLine($"  unverified {failure.Type} ({failure.Request}): {failure.Reason}");
        }
        if (missing.Length > 0 || truncated)
        {
            using var details = new StringWriter();
            WriteResult(details);
            throw new RLoopException("CATALOG_CAPTURE_INCOMPLETE",
                $"Partial catalog saved; {missing.Length} requested names lack acquired component/type metadata; acquisition truncated: {truncated.ToString().ToLowerInvariant()}." +
                Environment.NewLine + details.ToString().TrimEnd(),
                ExitCodes.ValidationFailed, new Dictionary<string, object?> { ["result"] = result });
        }
        output.Success(result, WriteResult);
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
}

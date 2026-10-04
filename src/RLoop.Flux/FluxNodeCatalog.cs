using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RLoop.Flux;

public sealed record FluxNodePort(string Name, string Type);

public sealed record FluxNodeDefinition(string Name, string FullName, string Category, bool IsGeneric, string? ThisType,
    IReadOnlyList<FluxNodePort> Inputs, IReadOnlyList<FluxNodePort> Outputs, IReadOnlyList<FluxNodePort> Globals);

public sealed record FluxNodeCatalogDocument(string FluxSdkVersion, string LibraryIdentity, DateTimeOffset GeneratedAt,
    IReadOnlyList<FluxNodeDefinition> Nodes, string CachePath);

public static class FluxNodeCatalog
{
    public static async Task<FluxNodeCatalogDocument> GetOrCreateAsync(FluxProcessTool tool, string? libraryPath,
        string cacheDirectory, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var status = await tool.GetStatusAsync(cancellationToken);
        var version = status.Version ?? "unknown";
        var libraryIdentity = string.IsNullOrWhiteSpace(libraryPath) ? "auto" : Path.GetFullPath(libraryPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version + "\n" + libraryIdentity)))[..16];
        var cachePath = Path.Combine(Path.GetFullPath(cacheDirectory), $"nodes-{Safe(version)}-{key}.json");
        if (!refresh && File.Exists(cachePath))
        {
            var cached = JsonSerializer.Deserialize<FluxNodeCatalogDocument>(await File.ReadAllTextAsync(cachePath, cancellationToken), JsonOptions);
            if (cached is not null) return cached with { CachePath = cachePath };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporary = cachePath + ".metadata.tmp";
        try
        {
            var result = await tool.GenerateNodeDocsAsync(temporary, libraryPath, cancellationToken);
            if (!result.Success || !File.Exists(temporary))
                throw new RLoop.Core.RLoopException("FLUX_NODE_CATALOG_FAILED",
                    "Flux-SDK could not generate Froox node metadata.", RLoop.Core.ExitCodes.ExternalToolFailed,
                    new Dictionary<string, object?> { ["stdout"] = result.StandardOutput, ["stderr"] = result.StandardError });
            var nodes = Parse(await File.ReadAllTextAsync(temporary, cancellationToken));
            var document = new FluxNodeCatalogDocument(version, libraryIdentity, DateTimeOffset.UtcNow, nodes, cachePath);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(document, JsonOptions) + "\n", cancellationToken);
            return document;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static IReadOnlyList<FluxNodeDefinition> Parse(string metadata)
    {
        var nodes = new List<FluxNodeDefinition>();
        Builder? current = null;
        string? section = null;
        foreach (var raw in metadata.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Finish(); continue; }
            if (!char.IsWhiteSpace(line[0]) && line.EndsWith(':'))
            {
                Finish();
                current = new Builder(line[..^1]);
                section = null;
                continue;
            }
            if (current is null) continue;
            if (line.StartsWith("  FullName: ", StringComparison.Ordinal)) current.FullName = Unquote(line[12..]);
            else if (line.StartsWith("  ThisType: ", StringComparison.Ordinal)) current.ThisType = Unquote(line[12..]);
            else if (line is "  Inputs:" or "  Outputs:" or "  Globals:") section = line.Trim()[..^1];
            else if (line.StartsWith("  Inputs: {}", StringComparison.Ordinal) || line.StartsWith("  Outputs: {}", StringComparison.Ordinal) ||
                     line.StartsWith("  Globals: {}", StringComparison.Ordinal)) section = null;
            else if (section is not null && line.StartsWith("    ", StringComparison.Ordinal))
            {
                var separator = line.IndexOf(':', 4);
                if (separator > 4)
                    current.Ports(section).Add(new FluxNodePort(Unquote(line[4..separator].Trim()), Unquote(line[(separator + 1)..].Trim())));
            }
        }
        Finish();
        return nodes;

        void Finish()
        {
            if (current is null || string.IsNullOrWhiteSpace(current.FullName)) return;
            var namespaceParts = (current.FullName.Contains(".Nodes.", StringComparison.Ordinal)
                ? current.FullName.Split(".Nodes.", 2, StringSplitOptions.None)[1]
                : current.FullName).Split('.');
            var category = namespaceParts.Length > 1 ? string.Join('/', namespaceParts[..^1]) : string.Empty;
            nodes.Add(new FluxNodeDefinition(current.Name, current.FullName, category,
                current.Name.Contains('<') || current.FullName.Contains('`'), current.ThisType,
                current.Inputs, current.Outputs, current.Globals));
            current = null;
        }
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
        ? value[1..^1].Replace("\\\"", "\"") : value;
    private static string Safe(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private sealed class Builder(string name)
    {
        public string Name { get; } = name;
        public string FullName { get; set; } = string.Empty;
        public string? ThisType { get; set; }
        public List<FluxNodePort> Inputs { get; } = [];
        public List<FluxNodePort> Outputs { get; } = [];
        public List<FluxNodePort> Globals { get; } = [];
        public List<FluxNodePort> Ports(string section) => section switch
        {
            "Inputs" => Inputs,
            "Outputs" => Outputs,
            _ => Globals
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public sealed record FluxModulePort(string Name, string Direction, string Type, string? Modifier);

/// <summary>A header line that starts like a port declaration (<c>in</c> / <c>out</c>) but could not be read as one.</summary>
/// <param name="Line">1-based line number in the source.</param>
/// <param name="Reason"><c>noTypeSeparator</c>, <c>emptyName</c>, <c>invalidName</c> or <c>emptyType</c>.</param>
public sealed record FluxModulePortParseIssue(int Line, string Text, string Reason);

/// <param name="Unparsed">Port-like lines that were not read as ports. Lines that are not port-like are not listed.</param>
public sealed record FluxModuleSignatureReading(IReadOnlyList<FluxModulePort> Ports, IReadOnlyList<FluxModulePortParseIssue> Unparsed);

/// <summary>
/// Reads the port declarations of a ProtoGraph module header, line by line, up to <c>where</c>. This is a pre-check of
/// the source text only (the deployer's compile reports the ports actually built). A line is ignored only when it
/// cannot declare a port: blank, a <c>//</c> comment, the <c>module</c> declaration, or a line that does not start with
/// the <c>in</c> / <c>out</c> keyword. A line that starts with <c>in</c> / <c>out</c> but cannot be read as
/// <c>NAME: TYPE [modifier]</c> is never dropped silently: <see cref="Parse"/> fails with FLUX_PORT_PARSE_FAILED,
/// because a lost port declaration leads to a wrong placement.
/// </summary>
public static partial class FluxModuleSignature
{
    /// <summary>The declared ports. Throws FLUX_PORT_PARSE_FAILED when a port-like line could not be read.</summary>
    /// <param name="sourcePath">Reported with the error; not read.</param>
    public static IReadOnlyList<FluxModulePort> Parse(string source, string? sourcePath = null)
    {
        var reading = Read(source);
        if (reading.Unparsed.Count == 0) return reading.Ports;
        var first = reading.Unparsed[0];
        throw new RLoop.Core.RLoopException("FLUX_PORT_PARSE_FAILED",
            $"{reading.Unparsed.Count} port declaration line(s) of {(sourcePath is null ? "the module source" : $"'{sourcePath}'")} could not be read (line {first.Line}: '{first.Text}', {first.Reason}). Nothing was built or deployed.",
            RLoop.Core.ExitCodes.ValidationFailed,
            new Dictionary<string, object?> { ["source"] = sourcePath, ["lines"] = reading.Unparsed },
            ["Write each port as 'in NAME: TYPE [element|global|mutable]' or 'out NAME: TYPE' on its own line before 'where'."]);
    }

    /// <summary>Reads the header without throwing: the ports and every port-like line that could not be read.</summary>
    public static FluxModuleSignatureReading Read(string source)
    {
        var ports = new List<FluxModulePort>();
        var unparsed = new List<FluxModulePortParseIssue>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Split("//", 2, StringSplitOptions.None)[0].Trim();
            // The header ends at 'where', also when it shares a line with the module declaration.
            var where = WhereKeyword().Match(line);
            var header = where.Success ? line[..where.Index].Trim() : line;
            if (header.Length > 0 && PortKeyword().Match(header) is { Success: true } keyword)
            {
                var direction = keyword.Groups["keyword"].Value == "in" ? "source" : "drive";
                var (port, reason) = ReadPort(direction, header[keyword.Length..].Trim());
                if (port is not null) ports.Add(port);
                else unparsed.Add(new FluxModulePortParseIssue(index + 1, header, reason!));
            }
            if (where.Success) break;
        }
        return new FluxModuleSignatureReading(ports, unparsed);
    }

    private static (FluxModulePort? Port, string? Reason) ReadPort(string direction, string body)
    {
        var separator = body.IndexOf(':');
        if (separator < 0) return (null, "noTypeSeparator");
        var name = body[..separator].Trim();
        if (name.Length == 0) return (null, "emptyName");
        if (!PortName().IsMatch(name)) return (null, "invalidName");
        var typeParts = body[(separator + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var modifier = typeParts.LastOrDefault() is "global" or "element" or "mutable" ? typeParts[^1] : null;
        var type = modifier is null ? string.Join(' ', typeParts) : string.Join(' ', typeParts[..^1]);
        return type.Length == 0 ? (null, "emptyType") : (new FluxModulePort(name, direction, type, modifier), null);
    }

    // 'in' / 'out' as a whole word at the start of the header text ('input: …' is not a port line).
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<keyword>in|out)(?=\s|:|$)")]
    private static partial System.Text.RegularExpressions.Regex PortKeyword();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w$])where(?![\w$])")]
    private static partial System.Text.RegularExpressions.Regex WhereKeyword();

    // A port name is one token: no whitespace and none of the characters that separate declarations or types.
    [System.Text.RegularExpressions.GeneratedRegex(@"^[^\s,;:(){}<>\[\]=""']+$")]
    private static partial System.Text.RegularExpressions.Regex PortName();
}

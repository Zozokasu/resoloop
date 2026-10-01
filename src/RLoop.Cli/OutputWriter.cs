using System.Text.Json;
using System.Text.Json.Serialization;
using RLoop.Core;

namespace RLoop.Cli;

public sealed class OutputWriter(bool json, bool brief = false) : IDisposable
{
    private static readonly JsonSerializerOptions Compact = CreateOptions(false);
    private static readonly JsonSerializerOptions Indented = CreateOptions(true);

    private StreamWriter? report;
    private ApplyDiagnosticReport diagnosticReport = new("1", []);
    private string diagnosticPhase = "validate";
    private string? diagnosticBuildId;
    private string? diagnosticsPath;
    private bool showApplyDiagnostics;
    private readonly List<string> protectedDiagnosticsPaths = [];
    public string? ReportPath { get; private set; }

    public void ConfigureDiagnostics(string? path, string phase, string? buildId)
    {
        diagnosticsPath = path;
        showApplyDiagnostics = true;
        diagnosticPhase = phase;
        diagnosticBuildId = buildId;
    }

    public void SetDiagnostics(IReadOnlyList<ApplyDiagnostic> diagnostics) => diagnosticReport = new("1",
        diagnostics.Select(d => d with { Phase = diagnosticPhase }).ToArray());

    internal void ProtectDiagnosticsPath(string? path)
    {
        if (path is not null) protectedDiagnosticsPaths.Add(path);
    }

    internal void ProtectDiagnosticsState(ApplyDocument document, string? path)
    {
        if (diagnosticsPath is null) return;
        try { ProtectDiagnosticsPath(document.ResolveStatePath(path)); }
        // State resolution is auxiliary here; the command retains its own judgement.
        catch (Exception ex) when (ex is RLoopException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { }
    }

    // Auxiliary output never changes validation/mutation judgement or exit status.
    // Protect selected paths even when missing; CreateNew also guards existing files.
    public void WriteDiagnostics()
    {
        if (diagnosticsPath is null) return;
        try
        {
            var fullPath = Path.GetFullPath(diagnosticsPath);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (protectedDiagnosticsPaths.Any(path => comparer.Equals(fullPath, Path.GetFullPath(path))))
                throw new IOException("The diagnostics destination is a selected input, catalog or state path.");
            using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, diagnosticReport, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Console.Error.WriteLine($"diagnostics write failed ({diagnosticsPath}): {ex.Message}; command judgement and exit code are unchanged."); }
    }

    // Reserve before connecting or mutating. Never overwrite a manifest/checkpoint by accident.
    public void OpenReport(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            report = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            ReportPath = fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RLoopException("REPORT_CREATE_FAILED", "--report requires a writable, new file: " + fullPath,
                ExitCodes.InvalidArguments, suggestions: ["Choose a new report filename; existing files are never overwritten."], innerException: ex);
        }
    }

    public void Success(object? data, Action<TextWriter>? human = null, object? briefData = null)
    {
        if (data is ApplyValidationResult validation) SetDiagnostics(ApplyDiagnostics.ForResult(validation).Diagnostics);
        WriteReport(new { ok = true, data });
        var visible = brief ? briefData ?? BriefOutput.Project(data) : data;
        if (json || brief || ReportPath is not null)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, data = visible, report = ReportPath }, Compact));
            return;
        }
        if (human is not null) human(Console.Out);
        else Console.Out.WriteLine(JsonSerializer.Serialize(data, Indented));
    }

    public void Error(RLoopException error)
    {
        diagnosticReport = ApplyDiagnostics.ForException(error, diagnosticPhase, diagnosticBuildId);
        var payload = new
        {
            ok = false,
            error = new { code = error.Code, message = error.Message, context = error.Context, suggestions = error.Suggestions }
        };
        WriteReport(payload);
        if (json || brief || ReportPath is not null)
        {
            var context = brief && error.Context is not null
                ? error.Context.ToDictionary(pair => pair.Key, pair => BriefOutput.Project(pair.Value))
                : error.Context;
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                ok = false,
                error = new { code = error.Code, message = error.Message, context, suggestions = error.Suggestions },
                report = ReportPath
            }, Compact));
        }
        else
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
            foreach (var diagnostic in showApplyDiagnostics ? diagnosticReport.Diagnostics : [])
            {
                var source = diagnostic.Source;
                var location = source.Status == "known" ? $"{source.File}:{source.Range!.Start.Line}:{source.Range.Start.Column}" : "unknown";
                Console.Error.WriteLine($"  {diagnostic.Code}: TSX {location}; IR {diagnostic.JsonPath ?? "unknown"}: {diagnostic.Message}");
            }
            foreach (var suggestion in error.Suggestions) Console.Error.WriteLine($"  next: {suggestion}");
        }
    }

    private void WriteReport(object payload)
    {
        if (report is null) return;
        report.WriteLine(JsonSerializer.Serialize(payload, Compact));
        report.Flush();
    }

    public void Dispose() => report?.Dispose();

    public void Progress(ApplyProgress progress, bool ndjson)
    {
        if (ndjson)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { eventType = "progress", data = progress }, Compact));
            return;
        }
        var amount = progress.Total > 0 ? $"[{progress.Current}/{progress.Total}] " : string.Empty;
        var target = string.IsNullOrWhiteSpace(progress.Path) ? string.Empty : $" {progress.Path}";
        Console.Error.WriteLine($"{amount}{progress.Stage}: {progress.Message}{target}");
    }

    public static void Hierarchy(TextWriter writer, SlotInfo root)
    {
        void Walk(SlotInfo slot, string prefix, bool last)
        {
            writer.Write(prefix);
            if (prefix.Length > 0) writer.Write(last ? "└─ " : "├─ ");
            writer.WriteLine($"{slot.Name} [{slot.Id}]" + (slot.Components.Count > 0 ? $" ({slot.Components.Count} components)" : string.Empty));
            for (var i = 0; i < slot.Children.Count; i++)
                Walk(slot.Children[i], prefix + (prefix.Length == 0 ? string.Empty : last ? "   " : "│  "), i == slot.Children.Count - 1);
        }
        Walk(root, string.Empty, true);
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = indented
    };
}

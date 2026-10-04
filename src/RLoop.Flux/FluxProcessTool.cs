using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RLoop.Core;

namespace RLoop.Flux;

// Runs the Flux-SDK CLI for build, check, watch and node docs. It never deploys: placing a module goes through
// FluxDeployGuard (RLoop.Core) and the IFluxDeployer.
public sealed class FluxProcessTool(string executable) : IFluxTool
{
    public Task<FluxResult> BuildAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) =>
        RunBuild(request, [], cancellationToken);

    public Task<FluxResult> CheckAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) =>
        RunBuild(request, ["--stop-after-resolve"], cancellationToken);

    // The SDK's own watch rebuilds many times in one process, so its output is not judged as one build.
    public Task<FluxResult> WatchAsync(FluxBuildRequest request, CancellationToken cancellationToken = default) =>
        StartBuild(request, ["--watch"], cancellationToken);

    public Task<FluxResult> GenerateNodeDocsAsync(string output, string? libraryPath,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "froox-docs", "--out", Path.GetFullPath(output) };
        if (!string.IsNullOrWhiteSpace(libraryPath)) { args.Add("--library-path"); args.Add(Path.GetFullPath(libraryPath)); }
        return Run(args, Path.GetDirectoryName(Path.GetFullPath(output))!, cancellationToken);
    }

    public async Task<FluxToolStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Run(["--version"], Environment.CurrentDirectory, cancellationToken);
            var text = (result.StandardOutput + " " + result.StandardError).Trim();
            var version = Regex.Match(text, @"\d+\.\d+\.\d+(?:[-+][^\s]+)?").Value;
            return new FluxToolStatus(result.ExitCode == 0 || !string.IsNullOrEmpty(version), executable, string.IsNullOrEmpty(version) ? text : version);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new FluxToolStatus(false, executable, null);
        }
    }

    private async Task<FluxResult> RunBuild(FluxBuildRequest request, IReadOnlyList<string> extra, CancellationToken cancellationToken)
    {
        // P9: a build or check is judged by its error diagnostics, not by the exit code alone.
        var result = await StartBuild(request, extra, cancellationToken);
        var verdict = FluxBuildJudgement.Judge(result.ExitCode, result.StandardOutput, result.StandardError, result.Diagnostics);
        return result with { Success = verdict.Success, Verdict = verdict };
    }

    private Task<FluxResult> StartBuild(FluxBuildRequest request, IReadOnlyList<string> extra, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(request.Source);
        if (!File.Exists(source))
            throw new RLoopException("FLUX_SOURCE_NOT_FOUND", $"ProtoGraph source '{source}' does not exist.", ExitCodes.NotFound);
        var args = new List<string> { "build" };
        if (!string.IsNullOrWhiteSpace(request.ProjectDirectory)) { args.Add("--project-directory"); args.Add(Path.GetFullPath(request.ProjectDirectory)); }
        if (!string.IsNullOrWhiteSpace(request.Output)) { args.Add("--out"); args.Add(Path.GetFullPath(request.Output)); }
        if (!string.IsNullOrWhiteSpace(request.LibraryPath)) { args.Add("--library-path"); args.Add(Path.GetFullPath(request.LibraryPath)); }
        if (request.CompactErrors) args.Add("--compact-error-messages");
        args.AddRange(extra);
        args.Add(source);
        return Run(args, request.ProjectDirectory ?? Path.GetDirectoryName(source)!, cancellationToken);
    }

    private async Task<FluxResult> Run(IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{executable}'.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
            var outIndex = args.Select((value, index) => (value, index)).FirstOrDefault(x => x.value == "--out").index;
            var outputPath = args.Contains("--out") && outIndex + 1 < args.Count ? args[outIndex + 1] : null;
            var standardOutput = await stdout;
            var standardError = await stderr;
            var diagnostics = FluxDiagnostics.Parse(standardOutput, standardError);
            return new FluxResult(process.ExitCode == 0, process.ExitCode, standardOutput, standardError, outputPath,
                diagnostics, diagnostics.Where(diagnostic => diagnostic.IsPrimary).ToArray());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RLoopException("FLUX_SDK_NOT_FOUND", $"Could not start Flux-SDK executable '{executable}'.", ExitCodes.ExternalToolFailed,
                new Dictionary<string, object?> { ["executable"] = executable },
                ["Install with: dotnet tool install --global Papaltine.FluxSDK --version 1.9.0", "Or configure RESOLOOP_FLUX_EXECUTABLE."], ex);
        }
    }
}

/// <summary>
/// Judges a Flux-SDK build or check from its output (ROADMAP-9, P9). Pure: it never starts a process. Flux-SDK 1.9.0
/// writes every diagnostic to stdout, exits 1 for a build with warnings only, and ends a build with
/// <c>Finished compiling … but encountered N errors and M warnings.</c> (a check that stops early ends with
/// <c>Resolving completed with N errors.</c>). Only error diagnostics fail; a non-zero exit is accepted only when
/// parsed warnings and a summary of 0 errors explain it. Anything that cannot be explained fails.
/// </summary>
public static partial class FluxBuildJudgement
{
    public const string Clean = "clean";
    public const string WarningsOnly = "warningsOnly";
    public const string ErrorDiagnostics = "errorDiagnostics";
    public const string ReportedErrors = "reportedErrors";
    public const string UnexplainedExitCode = "unexplainedExitCode";
    public const string ToolReportedFailure = "toolReportedFailure";

    /// <param name="diagnostics">Diagnostics already parsed from the same output; parsed here when null.</param>
    public static FluxBuildVerdict Judge(int exitCode, string standardOutput, string standardError,
        IReadOnlyList<FluxDiagnostic>? diagnostics = null)
    {
        diagnostics ??= FluxDiagnostics.Parse(standardOutput, standardError);
        var errors = diagnostics.Count(diagnostic => Is(diagnostic, "error"));
        var warnings = diagnostics.Count(diagnostic => Is(diagnostic, "warning"));
        var summaries = ReportedErrorCounts(standardOutput + "\n" + standardError);
        int? reported = summaries.Count == 0 ? null : summaries.Sum();
        FluxBuildVerdict Verdict(bool success, string basis, string detail) =>
            new(success, basis, exitCode, errors, warnings, reported, detail);

        if (errors > 0)
            return Verdict(false, ErrorDiagnostics,
                $"Flux-SDK reported {errors} error diagnostic(s) (exit code {exitCode}).");
        if (reported > 0)
            return Verdict(false, ReportedErrors,
                $"Flux-SDK's summary reports {reported} error(s), but no error diagnostic could be parsed from its output (exit code {exitCode}).");
        if (exitCode == 0)
            return Verdict(true, Clean, warnings == 0
                ? "Flux-SDK finished without diagnostics."
                : $"Flux-SDK finished with {warnings} warning(s) and no error.");
        if (warnings > 0 && reported == 0)
            return Verdict(true, WarningsOnly,
                $"Flux-SDK exited with code {exitCode} for {warnings} warning(s) and no error; the warnings are reported and the build continues.");
        return Verdict(false, UnexplainedExitCode, warnings > 0
            ? $"Flux-SDK exited with code {exitCode} with {warnings} warning(s), no error diagnostic and no summary stating 0 errors; the result cannot be judged a success."
            : $"Flux-SDK exited with code {exitCode} without any diagnostic; the result cannot be judged a success.");
    }

    /// <summary>
    /// The verdict of a result: the tool's own verdict when it has one, otherwise judged from the exit code and the
    /// output. A tool that reports failure for an exit code of 0 is not turned into a success: nothing in its output
    /// explains the failure, so it cannot be judged.
    /// </summary>
    public static FluxBuildVerdict Of(FluxResult result)
    {
        if (result.Verdict is { } verdict) return verdict;
        var judged = Judge(result.ExitCode, result.StandardOutput, result.StandardError, result.Diagnostics);
        return !result.Success && judged is { Success: true, Basis: Clean }
            ? judged with { Success = false, Basis = ToolReportedFailure, Detail = $"The Flux tool reported failure with exit code {result.ExitCode} and its output does not explain it." }
            : judged;
    }

    /// <summary>
    /// Text that explains a failed build: the error diagnostics (from stdout, where the SDK writes them), or the
    /// whole stdout and stderr when no diagnostic explains the failure.
    /// </summary>
    public static string DescribeFailure(FluxBuildVerdict verdict, FluxResult result)
    {
        var errors = (result.Diagnostics ?? FluxDiagnostics.Parse(result.StandardOutput, result.StandardError))
            .Where(diagnostic => Is(diagnostic, "error")).ToArray();
        var builder = new StringBuilder(verdict.Detail);
        if (errors.Length > 0)
            foreach (var error in errors)
                builder.Append('\n').Append($"{error.File}({error.StartLine},{error.StartColumn},{error.EndLine},{error.EndColumn}): error: {error.Message}");
        else
        {
            builder.Append("\nstdout:\n").Append(string.IsNullOrWhiteSpace(result.StandardOutput) ? "(empty)" : result.StandardOutput.TrimEnd());
            builder.Append("\nstderr:\n").Append(string.IsNullOrWhiteSpace(result.StandardError) ? "(empty)" : result.StandardError.TrimEnd());
        }
        return builder.ToString();
    }

    private static bool Is(FluxDiagnostic diagnostic, string severity) =>
        string.Equals(diagnostic.Severity, severity, StringComparison.OrdinalIgnoreCase);

    private static List<int> ReportedErrorCounts(string text) =>
        SummaryLine().Matches(text).Select(match => int.Parse(match.Groups["errors"].Value, CultureInfo.InvariantCulture)).ToList();

    // "Finished compiling Main but encountered 3 errors and 0 warnings." / "Resolving completed with 5 errors."
    [GeneratedRegex(@"(?:encountered|Resolving completed with)\s+(?<errors>\d+)\s+errors?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLine();
}

public static partial class FluxDiagnostics
{
    private static readonly Regex Location = new(
        @"^(?<file>.+?)\((?<sl>\d+),(?<sc>\d+),(?<el>\d+),(?<ec>\d+)\):\s*(?<severity>error|warning|hint):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<FluxDiagnostic> Parse(string standardOutput, string standardError)
    {
        var diagnostics = new List<FluxDiagnostic>();
        ParseChannel(standardOutput, "stdout", diagnostics);
        ParseChannel(standardError, "stderr", diagnostics);
        return diagnostics;
    }

    private static void ParseChannel(string text, string channel, List<FluxDiagnostic> diagnostics)
    {
        FluxDiagnostic? current = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Location.Match(line);
            if (match.Success)
            {
                if (current is not null) diagnostics.Add(current);
                var severity = match.Groups["severity"].Value.ToLowerInvariant();
                var message = match.Groups["message"].Value;
                var category = Classify(message, severity);
                current = new FluxDiagnostic(match.Groups["file"].Value,
                    int.Parse(match.Groups["sl"].Value), int.Parse(match.Groups["sc"].Value),
                    int.Parse(match.Groups["el"].Value), int.Parse(match.Groups["ec"].Value),
                    severity, message, channel, category, IsPrimary(severity, message, category));
            }
            else if (current is not null && string.IsNullOrWhiteSpace(line))
            {
                diagnostics.Add(current);
                current = null;
            }
            else if (current is not null)
                current = current with { Message = current.Message + "\n" + line };
        }
        if (current is not null) diagnostics.Add(current);
    }

    private static string Classify(string message, string severity)
    {
        if (message.Contains("bottom value", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Skipping node generation", StringComparison.OrdinalIgnoreCase)) return "cascade";
        if (message.Contains("parse", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Expecting:", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Error in ", StringComparison.OrdinalIgnoreCase)) return "parse";
        if (message.Contains("type", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("constraint", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unif", StringComparison.OrdinalIgnoreCase)) return "type";
        return severity == "hint" ? "hint" : "compiler";
    }

    private static bool IsPrimary(string severity, string message, string category) =>
        severity == "error" && category != "cascade" &&
        !message.StartsWith("Error in ", StringComparison.OrdinalIgnoreCase);
}

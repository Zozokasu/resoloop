using System.Text.RegularExpressions;

namespace RLoop.Tests;

public sealed class OrdinaryApplySurfaceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ResoLoop.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void ErrorCodesMatchPinnedList()
    {
        var root = RepoRoot();
        var files = new[] { "WorldService.cs", "WorldService.ApplyPending.cs", "WorldService.ApplySafety.cs", "WorldService.ApplyDeletion.cs", "ApplyPendingDiscard.cs", "ApplyWorkflow.cs" };
        var actual = files.SelectMany(file => Regex.Matches(File.ReadAllText(Path.Combine(root, "src", "RLoop.Core", file)), "\"((?:APPLY|STABLE)_[A-Z0-9_]+)\"")
            .Select(match => match.Groups[1].Value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        AssertList("error-codes.txt", actual);
    }

    [Fact]
    public void OptionsMatchPinnedList()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "src", "RLoop.Cli", "Program.cs"));
        var begins = Enumerable.Range(0, lines.Length).Where(i => lines[i].Contains("ordinary-apply-surface:begin", StringComparison.Ordinal)).ToArray();
        var ends = Enumerable.Range(0, lines.Length).Where(i => lines[i].Contains("ordinary-apply-surface:end", StringComparison.Ordinal)).ToArray();
        Assert.True(begins.Length == 3 && ends.Length == 3, $"Expected 3 begin/end marker pairs; found {begins.Length} begin and {ends.Length} end markers.");
        var actual = begins.SelectMany((begin, index) => Enumerable.Range(begin + 1, ends[index] - begin - 1)
            .SelectMany(i => Regex.Matches(lines[i], "\"([a-z][a-z0-9]*(?:-[a-z0-9]+)*)\"").Select(match => match.Groups[1].Value)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        AssertList("options.txt", actual);
    }

    private static void AssertList(string name, IEnumerable<string> actual)
    {
        var root = RepoRoot();
        var expected = File.ReadAllText(Path.Combine(root, "tests", "RLoop.Tests", "fixtures", "ordinary-apply-surface", name))
            .Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var observed = actual.ToArray();
        var onlyActual = observed.Except(expected, StringComparer.Ordinal).ToArray();
        var onlyExpected = expected.Except(observed, StringComparer.Ordinal).ToArray();
        Assert.True(onlyActual.Length == 0 && onlyExpected.Length == 0,
            $"{name} differs. Implementation only: [{string.Join(", ", onlyActual)}]. Pinned file only: [{string.Join(", ", onlyExpected)}].");
    }
}

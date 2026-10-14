using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class BackendSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-tests-" + Guid.NewGuid().ToString("N"));

    public BackendSupportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void BackendDefaultsToLink()
    {
        var result = ConfigResolver.Resolve(_root, new Dictionary<string, string?>(), _ => null, Path.Combine(_root, "none"));
        Assert.Equal("link", result.Config.Backend);
        Assert.False(result.Sources.ContainsKey("backend"));
    }

    [Theory]
    [InlineData("LINK", "link")]
    public void BackendIsCaseInsensitiveAndNormalized(string value, string expected)
    {
        var result = ConfigResolver.Resolve(_root,
            new Dictionary<string, string?> { ["backend"] = value }, _ => null, Path.Combine(_root, "none"));
        Assert.Equal(expected, result.Config.Backend);
    }

    [Fact]
    public void BackendRejectsInvalidValues()
    {
        var ex = Assert.Throws<RLoopException>(() => ConfigResolver.Resolve(_root,
            new Dictionary<string, string?> { ["backend"] = "resonitelink" }, _ => null, Path.Combine(_root, "none")));
        Assert.Equal("INVALID_BACKEND", ex.Code);
        Assert.Equal(ExitCodes.InvalidArguments, ex.ExitCode);
        Assert.Equal("Backend must be 'link', got 'resonitelink'.", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

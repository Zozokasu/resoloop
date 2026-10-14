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

    [Fact]
    public void BackendUsesCliEnvironmentProjectUserPrecedence()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"backend\":\"link\"}");
        var project = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(project, "{\"backend\":\"workbench\"}");
        var cli = new Dictionary<string, string?> { ["backend"] = "workbench" };
        string? Env(string key) => key == "RESOLOOP_BACKEND" ? "link" : null;

        var cliException = Assert.Throws<RLoopException>(() => ConfigResolver.Resolve(_root, cli, Env, user));
        Assert.Equal("BACKEND_REMOVED", cliException.Code);

        cli.Clear();
        var envResult = ConfigResolver.Resolve(_root, cli, Env, user);
        Assert.Equal("link", envResult.Config.Backend);
        Assert.Equal("environment:RESOLOOP_BACKEND", envResult.Sources["backend"]);

        var projectException = Assert.Throws<RLoopException>(() => ConfigResolver.Resolve(_root, cli, _ => null, user));
        Assert.Equal("BACKEND_REMOVED", projectException.Code);

        File.Delete(project);
        var userResult = ConfigResolver.Resolve(_root, cli, _ => null, user);
        Assert.Equal("link", userResult.Config.Backend);
        Assert.Equal(Path.Combine(user, ".resoloop", "config.json"), userResult.Sources["backend"]);
    }

    [Fact]
    public void RejectRemovedBackendUsesUserWorkbenchWhenNoHigherPriorityValueExists()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"backend\":\"workbench\"}");

        var exception = Assert.Throws<RLoopException>(() => ConfigResolver.RejectRemovedBackend(_root, null, _ => null, user));
        Assert.Equal("BACKEND_REMOVED", exception.Code);
    }

    [Fact]
    public void RejectRemovedBackendProjectLinkOverridesUserWorkbench()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"backend\":\"workbench\"}");
        File.WriteAllText(Path.Combine(_root, ".resoloop.json"), "{\"backend\":\"link\"}");

        ConfigResolver.RejectRemovedBackend(_root, null, _ => null, user);
    }

    [Fact]
    public void RejectRemovedBackendDefersMalformedProjectConfigToResolve()
    {
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(user, ".resoloop"));
        File.WriteAllText(Path.Combine(user, ".resoloop", "config.json"), "{\"backend\":\"workbench\"}");
        File.WriteAllText(Path.Combine(_root, ".resoloop.json"), "{");

        ConfigResolver.RejectRemovedBackend(_root, null, _ => null, user);
    }

    [Fact]
    public void RejectRemovedBackendCliLinkOverridesEnvironmentWorkbench()
    {
        ConfigResolver.RejectRemovedBackend(_root, "link", key => key == "RESOLOOP_BACKEND" ? "workbench" : null, Path.Combine(_root, "none"));
    }

    [Fact]
    public void RejectRemovedBackendAllowsNoConfiguredValue()
    {
        ConfigResolver.RejectRemovedBackend(_root, null, _ => null, Path.Combine(_root, "none"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

using System.Text.Json.Nodes;
using RLoop.Core;
using Xunit.Abstractions;

namespace RLoop.Tests;

public sealed class CompilationLimitTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "resoloop-limits-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfiguredBudgetAllowsOneOwnershipAcrossIncludedSharedResources()
    {
        Directory.CreateDirectory(_directory);
        var children = new JsonArray();
        for (var i = 0; i < 2300; i++) children.Add(JsonNode.Parse($$"""
            {"slot":{"key":"slot-{{i}}","name":"Sample {{i}}"},"components":[]}
            """));
        File.WriteAllText(Path.Combine(_directory, "parts.json"), new JsonObject { ["children"] = children }.ToJsonString());
        var path = Path.Combine(_directory, "main.json");
        var document = JsonNode.Parse("""
            {"include":["parts.json"],"schemaVersion":"1","ownership":{"key":"large-library"},"slot":{"key":"root","name":"Library"}}
            """)!;
        File.WriteAllText(path, document.ToJsonString());
        Assert.Equal("APPLY_EXPANDED_NODE_LIMIT", Assert.Throws<RLoopException>(() => ApplyDocument.Load(path)).Code);
        document["limits"] = new JsonObject { ["expandedNodes"] = 20000 };
        File.WriteAllText(path, document.ToJsonString());
        var result = ApplyDocument.Load(path);
        Assert.Equal(2300, result.Children!.Count);
        Assert.True(result.Compilation!.ExpandedNodes > 10000);
        Assert.Equal(20000, result.Compilation.ExpandedNodeLimit);
        document["limits"]!["expandedNodes"] = result.Compilation.ExpandedNodes;
        File.WriteAllText(path, document.ToJsonString());
        Assert.NotNull(ApplyDocument.Load(path));
        document["limits"]!["expandedNodes"] = result.Compilation.ExpandedNodes - 1;
        File.WriteAllText(path, document.ToJsonString());
        Assert.Equal("APPLY_EXPANDED_NODE_LIMIT", Assert.Throws<RLoopException>(() => ApplyDocument.Load(path)).Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("250001")]
    [InlineData("1.5")]
    [InlineData("\"20000\"")]
    public void RejectsInvalidNodeBudgets(string value)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "invalid.json");
        File.WriteAllText(path, "{\"limits\":{\"expandedNodes\":" + value + "}}");
        Assert.Equal("APPLY_LIMITS_INVALID", Assert.Throws<RLoopException>(() => ApplyDocument.Load(path)).Code);
    }

    [Fact]
    public void LargeExplicitBudgetReportsTimeAndAllocationWithoutChangingByteLimit()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "large.json");
        var children = string.Join(",", Enumerable.Range(0, 40000).Select(i =>
            "{\"slot\":{\"key\":\"k" + i + "\",\"name\":\"Panel " + i + "\"}}"));
        File.WriteAllText(path, "{\"limits\":{\"expandedNodes\":250000},\"schemaVersion\":\"1\",\"slot\":{\"name\":\"Large\"},\"children\":[" + children + "]}");
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = ApplyDocument.Load(path);
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        Assert.Equal(40000, result.Children!.Count);
        Assert.True(result.Compilation!.ExpandedNodes > 100000);
        Assert.True(result.Compilation.ExpandedBytes < 10 * 1024 * 1024);
        output.WriteLine($"JSON nodes={result.Compilation.ExpandedNodes}, bytes={result.Compilation.ExpandedBytes}, elapsedMs={watch.ElapsedMilliseconds}, allocatedBytes={allocated}");
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

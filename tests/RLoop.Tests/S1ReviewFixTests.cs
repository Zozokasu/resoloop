using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

/// <summary>
/// S1-R1: regressions for the Codex review of S1 (unknown is not NotFound, ambiguity is not absence,
/// enum value type failure is per member, generation change aborts a query, generation check and cache write are atomic,
/// category walk SDK exceptions). Expected values are written by hand.
/// </summary>
public sealed class S1ReviewFixTests
{
    private const string Listed = "[FrooxEngine]FrooxEngine.GradientStripTexture";
    private const string Comp = "[FrooxEngine]FrooxEngine.Widget";
    private static readonly Uri A = new("ws://127.0.0.1:1111/");
    private static readonly Uri B = new("ws://127.0.0.1:2222/");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ResoniteLinkClientAdapter Adapter(ScriptedMetadataLink link, TimeSpan? timeout = null) =>
        new(link, timeout ?? Timeout, new ReflectionCacheOptions("off"));

    private static Link.ComponentDefinition WidgetDefinition() => new()
    {
        Type = new Link.TypeDefinition { FullTypeName = Comp },
        Methods = [],
        Members = new()
        {
            ["Intensity"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "float" } },
            ["Mode"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "Test.Mode" } }
        }
    };

    // C1
    [Fact]
    public async Task ListedTypeWhoseTypeDefinitionFailsIsUnreadableNotNotFound()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Listed] };
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeTypeAsync("GradientStripTexture"));
        Assert.Equal("TYPE_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal("Type not found.", ex.Context!["errorInfo"]);
    }

    // C2
    [Fact]
    public async Task AmbiguousShortNameIsAmbiguousNotNotFound()
    {
        var link = new ScriptedMetadataLink { AllTypes = ["[FrooxEngine]FrooxEngine.Light", "[Other]Other.Light"] };
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("Light"));
        Assert.Equal("COMPONENT_TYPE_AMBIGUOUS", ex.Code);
        Assert.Equal(ExitCodes.InvalidArguments, ex.ExitCode);
        Assert.Equal(["[FrooxEngine]FrooxEngine.Light", "[Other]Other.Light"], Assert.IsAssignableFrom<IEnumerable<string>>(ex.Context!["candidates"]));

        // The plain-type path must not turn ambiguity into TYPE_NOT_FOUND either.
        var typeEx = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeTypeAsync("Light"));
        Assert.Equal("COMPONENT_TYPE_AMBIGUOUS", typeEx.Code);
    }

    [Fact]
    public async Task ReflectionQueryReportsAmbiguousNameAsUnknown()
    {
        var link = new ScriptedMetadataLink { AllTypes = ["[FrooxEngine]FrooxEngine.Light", "[Other]Other.Light"] };
        await using var client = Adapter(link);
        var report = await ReflectionQuery.RunAsync(client, new ReflectionRequest([new("Light", ["Intensity"])]));
        Assert.Equal(ReflectionStatus.Unknown, Assert.Single(report.Types).Status);
        Assert.Contains(report.Differences, d => d.Code == "TYPE_DEFINITION_UNAVAILABLE" && d.Actual!.StartsWith("COMPONENT_TYPE_AMBIGUOUS"));
    }

    // C3
    [Fact]
    public async Task EnumValueTypeFailureMarksOnlyThatMemberUnknownAndKeepsTheComponentResolved()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Comp] };
        link.Definitions[Comp] = WidgetDefinition();
        await using var client = Adapter(link);
        var request = new ReflectionRequest([new(Comp, ["Intensity", "Mode"], ["Mode"],
            new Dictionary<string, ReflectionExpectation> { ["Mode"] = new(EnumValues: new Dictionary<string, long> { ["A"] = 0 }) })]);
        var report = await ReflectionQuery.RunAsync(client, request);

        var result = Assert.Single(report.Types);
        Assert.Equal(ReflectionStatus.Unknown, result.Status);
        Assert.False(result.Verified);
        Assert.Equal(Comp, result.FullTypeName);
        Assert.Equal(["Intensity", "Mode"], result.Members.Select(m => m.Name).ToArray());
        var difference = Assert.Single(report.Differences);
        Assert.Equal("ENUM_VALUE_TYPE_UNAVAILABLE", difference.Code);
        Assert.Equal("Mode", difference.Member);
        Assert.DoesNotContain(report.Differences, d => d.Code is "COMPONENT_TYPE_NOT_FOUND" or "ENUM_VALUE_MISMATCH");
    }

    // C4
    [Fact]
    public async Task GenerationChangeAbortsTheWholeQueryInsteadOfMovingToTheNextType()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Comp, "[FrooxEngine]FrooxEngine.Other"], Connected = false };
        link.Definitions[Comp] = WidgetDefinition();
        link.Definitions["[FrooxEngine]FrooxEngine.Other"] = WidgetDefinition();
        link.DefinitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.DefinitionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var query = ReflectionQuery.RunAsync(client, new ReflectionRequest([new(Comp, ["Intensity"]), new("[FrooxEngine]FrooxEngine.Other", ["Intensity"])]));
        await link.DefinitionStarted.Task.WaitAsync(Timeout);
        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        link.DefinitionGate.SetResult();

        var ex = await Assert.ThrowsAsync<RLoopException>(() => query);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
        Assert.Equal(1, link.DefinitionCalls);
    }

    // C5: a reconnect that starts right after the generation check cannot interleave with the cache update.
    [Fact]
    public async Task ReconnectBetweenGenerationCheckAndCacheWriteWaitsForTheCommit()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Comp], Connected = false };
        link.Definitions[Comp] = WidgetDefinition();
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        Task? reconnect = null;
        var reconnectFinishedInsideCommit = true;
        var commits = 0;
        client.AfterGenerationCheckForTests = () =>
        {
            if (++commits < 2) return; // the first commit creates the disk-cache handle; the second is the definition write
            client.AfterGenerationCheckForTests = null;
            link.Connected = false;
            reconnect = Task.Run(() => client.ConnectAsync(B, Timeout));
            Thread.Sleep(300); // give an unprotected reconnect ample time to run
            reconnectFinishedInsideCommit = reconnect.IsCompleted;
        };

        Assert.Equal(Comp, (await client.DescribeComponentTypeAsync(Comp)).FullTypeName);
        await reconnect!.WaitAsync(Timeout);
        Assert.False(reconnectFinishedInsideCommit);

        // The commit landed first and the reconnect cleared it: the new connection reads live again.
        var before = link.DefinitionCalls;
        await client.DescribeComponentTypeAsync(Comp);
        Assert.Equal(before + 1, link.DefinitionCalls);
    }

    // W1
    [Fact]
    public async Task SdkExceptionDuringCategoryWalkBecomesSearchIncomplete()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level([], ["Child"]);
        link.CategoryFaults["Child"] = () => throw new InvalidOperationException("child boom");
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync("X", 10));
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal("Child", ex.Context!["category"]);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task CancellationDuringCategoryWalkStaysCancellation()
    {
        var link = new ScriptedMetadataLink();
        link.CategoryFaults[""] = () => throw new OperationCanceledException();
        await using var client = Adapter(link);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchComponentTypesAsync("X", 10));
    }

    [Fact]
    public async Task TimeoutDuringCategoryWalkStaysRequestTimeout()
    {
        var link = new ScriptedMetadataLink();
        link.CategoryFaults[""] = () => new TaskCompletionSource<LinkTypeList>().Task;
        await using var client = Adapter(link, TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync("X", 10));
        Assert.Equal("REQUEST_TIMEOUT", ex.Code);
        Assert.Equal(ExitCodes.Timeout, ex.ExitCode);
    }

    [Fact]
    public async Task ConnectionLossDuringCategoryWalkStaysNotConnected()
    {
        var link = new ScriptedMetadataLink();
        link.CategoryFaults[""] = () => { link.Connected = false; throw new IOException("socket closed"); };
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync("X", 10));
        Assert.Equal("NOT_CONNECTED", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }
}

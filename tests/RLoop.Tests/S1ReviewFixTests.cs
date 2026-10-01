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
    public async Task SessionFailureAnswerAfterReconnectAbortsTheWholeQuery()
    {
        var link = new ScriptedMetadataLink { Connected = false };
        link.SessionFailureGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.SessionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new ResoniteLinkClientAdapter(link, Timeout, new ReflectionCacheOptions("auto"));
        await client.ConnectAsync(A, Timeout);
        var query = ReflectionQuery.RunAsync(client, new ReflectionRequest([new(Comp, ["Intensity"]), new(Listed, ["Intensity"])]));
        await link.SessionStarted.Task.WaitAsync(Timeout);
        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        link.SessionFailureGate.SetResult();

        var ex = await Assert.ThrowsAsync<RLoopException>(() => query);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
        Assert.Equal(1, link.SessionCalls);
        Assert.Equal(0, link.DefinitionCalls);
        Assert.Equal(0, link.GetAllCalls);
    }

    [Fact]
    public async Task NonComponentTypeDefinitionFailureIsUnreadableRatherThanNotFound()
    {
        const string enumType = "[Test]Test.Mode";
        var link = new ScriptedMetadataLink { AllTypes = [Comp] };
        link.TypeDefinitions[enumType] = new Link.TypeDefinition { FullTypeName = enumType, IsEnum = true };
        link.TypeDefinitionFailures[enumType] = "world loading";
        await using var client = Adapter(link);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeTypeAsync(enumType));
        Assert.Equal("TYPE_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal("world loading", ex.Context!["errorInfo"]);
    }

    [Theory]
    [InlineData("component-types")]
    [InlineData("type")]
    [InlineData("component")]
    public async Task CacheFastPathRejectsNewGenerationCacheForAnOldRequest(string cache)
    {
        var link = new ScriptedMetadataLink { AllTypes = [Comp], Connected = false };
        link.Definitions[Comp] = WidgetDefinition();
        link.TypeDefinitions[Comp] = new Link.TypeDefinition { FullTypeName = Comp, Name = "old" };
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        async Task Read()
        {
            if (cache == "component-types") await client.SearchComponentTypesAsync("Widget", 10);
            else if (cache == "type") await client.DescribeTypeMetadataAsync(Comp);
            else await client.DescribeComponentMetadataAsync(Comp);
        }
        await Read();
        client.BeforeCacheReadForTests = kind =>
        {
            Assert.Equal(cache, kind);
            client.BeforeCacheReadForTests = null;
            // Deterministically reconnect and populate the new cache after the old request captured its generation.
            link.Connected = false;
            client.ConnectAsync(B, Timeout).GetAwaiter().GetResult();
            link.AllTypes = [Listed];
            link.TypeDefinitions[Comp] = new Link.TypeDefinition { FullTypeName = Comp, Name = "new" };
            link.Definitions[Comp].CategoryPath = "new";
            Read().GetAwaiter().GetResult();
        };

        var ex = await Assert.ThrowsAsync<RLoopException>(Read);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
    }

    [Fact]
    public async Task TypeListCacheHitIsCopiedUnderLockBeforeReconnectCanClearIt()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Comp], Connected = false };
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        await client.SearchComponentTypesAsync("Widget", 10);
        var events = new List<string>();
        void Record(string value) { lock (events) events.Add(value); }
        var reconnectDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.InsideReconnectLockForTests = () => Record("reconnect-locked");
        client.AfterCacheReadForTests = kind =>
        {
            Assert.Equal("component-types", kind);
            client.AfterCacheReadForTests = null;
            Record("cache-copied");
            link.Connected = false;
            var thread = new Thread(() =>
            {
                try { client.ConnectAsync(B, Timeout).GetAwaiter().GetResult(); reconnectDone.SetResult(); }
                catch (Exception ex) { reconnectDone.SetException(ex); }
            }) { IsBackground = true };
            thread.Start();
            // Thread state supplies synchronization evidence; the deadline only bounds a broken test.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (events) if (events.Contains("reconnect-locked")) return true;
                return (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;
            }, Timeout));
            lock (events) Assert.Equal(["cache-copied"], events);
            Record("read-lock-held");
        };

        Assert.Equal([Comp], await client.SearchComponentTypesAsync("Widget", 10));
        await reconnectDone.Task.WaitAsync(Timeout);
        lock (events) Assert.Equal(["cache-copied", "read-lock-held", "reconnect-locked"], events);
        link.AllTypes = [Listed];
        Assert.Equal([Listed], await client.SearchComponentTypesAsync("Gradient", 10));
        Assert.Equal(2, link.GetAllCalls);
    }

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
    // Order is recorded by hooks inside the lock; no timing decides the result.
    [Fact]
    public async Task ReconnectBetweenGenerationCheckAndCacheWriteWaitsForTheCommit()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Comp], Connected = false };
        link.Definitions[Comp] = WidgetDefinition();
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var events = new List<string>();
        void Record(string name) { lock (events) events.Add(name); }
        var reconnectDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        client.AfterCommitForTests = () => { if (commits >= 2) Record("commit-done"); };
        client.AfterGenerationCheckForTests = () =>
        {
            if (++commits < 2) return; // the first commit creates the disk-cache handle; the second is the definition write
            client.AfterGenerationCheckForTests = null;
            Record("commit-enter");
            link.Connected = false;
            client.InsideReconnectLockForTests = () => Record("reconnect-locked");
            var thread = new Thread(() =>
            {
                try { client.ConnectAsync(B, Timeout).GetAwaiter().GetResult(); reconnectDone.SetResult(); }
                catch (Exception ex) { reconnectDone.SetException(ex); }
            }) { IsBackground = true };
            thread.Start();
            // Wait until the reconnect is either blocked on the cache lock (which this commit holds) or already inside it.
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (events) if (events.Contains("reconnect-locked")) break;
                if ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0) break;
                Thread.Yield();
            }
        };

        Assert.Equal(Comp, (await client.DescribeComponentTypeAsync(Comp)).FullTypeName);
        await reconnectDone.Task.WaitAsync(Timeout);
        lock (events) Assert.Equal(["commit-enter", "commit-done", "reconnect-locked"], events);

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

    // C4 remainder: a failure answer of an old connection aborts the query instead of becoming a per-type error.
    [Fact]
    public async Task TypeListFailureAnswerAfterGenerationChangeAbortsTheQuery()
    {
        var link = new ScriptedMetadataLink { Connected = false };
        link.AllTypesFailureGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.AllTypesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var query = ReflectionQuery.RunAsync(client, new ReflectionRequest([new("Missing1", ["Intensity"]), new("Missing2", ["Intensity"])]));
        await link.AllTypesStarted.Task.WaitAsync(Timeout);
        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        link.AllTypesFailureGate.SetResult();

        var ex = await Assert.ThrowsAsync<RLoopException>(() => query);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
        Assert.Equal(1, link.GetAllCalls);
    }

    [Fact]
    public async Task TypeListFailureAnswerOnTheSameGenerationStaysTypeSearchFailed()
    {
        var link = new ScriptedMetadataLink();
        link.AllTypesFailureGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.AllTypesFailureGate.SetResult();
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync("X", 10));
        Assert.Equal("TYPE_SEARCH_FAILED", ex.Code);
    }

    [Fact]
    public async Task TypeDefinitionFailureAnswerAfterGenerationChangeIsGenerationChanged()
    {
        var link = new ScriptedMetadataLink { Connected = false };
        link.TypeDefinitionFailureGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.TypeDefinitionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var describe = client.DescribeTypeAsync("Some.Type");
        await link.TypeDefinitionStarted.Task.WaitAsync(Timeout);
        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        link.TypeDefinitionFailureGate.SetResult();

        var ex = await Assert.ThrowsAsync<RLoopException>(() => describe);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
        Assert.Equal(0, link.GetAllCalls);
    }

    // W1 remainder: a failure answer that comes with a dropped connection is a connection failure.
    [Fact]
    public async Task CategoryFailureAnswerWithDroppedConnectionIsNotConnected()
    {
        var link = new ScriptedMetadataLink();
        link.CategoryFaults[""] = () =>
        {
            link.Connected = false;
            return Task.FromResult(new LinkTypeList(false, "socket closed", null, null));
        };
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync("X", 10));
        Assert.Equal("NOT_CONNECTED", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task CategoryFailureAnswerWhileConnectedStaysSearchIncomplete()
    {
        var link = new ScriptedMetadataLink();
        link.CategoryFaults[""] = () => Task.FromResult(new LinkTypeList(false, "world loading", null, null));
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync("X", 10));
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
    }

    // C4 for the category walk: an old connection's category answer aborts the whole query, failure or success.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CategoryAnswerAfterGenerationChangeAbortsTheQueryAndIsNotCached(bool successAnswer)
    {
        var link = new ScriptedMetadataLink { Connected = false };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.CategoryFaults[""] = async () =>
        {
            started.TrySetResult();
            await gate.Task;
            return successAnswer ? ScriptedMetadataLink.Level([Comp], []) : new LinkTypeList(false, "old connection failed", null, null);
        };
        await using var client = Adapter(link);
        await client.ConnectAsync(A, Timeout);
        var query = ReflectionQuery.RunAsync(client, new ReflectionRequest([new("Missing1", ["Intensity"]), new("Missing2", ["Intensity"])]));
        await started.Task.WaitAsync(Timeout);
        link.Connected = false;
        await client.ConnectAsync(B, Timeout);
        gate.SetResult();

        var ex = await Assert.ThrowsAsync<RLoopException>(() => query);
        Assert.Equal("CONNECTION_GENERATION_CHANGED", ex.Code);
        Assert.Equal([""], link.RequestedCategories); // did not go on to a second type or another category
        Assert.Equal(1, link.GetAllCalls);

        // Nothing of the old answer was cached: the new connection walks the categories again.
        link.CategoryFaults.Clear();
        link.Categories[""] = ScriptedMetadataLink.Level([Comp], []);
        Assert.Contains(Comp, await client.SearchComponentTypesAsync("Widget", 10));
        Assert.Equal(2, link.GetAllCalls);
    }
}

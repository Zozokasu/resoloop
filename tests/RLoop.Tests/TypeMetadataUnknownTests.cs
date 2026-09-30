using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

/// <summary>
/// Offline coverage for "unknown is not NotFound" on the direct-link type metadata path (S1-I1),
/// and for a release failure never hiding the original failure. Expected values are written by hand,
/// not produced by the code under test.
/// </summary>
public sealed class TypeMetadataUnknownTests
{
    private const string Listed = "[FrooxEngine]FrooxEngine.GradientStripTexture";
    private const string Readable = "[FrooxEngine]FrooxEngine.Light";

    private static ResoniteLinkClientAdapter Adapter(FakeMetadataLink link) =>
        new(link, TimeSpan.FromSeconds(5), new ReflectionCacheOptions("off"));

    private static FakeMetadataLink ListedButUnreadable(bool throws = false)
    {
        var link = new FakeMetadataLink { AllTypes = [Listed, Readable] };
        link.Definitions[Readable] = ComponentDefinition(Readable);
        link.TypeDefinitions[Listed] = new Link.TypeDefinition { FullTypeName = Listed, Name = "GradientStripTexture", IsComponent = true };
        link.TypeDefinitions[Readable] = new Link.TypeDefinition { FullTypeName = Readable, Name = "Light", IsComponent = true };
        if (throws) link.ThrowingDefinitions.Add(Listed);
        else link.FailingDefinitions[Listed] = "Object reference not set to an instance of an object.";
        return link;
    }

    private static Link.ComponentDefinition ComponentDefinition(string fullName) => new()
    {
        Type = new Link.TypeDefinition { FullTypeName = fullName },
        Methods = [],
        Members = new() { ["Intensity"] = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "float" } } }
    };

    // (1) listed type + failed definition is not NotFound.
    [Fact]
    public async Task ListedTypeWithFailedDefinitionIsUnreadableNotNotFound()
    {
        await using var client = Adapter(ListedButUnreadable());
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("GradientStripTexture"));
        Assert.Equal("COMPONENT_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal(Listed, ex.Context!["resolvedType"]);
        Assert.Equal("Object reference not set to an instance of an object.", ex.Context["errorInfo"]);
    }

    [Fact]
    public async Task DefinitionThatThrowsIsWrappedAsUnreadableWithTheOriginalException()
    {
        await using var client = Adapter(ListedButUnreadable(throws: true));
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync(Listed));
        Assert.Equal("COMPONENT_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.IsType<NullReferenceException>(ex.InnerException);
        Assert.Equal(typeof(NullReferenceException).FullName, ex.Context!["exception"]);
    }

    [Fact]
    public async Task TypeDescribeFallsBackToTypeInfoAndSaysMembersAreUnavailable()
    {
        await using var client = Adapter(ListedButUnreadable());
        var result = Assert.IsAssignableFrom<JsonObject>(await Program.DescribeTypeOrComponentAsync(client, "GradientStripTexture", default));
        Assert.Equal(Listed, (string?)result["fullTypeName"]);
        Assert.Equal(false, (bool?)result["membersAvailable"]);
        Assert.Equal("COMPONENT_DEFINITION_UNREADABLE", (string?)result["membersUnavailableCode"]);
        Assert.Contains("could not read its definition", (string?)result["membersUnavailableReason"]);
    }

    [Fact]
    public async Task TypeDescribeOfReadableComponentKeepsTheExistingShape()
    {
        await using var client = Adapter(ListedButUnreadable());
        var result = await Program.DescribeTypeOrComponentAsync(client, Readable, default);
        var info = Assert.IsType<ComponentTypeInfo>(result);
        Assert.Equal(Readable, info.FullTypeName);
        Assert.Equal("Intensity", Assert.Single(info.Members).Name);
    }

    // (2) member-requiring operations are rejected, and the reason names the failed definition.
    [Fact]
    public async Task MemberRequiringOperationIsRejectedWithTheDefinitionFailureCode()
    {
        await using var client = Adapter(ListedButUnreadable());
        var ex = await Assert.ThrowsAsync<RLoopException>(() =>
            client.AddComponentAsync("slot-1", "GradientStripTexture", new Dictionary<string, string> { ["Foo"] = "1" }));
        Assert.Equal("COMPONENT_DEFINITION_UNREADABLE", ex.Code);
        Assert.DoesNotContain(client.SnapshotMetrics().Operations, o => o.Operation == "component.add");
    }

    // (3) empty / undecidable list is unknown, not NotFound.
    [Fact]
    public async Task EmptyTypeListIsUnknownNotNotFound()
    {
        var link = new FakeMetadataLink { AllTypes = [] };
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("Light"));
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.NotEqual(ExitCodes.NotFound, ex.ExitCode);
    }

    [Fact]
    public async Task EmptyTypeListIsNotRememberedSoALaterCallCanSucceed()
    {
        var link = new FakeMetadataLink { AllTypes = [] };
        link.Definitions[Readable] = ComponentDefinition(Readable);
        await using var client = Adapter(link);
        await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("Light"));
        link.AllTypes = [Readable];
        var described = await client.DescribeComponentTypeAsync("Light");
        Assert.Equal(Readable, described.FullTypeName);
    }

    [Fact]
    public async Task TypeDescribeWithUnknownListStillReturnsTypeInfoWithMembersUnavailable()
    {
        var link = new FakeMetadataLink { AllTypes = [] };
        link.TypeDefinitions["Light"] = new Link.TypeDefinition { FullTypeName = Readable, Name = "Light", IsComponent = true };
        await using var client = Adapter(link);
        var result = Assert.IsAssignableFrom<JsonObject>(await Program.DescribeTypeOrComponentAsync(client, "Light", default));
        Assert.Equal(false, (bool?)result["membersAvailable"]);
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", (string?)result["membersUnavailableCode"]);
    }

    // (4) a complete list that lacks the type is the only true NotFound.
    [Fact]
    public async Task CompleteListWithoutTheTypeIsNotFound()
    {
        var link = new FakeMetadataLink { AllTypes = [Readable] };
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("NoSuchThing"));
        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.NotFound, ex.ExitCode);
    }

    [Fact]
    public async Task TypeDescribeOfAbsentComponentFallsBackToPlainTypeWithoutMembersFlag()
    {
        var link = new FakeMetadataLink { AllTypes = [Readable] };
        link.TypeDefinitions["Test.Mode"] = new Link.TypeDefinition { FullTypeName = "Test.Mode", Name = "Mode", IsEnum = true };
        link.Enums["Test.Mode"] = (new Dictionary<string, long> { ["A"] = 0 }, false);
        await using var client = Adapter(link);
        var result = await Program.DescribeTypeOrComponentAsync(client, "Test.Mode", default);
        var info = Assert.IsType<TypeInfo>(result);
        Assert.True(info.IsEnum);
    }

    // (5) one type failing does not stop the other types of a reflection query.
    [Fact]
    public async Task ReflectionQueryIsolatesOneUnreadableTypeAndAnswersTheRest()
    {
        var link = ListedButUnreadable(throws: true);
        await using var client = Adapter(link);
        var request = new ReflectionRequest([new(Listed, ["Anything"]), new(Readable, ["Intensity"]), new("Test.Missing", ["X"])]);
        var report = await ReflectionQuery.RunAsync(client, request, new ReflectionCacheOptions("off"));

        Assert.Equal(3, report.Types.Count);
        var bad = report.Types[0];
        Assert.Equal(ReflectionStatus.Unknown, bad.Status);
        Assert.False(bad.Verified);
        var good = report.Types[1];
        Assert.Equal(ReflectionStatus.Verified, good.Status);
        Assert.True(good.Verified);
        Assert.Equal("Intensity", Assert.Single(good.Members).Name);
        Assert.Equal(ReflectionStatus.NotFound, report.Types[2].Status);

        Assert.False(report.Verified);
        Assert.False(report.Complete);
        Assert.Contains(report.Differences, d => d.Type == Listed && d.Code == "TYPE_DEFINITION_UNAVAILABLE");
        Assert.Contains(report.Differences, d => d.Type == "Test.Missing" && d.Code == "COMPONENT_TYPE_NOT_FOUND");
        Assert.DoesNotContain(report.Differences, d => d.Type == Readable);
    }

    [Fact]
    public async Task ReflectionQueryStillAbortsOnConnectionLevelFailure()
    {
        var link = new FakeMetadataLink { AllTypes = [Readable] };
        link.DefinitionFault = new RLoopException("CONNECTION_FAILED", "lost", ExitCodes.ConnectionFailed);
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() =>
            ReflectionQuery.RunAsync(client, new ReflectionRequest([new(Readable, ["Intensity"])]), new ReflectionCacheOptions("off")));
        Assert.Equal("CONNECTION_FAILED", ex.Code);
    }

    // U4: an exception from Dispose never replaces the command's own exception.
    [Fact]
    public async Task ThrowingDisposeDoesNotReplaceTheOriginalCommandFailure()
    {
        var link = new FakeMetadataLink { AllTypes = [Readable], DisposeFault = new InvalidOperationException("dispose boom") };
        var adapter = Adapter(link);
        var original = await Assert.ThrowsAsync<RLoopException>(async () =>
        {
            await using var client = adapter;
            await client.DescribeComponentTypeAsync("NoSuchThing");
        });
        Assert.Equal("COMPONENT_TYPE_NOT_FOUND", original.Code);
        Assert.Equal("dispose boom", adapter.DisposeException?.Message);
    }

    [Fact]
    public async Task ThrowingDisposeIsAbsorbedEvenWhenNothingFailed()
    {
        var adapter = Adapter(new FakeMetadataLink { DisposeFault = new InvalidOperationException("boom") });
        await adapter.DisposeAsync();
        Assert.IsType<InvalidOperationException>(adapter.DisposeException);
    }

    private sealed class FakeMetadataLink : IMetadataLink
    {
        public IReadOnlyList<string> AllTypes = [];
        public readonly Dictionary<string, Link.ComponentDefinition> Definitions = new();
        public readonly Dictionary<string, string> FailingDefinitions = new();
        public readonly HashSet<string> ThrowingDefinitions = new();
        public readonly Dictionary<string, Link.TypeDefinition> TypeDefinitions = new();
        public readonly Dictionary<string, (IReadOnlyDictionary<string, long> Values, bool IsFlags)> Enums = new();
        public Exception? DefinitionFault;
        public Exception? DisposeFault;

        public bool IsConnected => true;

        public Task<LinkTypeList> GetAllComponentTypes() => Task.FromResult(new LinkTypeList(true, null, AllTypes, []));
        public Task<LinkTypeList> GetComponentTypes(string category) => Task.FromResult(new LinkTypeList(true, null, [], []));

        public Task<LinkComponentDefinition> GetComponentDefinition(string type)
        {
            if (DefinitionFault is not null) throw DefinitionFault;
            if (ThrowingDefinitions.Contains(type)) throw new NullReferenceException("Object reference not set to an instance of an object.");
            if (FailingDefinitions.TryGetValue(type, out var error)) return Task.FromResult(new LinkComponentDefinition(false, error, default!));
            return Task.FromResult(Definitions.TryGetValue(type, out var definition)
                ? new LinkComponentDefinition(true, null, definition)
                : new LinkComponentDefinition(false, "Component type not found.", default!));
        }

        public Task<LinkTypeDefinition> GetTypeDefinition(string type) =>
            Task.FromResult(TypeDefinitions.TryGetValue(type, out var definition)
                ? new LinkTypeDefinition(true, null, definition)
                : new LinkTypeDefinition(false, "Type not found.", default!));

        public Task<LinkEnumDefinition> GetEnumDefinition(string type) =>
            Task.FromResult(Enums.TryGetValue(type, out var value)
                ? new LinkEnumDefinition(true, null, value.Values, value.IsFlags)
                : new LinkEnumDefinition(false, "Enum not found.", default!, false));

        public void Dispose()
        {
            if (DisposeFault is not null) throw DisposeFault;
        }
    }
}

using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class TypeAbsenceFailureTests
{
    private const string Component = "[Test]Test.Widget";
    private const string Missing = "NoSuchComponentXyz";
    private static ResoniteLinkClientAdapter Adapter(ScriptedMetadataLink link) =>
        new(link, TimeSpan.FromSeconds(5), new ReflectionCacheOptions("off"));

    public static TheoryData<string?, bool> TypeFailures => new()
    {
        { "NoSuchComponentXyz is not a valid type", true },
        { " \tNoSuchComponentXyz is not a valid type\r\n", true },
        { "OtherType is not a valid type", false },
        { null, false },
        { "", false },
        { " \t\r\n", false },
        { "Object reference not set to an instance of an object.", false },
        { "nosuchcomponentxyz is not a valid type", false },
        { "NoSuchComponentXyz is not a valid Type", false },
        { "NoSuchComponentXyz is not a valid type.", false },
        { "Error: NoSuchComponentXyz is not a valid type", false },
        { "NoSuchComponentXyz is not a valid type Retry.", false }
    };

    public static IEnumerable<object?[]> ListedTypeFailures => TypeFailures.SelectMany(failure =>
        new[] { "Widget", "Test.Widget", Component }.Select(query => new object?[]
        {
            query, ((string?)failure[0])?.Replace(Missing, Component, StringComparison.Ordinal)
        }));

    [Theory]
    [MemberData(nameof(TypeFailures))]
    public async Task GeneralTypeFailureUsesOnlyExactRequestedTypeAbsence(string? errorInfo, bool absent)
    {
        var link = new ScriptedMetadataLink { AllTypes = [Component] };
        link.TypeDefinitionFailures[Missing] = errorInfo;
        await using var client = Adapter(link);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeTypeAsync(Missing));

        Assert.Equal(absent ? "TYPE_NOT_FOUND" : "TYPE_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(absent ? ExitCodes.NotFound : ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal(errorInfo, ex.Context["errorInfo"]);
        Assert.Equal(Missing, Assert.Single(link.RequestedTypeDefinitions));
    }

    [Theory]
    [MemberData(nameof(ListedTypeFailures))]
    public async Task ListedComponentDefinitionFailureAndQueryStatusAreAlwaysUnknown(string query, string? errorInfo)
    {
        var link = new ScriptedMetadataLink { AllTypes = [Component] };
        link.ComponentDefinitionFailures[Component] = errorInfo;
        await using var client = Adapter(link);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync(query));
        Assert.Equal("COMPONENT_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal(errorInfo, ex.Context["errorInfo"]);
        Assert.Equal([query, Component], link.RequestedComponentDefinitions);

        foreach (var check in new[] { false, true })
        {
            var report = await ReflectionQuery.RunAsync(client, new ReflectionRequest([new(query, ["Intensity"])]), check: check);
            Assert.Equal(ReflectionStatus.Unknown, Assert.Single(report.Types).Status);
            Assert.False(report.Complete);
            Assert.False(report.Verified);
            Assert.False(report.Compatible);
            Assert.Equal("TYPE_DEFINITION_UNAVAILABLE", Assert.Single(report.Differences).Code);
        }
    }

    [Theory]
    [MemberData(nameof(ListedTypeFailures))]
    public async Task ListedGeneralTypeDefinitionFailureIsAlwaysUnreadable(string query, string? errorInfo)
    {
        var link = new ScriptedMetadataLink { AllTypes = [Component] };
        link.TypeDefinitionFailures[query] = $"{query} is not a valid type";
        link.TypeDefinitionFailures[Component] = errorInfo;
        await using var client = Adapter(link);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeTypeAsync(query));

        Assert.Equal("TYPE_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.Equal(errorInfo, ex.Context["errorInfo"]);
        Assert.Equal([query, Component], link.RequestedTypeDefinitions);
    }

    [Theory]
    [InlineData("Widget is not a valid type")]
    [InlineData("OtherType is not a valid type")]
    public async Task ComponentRetryRejectsAbsenceForAnotherRequestedName(string errorInfo)
    {
        var link = new ScriptedMetadataLink { AllTypes = [Component] };
        link.ComponentDefinitionFailures["Widget"] = "Widget is not a valid type";
        link.ComponentDefinitionFailures[Component] = errorInfo;
        await using var client = Adapter(link);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("Widget"));

        Assert.Equal("COMPONENT_DEFINITION_UNREADABLE", ex.Code);
        Assert.Equal(["Widget", Component], link.RequestedComponentDefinitions);
    }

    [Fact]
    public async Task InvalidShortTypeNameStillResolvesAndReadsDefinition()
    {
        var link = new ScriptedMetadataLink { AllTypes = [Component] };
        link.TypeDefinitionFailures["Widget"] = "Widget is not a valid type";
        link.ComponentDefinitionFailures["Widget"] = "Widget is not a valid type";
        link.TypeDefinitions[Component] = new Link.TypeDefinition { FullTypeName = Component };
        link.Definitions[Component] = ScriptedMetadataLink.Definition(Component);
        await using var client = Adapter(link);

        Assert.Equal(Component, (await client.DescribeTypeAsync("Widget")).FullTypeName);
        Assert.Equal(Component, (await client.DescribeComponentTypeAsync("Widget")).FullTypeName);
        Assert.Equal(["Widget", Component], link.RequestedTypeDefinitions);
        Assert.Equal(["Widget", Component], link.RequestedComponentDefinitions);
    }
}

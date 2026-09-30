using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.Tests;

/// <summary>
/// Category-walk fallback used when GetAllComponentTypes returns an empty list (S1-I2, U2).
/// The cases carry the meaning of the resonite-workbench regression tests for
/// GetAllComponentTypeNamesAsync (ResoniteLinkSessionLinkTests, source commit 7d40c92); expected values
/// are written by hand. Every failure is TYPE_SEARCH_INCOMPLETE (unknown), never NotFound, and no partial list is returned.
/// </summary>
public sealed class CategoryFallbackBoundaryTests
{
    private static ResoniteLinkClientAdapter Adapter(ScriptedMetadataLink link) =>
        new(link, TimeSpan.FromSeconds(5), new ReflectionCacheOptions("off"));

    private static async Task<RLoopException> ExpectIncomplete(ScriptedMetadataLink link, string query = "Light")
    {
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync(query, 500));
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);
        Assert.NotEqual(ExitCodes.NotFound, ex.ExitCode);
        return ex;
    }

    // Workbench: ...CollectsTypesAcrossMultipleLevelsAndDedupes
    [Fact]
    public async Task MultiLevelWalkCollectsAndDedupesInTraversalOrder()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level(["A"], ["Rendering", "Audio"]);
        link.Categories["Rendering"] = ScriptedMetadataLink.Level(["B", "C"], ["Materials"]);
        link.Categories["Audio"] = ScriptedMetadataLink.Level(["E"], []);
        link.Categories["Rendering/Materials"] = ScriptedMetadataLink.Level(["D", "B"], []);
        await using var client = Adapter(link);
        var found = await client.SearchComponentTypesAsync("", 500);
        Assert.Equal(["A", "B", "C", "D", "E"], found);
        Assert.Equal(["", "Rendering", "Rendering/Materials", "Audio"], link.RequestedCategories);
        Assert.All(link.RequestedCategories, p => Assert.DoesNotContain("*", p));
    }

    [Fact]
    public async Task CompleteWalkWithoutAMatchIsAnEmptyListNotAnError()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level(["FrooxEngine.Light"], []);
        await using var client = Adapter(link);
        Assert.Empty(await client.SearchComponentTypesAsync("Nope", 10));
    }

    [Fact]
    public async Task CompleteWalkIsRememberedSoTheTreeIsWalkedOnce()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level(["FrooxEngine.Light"], []);
        await using var client = Adapter(link);
        await client.SearchComponentTypesAsync("Light", 10);
        await client.SearchComponentTypesAsync("Light", 10);
        Assert.Equal([""], link.RequestedCategories);
        Assert.Equal(1, link.GetAllCalls);
    }

    // Unchanged normal path: a non-empty list never triggers the walk (e.g. Light search).
    [Fact]
    public async Task NonEmptyTypeListDoesNotFallBackToTheCategoryWalk()
    {
        var link = new ScriptedMetadataLink { AllTypes = ["[FrooxEngine]FrooxEngine.Light", "[FrooxEngine]FrooxEngine.Camera"] };
        await using var client = Adapter(link);
        var found = await client.SearchComponentTypesAsync("Light", 10);
        Assert.Equal(["[FrooxEngine]FrooxEngine.Light"], found);
        Assert.Empty(link.RequestedCategories);
    }

    // Workbench: ...PropagatesMidTraversalFailureWithoutReturningPartialResults
    [Fact]
    public async Task MidWalkFailureReturnsNoPartialResultAndIsNotRemembered()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level(["A"], ["Rendering"]);
        link.Categories["Rendering"] = new LinkTypeList(false, "boom", null, null);
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.SearchComponentTypesAsync("A", 500));
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", ex.Code);
        Assert.NotEqual(ExitCodes.NotFound, ex.ExitCode);
        // A later, healthy answer is used: the failure was not cached.
        link.Categories["Rendering"] = ScriptedMetadataLink.Level(["B"], []);
        Assert.Equal(["A", "B"], await client.SearchComponentTypesAsync("", 500));
    }

    // Workbench: ...ThrowsWhenCategoryCountExceedsTheLimit
    [Fact]
    public async Task MoreCategoriesThanTheLimitIsIncomplete()
    {
        var link = new ScriptedMetadataLink();
        var siblings = Enumerable.Range(0, ResoniteLinkClientAdapter.MaxTypeCategories + 1).Select(i => $"Cat{i:D4}").ToArray();
        link.Categories[""] = ScriptedMetadataLink.Level([], siblings);
        foreach (var name in siblings) link.Categories[name] = ScriptedMetadataLink.Level([], []);
        await ExpectIncomplete(link);
        Assert.True(link.RequestedCategories.Count <= ResoniteLinkClientAdapter.MaxTypeCategories);
    }

    // Workbench: ...ThrowsWhenDepthExceedsTheLimit
    [Fact]
    public async Task DeeperTreeThanTheLimitIsIncomplete()
    {
        var link = new ScriptedMetadataLink();
        var path = "";
        for (var depth = 1; depth <= ResoniteLinkClientAdapter.MaxTypeCategoryDepth + 1; depth++)
        {
            var child = $"L{depth}";
            link.Categories[path] = ScriptedMetadataLink.Level([], [child]);
            path = path.Length == 0 ? child : path + "/" + child;
        }
        link.Categories[path] = ScriptedMetadataLink.Level([], []);
        await ExpectIncomplete(link);
    }

    [Fact]
    public async Task TreeExactlyAtTheDepthLimitIsAccepted()
    {
        var link = new ScriptedMetadataLink();
        var path = "";
        for (var depth = 1; depth <= ResoniteLinkClientAdapter.MaxTypeCategoryDepth; depth++)
        {
            var child = $"L{depth}";
            link.Categories[path] = ScriptedMetadataLink.Level([], [child]);
            path = path.Length == 0 ? child : path + "/" + child;
        }
        link.Categories[path] = ScriptedMetadataLink.Level(["Deep"], []);
        await using var client = Adapter(link);
        Assert.Equal(["Deep"], await client.SearchComponentTypesAsync("Deep", 10));
    }

    // Workbench: ...ThrowsWhenASubcategoryNameContainsASeparator
    [Theory]
    [InlineData("Foo/Bar")]
    [InlineData("Foo\\Bar")]
    public async Task SeparatorInSubCategoryNameIsIncompleteAndNeverRequested(string child)
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level([], [child]);
        await ExpectIncomplete(link);
        Assert.Equal([""], link.RequestedCategories);
    }

    // Workbench: ...ThrowsWhenASubcategoryNameIsBlank
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSubCategoryNameIsIncompleteAndNeverRequested(string child)
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level([], [child]);
        await ExpectIncomplete(link);
        Assert.Equal([""], link.RequestedCategories);
    }

    // Workbench: ...ThrowsWhenComponentTypesAreMissing
    [Fact]
    public async Task MissingComponentTypesIsIncomplete()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level(null, []);
        await ExpectIncomplete(link);
    }

    // Workbench: ...ThrowsWhenSubCategoriesAreMissing
    [Fact]
    public async Task MissingSubCategoriesIsIncomplete()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level([], null);
        await ExpectIncomplete(link);
    }

    // Workbench: ...ThrowsWhenANestedCategoryMissesSubCategories
    [Fact]
    public async Task NestedCategoryMissingSubCategoriesIsIncomplete()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level(["A"], ["Rendering"]);
        link.Categories["Rendering"] = ScriptedMetadataLink.Level(["B"], null);
        await ExpectIncomplete(link);
    }

    [Fact]
    public async Task EmptyCompleteListIsUnknownForSearchAndForResolve()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level([], []);
        await ExpectIncomplete(link);
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("Light"));
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", ex.Code);
    }

    [Fact]
    public async Task IncompleteWalkDuringResolveIsUnknownNotNotFound()
    {
        var link = new ScriptedMetadataLink();
        link.Categories[""] = ScriptedMetadataLink.Level(["A"], ["Foo/Bar"]);
        await using var client = Adapter(link);
        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.DescribeComponentTypeAsync("Light"));
        Assert.Equal("TYPE_SEARCH_INCOMPLETE", ex.Code);
    }
}

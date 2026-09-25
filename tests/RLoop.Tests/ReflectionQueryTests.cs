using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;
using TypeInfo = RLoop.Core.TypeInfo;

namespace RLoop.Tests;

public sealed class ReflectionQueryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "resoloop-reflection-" + Guid.NewGuid().ToString("N"));
    public ReflectionQueryTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private ReflectionCacheOptions Cache(string mode = "auto") => new(mode, directory);
    private static ReflectionRequest Request(params string[] members) => new([new("Test.Component", members)]);
    private static (IResoniteClient Client, MetadataClient Fake) Client()
    {
        var client = DispatchProxy.Create<ITestMetadataClient, MetadataClient>();
        return (client, (MetadataClient)client);
    }

    [Fact]
    public async Task QueryProjectsRequestedMembersDeduplicatesTypesAndSharesEnums()
    {
        var (client, fake) = Client();
        var request = new ReflectionRequest([new("Test.Component", ["Mode", "Target"], ["Mode"]), new("Test.Component", ["Mode"], ["Mode"])]);
        var result = await ReflectionQuery.RunAsync(client, request, Cache());
        Assert.True(result.Complete); Assert.True(result.Verified); Assert.True(result.Compatible);
        Assert.Equal(1, fake.Components); Assert.Equal(1, fake.Types);
        Assert.Equal("IProvider<float>", result.Types[0].Members[1].TargetType);
        Assert.Equal(1, result.Types[0].Members[0].EnumValues!["B"]);
        Assert.Equal(2, result.Types[0].Members.Count);
        Assert.DoesNotContain(result.Types[0].Members, m => m.Name == "Unused");
    }

    [Fact]
    public async Task VersionMatchedEntriesAreTrustedByChecksAndRefreshReadsCurrentMetadata()
    {
        var (client, _) = Client();
        await ReflectionQuery.RunAsync(client, Request("Mode"), Cache());
        var (second, secondFake) = Client();
        var warm = await ReflectionQuery.RunAsync(second, Request("Mode"), Cache());
        Assert.True(warm.Verified); Assert.True(warm.Compatible); Assert.Equal(1, warm.DiskHits);
        Assert.Equal(0, secondFake.Components);
        secondFake.Component = secondFake.Component with { Members = [new("Mode", "reference", "SyncRef", null, "Other")] };
        var contract = new ReflectionRequest([new("Test.Component", ["Mode"], Expect: new Dictionary<string, ReflectionExpectation> { ["Mode"] = new(Kind: "field", ValueType: "Test.Mode") })]);
        var trusted = await ReflectionQuery.RunAsync(second, contract, Cache(), check: true);
        Assert.True(trusted.Verified); Assert.True(trusted.Compatible); Assert.Equal(0, secondFake.Components);
        var check = await ReflectionQuery.RunAsync(second, contract, Cache("refresh"), check: true);
        Assert.True(check.Verified); Assert.False(check.Compatible); Assert.Equal(0, check.DiskHits);
        Assert.Equal(1, secondFake.Components); Assert.Equal(2, check.Differences.Count);
    }

    [Fact]
    public async Task MissingCachedMemberRetriesLiveOnceWithoutCachingFailure()
    {
        var (client, _) = Client();
        await ReflectionQuery.RunAsync(client, Request("Mode"), Cache());
        var (next, fake) = Client();
        fake.Component = fake.Component with { Members = fake.Component.Members.Append(new("Added", "field", null, "bool", null)).ToArray() };
        var result = await ReflectionQuery.RunAsync(next, Request("Added"), Cache());
        Assert.True(result.Verified); Assert.True(result.Complete); Assert.Equal(1, fake.Components);
        var failure = await ReflectionQuery.RunAsync(next, Request("Absent"), Cache("off"));
        Assert.False(failure.Complete); Assert.Equal("MEMBER_NOT_FOUND", Assert.Single(failure.Differences).Code);
        Assert.Equal(2, fake.Components);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("corrupt")]
    [InlineData("null-members")]
    [InlineData("version")]
    [InlineData("endpoint")]
    [InlineData("off")]
    [InlineData("refresh")]
    [InlineData("future")]
    [InlineData("wrong-key")]
    [InlineData("missing-version")]
    public async Task InvalidOrBypassedEntriesFetchLive(string scenario)
    {
        var (client, _) = Client();
        await ReflectionQuery.RunAsync(client, Request("Mode"), Cache());
        var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
        if (scenario == "corrupt") File.WriteAllText(path, "{bad");
        if (scenario is "expired" or "null-members" or "future" or "wrong-key")
        {
            var entry = JsonNode.Parse(File.ReadAllText(path))!;
            if (scenario == "expired") entry["observedAt"] = DateTimeOffset.UtcNow.AddDays(-2);
            else if (scenario == "future") entry["observedAt"] = DateTimeOffset.UtcNow.AddDays(1);
            else if (scenario == "wrong-key") entry["key"] = "incompatible-schema-key";
            else entry["value"]!["members"] = null;
            File.WriteAllText(path, entry.ToJsonString());
        }
        var (next, fake) = Client();
        if (scenario == "version") fake.Session = fake.Session with { ResoniteVersion = "v2" };
        if (scenario == "endpoint") fake.Session = fake.Session with { Url = "ws://other:2/" };
        if (scenario == "missing-version") fake.Session = fake.Session with { ResoniteVersion = null };
        var result = await ReflectionQuery.RunAsync(next, Request("Mode"), Cache(scenario is "off" or "refresh" ? scenario : "auto") with { MaxAge = scenario == "expired" ? TimeSpan.FromHours(24) : null });
        Assert.True(result.Verified); Assert.Equal(0, result.DiskHits); Assert.Equal(1, fake.Components);
    }

    [Fact]
    public async Task MatchingVersionsTrustOldEntriesAndDifferentConnectionIds()
    {
        var (client, _) = Client();
        await ReflectionQuery.RunAsync(client, Request("Mode"), Cache());
        var path = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var entry = JsonNode.Parse(File.ReadAllText(path))!;
        entry["observedAt"] = DateTimeOffset.UtcNow.AddYears(-1);
        File.WriteAllText(path, entry.ToJsonString());
        var (next, fake) = Client(); fake.Session = fake.Session with { UniqueSessionId = "another-connection" };
        var result = await ReflectionQuery.RunAsync(next, Request("Mode"), Cache(), check: true);
        Assert.True(result.Verified); Assert.True(result.Compatible);
        Assert.Equal("version-cache", Assert.Single(result.Types).Source);
        Assert.Equal(1, result.DiskHits); Assert.Equal(0, fake.Components);
    }

    [Fact]
    public async Task CorruptEnumCacheAndFailedCacheWritesDoNotBlockLiveQuery()
    {
        var (client, _) = Client();
        var request = new ReflectionRequest([new("Test.Component", ["Mode"], ["Mode"])]);
        await ReflectionQuery.RunAsync(client, request, Cache());
        foreach (var path in Directory.GetFiles(directory, "*.json"))
        {
            var entry = JsonNode.Parse(File.ReadAllText(path))!;
            if (entry["value"]!["isEnum"] is not null)
            { entry["value"]!["enumValues"] = null; File.WriteAllText(path, entry.ToJsonString()); }
        }
        var (next, fake) = Client();
        var repaired = await ReflectionQuery.RunAsync(next, request, Cache());
        Assert.True(repaired.Complete); Assert.True(repaired.Verified);
        Assert.Equal("version-cache", repaired.Types[0].Source);
        Assert.Equal(0, fake.Components); Assert.Equal(1, fake.Types);
        var blockedDirectory = Path.Combine(directory, "file"); File.WriteAllText(blockedDirectory, "keep");
        var uncached = await ReflectionQuery.RunAsync(next, request, new("auto", blockedDirectory));
        Assert.True(uncached.Complete); Assert.True(uncached.CacheWriteFailures > 0);
        Assert.Equal("keep", File.ReadAllText(blockedDirectory));
    }

    [Fact]
    public async Task EnumContractMismatchRefreshesDiskDataAndReportsExactDifference()
    {
        var (client, _) = Client();
        var query = new ReflectionRequest([new("Test.Component", ["Mode"], ["Mode"])]);
        await ReflectionQuery.RunAsync(client, query, Cache());
        var (next, fake) = Client();
        fake.Enum = fake.Enum with { EnumValues = new Dictionary<string, long> { ["A"] = 0, ["B"] = 2, ["Extra"] = 3 } };
        var contract = new ReflectionRequest([new("Test.Component", ["Mode"], Expect: new Dictionary<string, ReflectionExpectation>
            { ["Mode"] = new(EnumValues: new Dictionary<string, long> { ["B"] = 2 }) })]);
        var refreshed = await ReflectionQuery.RunAsync(next, contract, Cache());
        Assert.True(refreshed.Verified); Assert.True(refreshed.Compatible);
        Assert.Equal(1, fake.Components); Assert.Equal(1, fake.Types);
        fake.Enum = fake.Enum with { EnumValues = new Dictionary<string, long> { ["B"] = 4 } };
        var changed = await ReflectionQuery.RunAsync(next, contract, Cache("refresh"), check: true);
        var difference = Assert.Single(changed.Differences);
        Assert.Equal("ENUM_VALUE_MISMATCH", difference.Code);
        Assert.Equal("B=2", difference.Expected); Assert.Equal("B=4", difference.Actual);
    }

    [Fact]
    public void PublishedRequestExampleRoundTrips()
    {
        var path = AuthoringSchema.WriteNew(AuthoringSchema.Describe("reflection").Example, Path.Combine(directory, "request.json"));
        var request = ReflectionRequest.Load(path);
        Assert.Single(request.Types); Assert.Contains("Sidedness", request.Types[0].Enums!);
    }

    [Fact]
    public async Task BriefFailureRetainsDifferencesWithoutMetadataDumpAndRejectsInvalidCacheMode()
    {
        var (client, _) = Client();
        var report = await ReflectionQuery.RunAsync(client, Request("Absent"), Cache("off"), check: true);
        var json = JsonSerializer.SerializeToNode(BriefOutput.Project(report), AuthoringSchema.JsonOptions)!;
        Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Null(json["types"]);
        Assert.Equal("MEMBER_NOT_FOUND", json["differences"]![0]!["code"]!.GetValue<string>());
        var error = await Assert.ThrowsAsync<RLoopException>(() => ReflectionQuery.RunAsync(client, Request("Mode"), Cache("invalid")));
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
    }

    [Fact]
    public void RequestRejectsUnboundedUnknownOrUnselectedFields()
    {
        Assert.Throws<RLoopException>(() => new ReflectionRequest([]).Validate());
        Assert.Throws<RLoopException>(() => new ReflectionRequest(Enumerable.Repeat(new ReflectionSelection("T", ["M"]), 65).ToArray()).Validate());
        Assert.Throws<RLoopException>(() => new ReflectionRequest([new("T", ["M"], ["Other"])]).Validate());
        var path = Path.Combine(directory, "bad.json");
        File.WriteAllText(path, """{"types":[{"type":"T","members":["M"],"typo":true}]}""");
        Assert.Equal("REFLECTION_REQUEST_INVALID", Assert.Throws<RLoopException>(() => ReflectionRequest.Load(path)).Code);
    }

    [Fact]
    public async Task EnumConversionUsesSharedResolverThroughNestedListsAndRejectsInvalidNames()
    {
        var (_, fake) = Client();
        var calls = 0;
        Task<TypeInfo> Resolve(string name, CancellationToken ct) { calls++; Assert.Equal("Test.Mode", name); return Task.FromResult(fake.Enum); }
        var definition = new Link.ListDefinition { ElementDefinition = new Link.FieldDefinition { ValueType = new Link.TypeReference { Type = "Test.Mode" } } };
        var link = new Link.LinkInterface(); // Unconnected SDK instances cannot be disposed safely.
        var result = Assert.IsType<Link.SyncList>(await ValueCodec.ParseAsync(link, definition, "[\"A\",\"B\"]", describeType: Resolve));
        Assert.Equal(2, result.Elements.Count); Assert.Equal(2, calls);
        var error = await Assert.ThrowsAsync<RLoopException>(() => ValueCodec.ParseAsync(link, definition, "[\"Invalid\"]", describeType: Resolve));
        Assert.Equal("ENUM_VALUE_INVALID", error.Code);
    }

    public interface ITestMetadataClient : IResoniteClient, IReflectionMetadataClient { }

    public class MetadataClient : DispatchProxy
    {
        public SessionInfo Session = new("ws://fake:1/", true, "v1", "link1", "connection-only");
        public ComponentTypeInfo Component = new("Test.Component", null, null, false,
            [new("Mode", "field", "Sync", "Test.Mode", null), new("Target", "reference", "SyncRef", null, "IProvider<float>"), new("Unused", "field", null, "float", null)]);
        public TypeInfo Enum = new("Test.Mode", "Test", "Test", "Enum", null, false, false, false, true, false, false, false, [], [], new Dictionary<string, long> { ["A"] = 0, ["B"] = 1 }, false);
        public int Components; public int Types;
        private ReflectionMetadataCache? cache;
        private ReflectionMetadata<T> Get<T>(string kind, string name, bool fresh, Func<T> fetch, Func<T, bool> valid) where T : class
        {
            var stored = fresh ? null : cache?.Read<T>(kind, name, valid);
            if (stored is not null) return new(stored.Value, stored.ObservedAt, false);
            var value = fetch(); var now = DateTimeOffset.UtcNow;
            cache?.Write(kind, name, value, now);
            return new(value, now, true);
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args?.LastOrDefault() is CancellationToken ct) ct.ThrowIfCancellationRequested();
            switch (targetMethod!.Name)
            {
                case "ConfigureReflectionCache": cache = new ReflectionMetadataCache(Session, (ReflectionCacheOptions)args![0]!); return null;
                case "SnapshotReflectionCache": return new ReflectionCacheStatistics(cache?.DiskHits ?? 0, cache?.DiskMisses ?? 0, cache?.WriteFailures ?? 0);
                case "DescribeComponentMetadataAsync":
                    return Task.FromResult(Get("component", (string)args![0]!, (bool)args[1]!, () => { Components++; return Component; }, c => c.Members is not null));
                case "DescribeTypeMetadataAsync":
                    return Task.FromResult(Get("type", (string)args![0]!, (bool)args[1]!, () => { Types++; return Enum; }, t => !t.IsEnum || t.EnumValues is not null && t.IsFlags is not null));
                case "GetSessionInfoAsync": return Task.FromResult(Session);
                case "DescribeComponentTypeAsync": Components++; return Task.FromResult(Component);
                case "DescribeTypeAsync": Types++; return Task.FromResult(Enum);
                case "DisposeAsync": return ValueTask.CompletedTask;
                default: throw new InvalidOperationException("Unexpected call (including any mutation): " + targetMethod.Name);
            }
        }
    }
}

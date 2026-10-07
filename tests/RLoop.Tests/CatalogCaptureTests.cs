using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

[CollectionDefinition("Catalog capture console", DisableParallelization = true)]
public sealed class CatalogCaptureConsoleCollection { }

[Collection("Catalog capture console")]
public sealed class CatalogCaptureTests : IDisposable
{
    private const string Subject = "Fixture.Subject";
    private readonly string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "catalog-capture-" + Guid.NewGuid().ToString("N"))).FullName;
    private static CatalogIdentity Identity => new("fixture-resonite", "fixture-link", CatalogMapper.ClientPackageVersion,
        ApplyCatalog.CurrentMapperVersion, DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
    private static Link.TypeDefinition Type(string name) => new() { FullTypeName = name, Interfaces = [], IsInterface = true };
    private static Link.ComponentDefinition Component(string name) => new() { Type = Type(name), Members = new(), Methods = [] };
    private static CatalogCapture.Reader Reader(
        Func<string, Link.ComponentDefinitionData>? component = null,
        Func<string, Link.TypeDefinitionData>? type = null) => new(
        name => Task.FromResult(component?.Invoke(name) ?? new() { Success = true, Definition = Component(name) }),
        name => Task.FromResult(type?.Invoke(name) ?? new() { Success = true, Definition = Type(name) }),
        _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), () => true);

    private async Task<(int Exit, string Stdout, string Stderr, JsonElement Report, string Catalog, string Snapshot)> Run(
        IReadOnlyList<string> names,
        Func<IReadOnlyList<string>, CancellationToken, Task<CatalogSnapshot>> capture, bool json = true)
    {
        var namesFile = Path.Combine(directory, "names.json");
        var catalogFile = Path.Combine(directory, "catalog.json");
        var snapshotFile = Path.Combine(directory, "snapshot.json");
        File.WriteAllText(namesFile, JsonSerializer.Serialize(names));
        var args = new List<string> { "catalog", "capture", "--types", namesFile, "--output", catalogFile,
            "--snapshot", snapshotFile, "--backend", "link", "--url", "ws://localhost:65530" };
        if (json) args.Add("--json");
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        int exit;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            exit = await Program.RunAsync(args.ToArray(),
                _ => throw new InvalidOperationException("offline fake capture must not connect"),
                (_, requested, token) => capture(requested, token));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        var report = default(JsonElement);
        if (json)
        {
            using var document = JsonDocument.Parse(exit == 0 ? stdout.ToString() : stderr.ToString());
            report = document.RootElement.Clone();
        }
        return (exit, stdout.ToString(), stderr.ToString(), report, catalogFile, snapshotFile);
    }

    private static void Counts(JsonElement result, int requested, int used, int output)
    {
        Assert.Equal(requested, result.GetProperty("requestedTypes").GetInt32());
        Assert.Equal(used, result.GetProperty("acquisitionBudgetUsed").GetInt32());
        Assert.Equal(512, result.GetProperty("acquisitionBudgetLimit").GetInt32());
        Assert.Equal(output, result.GetProperty("outputTypes").GetInt32());
    }

    private static JsonElement Partial(JsonElement report)
    {
        Assert.False(report.GetProperty("ok").GetBoolean());
        var error = report.GetProperty("error");
        Assert.Equal("CATALOG_CAPTURE_INCOMPLETE", error.GetProperty("code").GetString());
        return error.GetProperty("context").GetProperty("result");
    }

    private static void Readable(string catalogFile, string snapshotFile)
    {
        var catalog = ApplyCatalog.Load(catalogFile);
        Assert.Null(catalog.UnavailableReason());
        Assert.Equal(catalog.ContentHash, CatalogMapper.Export(CatalogMapper.LoadSnapshot(snapshotFile)).ContentHash);
        Assert.NotEmpty(CatalogTypesGenerator.Generate(catalog).Text);
        // A partial acquisition is usable; ordinary document validation determines
        // whether the metadata required by a particular document is sufficient.
        var empty = new ApplyDocument("1", new("capture-fixture"), new("Root", null, null, null, null, "root"), []);
        Assert.Empty(ApplyCatalogValidator.Validate(empty, catalog));
    }

    [Fact]
    public async Task CompleteCaptureReturnsZeroAndShowsRawDuplicateInputCount()
    {
        var calls = 0;
        var reader = Reader(component: name => { calls++; return new() { Success = true, Definition = Component(name) }; });
        var run = await Run([Subject, Subject], (names, token) => CatalogCapture.ReadAsync(names, reader, () => Task.FromResult(Identity), token));
        Assert.Equal(0, run.Exit); Assert.Equal(1, calls); Assert.Empty(run.Stderr);
        Assert.True(run.Report.GetProperty("ok").GetBoolean());
        Counts(run.Report.GetProperty("data"), 2, 1, 1);
        Readable(run.Catalog, run.Snapshot);
    }

    [Theory]
    [InlineData("component", false)]
    [InlineData("type", false)]
    [InlineData("component", true)]
    [InlineData("type", true)]
    public async Task MissingRequestedKindReturnsSixEvenIfFakeOmitsFailureEvidence(string kind, bool omitFailure)
    {
        var reader = Reader(
            component: name => kind == "component" ? new() { Success = false, ErrorInfo = "requested component rejected" }
                : new() { Success = true, Definition = Component(name) },
            type: name => kind == "type" ? new() { Success = false, ErrorInfo = "requested type rejected" }
                : new() { Success = true, Definition = Type(name) });
        var run = await Run([Subject], async (names, token) =>
        {
            var snapshot = await CatalogCapture.ReadAsync(names, reader, () => Task.FromResult(Identity), token);
            if (!omitFailure) return snapshot;
            var content = snapshot.Content with { AcquisitionFailures = [] };
            return snapshot with { Content = content, ContentHash = CatalogMapper.SnapshotHash(content) };
        });
        Assert.Equal(6, run.Exit); Assert.Empty(run.Stdout);
        Counts(Partial(run.Report), 1, 1, 1);
        Assert.Equal(omitFailure ? 0 : 1, Partial(run.Report).GetProperty("acquisitionFailures").GetArrayLength());
        Readable(run.Catalog, run.Snapshot);
    }

    [Theory]
    [InlineData("dependency rejected")]
    [InlineData("time-limit: upstream dependency rejected without local truncation")]
    [InlineData("type-limit: upstream dependency rejected without local truncation")]
    public async Task DependencyFailuresAndAliasesCountAttemptsBeforeNormalizationWithoutFailingRequestedCapture(string reason)
    {
        var component = Component(Subject);
        foreach (var name in new[] { "Fixture.Alias", "Fixture.Canonical", "Fixture.Bad" })
            component.Members[name] = new Link.ReferenceDefinition { TargetType = new() { Type = name } };
        var reader = Reader(_ => new() { Success = true, Definition = component }, name => name == "Fixture.Bad"
            ? new() { Success = false, ErrorInfo = reason }
            : new() { Success = true, Definition = Type(name == "Fixture.Alias" ? "Fixture.Canonical" : name) });
        var run = await Run([Subject, Subject], (names, token) => CatalogCapture.ReadAsync(names, reader, () => Task.FromResult(Identity), token));
        Assert.Equal(0, run.Exit);
        var result = run.Report.GetProperty("data");
        Counts(result, 2, 4, 2);
        Assert.Single(result.GetProperty("acquisitionFailures").EnumerateArray());
        Assert.Equal("Fixture.Canonical", ApplyCatalog.Load(run.Catalog).Content.Aliases["Fixture.Alias"]);
        Readable(run.Catalog, run.Snapshot);
    }

    [Fact]
    public async Task AliasCannotHideAFailedExplicitRequestedType()
    {
        var reader = Reader(type: name => name == Subject
            ? new() { Success = false, ErrorInfo = "requested type rejected" }
            : new() { Success = true, Definition = Type(Subject) });
        var run = await Run([Subject, "Fixture.Alias"], (names, token) => CatalogCapture.ReadAsync(names, reader, () => Task.FromResult(Identity), token));
        Assert.Equal(6, run.Exit);
        Counts(Partial(run.Report), 2, 2, 1);
        Readable(run.Catalog, run.Snapshot);
    }

    private static CatalogCapture.Reader BudgetReader()
    {
        var component = Component(Subject);
        component.Members["Scalar"] = new Link.FieldDefinition { ValueType = new() { Type = "float" } };
        for (var i = 0; i < 520; i++)
            component.Members["M" + i] = new Link.ReferenceDefinition { TargetType = new() { Type = "Fixture.T" + i } };
        component.Members["Tuple"] = new Link.FieldDefinition { ValueType = new() { Type = "float4" } };
        return Reader(_ => new() { Success = true, Definition = component });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BudgetTruncationSaves513MappedTypesFrom512AttemptedNamesAndReturnsSix(bool json)
    {
        var run = await Run([Subject], (names, token) => CatalogCapture.ReadAsync(names, BudgetReader(), () => Task.FromResult(Identity), token), json);
        Assert.Equal(6, run.Exit); Assert.Empty(run.Stdout);
        // The field's known float4 wire representation synthesizes one extra output
        // definition after its independent acquisition was refused by the budget.
        if (json) Counts(Partial(run.Report), 1, 512, 513);
        else
        {
            Assert.Contains("requested types: 1", run.Stderr);
            Assert.Contains("acquisition budget used: 512/512 unique attempted names", run.Stderr);
            Assert.Contains("output types: 513", run.Stderr);
            Assert.Contains("catalog: " + run.Catalog, run.Stderr);
            Assert.Contains("snapshot: " + run.Snapshot, run.Stderr);
            Assert.Contains("catalog hash: ", run.Stderr);
            Assert.Contains("unverified float4 (type): type-limit:", run.Stderr);
        }
        var snapshot = CatalogMapper.LoadSnapshot(run.Snapshot);
        Assert.True(snapshot.AcquisitionTruncated); Assert.Equal(512, snapshot.AcquisitionBudgetUsed);
        Assert.Contains(snapshot.Content.AcquisitionFailures!, failure => failure.Type == "float4" && failure.Reason.StartsWith("type-limit:"));
        Readable(run.Catalog, run.Snapshot);
    }

    [Fact]
    public async Task CompleteTextOutputShowsAllCounts()
    {
        var run = await Run([Subject, Subject], (names, token) => CatalogCapture.ReadAsync(names, Reader(), () => Task.FromResult(Identity), token), false);
        Assert.Equal(0, run.Exit);
        Assert.Contains("requested types: 2", run.Stdout);
        Assert.Contains("acquisition budget used: 1/512 unique attempted names", run.Stdout);
        Assert.Contains("output types: 1", run.Stdout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquisitionOrFinalIdentityDeadlineSavesReadablePartialCatalogAndReturnsSix(bool finalIdentity)
    {
        using var deadline = new CancellationTokenSource();
        var reader = finalIdentity ? Reader() : new CatalogCapture.Reader(_ =>
            { deadline.Cancel(); return new TaskCompletionSource<Link.ComponentDefinitionData>().Task; },
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), () => true);
        var identities = 0;
        var run = await Run([Subject], (names, token) => CatalogCapture.ReadAsync(names, reader, () =>
        {
            if (++identities == 1) return Task.FromResult(Identity);
            deadline.Cancel(); return new TaskCompletionSource<CatalogIdentity>().Task;
        }, token, deadline.Token));
        Assert.Equal(6, run.Exit);
        Counts(Partial(run.Report), 1, 1, finalIdentity ? 1 : 0);
        var snapshot = CatalogMapper.LoadSnapshot(run.Snapshot);
        Assert.True(snapshot.AcquisitionTruncated); Assert.Equal("live", snapshot.Source);
        Assert.Contains(snapshot.Content.AcquisitionFailures!, failure => failure.Request == "session" && failure.Reason.StartsWith("time-limit:"));
        Readable(run.Catalog, run.Snapshot);
    }

    [Theory]
    [InlineData("changed", 6, "APPLY_CATALOG_UNAVAILABLE")]
    [InlineData("disconnected", 6, "APPLY_CATALOG_UNAVAILABLE")]
    [InlineData("cancelled", 8, "COMMAND_TIMEOUT")]
    public async Task SessionChangeDisconnectAndCallerCancellationDoNotBecomeNormalPartialCapture(string mode, int exit, string code)
    {
        var connected = true; var identities = 0;
        using var caller = new CancellationTokenSource();
        var defaultReader = Reader();
        var reader = defaultReader with { Connected = () => connected };
        var run = await Run([Subject], (names, _) => CatalogCapture.ReadAsync(names, reader, () =>
        {
            if (++identities == 1) return Task.FromResult(Identity);
            if (mode == "cancelled") { caller.Cancel(); return new TaskCompletionSource<CatalogIdentity>().Task; }
            if (mode == "disconnected") connected = false;
            return Task.FromResult(mode == "changed" ? Identity with { ResoniteVersion = "changed" } : Identity);
        }, caller.Token));
        Assert.Equal(exit, run.Exit);
        Assert.Equal(code, run.Report.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(run.Catalog)); Assert.False(File.Exists(run.Snapshot));
    }

    [Fact]
    public async Task LegacySnapshotWithoutBudgetMetadataRemainsUsableAndReportsUnknown()
    {
        var run = await Run([Subject], async (names, token) =>
        {
            var snapshot = await CatalogCapture.ReadAsync(names, Reader(), () => Task.FromResult(Identity), token);
            var legacyJson = JsonSerializer.SerializeToNode(snapshot, ReflectionMetadataCache.Json)!.AsObject();
            legacyJson.Remove("acquisitionBudgetUsed"); legacyJson.Remove("acquisitionTruncated");
            return JsonSerializer.Deserialize<CatalogSnapshot>(legacyJson.ToJsonString(), ReflectionMetadataCache.Json)!;
        });
        Assert.Equal(0, run.Exit);
        Assert.Equal("unknown", run.Report.GetProperty("data").GetProperty("acquisitionBudgetUsed").GetString());
        Assert.Null(CatalogMapper.LoadSnapshot(run.Snapshot).AcquisitionBudgetUsed);
        Readable(run.Catalog, run.Snapshot);
    }

    public void Dispose() => Directory.Delete(directory, true);
}

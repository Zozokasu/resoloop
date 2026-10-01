using System.Text.Json;
using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class CatalogLiveFixTests
{
    private const string Sphere = "[FrooxEngine]FrooxEngine.SphereMesh";
    private const string Pbs = "[FrooxEngine]FrooxEngine.PBS_Metallic";
    private const string Provider = "[FrooxEngine]FrooxEngine.IAssetProvider";
    private const string MeshProvider = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Mesh>";
    private const string MaterialProvider = "[FrooxEngine]FrooxEngine.IAssetProvider<[FrooxEngine]FrooxEngine.Material>";
    private static CatalogIdentity Identity => new("2026.9.18.82", "0.13.1.0", "0.13.1", ApplyCatalog.CurrentMapperVersion,
        DateTimeOffset.Parse("2026-10-01T22:53:51.7540969+00:00"));
    private static CatalogSnapshotContent Live()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ResoLoop.slnx"))) directory = directory.Parent;
        return JsonSerializer.Deserialize<CatalogSnapshotContent>(File.ReadAllText(Path.Combine(directory!.FullName,
            "tests", "RLoop.Tests", "fixtures", "catalog-live-l1", "metadata.live-excerpt.json")), ReflectionMetadataCache.Json)!;
    }
    private static CatalogSnapshot Snapshot(CatalogSnapshotContent content) =>
        new(Identity, Identity, "live", true, CatalogMapper.SnapshotHash(content), content);
    private static Link.TypeDefinition Type(string name, string? parent = null, bool isInterface = false) =>
        new() { FullTypeName = name, Interfaces = [], IsInterface = isInterface,
            BaseType = parent is null ? null : new() { Type = parent } };
    private static Link.ComponentDefinition Component(string name) => new() { Type = Type(name), Members = new(), Methods = [] };
    private static ApplyDocument ReferenceDocument(string source) => new("1", new("fixture"),
        new("Root", null, null, null, null, "root"),
        [new("Synthetic.Holder", new Dictionary<string, JsonElement> { ["Value"] = JsonSerializer.SerializeToElement("$component:source") }, "holder"),
            new(source, new Dictionary<string, JsonElement>(), "source")]);
    private static IReadOnlyList<ApplyValidationIssue> ReferenceIssues(ApplyCatalog catalog, string source, string target)
    {
        var holder = new CatalogType("Synthetic.Holder", true, false, null, [], Members:
            new Dictionary<string, CatalogMember> { ["Value"] = new("reference", TargetType: target) }, MembersComplete: true);
        var content = catalog.Content with { Types = [.. catalog.Content.Types, holder] };
        return ApplyCatalogValidator.Validate(ReferenceDocument(source), catalog with { Content = content, ContentHash = ApplyCatalog.Hash(content) });
    }

    [Fact]
    public void IndependentTypeEdgesSurviveFlattenedComponentAndProveMeshReference()
    {
        var original = Live();
        var sphere = Type(Sphere, "Synthetic.MeshBase");
        sphere.Interfaces = [new() { Type = "Synthetic.Marker" }];
        var meshBase = Type("Synthetic.MeshBase", isInterface: true);
        meshBase.Interfaces = [new() { Type = MeshProvider }];
        var catalog = CatalogMapper.Export(Snapshot(original with { Types = [.. original.Types, sphere, meshBase, Type("Synthetic.Marker", isInterface: true)] }));
        Assert.Equal("Synthetic.MeshBase", catalog.Find(Sphere)!.BaseType);
        Assert.Equal(new[] { "Synthetic.Marker" }, catalog.Find(Sphere)!.Interfaces);
        Assert.Equal(new[] { MeshProvider }, catalog.Find("Synthetic.MeshBase")!.Interfaces);
        Assert.False(catalog.Find(Sphere)!.ClosureComplete); // Flattened and independent definitions differ.
        Assert.Empty(ReferenceIssues(catalog, Sphere, MeshProvider));
        var mesh = catalog.Find("[FrooxEngine]FrooxEngine.MeshRenderer")!.Members!["Mesh"];
        Assert.Equal(MeshProvider, mesh.TargetType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrFailedIndependentTypeReturnsUnavailableForZeroEdgeNonGenericCounterexample(bool failed)
    {
        var original = Live();
        var catalog = CatalogMapper.Export(Snapshot(original with { AcquisitionFailures = failed
            ? [new(Sphere, "type", "server rejected independent definition")] : null }));
        var sphere = catalog.Find(Sphere)!;
        Assert.False(sphere.ClosureComplete);
        Assert.Null(sphere.BaseType);
        Assert.Empty(sphere.Interfaces);
        var issue = Assert.Single(ReferenceIssues(catalog, Sphere, Provider));
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", issue.Code);
        Assert.DoesNotContain(ReferenceIssues(catalog, Sphere, MeshProvider), i => i.Code == "APPLY_REFERENCE_TYPE_MISMATCH");
    }

    [Fact]
    public void PbsMetallicMaterialReferenceStillProvenByLiveEdges()
    {
        var catalog = CatalogMapper.Export(Snapshot(Live()));
        Assert.False(catalog.Find(Pbs)!.ClosureComplete);
        Assert.Empty(ReferenceIssues(catalog, Pbs, MaterialProvider));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaseExpectedButMissingIsNeverComplete(bool sync)
    {
        // Even interface metadata cannot override an explicit "base exists" marker.
        var definition = Type("Synthetic.Subject", isInterface: true);
        var component = Component(definition.FullTypeName); component.Type = definition; component.BaseTypeIsComponent = true;
        var embedded = new Link.SyncObjectDefinition { Type = definition, Members = new(), Methods = [], BaseTypeIsSyncObject = true };
        var content = new CatalogSnapshotContent(sync ? [] : [component], [definition], sync ? [embedded] : [], new Dictionary<string, string>());
        Assert.False(CatalogMapper.Export(Snapshot(content)).Find(definition.FullTypeName)!.ClosureComplete);
    }

    [Fact]
    public void IndependentSyncObjectEdgesSurviveMemberMerge()
    {
        var independent = Type("Synthetic.Sync", "Synthetic.Base");
        var embedded = new Link.SyncObjectDefinition { Type = Type("Synthetic.Sync"), Members = new()
            { ["Amount"] = new Link.FieldDefinition { ValueType = new() { Type = "float" } } }, Methods = [], BaseTypeIsSyncObject = true };
        var content = new CatalogSnapshotContent([], [independent], [embedded], new Dictionary<string, string>());
        var mapped = CatalogMapper.Export(Snapshot(content)).Find("Synthetic.Sync")!;
        Assert.Equal("Synthetic.Base", mapped.BaseType);
        Assert.Equal("float", mapped.Members!["Amount"].ValueType);
        Assert.False(mapped.ClosureComplete);
    }

    [Fact]
    public void InterfaceListAloneDoesNotProveClassClosureButObservedRootAndInterfaceCan()
    {
        var content = new CatalogSnapshotContent([], [Type("Synthetic.Unknown"), Type("System.Object"), Type("Synthetic.Interface", isInterface: true)], [], new Dictionary<string, string>());
        var catalog = CatalogMapper.Export(Snapshot(content));
        Assert.False(catalog.Find("Synthetic.Unknown")!.ClosureComplete);
        Assert.True(catalog.Find("System.Object")!.ClosureComplete);
        Assert.True(catalog.Find("Synthetic.Interface")!.ClosureComplete);
    }

    [Theory]
    [InlineData("agree")]
    [InlineData("disagree")]
    [InlineData("failed")]
    public void ClosureOnlyCertifiesNegativeWhenIndependentEvidenceAgreesAndSucceeded(string mode)
    {
        var acquired = Type("Synthetic.Source", "System.Object");
        var component = Component(acquired.FullTypeName);
        component.Type = Type(acquired.FullTypeName, mode == "disagree" ? "Synthetic.OtherBase" : "System.Object");
        var content = new CatalogSnapshotContent([component], [acquired, Type("System.Object"), Type("Synthetic.Target", isInterface: true)],
            [], new Dictionary<string, string>(), AcquisitionFailures: mode == "failed" ? [new(acquired.FullTypeName, "type", "incomplete read")] : null);
        var catalog = CatalogMapper.Export(Snapshot(content));
        Assert.Equal("System.Object", catalog.Find(acquired.FullTypeName)!.BaseType);
        Assert.Equal(mode == "agree", catalog.Find(acquired.FullTypeName)!.ClosureComplete);
        Assert.Equal(mode == "agree" ? "APPLY_REFERENCE_TYPE_MISMATCH" : "APPLY_CATALOG_UNAVAILABLE",
            Assert.Single(ReferenceIssues(catalog, acquired.FullTypeName, "Synthetic.Target")).Code);
    }

    [Fact]
    public void GenericNegativeGuardRemainsUnavailable()
    {
        var source = Type("Synthetic.Generic<int>", "System.Object"); source.IsGenericType = true;
        var component = Component(source.FullTypeName); component.Type = source;
        var content = new CatalogSnapshotContent([component], [source, Type("System.Object"), Type("Synthetic.Unrelated", isInterface: true)], [], new Dictionary<string, string>());
        var catalog = CatalogMapper.Export(Snapshot(content));
        Assert.True(catalog.Find(source.FullTypeName)!.ClosureComplete);
        Assert.Equal("APPLY_CATALOG_UNAVAILABLE", Assert.Single(ReferenceIssues(catalog, source.FullTypeName, "Synthetic.Unrelated")).Code);
    }

    [Fact]
    public async Task CaptureSeparatesKindsAndRetainsFailuresThroughSnapshotAndCatalog()
    {
        var calls = new List<(string Name, string Kind)>();
        var reader = new CatalogCapture.Reader(
            name => { calls.Add((name, "component")); if (name == "Synthetic.BadComponent") return Task.FromResult(new Link.ComponentDefinitionData { Success = false, ErrorInfo = "component rejected" });
                var c = Component(name); c.Members["Embedded"] = new Link.SyncObjectMemberDefinition { Type = new() { Type = "Synthetic.Sync" } };
                return Task.FromResult(new Link.ComponentDefinitionData { Success = true, Definition = c }); },
            name => { calls.Add((name, "type")); if (name == "Synthetic.Subject") throw new InvalidOperationException("type exploded");
                if (name == "Synthetic.Sync") return Task.FromResult(new Link.TypeDefinitionData { Success = false, ErrorInfo = "type rejected" });
                var t = Type(name); t.IsEnum = true;
                return Task.FromResult(new Link.TypeDefinitionData { Success = true, Definition = t }); },
            name => { calls.Add((name, "syncObject")); throw new InvalidOperationException("sync exploded"); },
            name => { calls.Add((name, "enum")); return Task.FromResult(new Link.EnumDefinitionData { Success = false, ErrorInfo = "enum rejected" }); }, () => true);
        var snapshot = await CatalogCapture.AcquireAsync(Identity, ["Synthetic.Subject", "Synthetic.BadComponent", "Synthetic.Subject"], reader);
        Assert.Equal(1, calls.Count(c => c == ("Synthetic.Subject", "component")));
        Assert.Equal(1, calls.Count(c => c == ("Synthetic.Subject", "type")));
        Assert.Contains(("Synthetic.Sync", "type"), calls);
        Assert.Contains(("Synthetic.Sync", "syncObject"), calls);
        Assert.Equal(5, snapshot.Content.AcquisitionFailures!.Count);
        Assert.Contains(snapshot.Content.AcquisitionFailures, f => f.Type == "Synthetic.Subject" && f.Request == "type" && f.Reason.Contains("type exploded"));
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "l1-capture-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var file = Path.Combine(directory, "snapshot.json"); CatalogMapper.SaveSnapshot(snapshot, file);
            var saved = CatalogMapper.LoadSnapshot(file);
            Assert.Equal(snapshot.Content.AcquisitionFailures, saved.Content.AcquisitionFailures);
            var catalog = CatalogMapper.Export(saved);
            catalog.Save(Path.Combine(directory, "catalog.json")); catalog = ApplyCatalog.Load(Path.Combine(directory, "catalog.json"));
            Assert.Equal(snapshot.Content.AcquisitionFailures, catalog.Content.AcquisitionFailures);
            Assert.Equal("2", catalog.FormatVersion); Assert.Null(catalog.UnavailableReason());
            Assert.False(catalog.Find("Synthetic.Subject")!.ClosureComplete);
            var corrupted = catalog.Content with { AcquisitionFailures = [] };
            Assert.NotNull((catalog with { Content = corrupted }).UnavailableReason());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureRecordsTypeOrTimeLimitWithoutExceedingBudget(bool time)
    {
        var reads = 0;
        var c = Component("Synthetic.Subject");
        for (var i = 0; i < 520; i++) c.Members["M" + i] = new Link.ReferenceDefinition { TargetType = new() { Type = "Synthetic.T" + i } };
        var reader = new CatalogCapture.Reader(_ => Task.FromResult(new Link.ComponentDefinitionData { Success = true, Definition = c }),
            name => { reads++; return Task.FromResult(new Link.TypeDefinitionData { Success = true, Definition = Type(name) }); },
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), () => true);
        using var deadline = new CancellationTokenSource(); if (time) deadline.Cancel();
        var snapshot = await CatalogCapture.AcquireAsync(Identity, ["Synthetic.Subject"], reader, deadline: deadline.Token);
        Assert.Equal(time ? 0 : 512, reads);
        Assert.Contains(snapshot.Content.AcquisitionFailures!, f => f.Reason.StartsWith(time ? "time-limit:" : "type-limit:"));
    }

    [Fact]
    public async Task CaptureConnectionFailureAndCallerCancellationAbort()
    {
        var reader = new CatalogCapture.Reader(_ => throw new IOException("disconnected"), _ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), () => false);
        await Assert.ThrowsAsync<IOException>(() => CatalogCapture.AcquireAsync(Identity, ["Synthetic.Subject"], reader));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CatalogCapture.AcquireAsync(Identity, ["Synthetic.Subject"], reader, ct.Token));
    }

    [Fact]
    public async Task CaptureSuccessfulSyncObjectAlsoAcquiresItsIndependentTypeOnce()
    {
        var calls = new List<(string Name, string Kind)>();
        var component = Component("Synthetic.Subject");
        component.Members["One"] = new Link.SyncObjectMemberDefinition { Type = new() { Type = "Synthetic.Sync" } };
        component.Members["Two"] = new Link.SyncObjectMemberDefinition { Type = new() { Type = "Synthetic.Sync" } };
        var reader = new CatalogCapture.Reader(
            name => Task.FromResult(new Link.ComponentDefinitionData { Success = true, Definition = component }),
            name => { calls.Add((name, "type")); return Task.FromResult(new Link.TypeDefinitionData { Success = true, Definition = Type(name) }); },
            name => { calls.Add((name, "syncObject")); return Task.FromResult(new Link.SyncObjectDefinitionData { Success = true,
                Definition = new() { Type = Type(name), Members = new(), Methods = [] } }); },
            _ => throw new NotSupportedException(), () => true);
        var snapshot = await CatalogCapture.AcquireAsync(Identity, ["Synthetic.Subject"], reader);
        Assert.Empty(snapshot.Content.AcquisitionFailures!);
        Assert.Equal(1, calls.Count(c => c == ("Synthetic.Sync", "type")));
        Assert.Equal(1, calls.Count(c => c == ("Synthetic.Sync", "syncObject")));
        Assert.Contains(snapshot.Content.Types, t => t.FullTypeName == "Synthetic.Sync");
        Assert.Single(snapshot.Content.SyncObjects);
    }

    [Fact]
    public async Task CaptureDeadlineDuringAReadRecordsCurrentAndPendingRequests()
    {
        using var deadline = new CancellationTokenSource();
        var reader = new CatalogCapture.Reader(_ => { deadline.Cancel(); return new TaskCompletionSource<Link.ComponentDefinitionData>().Task; },
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), _ => throw new NotSupportedException(), () => true);
        var snapshot = await CatalogCapture.AcquireAsync(Identity, ["Synthetic.Subject"], reader, deadline: deadline.Token);
        Assert.Equal(new[] { "component", "type" }, snapshot.Content.AcquisitionFailures!.Select(f => f.Request));
        Assert.All(snapshot.Content.AcquisitionFailures!, f => Assert.StartsWith("time-limit:", f.Reason));
    }
}

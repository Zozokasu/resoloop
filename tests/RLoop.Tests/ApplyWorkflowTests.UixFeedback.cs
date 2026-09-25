using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("{\"source\":\"texture.png\"}", "ASSET_KIND_MISSING")]
    [InlineData("{\"kind\":null,\"source\":\"texture.png\"}", "ASSET_KIND_MISSING")]
    [InlineData("null", "ASSET_INVALID")]
    public async Task InvalidAssetsReturnValidationIssuesBeforeMutation(string asset, string code)
    {
        var path = Path.Combine(_root, "invalid-asset.json");
        File.WriteAllText(path, "{\"schemaVersion\":\"1\",\"ownership\":{\"key\":\"assets\"},\"slot\":{\"key\":\"root\",\"name\":\"Managed\"},\"assets\":{\"sample\":" + asset + "}}");
        var document = ApplyDocument.Load(path);
        var report = await ApplyDocumentValidator.ValidateAsync(document);
        Assert.Contains(report.Issues, issue => issue.Code == code);
        var client = new FakeResoniteClient();
        await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task DuplicateSiblingNamesFailBeforeCreatingAnUnresolvableTree()
    {
        var path = Path.Combine(_root, "duplicate-names.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"duplicates"},"slot":{"key":"root","name":"Managed"},
             "children":[{"slot":{"key":"first","name":"Option"}},{"slot":{"key":"second","name":"Option"}}]}
            """);
        var document = ApplyDocument.Load(path);
        Assert.Contains((await ApplyDocumentValidator.ValidateAsync(document)).Issues, issue => issue.Code == "APPLY_SIBLING_NAME_DUPLICATE");
        var client = new FakeResoniteClient();
        await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactNamesSurviveReconnectionAndPruneWithoutPathCollisions(bool reconnect)
    {
        var path = Path.Combine(_root, "exact-names.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"exact"},"slot":{"key":"root","name":"Managed"},
             "children":[{"slot":{"key":"flat","name":"A/B"},"components":[{"key":"flat-target","type":"Test.Target","fields":{"Enabled":true}}]},
              {"slot":{"key":"a","name":"A"},"children":[{"slot":{"key":"b","name":"B"}}]},
              {"slot":{"key":"space","name":" Label "}}, {"slot":{"key":"root-child","name":"Root"},"children":[{"slot":{"key":"nested","name":"Nested"}}]}]}
            """);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "exact.state.json"));
        var document = ApplyDocument.Load(path);
        await service.ApplyAsync(document, options);
        var retained = await service.ResolveSlotSelectorAsync("$slot:flat", options.StateFile);
        var component = await service.ResolveComponentSelectorAsync("$component:flat-target", options.StateFile);
        if (reconnect) client.SessionId = "session-2";
        Assert.Equal(retained, await service.ResolveSlotIdAsync("path:[\"Root\",\"Managed\",\"A/B\"]"));
        Assert.Equal(retained, await service.ResolveSlotSelectorAsync("$slot:flat", options.StateFile));
        Assert.Equal(component, await service.ResolveComponentSelectorAsync("$component:flat-target", options.StateFile));
        Assert.NotEmpty(await service.ResolveSlotIdAsync("Root/Managed/ Label "));
        Assert.NotEmpty(await service.ResolveSlotSelectorAsync("$slot:nested", options.StateFile));
        client.ResetWriteCounts();
        await service.ApplyAsync(document, options);
        Assert.Equal(0, client.Writes);
        var changed = JsonNode.Parse(File.ReadAllText(path))!;
        ((JsonArray)changed["children"]!).RemoveAt(1);
        File.WriteAllText(path, changed.ToJsonString());
        await service.ApplyAsync(ApplyDocument.Load(path), options with { Prune = true, ConfirmDeletes = true });
        Assert.Equal(retained, await service.ResolveSlotSelectorAsync("$slot:flat", options.StateFile));
        Assert.Contains(Assert.Single(client.Root.Children).Children, child => child.Id == retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewSlotKeyDoesNotReuseAStaleSlotWithTheSameName(bool reconnect)
    {
        var path = Path.Combine(_root, "slot-replacement.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"replace-slot"},"slot":{"key":"root","name":"Managed"},
             "children":[{"slot":{"key":"old","name":"Background"}}]}
            """);
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "replace-slot.state.json"), Prune: true, ConfirmDeletes: true);
        await service.ApplyAsync(ApplyDocument.Load(path), options);
        var oldId = await service.ResolveSlotSelectorAsync("$slot:old", options.StateFile);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"old\"", "\"new\""));
        if (reconnect) client.SessionId = "session-2";
        await service.ApplyAsync(ApplyDocument.Load(path), options);
        var current = Assert.Single(Assert.Single(client.Root.Children).Children);
        Assert.NotEqual(oldId, current.Id);
        Assert.Equal(current.Id, await service.ResolveSlotSelectorAsync("$slot:new", options.StateFile));
        client.ResetWriteCounts();
        await service.ApplyAsync(ApplyDocument.Load(path), options);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ConcurrentApplyFailsBeforeWorldMutationWhileReadersRemainAllowed()
    {
        var document = Document("writer-lock", "[]");
        var options = new ApplyOptions(Path.Combine(_root, "writer.state.json"));
        var client = new FakeResoniteClient();
        using (CheckpointFiles.AcquireWriter(options.StateFile!))
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(document, options));
            Assert.Equal("APPLY_STATE_BUSY", error.Code);
            Assert.Equal(0, client.Writes);
        }
        await new WorldService(client).ApplyAsync(document, options);
    }

    [Fact]
    public async Task LegacyCheckpointUpgradesWithObservedNameSegments()
    {
        var document = Document("legacy-state", "[]");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "legacy.state.json"));
        await service.ApplyAsync(document, options);
        var state = JsonNode.Parse(File.ReadAllText(options.StateFile!))!;
        state["schemaVersion"] = 1;
        foreach (var slot in ((JsonObject)state["slots"]!).Select(pair => (JsonObject)pair.Value!)) slot.Remove("pathSegments");
        File.WriteAllText(options.StateFile!, state.ToJsonString());
        client.SessionId = "session-2";
        client.ResetWriteCounts();
        await service.ApplyAsync(document, options);
        Assert.Equal(0, client.Writes);
        var upgraded = JsonNode.Parse(File.ReadAllText(options.StateFile!))!;
        Assert.Equal(2, upgraded["schemaVersion"]!.GetValue<int>());
        Assert.Equal(new[] { "Root", "Managed" }, ((JsonArray)upgraded["slots"]!["root"]!["pathSegments"]!).Select(value => value!.GetValue<string>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementDriverReconcilesAfterPruneAndRejectsAnUnreleasedOwner(bool prune)
    {
        var initial = Document("driver-swap", """
            [{"key":"target","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"old","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}]
            """);
        var client = new FakeResoniteClient(initial);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "drivers.state.json"));
        await service.ApplyAsync(initial, options);
        client.TargetClaimedBy = await service.ResolveComponentSelectorAsync("$component:old", options.StateFile);
        var desired = Document("driver-swap", """
            [{"key":"target","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"replacement","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}]
            """);
        if (!prune)
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
            Assert.Equal("APPLY_REFERENCE_NOT_RETAINED", error.Code);
        }
        await service.ApplyAsync(desired, options with { Prune = true, ConfirmDeletes = true });
        var replacement = await service.ResolveComponentSelectorAsync("$component:replacement", options.StateFile);
        Assert.NotNull((await client.GetComponentAsync(replacement)).Members["Target"].TargetId);
        client.ResetWriteCounts();
        await service.ApplyAsync(desired, options);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task CorruptSlotAliasCannotPruneAnActiveSlot()
    {
        var document = Document("slot-alias", "[]");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "slot-alias.state.json"));
        var json = JsonNode.Parse(File.ReadAllText(document.SourcePath!))!;
        json["children"] = JsonNode.Parse("""[{"slot":{"key":"kept","name":"Kept"}}]""");
        File.WriteAllText(document.SourcePath!, json.ToJsonString());
        document = ApplyDocument.Load(document.SourcePath!);
        await service.ApplyAsync(document, options);
        var state = JsonNode.Parse(File.ReadAllText(options.StateFile!))!;
        state["slots"]!["obsolete"] = state["slots"]!["kept"]!.DeepClone();
        File.WriteAllText(options.StateFile!, state.ToJsonString());
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, options with { Prune = true, ConfirmDeletes = true }));
        Assert.Equal("APPLY_SLOT_OWNERSHIP_CONFLICT", error.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task CheckpointReaderRecoversAfterTemporarySharingViolation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "temporarily-locked.json");
        const string content = "{\"sequence\":42}";
        CheckpointFiles.Write(path, content);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> read;
        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            read = Task.Run(() =>
            {
                started.SetResult();
                return CheckpointFiles.Read(path);
            });
            await started.Task;
            await Task.Delay(300);
        }
        Assert.Equal(content, await read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CheckpointReaderReportsPersistentSharingViolation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "persistently-locked.json");
        CheckpointFiles.Write(path, "{}");
        using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var read = Task.Run(() => Assert.Throws<IOException>(() => CheckpointFiles.Read(path)));
        var error = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(32, error.HResult & 0xffff);
    }

    [Fact]
    public async Task ConcurrentCheckpointReadersAlwaysSeeACompleteSnapshot()
    {
        var path = Path.Combine(_root, "snapshot.json");
        string Snapshot(int value) => JsonSerializer.Serialize(new { schemaVersion = 2, sequence = value, padding = new string('x', 100000) });
        CheckpointFiles.Write(path, Snapshot(0));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                using var snapshot = JsonDocument.Parse(CheckpointFiles.Read(path));
                Assert.Equal(100000, snapshot.RootElement.GetProperty("padding").GetString()!.Length);
            }
        })).ToArray();
        for (var i = 1; i <= 100; i++) CheckpointFiles.Write(path, Snapshot(i));
        await Task.WhenAll(readers);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task CheckpointReaderRecoversWhenSharingViolationBecomesMissingPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "reappearing.json");
        CheckpointFiles.Write(path, "{\"sequence\":41}");
        using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = Task.Run(() =>
        {
            started.SetResult();
            return CheckpointFiles.Read(path);
        });
        await started.Task;
        await Task.Delay(300);
        File.Move(path, path + ".old"); // Allowed by Delete sharing while reads still fail.
        await Task.Delay(300); // Missing-path errors follow more than five sharing attempts.
        CheckpointFiles.Write(path, "{\"sequence\":42}");
        Assert.Equal("{\"sequence\":42}", await read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CheckpointReaderReportsPersistentlyMissingSnapshot()
    {
        var path = Path.Combine(_root, "never-created.json");
        var read = Task.Run(() => Assert.Throws<FileNotFoundException>(() => CheckpointFiles.Read(path)));
        await read.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task ReferencedComponentMoveClearsOnlyDeletedTargetBeforeUpdatingReference()
    {
        var (state, initialPath, desiredPath) = RelocationReferenceDocuments("move-reference");
        var client = new FakeResoniteClient(ApplyDocument.Load(initialPath));
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        var managedRoot = client.Root.Children.Single();
        var source = managedRoot.Children.Single(slot => slot.Name == "Source");
        var oldTarget = source.Components.Single(component => component.Type == "Test.Target");
        var watcher = source.Components.Single(component => component.Type == "Test.Source");
        var watcherId = watcher.Id;

        var first = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));

        Assert.DoesNotContain(source.Components, component => component.Id == oldTarget.Id);
        var destination = managedRoot.Children.Single(slot => slot.Name == "Destination");
        var replacement = Assert.Single(destination.Components);
        var observedWatcher = await client.GetComponentAsync(watcherId);
        Assert.NotEqual(oldTarget.Id, replacement.Id);
        Assert.Equal(replacement.Members["Enabled"].Id, observedWatcher.Members["Target"].TargetId);
        Assert.Equal(1, first.ComponentsDeleted);

        var diff = await service.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        Assert.DoesNotContain(diff.Operations, operation => operation.Action is "create" or "update" or "relocate" or "delete");
        client.ResetWriteCounts();
        var second = await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state));
        Assert.Equal(0, second.ComponentsAdded);
        Assert.Equal(0, second.ComponentsUpdated);
        Assert.Equal(0, second.ComponentsDeleted);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("other-deleted-member")]
    public async Task ReferencedComponentMoveStillRejectsExternalRetargetDuringDelete(string drift)
    {
        var (state, initialPath, desiredPath) = RelocationReferenceDocuments("move-reference-drift");
        var client = new FakeResoniteClient(ApplyDocument.Load(initialPath));
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        var managedRoot = client.Root.Children.Single();
        var source = managedRoot.Children.Single(slot => slot.Name == "Source");
        var oldTarget = source.Components.Single(component => component.Type == "Test.Target");
        var watcher = source.Components.Single(component => component.Type == "Test.Source");
        var externalTarget = drift == "other-deleted-member" ? oldTarget.Members["Other"].Id : "external-target-id";
        client.BeforeDeletionObservation = id =>
        {
            if (id == source.Id) client.RetargetReference(watcher.Id, "Target", externalTarget);
        };

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state)));

        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal("valueChanged", error.Context["reason"]);
        Assert.Equal("Target", error.Context["member"]);
        Assert.Contains(source.Components, component => component.Id == watcher.Id);
        Assert.Equal(externalTarget, (await client.GetComponentAsync(watcher.Id)).Members["Target"].TargetId);
    }

    private (string State, string Initial, string Desired) RelocationReferenceDocuments(string key)
    {
        var state = Path.Combine(_root, key + ".state.json");
        var initial = Path.Combine(_root, key + "-initial.json");
        var desired = Path.Combine(_root, key + "-desired.json");
        File.WriteAllText(initial, """
            { "schemaVersion":"1", "ownership":{"key":"move-reference"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "children":[
                {"slot":{"key":"destination","name":"Destination"}},
                {"slot":{"key":"source","name":"Source"},"components":[
                  {"key":"moved","type":"Test.Target","fields":{"Enabled":true,"Other":false}},
                  {"key":"watcher","type":"Test.Source","fields":{"Target":"$member:moved.Enabled","Enabled":false}}
                ]}
              ] }
            """);
        File.WriteAllText(desired, """
            { "schemaVersion":"1", "ownership":{"key":"move-reference"},
              "slot":{"key":"root","name":"Managed","parent":"Root"},
              "children":[
                {"slot":{"key":"destination","name":"Destination"},"components":[
                  {"key":"moved","type":"Test.Target","fields":{"Enabled":true,"Other":false}}
                ]},
                {"slot":{"key":"source","name":"Source"},"components":[
                  {"key":"watcher","type":"Test.Source","fields":{"Target":"$member:moved.Enabled","Enabled":true}}
                ]}
              ] }
            """);
        return (state, initial, desired);
    }

    [Fact]
    public async Task ReferencedComponentMoveStillChecksUnrelatedManagedMember()
    {
        var (state, initialPath, desiredPath) = RelocationReferenceDocuments("move-reference-other-field");
        var client = new FakeResoniteClient(ApplyDocument.Load(initialPath));
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        var managedRoot = client.Root.Children.Single();
        var source = managedRoot.Children.Single(slot => slot.Name == "Source");
        var oldTarget = source.Components.Single(component => component.Type == "Test.Target");
        var watcher = source.Components.Single(component => component.Type == "Test.Source");
        var mutated = false;
        client.BeforeDeletionObservation = id =>
        {
            if (id == source.Id)
            {
                mutated = true;
                client.SetMemberValue(watcher.Id, "Enabled", System.Text.Json.Nodes.JsonValue.Create("external-drift"));
            }
        };

        RLoopException? error = null;
        try { await service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state)); }
        catch (RLoopException caught) { error = caught; }

        Assert.True(mutated, "the mutation hook must run at the source deletion boundary");
        var actualError = Assert.IsType<RLoopException>(error);
        Assert.Equal("APPLY_PRECONDITION_FAILED", actualError.Code);
        Assert.Equal("valueChanged", actualError.Context["reason"]);
        Assert.Equal("Enabled", actualError.Context["member"]);
        Assert.DoesNotContain(source.Components, component => component.Id == oldTarget.Id);
        Assert.Equal("external-drift", (await client.GetComponentAsync(watcher.Id)).Members["Enabled"].Value!.GetValue<string>());
    }

    [Fact]
    public async Task DeletedComponentReferenceRefreshRejectsRetargetAfterDeletion()
    {
        var (state, initialPath, desiredPath) = RelocationReferenceDocuments("move-reference-after-delete");
        var client = new FakeResoniteClient(ApplyDocument.Load(initialPath));
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(state));
        var managedRoot = client.Root.Children.Single();
        var source = managedRoot.Children.Single(slot => slot.Name == "Source");
        var oldTarget = source.Components.Single(component => component.Type == "Test.Target");
        var watcher = source.Components.Single(component => component.Type == "Test.Source");
        client.AfterMutation = (kind, id) =>
        {
            if (kind == "removeComponent" && id == oldTarget.Id)
                client.RetargetReference(watcher.Id, "Target", "external-target-id");
        };

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(state)));

        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal("valueChanged", error.Context["reason"]);
        Assert.Equal("Target", error.Context["member"]);
        Assert.DoesNotContain(source.Components, component => component.Id == oldTarget.Id);
        Assert.Empty(ApplyStateStore.Load(state, "move-reference").Pending);
        Assert.Equal("external-target-id", (await client.GetComponentAsync(watcher.Id)).Members["Target"].TargetId);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("scalar")]
    [InlineData("tinyNumeric")]
    [InlineData("slot")]
    [InlineData("reference")]
    [InlineData("componentType")]
    [InlineData("memberKind")]
    [InlineData("memberType")]
    [InlineData("generation")]
    [InlineData("disconnected")]
    public async Task S3PlanDriftStopsBeforeSend(string fault)
    {
        var baseline = Document("s3-drift", """[{"key":"c","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(baseline) { Generation = "planned" };
        var service = new WorldService(client);
        await service.ApplyAsync(baseline);
        var slot = client.Root.Children.Single();
        var component = slot.Components.Single();
        var desired = baseline with { Components = [baseline.Components![0] with
            { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) } }] };
        if (fault == "slot") desired = desired with { Slot = desired.Slot! with { Position = [1, 2, 3] } };
        if (fault == "tinyNumeric") component.Members["Enabled"] = new("field", "tiny", "float", JsonValue.Create(1.0));
        if (fault == "reference") component.Members["Enabled"] = new("reference", "ref", TargetId: "old");
        client.OnDescribe = () =>
        {
            switch (fault)
            {
                case "scalar": component.Members["Enabled"] = component.Members["Enabled"] with { Value = JsonValue.Create(99) }; break;
                case "tinyNumeric": component.Members["Enabled"] = component.Members["Enabled"] with { Value = JsonValue.Create(1.0000000001) }; break;
                case "slot": slot.Position = new(0.000001f, 0, 0); break;
                case "reference": component.Members["Enabled"] = component.Members["Enabled"] with { TargetId = "external" }; break;
                case "componentType": component.Type = "Test.Other"; break;
                case "memberKind": component.Members["Enabled"] = component.Members["Enabled"] with { Kind = "reference" }; break;
                case "memberType": component.Members["Enabled"] = component.Members["Enabled"] with { Type = "double" }; break;
                case "generation": client.Generation = "replacement"; break;
                case "disconnected": client.Connected = false; break;
            }
        };
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired));
        Assert.Equal(fault == "generation" ? "CONNECTION_GENERATION_CHANGED" : "APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal(fault == "generation" ? 7 : 6, error.ExitCode);
        Assert.Equal(0, client.Writes);
        var diagnostic = ApplyDiagnostics.ForException(error, "apply").Diagnostics.Last();
        Assert.NotNull(diagnostic.Key);
        Assert.Equal("known", diagnostic.Expected.Status);
        Assert.Equal("known", diagnostic.Observed.Status);
        Assert.Equal("unknown", diagnostic.Completeness["writerOutsideObservation"]);
    }

    [Theory]
    [InlineData("[FrooxEngine]FrooxEngine.BooleanValueDriver<bool>", "TargetField")]
    [InlineData("[FrooxEngine]FrooxEngine.ValueCopy<bool>", "Target")]
    [InlineData("[FrooxEngine]FrooxEngine.ValueCopy<bool>", "Source")]
    public async Task S3ObservedIFieldReferenceStopsBeforeSend(string writerType, string referenceName)
    {
        var baseline = Document("s3-writer", """[{"key":"c","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(baseline);
        var service = new WorldService(client);
        await service.ApplyAsync(baseline);
        var slot = client.Root.Children.Single();
        var target = slot.Components.Single();
        var writer = client.PrependComponent(slot, writerType, new Dictionary<string, string>());
        writer.Members[referenceName] = new("reference", "writer-ref", TargetId: target.Members["Enabled"].Id,
            TargetType: "[FrooxEngine]FrooxEngine.IField<bool>");
        var desired = baseline with { Components = [baseline.Components![0] with
            { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) } }] };
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired));
        Assert.Equal("writerDetected", error.Context["reason"]);
        Assert.Equal("Enabled", error.Context["member"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task S3SpinnerIFieldReferenceStopsSlotRotationWrite()
    {
        var baseline = Document("s3-spinner", "[]");
        var client = new FakeResoniteClient(baseline);
        var service = new WorldService(client);
        await service.ApplyAsync(baseline);
        var slot = client.Root.Children.Single();
        var spinner = client.PrependComponent(slot, "[FrooxEngine]FrooxEngine.Spinner", new Dictionary<string, string>());
        spinner.Members["_target"] = new("reference", "target", TargetId: slot.Id + ":Rotation",
            TargetType: "[FrooxEngine]FrooxEngine.IField<floatQ>");
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(baseline with
            { Slot = baseline.Slot! with { Rotation = [0, 0, 0, 1] } }));
        Assert.Equal("writerDetected", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("self")]
    [InlineData("otherType")]
    public async Task S3UnknownOutsideAndExcludedReferencesDoNotBlock(string evidence)
    {
        var baseline = Document("s3-unknown", """[{"key":"c","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(baseline);
        var service = new WorldService(client);
        await service.ApplyAsync(baseline);
        var slot = client.Root.Children.Single();
        var target = slot.Components.Single();
        var referrer = evidence == "self" ? target : client.PrependComponent(evidence == "outside" ? client.Root : slot,
            "Test.Referrer", new Dictionary<string, string>());
        referrer.Members["TargetField"] = new("reference", "referrer", TargetId: target.Members["Enabled"].Id,
            TargetType: evidence == "otherType" ? "Synthetic.IField<bool>" : "[FrooxEngine]FrooxEngine.IField<bool>");
        var desired = baseline with { Components = [baseline.Components![0] with
            { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) } }] };
        client.ResetWriteCounts();
        var result = await service.ApplyAsync(desired);
        Assert.Equal(1, client.Writes);
        var diagnostic = ApplyDiagnostics.ForRuntime(result).Diagnostics.Last();
        Assert.Equal("APPLY_EVIDENCE_INCOMPLETE", diagnostic.Code);
        Assert.Equal("unknown", diagnostic.Completeness["writerOutsideObservation"]);
        Assert.Equal("unknown", diagnostic.Completeness["identity"]);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("driver-owned")]
    public async Task S3PropertyModesPreserveOnCreateAndReapply(string mode)
    {
        var document = Document("s3-modes", """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        document = document with { Components = [document.Components![0] with
            { PropertyModes = new Dictionary<string, string> { ["Enabled"] = mode } }] };
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var preview = await service.PlanApplyAsync(document);
        Assert.Contains(preview.Operations.Single(e => e.Kind == "component").Diffs!, d => d.Member == "Enabled" && d.Kind == "preserve");
        await service.ApplyAsync(document);
        Assert.False(client.Root.Children.Single().Components.Single().Members["Enabled"].Value!.GetValue<bool>());
        client.ResetWriteCounts();
        await service.ApplyAsync(document);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("driver-owned")]
    public async Task S3InitialPropertyModesAlsoSkipCreation(string mode)
    {
        var document = Document("s3-initial-modes", """[{"key":"c","type":"Test.Target","initialFields":{"Enabled":true}}]""");
        document = document with { Components = [document.Components![0] with
            { PropertyModes = new Dictionary<string, string> { ["Enabled"] = mode } }] };
        var client = new FakeResoniteClient(document);
        await new WorldService(client).ApplyAsync(document);
        Assert.False(client.Root.Children.Single().Components.Single().Members["Enabled"].Value!.GetValue<bool>());
    }

    [Fact]
    public async Task S3ExplicitConfigAndInitialRetainExistingSemantics()
    {
        var document = Document("s3-explicit", """[{"key":"c","type":"Test.Modes","fields":{"Config":1},"initialFields":{"Seed":2},"propertyModes":{"Config":"config","Seed":"initial"}}]""");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        await service.ApplyAsync(document);
        var component = client.Root.Children.Single().Components.Single();
        component.Members["Config"] = component.Members["Config"] with { Value = JsonValue.Create(9) };
        component.Members["Seed"] = component.Members["Seed"] with { Value = JsonValue.Create(8) };
        client.ResetWriteCounts();
        await service.ApplyAsync(document);
        Assert.Equal(1, component.Members["Config"].Value!.GetValue<int>());
        Assert.Equal(8, component.Members["Seed"].Value!.GetValue<int>());
        Assert.Equal(1, client.Writes);
    }

    [Theory]
    [InlineData("initial", false)]
    [InlineData("config", true)]
    [InlineData("typo", false)]
    public async Task S3PropertyModeContradictionsUseExistingValidationCode(string mode, bool initial)
    {
        var document = Document("s3-invalid-modes", "[]");
        var values = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) };
        document = document with { Components = [new("Test.Target", initial ? null : values, "c", InitialFields: initial ? values : null,
            PropertyModes: new Dictionary<string, string> { ["Enabled"] = mode })] };
        var result = await ApplyDocumentValidator.ValidateAsync(document);
        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.Code == "APPLY_COMPONENT_FIELD_POLICY_CONFLICT");
    }

    [Fact]
    public async Task S3DeclaredFieldReferenceIsExcludedAndOwnSuccessfulWritesConverge()
    {
        var document = Document("s3-declared", """
            [
            {"key":"target","type":"Test.Target","fields":{"Enabled":false}},
            {"key":"source","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}
            ]
            """);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        await service.ApplyAsync(document);
        var source = client.Root.Children.Single().Components.Single(c => c.Type == "Test.Source");
        source.Members["Target"] = source.Members["Target"] with { TargetType = "[FrooxEngine]FrooxEngine.IField<bool>" };
        var desired = document with { Components = [document.Components![0] with
            { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) } }, document.Components[1]] };
        client.ResetWriteCounts();
        await service.ApplyAsync(desired);
        Assert.Equal(1, client.Writes);
        client.ResetWriteCounts();
        await service.ApplyAsync(desired);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("driver-owned")]
    public async Task S3PreservedReferenceDeclarationDoesNotExemptWriter(string mode)
    {
        var document = Document("s3-preserved-reference", """
            [
              {"key":"target","type":"Test.Target","fields":{"Enabled":false}},
              {"key":"source","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}
            ]
            """);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        await service.ApplyAsync(document);
        var source = client.Root.Children.Single().Components.Single(c => c.Type == "Test.Source");
        source.Members["Target"] = source.Members["Target"] with { TargetType = "[FrooxEngine]FrooxEngine.IField<bool>" };
        var desired = document with { Components = [document.Components![0] with
            { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) } },
            document.Components[1] with { PropertyModes = new Dictionary<string, string> { ["Target"] = mode } }] };
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired));
        Assert.Equal("writerDetected", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
    }
}

using System.Numerics;
using System.Text.Json;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class AuthoringAssistanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "resoloop-authoring-" + Guid.NewGuid().ToString("N"));
    public AuthoringAssistanceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task ScaffoldRoundTripsThroughCompilerAndValidationWithoutGraphics()
    {
        var path = AuthoringSchema.WriteNew(AuthoringSchema.Scaffold("settings"), Path.Combine(_directory, "main.json"));
        var document = ApplyDocument.Load(path);
        Assert.True((await ApplyDocumentValidator.ValidateAsync(document)).Valid);
        Assert.Empty(document.Components!);
        Assert.Equal("settings", document.Slot!.Key);
        var content = File.ReadAllText(path);
        Assert.Equal("SCAFFOLD_OUTPUT_FAILED", Assert.Throws<RLoopException>(() => AuthoringSchema.WriteNew(AuthoringSchema.Scaffold(), path)).Code);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task NamedProvidersHaveDistinctSlotAndComponentReferences()
    {
        var a = AuthoringSchema.Provider("front", "FrooxEngine.UI_UnlitMaterial");
        var b = AuthoringSchema.Provider("rear", "FrooxEngine.UI_UnlitMaterial");
        var document = AuthoringSchema.Scaffold() with { Children = [a, b] };
        var validation = await ApplyDocumentValidator.ValidateAsync(document);
        Assert.True(validation.Valid);
        Assert.Empty(validation.Issues);
        Assert.Equal("front", a.Components![0].Key);
        Assert.NotEqual(a.Slot.Name, b.Slot.Name);
        Assert.Empty(a.Components[0].Fields!);
    }

    [Fact]
    public void CameraContractMatchesParserAndUsesSmallExample()
    {
        var schema = AuthoringSchema.Describe("camera");
        var json = JsonSerializer.Serialize(schema.Example, AuthoringSchema.JsonOptions);
        var parsed = JsonSerializer.Deserialize<ApplyCameraSpec>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(parsed);
        Assert.Equal([0f, 0, 0], parsed.Target);
        Assert.Equal(typeof(ApplyCameraSpec).GetConstructors().First().GetParameters().Length, schema.Properties.Count);
        Assert.Contains(schema.Properties, p => p.Name == "target" && p.Required && p.Type == "number[]");
        Assert.True(json.Length < 500);
        Assert.Equal("SCHEMA_TOPIC_NOT_FOUND", Assert.Throws<RLoopException>(() => AuthoringSchema.Describe("unknown")).Code);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "$.schemaVersion", "schemaVersion")]
    [InlineData("\"schemaVersion\":\"1\",\"cameras\":[]", "$.cameras", "cameras")]
    [InlineData("\"schemaVersion\":\"1\",\"cameras\":{\"main\":{\"lookAt\":[0,0,0]}}", "$.cameras.main.lookAt", "target")]
    public void InvalidShapeReturnsLocalRecoveryHint(string body, string expectedPath, string hint)
    {
        var path = Path.Combine(_directory, "invalid.json");
        File.WriteAllText(path, "{" + body + "}");
        var error = Assert.Throws<RLoopException>(() => ApplyDocument.Load(path));
        Assert.Equal("APPLY_DOCUMENT_INVALID", error.Code);
        Assert.Equal(expectedPath, error.Context["jsonPath"]);
        Assert.Contains(error.Suggestions!, s => s.Contains(hint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateProviderRiskIsWarningAndPreservesValidDocument()
    {
        var a = AuthoringSchema.Provider("front", "FrooxEngine.UI_UnlitMaterial").Components![0];
        var b = a with { Key = "rear", Type = "[FrooxEngine]FrooxEngine.UI_UnlitMaterial" };
        var document = AuthoringSchema.Scaffold() with { Components = [a, b] };
        var validation = await ApplyDocumentValidator.ValidateAsync(document);
        Assert.True(validation.Valid);
        ApplyDocumentValidator.ThrowIfInvalid(validation);
        Assert.Equal("warning", Assert.Single(validation.Issues).Severity);
        Assert.Contains("front, rear", validation.Issues[0].Message);
        static ApplyComponentSpec Identify(ApplyComponentSpec c, string side) => c with
        {
            Fields = new Dictionary<string, JsonElement> { ["Sidedness"] = JsonSerializer.SerializeToElement(side) },
            IdentityFields = ["Sidedness"]
        };
        Assert.Empty(ComponentIdentityDiagnostics.Analyze(document with { Components = [Identify(a, "Front"), Identify(b, "Back")] }));
        Assert.Single(ComponentIdentityDiagnostics.Analyze(document with { Components = [Identify(a, "Front"), Identify(b, "Front")] }));
        Assert.Single(ComponentIdentityDiagnostics.Analyze(document with { Components = [Identify(a, "$asset:a"), Identify(b, "$asset:b")] }));
    }

    [Theory]
    [InlineData("front", 1280, 720)]
    [InlineData("rear", 720, 1280)]
    public void AutoFrameFitsAllCornersAfterParentScaleRotationAndOffset(string view, int width, int height)
    {
        var transform = Matrix4x4.CreateScale(.002f, .003f, .002f) *
            Matrix4x4.CreateFromYawPitchRoll(.4f, .5f, 1.2f) * Matrix4x4.CreateTranslation(3, 2, 1);
        var frame = CanvasFraming.Fit("panel", new(600, 400, 0), new(40, -30, 0), transform, view, width, height);
        var camera = frame.Camera;
        var rotation = LiveCaptureService.CameraRotation(camera);
        var inverse = Quaternion.Inverse(new(rotation.X, rotation.Y, rotation.Z, rotation.W));
        var position = new Vector3(camera.Position[0], camera.Position[1], camera.Position[2]);
        foreach (var point in frame.RootSpaceCorners)
        {
            var projected = Vector3.Transform(new Vector3(point[0], point[1], point[2]) - position, inverse);
            Assert.True(projected.Z > 0);
            var limit = projected.Z * MathF.Tan(camera.FieldOfView * MathF.PI / 360);
            Assert.True(Math.Abs(projected.Y) <= limit / 1.09f);
            Assert.True(Math.Abs(projected.X) <= limit * width / height / 1.09f);
        }
    }

    [Fact]
    public void FrameValidationAndCliNumbersRejectInvalidInputs()
    {
        Assert.Throws<RLoopException>(() => CanvasFraming.Fit("panel", new(1,1,0), Vector3.Zero, Matrix4x4.Identity, "side"));
        Assert.Throws<RLoopException>(() => CanvasFraming.Fit("panel", new(1,1,1), Vector3.Zero, Matrix4x4.Identity));
        Assert.Throws<RLoopException>(() => CanvasFraming.Fit("panel", new(1,1,0), Vector3.Zero, Matrix4x4.Identity, margin: float.NaN));
        Assert.Throws<RLoopException>(() => ParsedArguments.Parse(["--margin", "NaN"]).FloatOption("margin", 1.1f, 1, 3));
        Assert.Equal(1.25f, ParsedArguments.Parse(["--margin", "1.25"]).FloatOption("margin", 1.1f, 1, 3));
    }
}

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task IdentityWarningsSurviveStrictPlanAndBriefOutputWithoutMutation()
    {
        var client = new FakeResoniteClient();
        var document = Document("identity-warning", """
            [{"key":"a","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"b","type":"Test.Target","fields":{"Enabled":false}}]
            """);
        var strict = await ApplyDocumentValidator.ValidateAsync(document, client);
        Assert.True(strict.Valid);
        Assert.True(strict.Strict);
        Assert.Single(strict.Issues);
        var plan = await new WorldService(client).PlanApplyAsync(document, new(Path.Combine(_root, "identity-warning.state.json")));
        Assert.Single(plan.Warnings);
        var brief = JsonSerializer.Serialize(BriefOutput.Plan(plan, []));
        Assert.Contains("APPLY_COMPONENT_IDENTITY_RISK", brief);
        Assert.Equal(0, client.BatchUpdates);
    }
}

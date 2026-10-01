using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

public sealed class ComponentReadFailureTests
{
    [Theory]
    [InlineData("Reso_FFFFFF1", "Component with ID 'Reso_FFFFFF1' not found.")]
    [InlineData("not-a-component-id", "Component with ID 'not-a-component-id' not found.")]
    [InlineData("Reso_FFFFFF1", " \tComponent with ID 'Reso_FFFFFF1' not found.\r\n")]
    public Task ExactRequestedComponentAbsenceIsNotFound(string id, string errorInfo) =>
        AssertFailure(id, errorInfo, "COMPONENT_NOT_FOUND", ExitCodes.NotFound);

    [Theory]
    [InlineData("Component with ID 'Reso_FFFFFF2' not found.")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("Object reference not set to an instance of an object.")]
    [InlineData("Component with ID 'reso_ffffff1' not found.")]
    [InlineData("component with ID 'Reso_FFFFFF1' not found.")]
    [InlineData("Component with ID 'Reso_FFFFFF1' not found")]
    [InlineData("Component with ID ' Reso_FFFFFF1' not found.")]
    [InlineData("Error: Component with ID 'Reso_FFFFFF1' not found.")]
    [InlineData("Component with ID 'Reso_FFFFFF1' not found. Retry.")]
    public Task OtherComponentFailuresAreOperationFailed(string? errorInfo) =>
        AssertFailure("Reso_FFFFFF1", errorInfo, "RESONITE_OPERATION_FAILED", ExitCodes.OperationFailed);

    private static async Task AssertFailure(string id, string? errorInfo, string code, int exitCode)
    {
        Link.GetComponent? requested = null;
        await using var client = new ResoniteLinkClientAdapter(new ScriptedMetadataLink(),
            reflectionCache: new ReflectionCacheOptions("off"), getComponentData: request =>
            {
                requested = request;
                return Task.FromResult(new Link.ComponentData { Success = false, ErrorInfo = errorInfo! });
            });

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetComponentAsync(id));

        Assert.Equal(code, ex.Code);
        Assert.Equal(exitCode, ex.ExitCode);
        Assert.Equal(string.IsNullOrWhiteSpace(errorInfo) ? "ResoniteLink operation failed." : errorInfo, ex.Message);
        Assert.Equal(id, ex.Context["componentId"]);
        Assert.True(ex.Context.ContainsKey("errorInfo"));
        Assert.Equal(errorInfo, ex.Context["errorInfo"]);
        Assert.Equal(id, requested!.ComponentID);
    }

    [Fact]
    public async Task SuccessfulComponentReadStillMapsData()
    {
        await using var client = new ResoniteLinkClientAdapter(new ScriptedMetadataLink(),
            reflectionCache: new ReflectionCacheOptions("off"), getComponentData: request =>
                Task.FromResult(new Link.ComponentData
                {
                    Success = true,
                    Data = new Link.Component { ID = request.ComponentID, ComponentType = "[Test]Test.Widget", Members = new() }
                }));

        var component = await client.GetComponentAsync("Reso_9");

        Assert.Equal("Reso_9", component.Id);
        Assert.Equal("[Test]Test.Widget", component.Type);
    }
}

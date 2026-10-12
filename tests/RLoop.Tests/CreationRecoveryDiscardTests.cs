using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationRecoveryDiscardTests
{
    [Fact]
    public void ExactOperationDiscardClearsOnlyLocalPendingAndRetainsActiveRoot()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(ExactOperationDiscardClearsOnlyLocalPendingAndRetainsActiveRoot));
        var active = CreationRecoveryTestData.Candidate("active-root");
        var candidate = CreationRecoveryTestData.Candidate("possible-candidate");
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending(candidate: candidate), active);
        CreationStateStore.Save(path, state);

        var result = CreationRecoveryService.DiscardPending(path, "operation-4", true);

        var final = CreationStateStore.Load(path, state.CreationId, true)!;
        Assert.Null(final.Pending);
        Assert.Equal(active.Id, final.ActiveRoot!.Id);
        Assert.Equal("discard", result.Action);
        Assert.Contains("No world cleanup was performed.", System.Text.Json.JsonSerializer.Serialize(result.Diagnostics));
    }

    [Fact]
    public void OperationMismatchDoesNotChangePendingState()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(OperationMismatchDoesNotChangePendingState));
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending());
        CreationStateStore.Save(path, state);

        var error = Assert.Throws<RLoopException>(() => CreationRecoveryService.DiscardPending(path, "other-operation", true));

        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("operation-4", CreationStateStore.Load(path, state.CreationId, true)!.Pending!.OperationId);
    }

    [Fact]
    public void MissingConfirmationDoesNotAcquireWorldClientOrDiscard()
    {
        var path = CreationRecoveryTestData.PathFor(nameof(MissingConfirmationDoesNotAcquireWorldClientOrDiscard));
        var state = CreationRecoveryTestData.State(CreationRecoveryTestData.Pending());
        CreationStateStore.Save(path, state);

        var error = Assert.Throws<RLoopException>(() => CreationRecoveryService.DiscardPending(path, "operation-4", false));

        Assert.Equal("CONFIRMATION_REQUIRED", error.Code);
        Assert.NotNull(CreationStateStore.Load(path, state.CreationId, true)!.Pending);
    }
}

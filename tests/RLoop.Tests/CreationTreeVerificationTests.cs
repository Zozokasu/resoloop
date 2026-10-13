using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationTreeVerificationTests
{
    [Fact]
    public async Task CallerCancellationRemainsAnOperationCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new CreationMutationTestClient { CancelOnObserve = cancellation };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreationTreeVerification.VerifyAsync(client,
            "root-created", new CreationPlanCounts(1, 0), cancellation.Token));
    }
}

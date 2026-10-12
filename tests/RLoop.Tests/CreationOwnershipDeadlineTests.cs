using RLoop.Core;

namespace RLoop.Tests;

public sealed class CreationOwnershipDeadlineTests
{
    [Fact]
    public async Task PendingSessionReadEndsAtVerifierDeadline()
    {
        var client = CreationOwnershipFixture.Client();
        var pending = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Started();
        client.SessionAsyncRead = (_, _) => { started.TrySetResult(true); return pending.Task; };
        await AssertDeadline(client, started);
        Assert.Equal(1, client.SessionReads);
        pending.SetResult(new SessionInfo(CreationTestClient.Url, true, null, null, null));
    }

    [Fact]
    public async Task PendingRuntimeMetadataReadEndsAtVerifierDeadline()
    {
        var client = CreationOwnershipFixture.Client();
        var pending = new TaskCompletionSource<ComponentTypeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Started();
        client.TypeRead = _ => { started.TrySetResult(true); return pending.Task; };
        await AssertDeadline(client, started);
        Assert.Equal(new[] { CreationOwnershipVerifier.VariableComponentType }, client.TypeReads);
        pending.SetResult(client.StampMetadata);
    }

    [Fact]
    public async Task PendingExactSlotReadEndsAtVerifierDeadline()
    {
        var client = CreationOwnershipFixture.Client();
        var pending = new TaskCompletionSource<ApplyDeletionObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Started();
        client.SlotRead = (_, _) => { started.TrySetResult(true); return pending.Task; };
        await AssertDeadline(client, started);
        Assert.Equal(new[] { CreationOwnershipFixture.ParentId }, client.SlotReads);
        pending.SetResult(client.Slots[CreationOwnershipFixture.ParentId]);
    }

    [Fact]
    public async Task PendingComponentReadEndsAtVerifierDeadline()
    {
        var client = CreationOwnershipFixture.Client();
        var pending = new TaskCompletionSource<ComponentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Started();
        client.ComponentRead = (_, _) => { started.TrySetResult(true); return pending.Task; };
        await AssertDeadline(client, started);
        Assert.Equal(new[] { "stamp-component" }, client.ComponentReads);
        pending.SetResult(client.Components["stamp-component"]);
    }

    [Fact]
    public async Task CallerCancellationDuringReadPropagatesInsteadOfBecomingDeadlineFailure()
    {
        var client = CreationOwnershipFixture.Client();
        var pending = new TaskCompletionSource<ApplyDeletionObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SlotRead = (_, _) => { readStarted.TrySetResult(true); return pending.Task; };
        using var cancellation = new CancellationTokenSource();
        var verification = new CreationOwnershipVerifier(client,
            new CreationOwnershipOptions(MaxDuration: TimeSpan.FromSeconds(20)))
            .VerifyAsync(CreationOwnershipFixture.State(), cancellation.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cancellation.Cancel();
        // The verifier's own 20 second deadline would turn a missed cancellation into a different error;
        // the outer wait is only a hang guard and is long enough not to race a loaded host.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(new[] { CreationOwnershipFixture.ParentId }, client.SlotReads);
        Assert.Equal(0, client.MutationCalls);
        pending.SetResult(client.Slots[CreationOwnershipFixture.ParentId]);
    }

    private static TaskCompletionSource<bool> Started() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The deadline is wall-clock, so the generous budget only needs to outlast the reads before the held one.
    // If the held read is never reached, that is a broken test premise (host stall), reported as such.
    private static async Task AssertDeadline(CreationTestClient client, TaskCompletionSource<bool> started)
    {
        var verification = new CreationOwnershipVerifier(client,
            new CreationOwnershipOptions(MaxDuration: TimeSpan.FromMilliseconds(300)))
            .VerifyAsync(CreationOwnershipFixture.State());
        await Task.WhenAny(started.Task, verification);
        Assert.True(started.Task.IsCompleted, "Test premise failed: the deadline expired before the held read started.");
        var error = await Assert.ThrowsAsync<RLoopException>(() => verification);
        Assert.Equal("CREATION_OBSERVATION_INCOMPLETE", error.Code);
        Assert.Equal(ExitCodes.ValidationFailed, error.ExitCode);
        Assert.Equal(0, client.MutationCalls);
        Assert.Equal(0, client.GuardWriteCalls);
    }
}

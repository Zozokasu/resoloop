using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

namespace ResoniteWorkbench.Protocol;

/// <summary>
/// A local RPC connection to a running Workbench. Calls may run concurrently: one reader routes
/// replies by request ID, and cancelling a call asks the server to stop it.
/// </summary>
public sealed class WorkbenchRpcClient : IAsyncDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>> pending = new(StringComparer.Ordinal);
    private readonly Task reader;
    private Exception? closedBy;
    private long nextId;
    private int disposed;

    private WorkbenchRpcClient(NamedPipeClientStream pipe, RpcWelcome welcome)
    {
        this.pipe = pipe;
        Welcome = welcome;
        reader = ReadLoopAsync();
    }

    public RpcWelcome Welcome { get; }

    /// <summary>Connects and negotiates. Fails with <see cref="RpcHandshakeException"/> when the server refuses.</summary>
    /// <exception cref="TimeoutException">No server answered within <paramref name="timeout"/>.</exception>
    public static async Task<WorkbenchRpcClient> ConnectAsync(string pipeName, RpcHello hello, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        // CurrentUserOnly also verifies that the server end belongs to the current user.
        NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await RpcFrameCodec.WriteAsync(pipe, hello, deadline.Token).ConfigureAwait(false);

            return await RpcFrameCodec.ReadAsync(pipe, deadline.Token).ConfigureAwait(false) switch
            {
                RpcReject rejection => throw new RpcHandshakeException(rejection),
                RpcWelcome welcome when IsValid(welcome, hello) => new WorkbenchRpcClient(pipe, welcome),
                RpcError error => throw new RpcHandshakeException($"{error.Error.Code}: {error.Error.Message}"),
                _ => throw new RpcHandshakeException("The server did not answer the hello with a valid welcome."),
            };
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException($"No Workbench answered on pipe '{pipeName}' within {timeout}.", exception);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Calls one method. Fails with <see cref="RpcCallException"/> when the server answers with an
    /// error. Cancelling <paramref name="ct"/> sends a cancel for this request; the server may
    /// already have completed it, and a completed write is not undone.
    /// </summary>
    public async Task<RpcResponse> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        JsonElement? json = parameters switch
        {
            null => null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(parameters, parameters.GetType(), WorkbenchJson.Options),
        };

        string id = Interlocked.Increment(ref nextId).ToString(CultureInfo.InvariantCulture);
        TaskCompletionSource<RpcResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;

        try
        {
            // The reader fails every pending call when it stops; a call registered after that must fail itself.
            if (Volatile.Read(ref closedBy) is { } closed)
                throw new IOException("The RPC connection is closed.", closed);

            await SendAsync(new RpcRequest(id, method, json)).ConfigureAwait(false);

            try
            {
                return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await TrySendCancelAsync(id).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await lifetime.CancelAsync().ConfigureAwait(false);
        await reader.ConfigureAwait(false);
        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsValid(RpcWelcome welcome, RpcHello hello) =>
        welcome.GrantedCapabilities is not null
        && welcome.AvailableCapabilities is not null
        && hello.ProtocolVersions.Contains(welcome.SelectedProtocol)
        && welcome.GrantedCapabilities.All(c => hello.RequestedCapabilities.Contains(c, StringComparer.Ordinal))
        && welcome.GrantedCapabilities.All(c => welcome.AvailableCapabilities.Contains(c, StringComparer.Ordinal));

    private async Task SendAsync(RpcMessage message)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(WriteTimeout);

        bool acquired = false;
        try
        {
            await writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            await RpcFrameCodec.WriteAsync(pipe, message, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // A write timeout or a closed transport is a connection failure, not a caller cancellation.
            await lifetime.CancelAsync().ConfigureAwait(false);
            throw new IOException("The RPC connection closed, or a write to it timed out.", exception);
        }
        catch
        {
            // A frame may have been cut off, so nothing more can be written on this connection.
            await lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (acquired)
                writeGate.Release();
        }
    }

    private async Task TrySendCancelAsync(string id)
    {
        try
        {
            await SendAsync(new RpcCancel(id)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is gone, which ends the request on the server as well.
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception failure = new IOException("Workbench closed the RPC connection.");

        try
        {
            while (await RpcFrameCodec.ReadAsync(pipe, lifetime.Token).ConfigureAwait(false) is { } message)
            {
                switch (message)
                {
                    case RpcResponse response:
                        if (pending.TryGetValue(response.Id, out TaskCompletionSource<RpcResponse>? succeeded))
                            succeeded.TrySetResult(response);
                        break;

                    case RpcError { Id: { } id } error:
                        if (pending.TryGetValue(id, out TaskCompletionSource<RpcResponse>? failed))
                            failed.TrySetException(new RpcCallException(error.Error));
                        break;

                    case RpcError error:
                        throw new IOException($"Workbench closed the RPC connection: {error.Error.Code}: {error.Error.Message}");

                    default:
                        throw new RpcFrameException($"Unexpected {message.GetType().Name} from the server.");
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Disposed, or a write failed and left the connection unusable.
            failure = new IOException("The RPC connection was closed by this client.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            Volatile.Write(ref closedBy, failure);
            await lifetime.CancelAsync().ConfigureAwait(false);

            // Cancelling local I/O does not disconnect the server; closing the pipe does, and that
            // stops server work whose replies can no longer be read.
            await pipe.DisposeAsync().ConfigureAwait(false);

            foreach (TaskCompletionSource<RpcResponse> completion in pending.Values)
                completion.TrySetException(failure);
        }
    }
}

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using ResoniteWorkbench.Protocol;
using RLoop.Workbench;

namespace RLoop.Workbench.Tests;

/// <summary>Shared helpers for tests that drive a WorkbenchResoniteClient over a fake pipe peer.</summary>
internal static class Wb
{
    public static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The capabilities the client requests; a valid welcome grants a subset of them.</summary>
    public static readonly IReadOnlyList<string> ReadCapabilities =
        [RpcCapabilities.SessionRead, RpcCapabilities.WorldRead,
         RpcCapabilities.MemberRead, RpcCapabilities.ReflectionRead];

    public static string NewPipeName() => "RLoop.Workbench.Tests." + Guid.NewGuid().ToString("N");

    public static Uri PipeUri(string pipeName) => new($"{WorkbenchResoniteClient.UriScheme}:///{pipeName}");

    public static RpcWelcome Welcome(RpcActiveConnection? active = null) =>
        new("9.9.9-test", 1, ReadCapabilities, ReadCapabilities, active);

    public static RpcResponse Response(string id, string resultJson,
        string? connectionId = "conn-meta-1", bool? stale = null) =>
        new(id,
            new ResultMeta(DateTimeOffset.UtcNow,
                ConnectionId: connectionId, SessionId: "sess-meta-1", WorldRevision: 12, Stale: stale),
            JsonDocument.Parse(resultJson).RootElement.Clone());

    /// <summary>
    /// Answers handshake with a welcome, then answers every request with the handler.
    /// <paramref name="log"/> records each request in arrival order; <paramref name="onHello"/>
    /// sees the client's hello before the welcome is sent.
    /// </summary>
    public static async Task ServeAsync(FakeWorkbenchServer server, Func<RpcRequest, RpcMessage> respond,
        CancellationToken ct, ConcurrentQueue<RpcRequest>? log = null, Action<RpcHello>? onHello = null)
    {
        await server.WaitForConnectionAsync(ct);
        if (await server.ReadAsync(ct) is RpcHello hello)
            onHello?.Invoke(hello);
        await server.WriteAsync(Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
        while (await server.ReadAsync(ct) is { } message)
        {
            if (message is RpcRequest request)
            {
                log?.Enqueue(request);
                await server.WriteAsync(respond(request), ct);
            }
        }
    }
}

/// <summary>
/// A scripted Workbench peer on a named pipe. Script failures from teardown (pipe closed,
/// cancellation) are swallowed; the pipe stays open until disposal.
/// </summary>
internal sealed class FakeWorkbenchServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();

    private FakeWorkbenchServer(string pipeName)
    {
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public Task Script { get; private set; } = Task.CompletedTask;

    public static FakeWorkbenchServer Start(string pipeName, Func<FakeWorkbenchServer, CancellationToken, Task> script)
    {
        var server = new FakeWorkbenchServer(pipeName);
        server.Script = Task.Run(() => server.RunAsync(script));
        return server;
    }

    private async Task RunAsync(Func<FakeWorkbenchServer, CancellationToken, Task> script)
    {
        try
        {
            await script(this, _stop.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Teardown: the client hung up or the test disposed the pipe.
        }
    }

    public Task WaitForConnectionAsync(CancellationToken ct) => _pipe.WaitForConnectionAsync(ct);

    public async Task<RpcMessage?> ReadAsync(CancellationToken ct) =>
        await RpcFrameCodec.ReadAsync(_pipe, ct).ConfigureAwait(false);

    public Task WriteAsync(RpcMessage message, CancellationToken ct) =>
        RpcFrameCodec.WriteAsync(_pipe, message, ct).AsTask();

    public async Task WriteRawAsync(byte[] bytes, CancellationToken ct)
    {
        await _pipe.WriteAsync(bytes, ct);
        await _pipe.FlushAsync(ct);
    }

    public void Disconnect() => _pipe.Disconnect();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _pipe.DisposeAsync();
        try
        {
            await Script;
        }
        catch
        {
            // A script that faulted for another reason must not mask the test outcome.
        }
        _stop.Dispose();
    }
}

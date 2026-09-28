using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ResoniteWorkbench.Protocol;
using RLoop.Core;
using RLoop.Workbench;

namespace RLoop.Workbench.Tests;

public sealed class WorkbenchResoniteClientTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private static string NewPipeName() => "RLoop.Workbench.Tests." + Guid.NewGuid().ToString("N");

    private static Uri PipeUri(string pipeName) => new($"{WorkbenchResoniteClient.UriScheme}:///{pipeName}");

    private static RpcWelcome Welcome(RpcActiveConnection? active = null) =>
        new("9.9.9-test", 1, [RpcCapabilities.SessionRead], [RpcCapabilities.SessionRead], active);

    private static RpcResponse Response(string id, string resultJson,
        string? connectionId = "conn-meta-1", bool? stale = null) =>
        new(id,
            new ResultMeta(DateTimeOffset.UtcNow,
                ConnectionId: connectionId, SessionId: "sess-meta-1", WorldRevision: 12, Stale: stale),
            JsonDocument.Parse(resultJson).RootElement.Clone());

    /// <summary>Answers handshake with a welcome, then answers every request with the handler.</summary>
    private static async Task RespondAsync(FakeWorkbenchServer server, Func<RpcRequest, RpcMessage> respond, CancellationToken ct)
    {
        await server.WaitForConnectionAsync(ct);
        _ = await server.ReadAsync(ct); // hello
        await server.WriteAsync(Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
        while (await server.ReadAsync(ct) is { } message)
        {
            if (message is RpcRequest request)
                await server.WriteAsync(respond(request), ct);
        }
    }

    [Fact]
    public async Task ConnectAsync_ValidWelcome_SucceedsAndExposesHandshake()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct);
            await s.WriteAsync(Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        Assert.NotNull(client.Handshake);
        Assert.Equal("9.9.9-test", client.Handshake!.ServerVersion);
        Assert.Equal(1, client.Handshake.SelectedProtocol);
        Assert.Equal("conn-1", client.Handshake.ActiveConnectionId);
        Assert.Equal("sess-1", client.Handshake.ActiveSessionId);
        Assert.Contains(RpcCapabilities.SessionRead, client.Handshake.GrantedCapabilities);
        Assert.Equal("conn-1", client.Meta.ConnectionId);
        Assert.Equal("sess-1", client.Meta.SessionId);
    }

    [Fact]
    public async Task ConnectAsync_IncompatibleProtocol_ThrowsProtocolIncompatible()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct);
            await s.WriteAsync(new RpcReject(RpcErrorCodes.IncompatibleProtocol, "protocols 2..3 only", [2, 3]), ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token));

        Assert.Equal("WORKBENCH_PROTOCOL_INCOMPATIBLE", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task ConnectAsync_PipeClosedDuringHandshake_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            s.Disconnect();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task GetSessionInfoAsync_PipeClosedWhileAwaiting_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            await s.WriteAsync(Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
            _ = await s.ReadAsync(ct); // session.status request
            s.Disconnect();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task ConnectAsync_OversizedFrameHeader_FailsWithoutHanging()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, RpcFrameCodec.MaxFrameBytes + 1);
            await s.WriteRawAsync(header, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token));
        watch.Stop();

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"oversized frame handling took {watch.Elapsed}");
    }

    [Fact]
    public async Task ConnectAsync_ServerNeverResponds_ThrowsUnavailableWithinDeadline()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            await Task.Delay(Timeout.Infinite, ct); // accept the pipe, never answer
        });
        await using var client = new WorkbenchResoniteClient();

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.ConnectAsync(PipeUri(pipeName), TimeSpan.FromMilliseconds(500), guard.Token));
        watch.Stop();

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"handshake timeout took {watch.Elapsed}");
    }

    [Fact]
    public async Task GetSessionInfoAsync_SessionDisconnected_ThrowsNotConnected()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string disconnected = """
            {"state":"Disconnected","targetSession":null,"connection":null,"reconnectAttempt":0,"disconnectReason":"user"}
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, disconnected), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_NOT_CONNECTED", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
        Assert.Contains(ex.Suggestions, s => s.Contains("Workbench App", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSessionInfoAsync_SessionConnected_MapsSessionInfoAndMeta()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string connected = """
            {
              "state": "Connected",
              "targetSession": { "sessionId": "S-abc" },
              "connection": {
                "connectionId": "conn-1",
                "generation": 0,
                "session": { "sessionId": "S-abc" },
                "connectedAt": "2026-09-26T01:02:03+00:00",
                "remote": {
                  "resoniteVersion": "2025.9.2.1349",
                  "resoniteLinkVersion": "0.13.1",
                  "uniqueSessionId": "uni-123"
                }
              },
              "reconnectAttempt": 0,
              "disconnectReason": null
            }
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, connected, connectionId: "conn-1"), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        SessionInfo info = await client.GetSessionInfoAsync(guard.Token);

        Assert.Equal($"{WorkbenchResoniteClient.UriScheme}:///{pipeName}", info.Url);
        Assert.True(info.Connected);
        Assert.Equal("2025.9.2.1349", info.ResoniteVersion);
        Assert.Equal("0.13.1", info.ResoniteLinkVersion);
        Assert.Equal("uni-123", info.UniqueSessionId);

        Assert.Equal("conn-1", client.Meta.ConnectionId);
        Assert.Equal("sess-meta-1", client.Meta.SessionId);
        Assert.Equal(12, client.Meta.WorldRevision);
        Assert.Null(client.Meta.ObservedScopeRootId);
    }

    [Fact]
    public async Task GetSessionInfoAsync_MissingState_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string noState = """{"connection":null}""";
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, noState), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task GetSessionInfoAsync_TransitionalState_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string connecting = """
            {"state":"Connecting","targetSession":{"sessionId":"S-abc"},"connection":null,"reconnectAttempt":1,"disconnectReason":null}
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, connecting), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_ConnectedWithoutConnection_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string connectedNoConnection = """
            {"state":"Connected","targetSession":{"sessionId":"S-abc"},"connection":null,"reconnectAttempt":0,"disconnectReason":null}
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, connectedNoConnection, connectionId: "conn-1"), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_ConnectedWithoutConnectionId_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string connectedNoId = """
            {"state":"Connected","connection":{"connectionId":"","generation":0,"remote":{"resoniteVersion":"2025.9.2.1349"}}}
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, connectedNoId, connectionId: "conn-1"), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_ConnectionIdMismatch_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string connected = """
            {"state":"Connected","connection":{"connectionId":"conn-1","generation":0,"remote":{"resoniteVersion":"2025.9.2.1349"}}}
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, connected), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_StaleResponse_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string connected = """
            {"state":"Connected","connection":{"connectionId":"conn-1","generation":0,"remote":{"resoniteVersion":"2025.9.2.1349"}}}
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, connected, connectionId: "conn-1", stale: true), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_DisconnectedButStale_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string disconnectedJson = """{"state":"Disconnected","connection":null}""";
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, disconnectedJson, stale: true), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_DisconnectedWithConnection_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        const string disconnectedWithConnectionJson = """
            {"state":"Disconnected","connection":{"connectionId":"conn-1","generation":0}}
            """;
        await using var server = FakeWorkbenchServer.Start(pipeName,
            (s, ct) => RespondAsync(s, request => Response(request.Id, disconnectedWithConnectionJson), ct));
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task ConnectAsync_UnrequestedGrant_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            await s.WriteAsync(new RpcWelcome("9.9.9-test", 1,
                [RpcCapabilities.SessionRead, RpcCapabilities.WorldRead],
                [RpcCapabilities.SessionRead, RpcCapabilities.WorldRead],
                null), ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task GetSessionInfoAsync_CapabilityNotGranted_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            await s.WriteAsync(new RpcWelcome("9.9.9-test", 1,
                [RpcCapabilities.SessionRead], [], new RpcActiveConnection("conn-1", "sess-1")), ct);
            while (await s.ReadAsync(ct) is { } message)
            {
                if (message is RpcRequest request)
                {
                    await s.WriteAsync(new RpcError(request.Id,
                        new RpcErrorDetail(RpcErrorCodes.CapabilityNotGranted, "session.read is not granted.")), ct);
                }
            }
        });
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.Equal(ExitCodes.ConnectionFailed, ex.ExitCode);
    }

    [Fact]
    public async Task ConnectAsync_ZeroLengthFrameHeader_FailsWithoutHanging()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 0);
            await s.WriteRawAsync(header, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token));
        watch.Stop();

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"zero-length frame handling took {watch.Elapsed}");
    }

    [Fact]
    public async Task ConnectAsync_TruncatedFrame_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 100);
            await s.WriteRawAsync(header, ct);
            s.Disconnect();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_TruncatedFrame_ThrowsUnavailable()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            await s.WriteAsync(Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
            _ = await s.ReadAsync(ct); // session.status request
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 100);
            await s.WriteRawAsync(header, ct);
            s.Disconnect();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        var ex = await Assert.ThrowsAsync<RLoopException>(() => client.GetSessionInfoAsync(guard.Token));

        Assert.Equal("WORKBENCH_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetSessionInfoAsync_NoResponse_ObservesCallerCancellation()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        RpcRequest? received = null;
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            await s.WriteAsync(Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
            received = await s.ReadAsync(ct) as RpcRequest; // session.status request; never answered
            await Task.Delay(Timeout.Infinite, ct);
        });
        await using var client = new WorkbenchResoniteClient();
        await client.ConnectAsync(PipeUri(pipeName), ConnectTimeout, guard.Token);

        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        callTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSessionInfoAsync(callTimeout.Token));
        watch.Stop();

        // The fake server runs on its own task; wait briefly for it to surface the received request.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (received is null && DateTime.UtcNow < deadline)
            await Task.Delay(20, guard.Token);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"session.status cancellation took {watch.Elapsed}");
        Assert.NotNull(received);
        Assert.Equal(RpcMethods.SessionStatus, received!.Method);
    }

    [Fact]
    public async Task ConnectAsync_NonPipeScheme_ThrowsArgumentException()
    {
        await using var client = new WorkbenchResoniteClient();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ConnectAsync(new Uri("ws://localhost:5999"), ConnectTimeout, CancellationToken.None));
    }

    [Fact]
    public async Task GetSessionInfoAsync_BeforeConnect_ThrowsInvalidOperation()
    {
        await using var client = new WorkbenchResoniteClient();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSessionInfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnsupportedMembers_ThrowBackendUnsupported()
    {
        await using var client = new WorkbenchResoniteClient();

        var ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.GetSlotAsync("slot-1", 1, false, CancellationToken.None));
        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
        Assert.Equal(ExitCodes.OperationFailed, ex.ExitCode);

        ex = await Assert.ThrowsAsync<RLoopException>(
            () => client.RemoveComponentAsync("comp-1", CancellationToken.None));
        Assert.Equal("BACKEND_UNSUPPORTED", ex.Code);
    }

    [Fact]
    public async Task WbStatus_UnresponsiveServer_RecordsCommandTimeoutWithinDeadline()
    {
        string pipeName = NewPipeName();
        using var guard = new CancellationTokenSource(GuardTimeout);
        await using var server = FakeWorkbenchServer.Start(pipeName, async (s, ct) =>
        {
            await s.WaitForConnectionAsync(ct);
            _ = await s.ReadAsync(ct); // hello
            await s.WriteAsync(Welcome(new RpcActiveConnection("conn-1", "sess-1")), ct);
            _ = await s.ReadAsync(ct); // session.status request; never answered
            await Task.Delay(Timeout.Infinite, ct);
        });

        var reportPath = Path.Combine(Path.GetTempPath(), "resoloop-test-" + Guid.NewGuid().ToString("N") + ".ndjson");
        try
        {
            var watch = Stopwatch.StartNew();
            var exit = await RLoop.Cli.Program.Main([
                "wb", "status", "--backend", "workbench", "--workbench-pipe", pipeName,
                "--command-timeout", "1", "--report", reportPath
            ]);
            watch.Stop();

            Assert.Equal(RLoop.Core.ExitCodes.Timeout, exit);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"wb status command-timeout handling took {watch.Elapsed}");
            var report = File.Exists(reportPath) ? await File.ReadAllTextAsync(reportPath) : string.Empty;
            Assert.Contains("\"COMMAND_TIMEOUT\"", report);
        }
        finally
        {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }

    /// <summary>
    /// A scripted Workbench peer on a named pipe. Script failures from teardown (pipe closed,
    /// cancellation) are swallowed; the pipe stays open until disposal.
    /// </summary>
    private sealed class FakeWorkbenchServer : IAsyncDisposable
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
}

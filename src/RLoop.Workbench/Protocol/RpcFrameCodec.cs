using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace ResoniteWorkbench.Protocol;

/// <summary>
/// Frames are a 4-byte little-endian length followed by that many bytes of UTF-8 JSON.
/// Writers must not interleave frames, and must drop the connection after an interrupted write.
/// </summary>
public static class RpcFrameCodec
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    private const int HeaderBytes = 4;

    /// <summary>Reads one message. Null means the peer closed cleanly between frames.</summary>
    public static async ValueTask<RpcMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderBytes];
        int read = await stream.ReadAsync(header, ct).ConfigureAwait(false);

        if (read == 0)
            return null;

        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(read), ct).ConfigureAwait(false);

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > MaxFrameBytes)
            {
                throw new RpcFrameException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A frame of {length} bytes is outside 1..{MaxFrameBytes}."));
            }

            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

            return JsonSerializer.Deserialize<RpcMessage>(payload, WorkbenchJson.Options)
                ?? throw new RpcFrameException("A frame held JSON null instead of a message.");
        }
        catch (EndOfStreamException exception)
        {
            throw new RpcFrameException("The peer closed the connection inside a frame.", exception);
        }
        catch (JsonException exception)
        {
            throw new RpcFrameException("A frame is not a valid RPC message.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new RpcFrameException("A frame has an unsupported message type.", exception);
        }
    }

    /// <summary>Writes one message. Nothing is written when the message exceeds the frame limit.</summary>
    public static async ValueTask WriteAsync(Stream stream, RpcMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, WorkbenchJson.Options);
        if (payload.Length > MaxFrameBytes)
        {
            throw new RpcFrameException(string.Create(
                CultureInfo.InvariantCulture,
                $"A {payload.Length}-byte message exceeds the {MaxFrameBytes}-byte frame limit."));
        }

        byte[] frame = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, HeaderBytes);

        // One write per frame, so a frame is never split by another writer's bytes.
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

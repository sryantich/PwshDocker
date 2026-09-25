using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PwshDocker;

/// <summary>Stream identifiers used by the engine's multiplexed attach/logs/exec format.</summary>
public enum DockerStreamType
{
    StdIn = 0,
    StdOut = 1,
    StdErr = 2,
    /// <summary>Errors reported by the engine itself (not the container process).</summary>
    SystemError = 3,
}

/// <summary>One chunk of container output.</summary>
public readonly record struct DockerFrame(DockerStreamType Stream, byte[] Payload);

/// <summary>
/// Reads container output streams. Non-TTY streams are multiplexed: each frame has an 8-byte header
/// [stream type, 0, 0, 0, size (uint32 big-endian)] followed by the payload. TTY streams are raw bytes.
/// </summary>
public static class MultiplexedStreamReader
{
    public static async IAsyncEnumerable<DockerFrame> ReadAsync(Stream stream, bool tty,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (tty)
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    yield break;
                }

                yield return new DockerFrame(DockerStreamType.StdOut, buffer.AsSpan(0, read).ToArray());
            }
        }

        var header = new byte[8];
        while (true)
        {
            if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }

            var type = (DockerStreamType)header[0];
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var payload = new byte[size];
            if (size > 0 && !await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            {
                throw new EndOfStreamException("The engine closed the stream in the middle of a frame.");
            }

            yield return new DockerFrame(type, payload);
        }
    }

    /// <summary>Reads every frame from a stream synchronously (for small, finite streams and tests).</summary>
    public static DockerFrame[] ReadAll(Stream stream, bool tty)
    {
        var frames = new List<DockerFrame>();
        var enumerator = ReadAsync(stream, tty).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                frames.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return frames.ToArray();
    }

    /// <summary>Fills the buffer; returns false if the stream ended before any byte was read.</summary>
    private static async ValueTask<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The engine closed the stream in the middle of a frame header.");
            }

            total += read;
        }

        return true;
    }
}

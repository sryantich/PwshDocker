using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PwshDocker;

/// <summary>
/// Reads the engine's newline-delimited JSON streams (image pull/push/load progress, build output, events, stats).
/// </summary>
public static class JsonMessageReader
{
    /// <summary>Yields one JSON element per line as it arrives. Blank lines are skipped.</summary>
    public static async IAsyncEnumerable<JsonElement> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        using var pending = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                pending.Write(buffer, start, i - start);
                start = i + 1;
                if (TryParse(pending, out var element))
                {
                    yield return element;
                }

                pending.SetLength(0);
            }

            if (start < read)
            {
                pending.Write(buffer, start, read - start);
            }
        }

        if (TryParse(pending, out var last))
        {
            yield return last;
        }
    }

    /// <summary>Reads every message from a stream synchronously (for small, finite streams and tests).</summary>
    public static JsonElement[] ReadAll(Stream stream)
    {
        var messages = new List<JsonElement>();
        var enumerator = ReadAsync(stream).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                messages.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return messages.ToArray();
    }

    /// <summary>Returns the error text when a stream message reports a failure ({"error": ...} or {"errorDetail": ...}).</summary>
    public static string? GetError(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (message.TryGetProperty("errorDetail", out var detail) && detail.ValueKind == JsonValueKind.Object &&
            detail.TryGetProperty("message", out var detailMessage) && detailMessage.ValueKind == JsonValueKind.String)
        {
            return detailMessage.GetString();
        }

        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        return null;
    }

    private static bool TryParse(MemoryStream pending, out JsonElement element)
    {
        var span = pending.GetBuffer().AsSpan(0, (int)pending.Length).Trim(" \t\r\n"u8);
        if (span.IsEmpty)
        {
            element = default;
            return false;
        }

        using var document = JsonDocument.Parse(span.ToArray());
        element = document.RootElement.Clone();
        return true;
    }
}

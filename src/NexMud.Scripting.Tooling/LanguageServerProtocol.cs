using System.Text;
using System.Text.Json;

namespace NexMud.Scripting.Tooling;

public static class LanguageServerProtocolFraming
{
    private const int MaximumHeaderLineBytes = 8 * 1024;
    private const int MaximumMessageBytes = 16 * 1024 * 1024;

    public static async Task WriteAsync(
        Stream stream,
        JsonElement message,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(writeGate);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public static async Task<JsonElement?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int? contentLength = null;
        while (true)
        {
            string? line = await ReadHeaderLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null) return null;
            if (line.Length == 0) break;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out int parsed) || parsed < 0 || parsed > MaximumMessageBytes)
                    throw new InvalidDataException($"Invalid LSP Content-Length '{value}'.");
                contentLength = parsed;
            }
        }

        if (contentLength is null)
            throw new InvalidDataException("LSP message did not include Content-Length.");

        byte[] body = new byte[contentLength.Value];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("LSP stream ended before the declared message body was complete.");
            offset += read;
        }

        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<string?> ReadHeaderLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        List<byte> bytes = [];
        byte[] buffer = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (bytes.Count == 0) return null;
                throw new EndOfStreamException("LSP stream ended inside a header line.");
            }

            byte value = buffer[0];
            if (value == (byte)'\n') break;
            if (value != (byte)'\r') bytes.Add(value);
            if (bytes.Count > MaximumHeaderLineBytes)
                throw new InvalidDataException("LSP header line exceeded the supported length.");
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}

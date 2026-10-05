using System.Text.Json;
using NexMud.Scripting.Tooling;

namespace NexMud.Tests;

internal static class Phase6LanguageServerTests
{
    public static async Task FramingRoundTripsUtf8Payload()
    {
        JsonElement message = JsonSerializer.SerializeToElement(new
        {
            jsonrpc = "2.0",
            id = "17",
            method = "textDocument/hover",
            @params = new { label = "héllo ∑" }
        });
        await using MemoryStream stream = new();
        using SemaphoreSlim gate = new(1, 1);
        await LanguageServerProtocolFraming.WriteAsync(stream, message, gate);
        stream.Position = 0;
        JsonElement value = await LanguageServerProtocolFraming.ReadAsync(stream)
            ?? throw new InvalidOperationException("LSP framing did not return a message.");
        if (value.GetProperty("method").GetString() != "textDocument/hover")
            throw new InvalidOperationException("LSP framing did not round-trip the request.");
        if (value.GetProperty("params").GetProperty("label").GetString() != "héllo ∑")
            throw new InvalidOperationException("LSP Content-Length handling must preserve UTF-8 payload bytes.");
    }
}

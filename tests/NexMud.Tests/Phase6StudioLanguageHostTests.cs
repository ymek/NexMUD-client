using System.Text.Json;
using NexMud.Gui.AutomationStudio;
using NexMud.Scripting.Tooling;

namespace NexMud.Tests;

internal static class Phase6StudioLanguageHostTests
{
    public static async Task RehydratesUnsavedDocumentsAfterTransportRestart()
    {
        Queue<FakeTransport> transports = new();
        FakeTransport first = new();
        FakeTransport second = new();
        transports.Enqueue(first);
        transports.Enqueue(second);
        await using StudioTypeScriptLanguageHost host = new(
            _ => Path.GetTempPath(),
            _ => transports.Dequeue());

        const string uri = "file:///tmp/nexmud-language-host/main.ts";
        await host.OpenDocumentAsync("profile-a", uri, "src/main.ts", 1, "export const value = 1;");
        Assert.Equal(1, first.StartCount);
        Assert.Equal("export const value = 1;", first.Opened.Single().Text);

        await host.ChangeDocumentAsync("profile-a", uri, 2, "export const value = 2;");
        Assert.Equal("export const value = 2;", first.Changed.Single().Text);

        first.IsRunning = false;
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        _ = await host.RequestAsync("profile-a", "textDocument/hover", empty);

        Assert.Equal(1, second.StartCount);
        FakeDocument replay = Assert.Single(second.Opened);
        Assert.Equal(2, replay.Version);
        Assert.Equal("export const value = 2;", replay.Text);
    }

    public static Task LanguageIdsFollowScriptSourceKinds()
    {
        Assert.Equal("typescript", StudioTypeScriptLanguageHost.LanguageIdForPath("src/main.ts"));
        Assert.Equal("typescript", StudioTypeScriptLanguageHost.LanguageIdForPath("src/types.d.ts"));
        Assert.Equal("javascript", StudioTypeScriptLanguageHost.LanguageIdForPath("src/helper.js"));
        Assert.Equal<string?>(null, StudioTypeScriptLanguageHost.LanguageIdForPath("data/config.json"));
        return Task.CompletedTask;
    }

    private sealed record FakeDocument(string Uri, string LanguageId, int Version, string Text);

    private static class Assert
    {
        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }

        public static T Single<T>(IEnumerable<T> values)
        {
            T[] items = values.ToArray();
            if (items.Length != 1)
                throw new InvalidOperationException($"Expected one item, got {items.Length}.");
            return items[0];
        }
    }

    private sealed class FakeTransport : IStudioTypeScriptTransport
    {
        public bool IsRunning { get; set; } = true;
        public int StartCount { get; private set; }
        public List<FakeDocument> Opened { get; } = [];
        public List<FakeDocument> Changed { get; } = [];

        public event Action<LanguageServerNotification>? NotificationReceived { add { } remove { } }
        public event Action<string>? StandardErrorReceived { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken = default,
            TimeSpan? timeout = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(JsonSerializer.SerializeToElement<object?>(null));
        }

        public Task DidOpenAsync(
            string uri,
            string languageId,
            int version,
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(new FakeDocument(uri, languageId, version, text));
            return Task.CompletedTask;
        }

        public Task DidChangeAsync(
            string uri,
            int version,
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Changed.Add(new FakeDocument(uri, "typescript", version, text));
            return Task.CompletedTask;
        }

        public Task DidSaveAsync(string uri, string? text = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DidCloseAsync(string uri, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }
}

using System.Text.Json;
using NexMud.Gui;
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
        string root = Path.Combine(Path.GetTempPath(), "nexmud-language-host");
        await using StudioTypeScriptLanguageHost host = new(
            _ => root,
            _ => transports.Dequeue());

        string uri = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(Path.Combine(root, "main.ts"));
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

    public static async Task RoutesRequestsDiagnosticsAndProfileLifecycle()
    {
        Queue<FakeTransport> transports = new();
        FakeTransport first = new();
        FakeTransport second = new();
        transports.Enqueue(first);
        transports.Enqueue(second);
        string firstRoot = Path.Combine(Path.GetTempPath(), "nexmud-profile-a");
        string secondRoot = Path.Combine(Path.GetTempPath(), "nexmud-profile-b");
        await using StudioTypeScriptLanguageHost host = new(
            profile => profile == "profile-a" ? firstRoot : secondRoot,
            _ => transports.Dequeue());
        StudioLanguageDiagnostics? received = null;
        host.DiagnosticsPublished += diagnostics => received = diagnostics;
        string uri = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(Path.Combine(firstRoot, "pkg", "src", "main.ts"));
        await host.OpenDocumentAsync("profile-a", uri, "src/main.ts", 1, "const n = 1;");
        Assert.Equal(true, host.IsOpenDocument("profile-a", uri));
        Assert.Equal(false, host.IsOpenDocument("profile-b", uri));

        JsonElement parameters = JsonSerializer.SerializeToElement(new { textDocument = new { uri }, position = new { line = 0, character = 6 } });
        _ = await host.RequestAsync("profile-a", "textDocument/hover", parameters);
        var hoverRequest = first.Requests.Single(request => request.Method == "textDocument/hover");
        Assert.Equal("textDocument/hover", hoverRequest.Method);
        Assert.Equal(uri, hoverRequest.Parameters is JsonElement sent
            ? sent.GetProperty("textDocument").GetProperty("uri").GetString()
            : null);
        await host.ChangeDocumentAsync("profile-a", uri, 2, "const n = 2;");
        await host.ChangeDocumentAsync("profile-a", uri, 1, "stale change");
        await host.SaveDocumentAsync("profile-a", uri, "const n = 2;");
        Assert.Equal("const n = 2;", first.Changed.Single().Text);
        Assert.Equal("const n = 2;", first.Saved.Single().Text);
        first.Publish("textDocument/publishDiagnostics", new { uri, diagnostics = new[] { new { message = "problem" } } });
        Assert.Equal(uri, received?.Uri);

        await host.CloseDocumentAsync("profile-a", uri);
        Assert.Equal(false, host.IsOpenDocument("profile-a", uri));
        Assert.Equal(uri, first.Closed.Single());
        received = null;
        first.Publish("textDocument/publishDiagnostics", new { uri, diagnostics = new[] { new { message = "closed document" } } });
        Assert.Equal<StudioLanguageDiagnostics?>(null, received);
        string unsavedUri = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(Path.Combine(firstRoot, "pkg", "other.ts"));
        await host.OpenDocumentAsync("profile-a", unsavedUri, "other.ts", 1, "export const unsaved = true;");
        await host.SwitchProfileAsync("profile-b");
        Assert.Equal(false, host.IsOpenDocument("profile-a", unsavedUri));
        Assert.Equal(false, first.IsRunning);
        string profileBUri = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(Path.Combine(secondRoot, "pkg", "main.ts"));
        await host.OpenDocumentAsync("profile-b", profileBUri, "main.ts", 1, "export {};");
        Assert.Equal(true, host.IsOpenDocument("profile-b", profileBUri));
        Assert.Equal(1, second.StartCount);
        Assert.Equal(1, second.Opened.Count);
        Assert.Equal(profileBUri, second.Opened.Single().Uri);
        second.Publish("textDocument/publishDiagnostics", new { uri = profileBUri, diagnostics = new[] { new { message = "active" } } });
        Assert.Equal(profileBUri, received?.Uri);
        second.Publish("textDocument/publishDiagnostics", new { uri, diagnostics = new[] { new { message = "stale profile" } } });
        Assert.Equal(profileBUri, received?.Uri);
    }

    public static Task LanguageServerRequestsAreAllowListed()
    {
        foreach (string method in new[]
        {
            "textDocument/completion",
            "completionItem/resolve",
            "textDocument/hover",
            "textDocument/signatureHelp",
            "textDocument/definition",
            "textDocument/typeDefinition",
            "textDocument/references",
            "textDocument/rename",
            "textDocument/documentSymbol"
        })
            Assert.Equal(true, MonacoEditorHost.IsAllowedLanguageServerRequest(method));

        Assert.Equal(false, MonacoEditorHost.IsAllowedLanguageServerRequest("workspace/executeCommand"));
        Assert.Equal(false, MonacoEditorHost.IsAllowedLanguageServerRequest("textDocument/didChange"));
        Assert.Equal(false, MonacoEditorHost.IsAllowedLanguageServerRequest("unknown"));
        return Task.CompletedTask;
    }

    public static async Task UriValidationRejectsTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexmud-lsp-uri-" + Guid.NewGuid().ToString("N"));
        await using StudioTypeScriptLanguageHost host = new(_ => root, _ => new FakeTransport());
        string safe = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(Path.Combine(root, "pkg", "src", "main.ts"));
        string outside = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(Path.Combine(root, "..", "outside.ts"));
        Assert.Equal(true, host.IsCanonicalDocumentUri("profile-a", safe));
        Assert.Equal(false, host.IsCanonicalDocumentUri("profile-a", outside));
        bool rejected = false;
        try { await host.OpenDocumentAsync("profile-a", outside, "../outside.ts", 1, ""); }
        catch (ArgumentException) { rejected = true; }
        Assert.Equal(true, rejected);
    }

    public static async Task BundledLanguageServerUsesProjectTypesAndReportsLiveDiagnostics()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nexmud-studio-lsp-" + Guid.NewGuid().ToString("N"));
        string packageRoot = Path.Combine(workspaceRoot, "lsp-test");
        string sourceRoot = Path.Combine(packageRoot, "src");
        string dependencyRoot = Path.Combine(packageRoot, "node_modules", "pure-greeter");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(dependencyRoot);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(packageRoot, "package.json"),
                $$"""
                {
                  "name": "lsp-test",
                  "version": "1.0.0",
                  "type": "module",
                  "packageManager": "pnpm@10.6.5",
                  "dependencies": { "pure-greeter": "1.0.0" },
                  "nexmud": {
                    "id": "lsp-test",
                    "apiVersion": "{{NexMud.Scripting.TypeScript.Declarations.NexMudTypeDeclarations.ApiVersion}}",
                    "entry": "src/main.ts",
                    "permissions": []
                  }
                }
                """).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(dependencyRoot, "package.json"),
                "{\"name\":\"pure-greeter\",\"version\":\"1.0.0\",\"type\":\"module\",\"types\":\"index.d.ts\",\"exports\":{\".\":{\"types\":\"./index.d.ts\",\"import\":\"./index.js\"}}}")
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(dependencyRoot, "index.d.ts"),
                "export declare function greet(name: string): string;\n").ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(dependencyRoot, "index.js"),
                "export function greet(name) { return `hello ${name}`; }\n").ConfigureAwait(false);

            string helperSource = "import { greet } from 'pure-greeter';\n" +
                                  "import type { NexMudApi } from '@nexmud/api';\n" +
                                  "export const label = greet('world');\n" +
                                  "export function roomName(api: NexMudApi): string | null { return api.state.room.name; }\n" +
                                  "export const invalid = greet(42);\n";
            string mainSource = "import { label, roomName } from './helper';\n" +
                                "export const title = label;\n" +
                                "export const room = roomName(nex);\n" +
                                "export const api = nex.state;\n";
            string helperPath = Path.Combine(sourceRoot, "helper.ts");
            string mainPath = Path.Combine(sourceRoot, "main.ts");
            await File.WriteAllTextAsync(helperPath, helperSource).ConfigureAwait(false);
            await File.WriteAllTextAsync(mainPath, mainSource).ConfigureAwait(false);

            NexMud.Client.Scripting.ScriptPackageDocument manifest = NexMud.Client.Scripting.ScriptPackageDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(packageRoot, "package.json")).ConfigureAwait(false));
            NexMud.Client.Scripting.ScriptPackageCatalogEntry package = new(
                "lsp-test", packageRoot, manifest, true);
            await NexMud.Client.Scripting.ScriptLanguageProjectProjection.EnsureAsync(
                workspaceRoot,
                [package],
                NexMud.Scripting.TypeScript.Declarations.NexMudTypeDeclarations.Source).ConfigureAwait(false);

            string helperUri = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(helperPath);
            string mainUri = NexMud.Client.Scripting.ScriptLanguageProjectProjection.ToCanonicalFileUri(mainPath);
            TaskCompletionSource<StudioLanguageDiagnostics> diagnosticsCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            await using StudioTypeScriptLanguageHost host = new(_ => workspaceRoot);
            List<string> languageOutput = [];
            host.OutputReceived += languageOutput.Add;
            host.DiagnosticsPublished += diagnostics =>
            {
                if (string.Equals(diagnostics.Uri, helperUri, StringComparison.Ordinal))
                    diagnosticsCompletion.TrySetResult(diagnostics);
            };

            await host.OpenDocumentAsync("integration", helperUri, "src/helper.ts", 1, helperSource, timeout.Token);
            await host.OpenDocumentAsync("integration", mainUri, "src/main.ts", 1, mainSource, timeout.Token);
            StudioLanguageDiagnostics liveDiagnostics = await diagnosticsCompletion.Task.WaitAsync(timeout.Token);
            JsonElement diagnostics = liveDiagnostics.Parameters.GetProperty("diagnostics");
            if (diagnostics.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Unexpected live diagnostic payload: {liveDiagnostics.Parameters.GetRawText()}");
            Assert.Equal(true, diagnostics.EnumerateArray().Any(item =>
                item.TryGetProperty("message", out JsonElement message) &&
                message.GetString()?.Contains("not assignable to parameter of type 'string'", StringComparison.Ordinal) == true));

            string titleLine = mainSource.Split('\n')[1];
            JsonElement definition = await host.RequestAsync(
                "integration",
                "textDocument/definition",
                JsonSerializer.SerializeToElement(new
                {
                    textDocument = new { uri = mainUri },
                    position = new { line = 1, character = titleLine.IndexOf("label", StringComparison.Ordinal) }
                }),
                timeout.Token);
            JsonElement location = definition.ValueKind == JsonValueKind.Array ? definition[0] : definition;
            string? targetUri = location.TryGetProperty("targetUri", out JsonElement target)
                ? target.GetString()
                : location.TryGetProperty("uri", out JsonElement uriElement) ? uriElement.GetString() : null;
            Assert.Equal(helperUri, targetUri);

            string apiLine = mainSource.Split('\n')[3];
            JsonElement completion = await host.RequestAsync(
                "integration",
                "textDocument/completion",
                JsonSerializer.SerializeToElement(new
                {
                    textDocument = new { uri = mainUri },
                    position = new { line = 3, character = apiLine.IndexOf("nex.", StringComparison.Ordinal) + 4 },
                    context = new { triggerKind = 1 }
                }),
                timeout.Token);
            JsonElement completionItems = completion.ValueKind == JsonValueKind.Object &&
                                          completion.TryGetProperty("items", out JsonElement items)
                ? items
                : completion;
            if (completionItems.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Unexpected completion payload: {completion.GetRawText()}; {string.Join(" | ", languageOutput.TakeLast(5))}");
            Assert.Equal(true, completionItems.EnumerateArray().Any(item =>
                item.TryGetProperty("label", out JsonElement label) && label.GetString() == "state"));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
        }
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
        public List<FakeDocument> Saved { get; } = [];
        public List<(string Method, object? Parameters)> Requests { get; } = [];
        public List<string> Closed { get; } = [];

        public event Action<LanguageServerNotification>? NotificationReceived;
        public event Action<string>? StandardErrorReceived { add { } remove { } }
        public event Action<Exception>? Failed { add { } remove { } }

        public void Publish(string method, object parameters) =>
            NotificationReceived?.Invoke(new LanguageServerNotification(method, JsonSerializer.SerializeToElement(parameters)));

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
            Requests.Add((method, parameters));
            JsonElement response = method == "textDocument/diagnostic"
                ? JsonSerializer.SerializeToElement(new { kind = "full", items = Array.Empty<object>() })
                : JsonSerializer.SerializeToElement<object?>(null);
            return Task.FromResult(response);
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

        public Task DidSaveAsync(string uri, string? text = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(new FakeDocument(uri, "typescript", 0, text ?? string.Empty));
            return Task.CompletedTask;
        }

        public Task DidCloseAsync(string uri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Closed.Add(uri);
            return Task.CompletedTask;
        }

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

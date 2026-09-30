using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using NexMud.Scripting.TypeScript.Declarations;

namespace NexMud.Gui;

internal sealed record MonacoDocumentChanged(string Uri, int VersionId, int AlternativeVersionId);
internal sealed record MonacoSaveRequested(string? Uri, bool All);
internal sealed record MonacoSelectionChanged(string Uri, int StartLine, int StartColumn, int EndLine, int EndColumn);
internal sealed record MonacoEditorFailure(string Message);

/// <summary>
/// Narrow versioned bridge around Avalonia NativeWebView. The WebView has editor authority only;
/// all persistence, package state, build state, capabilities, and runtime lifecycle remain in C#.
/// </summary>
internal sealed class MonacoEditorHost : UserControl, IAsyncDisposable
{
    public const int EditorBridgeProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedIncomingMessages = new(StringComparer.Ordinal)
    {
        "editorReady",
        "documentChanged",
        "saveRequested",
        "selectionChanged",
        "activeDocumentChanged",
        "editorCommandInvoked",
        "editorFailure"
    };

    private readonly Grid _root = new();
    private readonly NativeWebView _webView = new();
    private readonly TextBlock _failure = new()
    {
        IsVisible = false,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Margin = new Avalonia.Thickness(16),
        Foreground = UiTheme.Danger
    };
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _initialized;
    private int _disposed;

    public MonacoEditorHost()
    {
        _root.Children.Add(_webView);
        _root.Children.Add(_failure);
        Content = _root;
        _webView.NavigationCompleted += HandleNavigationCompleted;
        _webView.WebMessageReceived += HandleWebMessageReceived;
    }

    public bool IsReady { get; private set; }
    public event Action? EditorReady;
    public event Action<MonacoDocumentChanged>? DocumentChanged;
    public event Action<MonacoSaveRequested>? SaveRequested;
    public event Action<MonacoSelectionChanged>? SelectionChanged;
    public event Action<string>? ActiveDocumentChanged;
    public event Action<MonacoEditorFailure>? EditorFailed;
    public event Action<string, JsonElement>? EditorCommandInvoked;

    public Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return Task.CompletedTask;
        string index = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco", "index.html");
        string loader = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco", "vs", "loader.js");
        if (!File.Exists(index) || !File.Exists(loader))
        {
            Fail("Monaco assets are unavailable. Run scripts/prepare-monaco-assets.mjs before packaging NexMUD.");
            return Task.CompletedTask;
        }
        _webView.Navigate(new Uri(index));
        return Task.CompletedTask;
    }

    public async Task OpenDocumentAsync(
        string profileId,
        string packageId,
        string relativePath,
        string content,
        bool readOnly = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("openDocument", new
        {
            uri = CreateDocumentUri(profileId, packageId, relativePath),
            path = relativePath,
            content,
            readOnly
        }).ConfigureAwait(false);
    }

    public async Task CloseDocumentAsync(string uri, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("closeDocument", new { uri }).ConfigureAwait(false);
    }

    public async Task SetActiveDocumentAsync(string uri, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("setActiveDocument", new { uri }).ConfigureAwait(false);
    }

    public async Task SetDocumentContentAsync(string uri, string content, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("setDocumentContent", new { uri, content }).ConfigureAwait(false);
    }

    public async Task SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("setReadOnly", new { readOnly }).ConfigureAwait(false);
    }

    public async Task FocusEditorAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("focus", new { }).ConfigureAwait(false);
    }

    public async Task SetThemeAsync(string theme, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("setTheme", new { theme }).ConfigureAwait(false);
    }

    public async Task SetFontAsync(string family, double size, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("setFont", new { family, size }).ConfigureAwait(false);
    }

    public Task RegisterSdkDeclarationsAsync() => SendAsync("registerSdkDeclarations", new
    {
        uri = "file:///nexmud-api.d.ts",
        content = NexMudTypeDeclarations.Source
    });

    public async Task RevealLocationAsync(string uri, int? line, int? column, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync("revealLocation", new { uri, line = line ?? 1, column = column ?? 1 }).ConfigureAwait(false);
    }

    public async Task<string> RequestDocumentContentAsync(string uri, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        JsonElement payload = await RequestAsync("requestDocumentContent", new { uri }, cancellationToken).ConfigureAwait(false);
        return payload.TryGetProperty("content", out JsonElement content) ? content.GetString() ?? string.Empty : string.Empty;
    }

    public async Task<JsonElement> RequestSymbolsAsync(string uri, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        return await RequestAsync("requestSymbols", new { uri }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (IsReady) return;
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
    }

    public static string CreateDocumentUri(string profileId, string packageId, string relativePath)
    {
        string profile = Uri.EscapeDataString(profileId.Trim());
        string package = Uri.EscapeDataString(packageId.Trim());
        string path = string.Join('/', relativePath.Replace('\\', '/').TrimStart('/').Split('/').Select(Uri.EscapeDataString));
        return $"nexmud://profile/{profile}/package/{package}/{path}";
    }

    private async Task<JsonElement> RequestAsync(string type, object payload, CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(requestId, completion)) throw new InvalidOperationException("Unable to register editor request.");
        try
        {
            JsonElement element = JsonSerializer.SerializeToElement(payload, JsonOptions);
            Dictionary<string, object?> request = new()
            {
                ["requestId"] = requestId
            };
            foreach (JsonProperty property in element.EnumerateObject())
                request[property.Name] = property.Value.Clone();
            await SendAsync(type, request).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(requestId, out _);
        }
    }

    private async Task SendAsync(string type, object payload)
    {
        if (!IsReady && type != "initialize" && type != "registerSdkDeclarations") return;
        string envelope = JsonSerializer.Serialize(new
        {
            version = EditorBridgeProtocolVersion,
            type,
            payload
        }, JsonOptions);
        string encodedEnvelope = JsonSerializer.Serialize(envelope, JsonOptions);
        try
        {
            await _webView.InvokeScript($"globalThis.nexmudEditorBridge?.receive(JSON.parse({encodedEnvelope}));").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail($"Monaco bridge failed: {exception.Message}");
        }
    }

    private async void HandleNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            Fail("Monaco editor failed to load its local page.");
            return;
        }
        try
        {
            await SendRawAsync("initialize", new
            {
                version = EditorBridgeProtocolVersion,
                theme = "vs-dark",
                fontFamily = "Menlo, SFMono-Regular, Consolas, monospace",
                fontSize = 13
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail($"Monaco editor initialization failed: {exception.Message}");
        }
    }

    private Task SendRawAsync(string type, object payload)
    {
        string envelope = JsonSerializer.Serialize(new
        {
            version = EditorBridgeProtocolVersion,
            type,
            payload
        }, JsonOptions);
        string encodedEnvelope = JsonSerializer.Serialize(envelope, JsonOptions);
        return _webView.InvokeScript($"globalThis.nexmudEditorBridge?.receive(JSON.parse({encodedEnvelope}));");
    }

    private void HandleWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        try
        {
            string body = args.Body ?? string.Empty;
            if (body.Length == 0)
            {
                Fail("Monaco editor sent an empty bridge message.");
                return;
            }
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("version", out JsonElement version) || version.GetInt32() != EditorBridgeProtocolVersion)
            {
                Fail("Monaco editor bridge protocol mismatch.");
                return;
            }
            string type = root.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            if (!AllowedIncomingMessages.Contains(type))
            {
                Fail($"Monaco editor sent an unsupported bridge message: {type}");
                return;
            }
            JsonElement payload = root.TryGetProperty("payload", out JsonElement payloadElement)
                ? payloadElement.Clone()
                : JsonSerializer.SerializeToElement(new { }, JsonOptions);
            Dispatch(type, payload);
        }
        catch (Exception exception)
        {
            Fail($"Monaco editor message was invalid: {exception.Message}");
        }
    }

    private void Dispatch(string type, JsonElement payload)
    {
        switch (type)
        {
            case "editorReady":
                IsReady = true;
                _failure.IsVisible = false;
                _ready.TrySetResult(true);
                _ = RegisterSdkDeclarationsAsync();
                EditorReady?.Invoke();
                break;
            case "documentChanged":
                DocumentChanged?.Invoke(new MonacoDocumentChanged(
                    payload.GetProperty("uri").GetString() ?? string.Empty,
                    payload.GetProperty("versionId").GetInt32(),
                    payload.GetProperty("alternativeVersionId").GetInt32()));
                break;
            case "saveRequested":
                SaveRequested?.Invoke(new MonacoSaveRequested(
                    payload.TryGetProperty("uri", out JsonElement uri) && uri.ValueKind == JsonValueKind.String ? uri.GetString() : null,
                    payload.TryGetProperty("all", out JsonElement all) && all.GetBoolean()));
                break;
            case "selectionChanged":
                SelectionChanged?.Invoke(new MonacoSelectionChanged(
                    payload.GetProperty("uri").GetString() ?? string.Empty,
                    payload.GetProperty("startLine").GetInt32(),
                    payload.GetProperty("startColumn").GetInt32(),
                    payload.GetProperty("endLine").GetInt32(),
                    payload.GetProperty("endColumn").GetInt32()));
                break;
            case "activeDocumentChanged":
                ActiveDocumentChanged?.Invoke(payload.GetProperty("uri").GetString() ?? string.Empty);
                break;
            case "editorFailure":
                Fail(payload.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? "Editor failure." : "Editor failure.");
                break;
            case "editorCommandInvoked":
                HandleEditorCommand(payload);
                break;
        }
    }

    private void HandleEditorCommand(JsonElement payload)
    {
        string command = payload.TryGetProperty("command", out JsonElement commandElement)
            ? commandElement.GetString() ?? string.Empty
            : string.Empty;
        if ((command is "documentContent" or "symbolsResult") &&
            payload.TryGetProperty("requestId", out JsonElement requestIdElement) &&
            _requests.TryRemove(requestIdElement.GetString() ?? string.Empty, out TaskCompletionSource<JsonElement>? completion))
        {
            completion.TrySetResult(payload);
            return;
        }
        EditorCommandInvoked?.Invoke(command, payload);
    }

    private void Fail(string message)
    {
        IsReady = false;
        _ready.TrySetException(new InvalidOperationException(message));
        _failure.Text = message;
        _failure.IsVisible = true;
        EditorFailed?.Invoke(new MonacoEditorFailure(message));
        foreach (TaskCompletionSource<JsonElement> completion in _requests.Values)
            completion.TrySetException(new InvalidOperationException(message));
        _requests.Clear();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _webView.NavigationCompleted -= HandleNavigationCompleted;
        _webView.WebMessageReceived -= HandleWebMessageReceived;
        foreach (TaskCompletionSource<JsonElement> completion in _requests.Values)
            completion.TrySetCanceled();
        _requests.Clear();
        return ValueTask.CompletedTask;
    }
}

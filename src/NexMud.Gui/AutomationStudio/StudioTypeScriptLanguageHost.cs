using System.Collections.Concurrent;
using System.Text.Json;
using NexMud.Client.Scripting;
using NexMud.Scripting.Tooling;

namespace NexMud.Gui.AutomationStudio;

internal interface IStudioTypeScriptTransport : IAsyncDisposable
{
    bool IsRunning { get; }
    event Action<LanguageServerNotification>? NotificationReceived;
    event Action<string>? StandardErrorReceived;
    event Action<Exception>? Failed;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null);
    Task DidOpenAsync(
        string uri,
        string languageId,
        int version,
        string text,
        CancellationToken cancellationToken = default);
    Task DidChangeAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default);
    Task DidSaveAsync(
        string uri,
        string? text = null,
        CancellationToken cancellationToken = default);
    Task DidCloseAsync(string uri, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

internal sealed class ToolingTypeScriptTransport : IStudioTypeScriptTransport
{
    private readonly TypeScriptLanguageService _inner;

    public ToolingTypeScriptTransport(string workspaceRoot) =>
        _inner = new TypeScriptLanguageService(workspaceRoot);

    public bool IsRunning => _inner.IsRunning;

    public event Action<LanguageServerNotification>? NotificationReceived
    {
        add => _inner.NotificationReceived += value;
        remove => _inner.NotificationReceived -= value;
    }

    public event Action<string>? StandardErrorReceived
    {
        add => _inner.StandardErrorReceived += value;
        remove => _inner.StandardErrorReceived -= value;
    }

    public event Action<Exception>? Failed
    {
        add => _inner.Failed += value;
        remove => _inner.Failed -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _inner.StartAsync(cancellationToken);

    public Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null) =>
        _inner.RequestAsync(method, parameters, cancellationToken, timeout);

    public Task DidOpenAsync(
        string uri,
        string languageId,
        int version,
        string text,
        CancellationToken cancellationToken = default) =>
        _inner.DidOpenAsync(uri, languageId, version, text, cancellationToken);

    public Task DidChangeAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default) =>
        _inner.DidChangeAsync(uri, version, text, cancellationToken);

    public Task DidSaveAsync(
        string uri,
        string? text = null,
        CancellationToken cancellationToken = default) =>
        _inner.DidSaveAsync(uri, text, cancellationToken);

    public Task DidCloseAsync(string uri, CancellationToken cancellationToken = default) =>
        _inner.DidCloseAsync(uri, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _inner.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

internal sealed record StudioLanguageDiagnostics(string Uri, JsonElement Parameters);

/// <summary>
/// Coordinates one filesystem-aware TypeScript language service for the active Studio profile.
/// Open editor buffers are retained in-memory so a crashed/restarted server can be rehydrated from
/// the unsaved Monaco state rather than stale disk content.
/// </summary>
internal sealed class StudioTypeScriptLanguageHost : IAsyncDisposable
{
    private sealed record OpenDocumentState(
        string Uri,
        string LanguageId,
        int Version,
        string Text);

    private readonly Func<string, string> _workspaceRoot;
    private readonly Func<string, IStudioTypeScriptTransport> _transportFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, OpenDocumentState> _documents = new(StringComparer.Ordinal);
    private IStudioTypeScriptTransport? _transport;
    private string? _profileId;
    private int _disposed;

    public StudioTypeScriptLanguageHost(
        Func<string, string> workspaceRoot,
        Func<string, IStudioTypeScriptTransport>? transportFactory = null)
    {
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _transportFactory = transportFactory ?? (root => new ToolingTypeScriptTransport(root));
    }

    public bool IsRunning => _transport?.IsRunning == true;

    public event Action<StudioLanguageDiagnostics>? DiagnosticsPublished;
    public event Action<string>? OutputReceived;
    public event Action<string>? FailureReceived;

    public async Task OpenDocumentAsync(
        string profileId,
        string uri,
        string relativePath,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        string? languageId = LanguageIdForPath(relativePath);
        if (languageId is null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(text);
        EnsureDocumentUri(profileId, uri);

        IStudioTypeScriptTransport transport;
        bool started;
        OpenDocumentState[] documentsToRefresh;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureProfileLockedAsync(profileId).ConfigureAwait(false);
            _documents[uri] = new OpenDocumentState(uri, languageId, version, text);
            (transport, started) = await EnsureStartedLockedAsync(cancellationToken).ConfigureAwait(false);
            documentsToRefresh = started ? _documents.Values.ToArray() : [_documents[uri]];
        }
        finally
        {
            _gate.Release();
        }

        if (!started)
            await transport.DidOpenAsync(uri, languageId, version, text, cancellationToken).ConfigureAwait(false);
        ScheduleDiagnosticsRefresh(transport, profileId, documentsToRefresh, cancellationToken);
    }

    public async Task ChangeDocumentAsync(
        string profileId,
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        EnsureDocumentUri(profileId, uri);
        IStudioTypeScriptTransport transport;
        bool started;
        OpenDocumentState[] documentsToRefresh;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureProfileLockedAsync(profileId).ConfigureAwait(false);
            if (!_documents.TryGetValue(uri, out OpenDocumentState? state) || version <= state.Version) return;
            OpenDocumentState changed = state with { Version = version, Text = text };
            _documents[uri] = changed;
            (transport, started) = await EnsureStartedLockedAsync(cancellationToken).ConfigureAwait(false);
            documentsToRefresh = started ? _documents.Values.ToArray() : [changed];
        }
        finally
        {
            _gate.Release();
        }

        // A newly-started transport was hydrated from the latest in-memory document states.
        if (!started)
            await transport.DidChangeAsync(uri, version, text, cancellationToken).ConfigureAwait(false);
        ScheduleDiagnosticsRefresh(transport, profileId, documentsToRefresh, cancellationToken);
    }

    public async Task SaveDocumentAsync(
        string profileId,
        string uri,
        string text,
        CancellationToken cancellationToken = default)
    {
        EnsureDocumentUri(profileId, uri);
        IStudioTypeScriptTransport transport;
        bool started;
        OpenDocumentState[] documentsToRefresh;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureProfileLockedAsync(profileId).ConfigureAwait(false);
            if (_documents.TryGetValue(uri, out OpenDocumentState? state))
                _documents[uri] = state with { Text = text };
            (transport, started) = await EnsureStartedLockedAsync(cancellationToken).ConfigureAwait(false);
            documentsToRefresh = started ? _documents.Values.ToArray() :
                _documents.TryGetValue(uri, out OpenDocumentState? document) ? [document] : [];
        }
        finally
        {
            _gate.Release();
        }

        await transport.DidSaveAsync(uri, text, cancellationToken).ConfigureAwait(false);
        ScheduleDiagnosticsRefresh(transport, profileId, documentsToRefresh, cancellationToken);
    }

    public async Task CloseDocumentAsync(
        string profileId,
        string uri,
        CancellationToken cancellationToken = default)
    {
        EnsureDocumentUri(profileId, uri);
        IStudioTypeScriptTransport? transport;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!string.Equals(_profileId, profileId, StringComparison.Ordinal)) return;
            if (!_documents.TryRemove(uri, out _)) return;
            transport = _transport is { IsRunning: true } current ? current : null;
        }
        finally
        {
            _gate.Release();
        }

        if (transport is not null)
            await transport.DidCloseAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> RequestAsync(
        string profileId,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        IStudioTypeScriptTransport transport;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureProfileLockedAsync(profileId).ConfigureAwait(false);
            if (parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("textDocument", out JsonElement textDocument) &&
                textDocument.ValueKind == JsonValueKind.Object &&
                textDocument.TryGetProperty("uri", out JsonElement documentUri) &&
                documentUri.ValueKind == JsonValueKind.String)
                EnsureDocumentUri(profileId, documentUri.GetString() ?? string.Empty);
            (transport, _) = await EnsureStartedLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return await transport.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAsync(string profileId, CancellationToken cancellationToken = default)
    {
        IStudioTypeScriptTransport transport;
        OpenDocumentState[] documentsToRefresh;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureProfileLockedAsync(profileId).ConfigureAwait(false);
            await DisposeTransportLockedAsync().ConfigureAwait(false);
            (transport, _) = await EnsureStartedLockedAsync(cancellationToken).ConfigureAwait(false);
            documentsToRefresh = _documents.Values.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        ScheduleDiagnosticsRefresh(transport, profileId, documentsToRefresh, cancellationToken);
    }

    public async Task SwitchProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await DisposeTransportLockedAsync().ConfigureAwait(false);
            _documents.Clear();
            _profileId = profileId;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal bool IsOpenDocument(string profileId, string uri) =>
        string.Equals(Volatile.Read(ref _profileId), profileId, StringComparison.Ordinal) &&
        _documents.ContainsKey(uri) &&
        IsCanonicalDocumentUri(profileId, uri);

    internal bool IsCanonicalDocumentUri(string profileId, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || !parsed.IsFile) return false;
        string root = Path.GetFullPath(_workspaceRoot(profileId));
        string path = Path.GetFullPath(parsed.LocalPath);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(rootPrefix, comparison) &&
               string.Equals(ScriptLanguageProjectProjection.ToCanonicalFileUri(path), uri, StringComparison.Ordinal);
    }

    private void EnsureDocumentUri(string profileId, string uri)
    {
        if (!IsCanonicalDocumentUri(profileId, uri))
            throw new ArgumentException("TypeScript document URI must be a canonical file URI inside the active profile workspace.", nameof(uri));
    }

    internal static string? LanguageIdForPath(string relativePath)
    {
        if (relativePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            return "typescript";
        if (relativePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
            return "javascript";
        return null;
    }

    private async Task EnsureProfileLockedAsync(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (_profileId is null)
        {
            Volatile.Write(ref _profileId, profileId);
            return;
        }
        if (string.Equals(_profileId, profileId, StringComparison.Ordinal)) return;

        await DisposeTransportLockedAsync().ConfigureAwait(false);
        _documents.Clear();
        Volatile.Write(ref _profileId, profileId);
    }

    private async Task<(IStudioTypeScriptTransport Transport, bool Started)> EnsureStartedLockedAsync(
        CancellationToken cancellationToken)
    {
        if (_transport is { IsRunning: true } running) return (running, false);
        await DisposeTransportLockedAsync().ConfigureAwait(false);

        string profileId = _profileId
            ?? throw new InvalidOperationException("A Studio profile must be selected before starting TypeScript tooling.");
        IStudioTypeScriptTransport transport = _transportFactory(_workspaceRoot(profileId));
        WireTransport(transport);
        try
        {
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
            foreach (OpenDocumentState document in _documents.Values.OrderBy(value => value.Uri, StringComparer.Ordinal))
            {
                await transport.DidOpenAsync(
                    document.Uri,
                    document.LanguageId,
                    document.Version,
                    document.Text,
                    cancellationToken).ConfigureAwait(false);
            }
            _transport = transport;
            OutputReceived?.Invoke("TypeScript language service started.");
            return (transport, true);
        }
        catch
        {
            UnwireTransport(transport);
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void WireTransport(IStudioTypeScriptTransport transport)
    {
        transport.NotificationReceived += TransportNotificationReceived;
        transport.StandardErrorReceived += TransportStandardErrorReceived;
        transport.Failed += TransportFailed;
    }

    private void UnwireTransport(IStudioTypeScriptTransport transport)
    {
        transport.NotificationReceived -= TransportNotificationReceived;
        transport.StandardErrorReceived -= TransportStandardErrorReceived;
        transport.Failed -= TransportFailed;
    }

    private void ScheduleDiagnosticsRefresh(
        IStudioTypeScriptTransport transport,
        string profileId,
        IEnumerable<OpenDocumentState> documents,
        CancellationToken cancellationToken)
    {
        foreach (OpenDocumentState document in documents)
            _ = RefreshDiagnosticsAsync(transport, profileId, document, cancellationToken);
    }

    private async Task RefreshDiagnosticsAsync(
        IStudioTypeScriptTransport transport,
        string profileId,
        OpenDocumentState document,
        CancellationToken cancellationToken)
    {
        try
        {
            JsonElement report = await transport.RequestAsync(
                "textDocument/diagnostic",
                new { textDocument = new { uri = document.Uri } },
                cancellationToken,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!report.TryGetProperty("items", out JsonElement diagnostics) ||
                diagnostics.ValueKind != JsonValueKind.Array)
                return;

            JsonElement parameters = JsonSerializer.SerializeToElement(new
            {
                uri = document.Uri,
                version = document.Version,
                diagnostics
            });
            if (!string.Equals(Volatile.Read(ref _profileId), profileId, StringComparison.Ordinal) ||
                !ReferenceEquals(Volatile.Read(ref _transport), transport))
                return;
            TransportNotificationReceived(new LanguageServerNotification("textDocument/publishDiagnostics", parameters));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            OutputReceived?.Invoke($"TypeScript diagnostics request failed: {exception.Message}");
        }
    }

    private void TransportNotificationReceived(LanguageServerNotification notification)
    {
        if (!notification.Method.Equals("textDocument/publishDiagnostics", StringComparison.Ordinal)) return;
        if (!notification.Parameters.TryGetProperty("uri", out JsonElement uriElement) ||
            uriElement.ValueKind != JsonValueKind.String) return;
        string uri = uriElement.GetString() ?? string.Empty;
        if (!notification.Parameters.TryGetProperty("diagnostics", out JsonElement diagnostics) ||
            diagnostics.ValueKind != JsonValueKind.Array) return;
        string? activeProfileId = Volatile.Read(ref _profileId);
        if (uri.Length == 0 || activeProfileId is null || !IsCanonicalDocumentUri(activeProfileId, uri) ||
            !_documents.TryGetValue(uri, out OpenDocumentState? openDocument)) return;
        if (notification.Parameters.TryGetProperty("version", out JsonElement versionElement) &&
            versionElement.TryGetInt32(out int version) && version != openDocument.Version) return;
        DiagnosticsPublished?.Invoke(new StudioLanguageDiagnostics(uri, notification.Parameters));
    }

    private void TransportStandardErrorReceived(string line)
    {
        if (!string.IsNullOrWhiteSpace(line)) OutputReceived?.Invoke($"TypeScript LSP: {line}");
    }

    private void TransportFailed(Exception exception) =>
        FailureReceived?.Invoke($"TypeScript language service stopped unexpectedly: {exception.Message}");

    private async Task DisposeTransportLockedAsync()
    {
        IStudioTypeScriptTransport? transport = _transport;
        _transport = null;
        if (transport is null) return;
        UnwireTransport(transport);
        try
        {
            await transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Dispose below forcibly terminates an unresponsive/crashed transport.
        }
        await transport.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(StudioTypeScriptLanguageHost));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DisposeTransportLockedAsync().ConfigureAwait(false);
            _documents.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace NexMud.Scripting.Tooling;

public sealed record LanguageServerNotification(string Method, JsonElement Parameters);

public sealed class LanguageServerRequestException : Exception
{
    public LanguageServerRequestException(int code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        DataElement = data;
    }

    public int Code { get; }
    public JsonElement? DataElement { get; }
}

/// <summary>
/// Owns one filesystem-aware typescript-language-server process for one Studio profile workspace.
/// Monaco integration consumes this host through ordinary LSP requests/notifications; the server
/// never receives filesystem authority beyond the profile workspace and installed dependencies.
/// </summary>
public sealed class TypeScriptLanguageService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _workspaceRoot;
    private readonly IToolchainLocator _toolchain;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private ToolProcessSession? _session;
    private CancellationTokenSource? _lifetime;
    private Task? _readerTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private int _disposed;

    public TypeScriptLanguageService(string workspaceRoot, IToolchainLocator? toolchain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _toolchain = toolchain ?? new ToolchainLocator();
    }

    public string WorkspaceRoot => _workspaceRoot;
    public string WorkspaceUri => ToFileUri(_workspaceRoot);
    public bool IsRunning => _session is { HasExited: false };
    public JsonElement? ServerCapabilities { get; private set; }

    public event Action<LanguageServerNotification>? NotificationReceived;
    public event Action<string>? StandardErrorReceived;
    public event Action<Exception>? Failed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning) return;
            Directory.CreateDirectory(_workspaceRoot);

            ToolchainComponentLocation node = _toolchain.ResolveRequired(ToolchainComponentNames.Node);
            ToolchainComponentLocation typescript = _toolchain.ResolveRequired(ToolchainComponentNames.TypeScript);

            CancellationTokenSource lifetime = new();
            _lifetime = lifetime;
            ToolProcessSession session = ToolProcessSession.Start(new ToolProcessRequest(
                node.FullPath,
                [typescript.FullPath, "--lsp", "--stdio"],
                _workspaceRoot,
                [Path.GetDirectoryName(node.FullPath)!],
                new Dictionary<string, string?>
                {
                    ["NODE_NO_WARNINGS"] = "1"
                }));
            _session = session;
            _readerTask = Task.Run(() => ReadLoopAsync(lifetime.Token), CancellationToken.None);
            _stderrTask = Task.Run(() => ReadStandardErrorAsync(lifetime.Token), CancellationToken.None);

            JsonElement initializeResult = await RequestAsync(
                "initialize",
                CreateInitializeParams(),
                cancellationToken,
                TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            ServerCapabilities = initializeResult.TryGetProperty("capabilities", out JsonElement capabilities)
                ? capabilities.Clone()
                : null;
            await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopCoreAsync(graceful: false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ThrowIfDisposed();
        ToolProcessSession session = _session
            ?? throw new InvalidOperationException("TypeScript language service has not been started.");
        if (session.HasExited)
            throw new InvalidOperationException("TypeScript language service is not running.");

        string requestId = Interlocked.Increment(ref _nextRequestId).ToString(CultureInfo.InvariantCulture);
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
            throw new InvalidOperationException("Unable to register a TypeScript language-service request.");

        try
        {
            JsonElement message = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method,
                ["params"] = parameters
            }, JsonOptions);
            await LanguageServerProtocolFraming.WriteAsync(
                session.StandardInput,
                message,
                _writeGate,
                cancellationToken).ConfigureAwait(false);
            TimeSpan wait = timeout ?? TimeSpan.FromSeconds(15);
            return await completion.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public async Task NotifyAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ThrowIfDisposed();
        ToolProcessSession session = _session
            ?? throw new InvalidOperationException("TypeScript language service has not been started.");
        if (session.HasExited)
            throw new InvalidOperationException("TypeScript language service is not running.");
        JsonElement message = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        }, JsonOptions);
        await LanguageServerProtocolFraming.WriteAsync(
            session.StandardInput,
            message,
            _writeGate,
            cancellationToken).ConfigureAwait(false);
    }

    public Task DidOpenAsync(
        string uri,
        string languageId,
        int version,
        string text,
        CancellationToken cancellationToken = default) =>
        NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId, version, text }
        }, cancellationToken);

    public Task DidChangeAsync(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default) =>
        NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } }
        }, cancellationToken);

    public Task DidSaveAsync(
        string uri,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        object payload = text is null
            ? new { textDocument = new { uri } }
            : new { textDocument = new { uri }, text };
        return NotifyAsync("textDocument/didSave", payload, cancellationToken);
    }

    public Task DidCloseAsync(string uri, CancellationToken cancellationToken = default) =>
        NotifyAsync("textDocument/didClose", new
        {
            textDocument = new { uri }
        }, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null) return;
            try
            {
                if (!_session.HasExited)
                {
                    _ = await RequestAsync(
                        "shutdown",
                        null,
                        cancellationToken,
                        TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    await NotifyAsync("exit", null, cancellationToken).ConfigureAwait(false);
                }
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // A crashed/unresponsive language server is terminated below.
            }
            await StopCoreAsync(graceful: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _session is { } session)
            {
                JsonElement? message = await LanguageServerProtocolFraming.ReadAsync(
                    session.StandardOutput,
                    cancellationToken).ConfigureAwait(false);
                if (message is null) break;
                await DispatchAsync(message.Value, cancellationToken).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
                FailPending(new EndOfStreamException("TypeScript language service stopped unexpectedly."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
            Failed?.Invoke(exception);
        }
    }

    private async Task DispatchAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.TryGetProperty("id", out JsonElement idElement))
        {
            string id = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString() ?? string.Empty,
                JsonValueKind.Number => idElement.GetRawText(),
                _ => string.Empty
            };

            if (message.TryGetProperty("method", out JsonElement requestMethodElement))
            {
                await RespondToServerRequestAsync(
                    idElement.Clone(),
                    requestMethodElement.GetString() ?? string.Empty,
                    message.TryGetProperty("params", out JsonElement requestParams) ? requestParams.Clone() : default,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion)) return;
            if (message.TryGetProperty("error", out JsonElement error))
            {
                int code = error.TryGetProperty("code", out JsonElement codeElement) && codeElement.TryGetInt32(out int parsedCode)
                    ? parsedCode
                    : -32603;
                string errorMessage = error.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString() ?? "Language server request failed."
                    : "Language server request failed.";
                JsonElement? data = error.TryGetProperty("data", out JsonElement dataElement) ? dataElement.Clone() : null;
                completion.TrySetException(new LanguageServerRequestException(code, errorMessage, data));
            }
            else
            {
                JsonElement result = message.TryGetProperty("result", out JsonElement resultElement)
                    ? resultElement.Clone()
                    : JsonSerializer.SerializeToElement<object?>(null);
                completion.TrySetResult(result);
            }
            return;
        }

        if (message.TryGetProperty("method", out JsonElement methodElement))
        {
            string method = methodElement.GetString() ?? string.Empty;
            JsonElement parameters = message.TryGetProperty("params", out JsonElement parameterElement)
                ? parameterElement.Clone()
                : JsonSerializer.SerializeToElement(new { });
            NotificationReceived?.Invoke(new LanguageServerNotification(method, parameters));
        }
    }

    private async Task RespondToServerRequestAsync(
        JsonElement id,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        object? result = method switch
        {
            "workspace/configuration" => CreateConfigurationResponse(parameters),
            "client/registerCapability" => null,
            "client/unregisterCapability" => null,
            "window/workDoneProgress/create" => null,
            "workspace/applyEdit" => new { applied = false, failureReason = "NexMUD applies workspace edits through its C# filesystem boundary." },
            _ => null
        };

        ToolProcessSession session = _session
            ?? throw new InvalidOperationException("TypeScript language service stopped before a server request could be answered.");
        JsonElement response = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        }, JsonOptions);
        await LanguageServerProtocolFraming.WriteAsync(
            session.StandardInput,
            response,
            _writeGate,
            cancellationToken).ConfigureAwait(false);
    }

    private static object?[] CreateConfigurationResponse(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("items", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return Enumerable.Repeat<object?>(null, items.GetArrayLength()).ToArray();
    }

    private object CreateInitializeParams() => new
    {
        processId = Environment.ProcessId,
        clientInfo = new { name = "NexMUD Automation Studio", version = "0.32.0" },
        rootUri = WorkspaceUri,
        workspaceFolders = new[] { new { uri = WorkspaceUri, name = "NexMUD Scripts" } },
        capabilities = new
        {
            workspace = new
            {
                workspaceFolders = true,
                symbol = new { dynamicRegistration = false },
                workspaceEdit = new { documentChanges = true }
            },
            textDocument = new
            {
                synchronization = new { dynamicRegistration = false, didSave = true },
                completion = new
                {
                    dynamicRegistration = false,
                    completionItem = new
                    {
                        snippetSupport = true,
                        documentationFormat = new[] { "markdown", "plaintext" },
                        resolveSupport = new { properties = new[] { "documentation", "detail", "additionalTextEdits" } }
                    }
                },
                hover = new { dynamicRegistration = false, contentFormat = new[] { "markdown", "plaintext" } },
                signatureHelp = new { dynamicRegistration = false },
                definition = new { dynamicRegistration = false, linkSupport = true },
                typeDefinition = new { dynamicRegistration = false, linkSupport = true },
                references = new { dynamicRegistration = false },
                rename = new { dynamicRegistration = false, prepareSupport = true },
                documentSymbol = new { dynamicRegistration = false, hierarchicalDocumentSymbolSupport = true },
                diagnostic = new { dynamicRegistration = false, relatedDocumentSupport = false }
            }
        }
    };

    private async Task ReadStandardErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _session is { } session)
            {
                string? line = await session.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Length > 0) StandardErrorReceived?.Invoke(line);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested) StandardErrorReceived?.Invoke(exception.Message);
        }
    }

    private async Task StopCoreAsync(bool graceful)
    {
        CancellationTokenSource? lifetime = Interlocked.Exchange(ref _lifetime, null);
        lifetime?.Cancel();
        ToolProcessSession? session = Interlocked.Exchange(ref _session, null);
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        Task? readerTask = Interlocked.Exchange(ref _readerTask, null);
        if (readerTask is not null)
        {
            try { await readerTask.ConfigureAwait(false); } catch { }
        }
        Task? stderrTask = Interlocked.Exchange(ref _stderrTask, null);
        if (stderrTask is not null)
        {
            try { await stderrTask.ConfigureAwait(false); } catch { }
        }
        lifetime?.Dispose();
        ServerCapabilities = null;
        if (!graceful) FailPending(new InvalidOperationException("TypeScript language service did not start."));
        else FailPending(new OperationCanceledException("TypeScript language service stopped."));
    }

    private void FailPending(Exception exception)
    {
        foreach ((string id, TaskCompletionSource<JsonElement> completion) in _pending.ToArray())
        {
            if (_pending.TryRemove(id, out _)) completion.TrySetException(exception);
        }
    }

    private static string ToFileUri(string path)
    {
        string normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (OperatingSystem.IsWindows() && !normalized.StartsWith('/')) normalized = "/" + normalized;
        return new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = normalized }.Uri.AbsoluteUri;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(TypeScriptLanguageService));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(graceful: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
            _writeGate.Dispose();
        }
    }
}

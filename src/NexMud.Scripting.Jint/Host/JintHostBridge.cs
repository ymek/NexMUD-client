using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jint;
using Jint.Native;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Diagnostics;
using NexMud.Scripting.Events;
using NexMud.Scripting.Execution;
using NexMud.Scripting.Host;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.Scheduling;
using NexMud.Scripting.Storage;

namespace NexMud.Scripting.Jint.Host;

/// <summary>
/// Narrow, string/DTO-only crossing between JavaScript and the capability-gated script host.
/// Host continuations never call Jint directly; every completion is posted to the instance mailbox.
/// </summary>
public sealed class JintHostBridge : IAsyncDisposable
{
    private sealed class InvocationContext : IDisposable
    {
        private readonly object _gate = new();
        private int _pendingRequests;
        private bool _handlerCompleted;
        private bool _disposed;
        private ScriptInvocationStatus _status = ScriptInvocationStatus.Queued;
        private string? _resultJson;
        private string? _error;

        public InvocationContext(
            Guid invocationId,
            Guid eventId,
            Guid? correlationId,
            CancellationToken ownerCancellation,
            AutomationInvocationContext? automationContext = null,
            Guid? parentOperationId = null,
            bool requireBooleanResult = false,
            bool external = false)
        {
            InvocationId = invocationId;
            EventId = eventId;
            CorrelationId = correlationId;
            AutomationContext = automationContext;
            ParentOperationId = parentOperationId;
            RequireBooleanResult = requireBooleanResult;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerCancellation);
            if (external)
                ExternalCompletion = new TaskCompletionSource<ScriptFunctionInvocationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Guid InvocationId { get; }
        public Guid EventId { get; }
        public Guid? CorrelationId { get; }
        public AutomationInvocationContext? AutomationContext { get; }
        public Guid? ParentOperationId { get; }
        public bool RequireBooleanResult { get; }
        public CancellationTokenSource Cancellation { get; }
        public ScriptInvocationStatus Status { get { lock (_gate) return _status; } }
        public TaskCompletionSource<ScriptInvocationStatus> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ScriptFunctionInvocationResult>? ExternalCompletion { get; }

        public void MarkRunning()
        {
            lock (_gate)
            {
                if (!_disposed) _status = ScriptInvocationStatus.Running;
            }
        }

        public void AddRequest()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _pendingRequests++;
                _status = ScriptInvocationStatus.WaitingOnHost;
            }
        }

        public bool CompleteRequest()
        {
            lock (_gate)
            {
                if (_pendingRequests > 0) _pendingRequests--;
                if (!_handlerCompleted && _pendingRequests == 0) _status = ScriptInvocationStatus.Running;
                return _handlerCompleted && _pendingRequests == 0;
            }
        }

        public bool CompleteHandler(
            ScriptInvocationStatus status = ScriptInvocationStatus.Completed,
            string? resultJson = null,
            string? error = null)
        {
            lock (_gate)
            {
                _handlerCompleted = true;
                _status = status;
                _resultJson = resultJson;
                _error = error;
                return _pendingRequests == 0;
            }
        }

        public ScriptInvocationSnapshot Snapshot()
        {
            lock (_gate) return new ScriptInvocationSnapshot(InvocationId, EventId, CorrelationId, _status);
        }

        public ScriptFunctionInvocationResult BuildExternalResult()
        {
            lock (_gate)
            {
                if (_status == ScriptInvocationStatus.Cancelled)
                    return ScriptFunctionInvocationResult.Failed("CancelledError", "Script invocation was cancelled.");
                if (_status != ScriptInvocationStatus.Completed)
                    return ScriptFunctionInvocationResult.Failed("ScriptRuntimeError", string.IsNullOrWhiteSpace(_error) ? "Script invocation failed." : _error);

                JsonElement? value = null;
                if (!string.IsNullOrWhiteSpace(_resultJson))
                {
                    using JsonDocument document = JsonDocument.Parse(_resultJson);
                    value = document.RootElement.Clone();
                }
                if (RequireBooleanResult && (!value.HasValue || value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                    return ScriptFunctionInvocationResult.Failed("ScriptPredicateResultError", "Script predicate must return a boolean value.");
                return new ScriptFunctionInvocationResult(true, value);
            }
        }

        public void Cancel()
        {
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Cancellation.Dispose();
        }
    }

    private sealed record PendingRequest(
        CancellationTokenSource Cancellation,
        InvocationContext? Invocation,
        Guid OperationId);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private readonly IScriptHost _host;
    private readonly IScriptExecutionScope _scope;
    private readonly Jint.Runtime.JintScriptDispatcher _dispatcher;
    private readonly ScriptManifestIdentity _identity;
    private readonly CompiledScriptPackage _package;
    private readonly IScriptDiagnosticsSink _diagnostics;
    private readonly Action<Exception>? _fatalFault;
    private readonly ConcurrentDictionary<string, PendingRequest> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, InvocationContext> _invocations = new();
    private readonly ConcurrentDictionary<string, IScriptEventSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IScriptScheduledTask> _timers = new(StringComparer.Ordinal);
    private InvocationContext? _activeInvocation;
    private int _stopped;

    public JintHostBridge(
        IScriptHost host,
        IScriptExecutionScope scope,
        Jint.Runtime.JintScriptDispatcher dispatcher,
        ScriptManifestIdentity identity,
        CompiledScriptPackage package,
        IScriptDiagnosticsSink? diagnostics = null,
        Action<Exception>? fatalFault = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _identity = identity;
        _package = package ?? throw new ArgumentNullException(nameof(package));
        _diagnostics = diagnostics ?? NullScriptDiagnosticsSink.Instance;
        _fatalFault = fatalFault;
    }

    public void Install(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetValue("__nexQueryRaw", new Func<string, string, string>(Query));
        engine.SetValue("__nexBeginRaw", new Action<string, string, string>(Begin));
        engine.SetValue("__nexCancelRaw", new Action<string>(CancelRequest));
        engine.SetValue("__nexSubscribeRaw", new Action<string, string>(Subscribe));
        engine.SetValue("__nexUnsubscribeRaw", new Action<string>(Unsubscribe));
        engine.SetValue("__nexInvocationCompleteRaw", new Action<string, bool, string, string>(CompleteInvocation));
        engine.SetValue("__nexScheduleRaw", new Action<string, double, bool>(Schedule));
        engine.SetValue("__nexCancelTimerRaw", new Action<string>(CancelTimer));

        engine.Execute(
            """
            for (const name of [
              '__nexQueryRaw', '__nexBeginRaw', '__nexCancelRaw',
              '__nexSubscribeRaw', '__nexUnsubscribeRaw', '__nexInvocationCompleteRaw',
              '__nexScheduleRaw', '__nexCancelTimerRaw'
            ]) {
              const descriptor = Object.getOwnPropertyDescriptor(globalThis, name);
              if (descriptor) Object.defineProperty(globalThis, name, { ...descriptor, writable: false, configurable: false });
            }
            """,
            "<nexmud-host-bridge>");
    }

    public async Task<ScriptFunctionInvocationResult> InvokeExportAsync(
        ScriptFunctionInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        ArgumentNullException.ThrowIfNull(request);
        string modulePath = ResolveCompiledModulePath(request.FunctionRef.ModulePath);
        Guid invocationId = Guid.NewGuid();
        InvocationContext invocation = new(
            invocationId,
            Guid.NewGuid(),
            request.Context.InvocationId,
            _scope.CancellationToken,
            request.Context,
            request.ParentAutomationInvocationId,
            request.RequireBooleanResult,
            external: true);
        if (!_invocations.TryAdd(invocationId, invocation))
        {
            invocation.Dispose();
            return ScriptFunctionInvocationResult.Failed("ScriptInvocationRegistrationError", "Unable to register script invocation.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.Cancellation.Token);
        Record(ScriptDiagnosticKind.InvocationStarted, Guid.NewGuid(), $"export:{request.FunctionRef.ModulePath}#{request.FunctionRef.ExportName}", invocation);
        try
        {
            invocation.MarkRunning();
            string contextJson = JsonSerializer.Serialize(request.Context, JsonOptions);
            await EnqueueWithInvocationAsync(
                invocation,
                engine =>
                {
                    var module = engine.Modules.Import(modulePath);
                    JsValue function = module.Get(request.FunctionRef.ExportName);
                    if (function.IsUndefined() || function.IsNull())
                        throw new MissingMethodException($"Export '{request.FunctionRef.ExportName}' was not found in '{request.FunctionRef.ModulePath}'.");
                    engine.Invoke("__nexInvokeCallable", function, contextJson, invocationId.ToString("D"));
                },
                linked.Token).ConfigureAwait(false);
            return await invocation.ExternalCompletion!.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            CancelInvocation(invocation, "Invocation cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            FaultInvocation(invocation, exception);
            return invocation.BuildExternalResult();
        }
    }

    private string ResolveCompiledModulePath(string sourcePath)
    {
        string normalized = CompiledScriptPackage.NormalizeModulePath(sourcePath);
        if (_package.Modules.ContainsKey(normalized)) return normalized;
        string? changed = Path.ChangeExtension(normalized, ".js")?.Replace('\\', '/');
        if (changed is not null && _package.Modules.ContainsKey(changed)) return changed;
        throw new FileNotFoundException($"Compiled module for '{sourcePath}' was not found in package '{_package.Manifest.Id}'.", sourcePath);
    }

    private string Query(string operation, string payload)
    {
        ThrowIfStopped();
        try
        {
            object? value = operation switch
            {
                "state.snapshot" => _host.State.Snapshot(),
                _ => throw new InvalidOperationException($"Unknown synchronous host operation '{operation}'.")
            };
            return JsonSerializer.Serialize(new { ok = true, value }, JsonOptions);
        }
        catch (UnauthorizedAccessException exception)
        {
            Record(ScriptDiagnosticKind.PermissionDenied, Guid.NewGuid(), operation, _activeInvocation);
            return JsonSerializer.Serialize(new { ok = false, error = new { code = "PermissionError", message = Sanitize(exception) } }, JsonOptions);
        }
        catch (OperationCanceledException exception)
        {
            return JsonSerializer.Serialize(new { ok = false, error = new { code = "CancelledError", message = Sanitize(exception) } }, JsonOptions);
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new { ok = false, error = new { code = "HostOperationError", message = Sanitize(exception) } }, JsonOptions);
        }
    }

    private void Begin(string operation, string requestId, string payload)
    {
        ThrowIfStopped();
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        InvocationContext? invocation = _activeInvocation;
        invocation?.AddRequest();
        CancellationTokenSource cts = invocation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(_scope.CancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_scope.CancellationToken, invocation.Cancellation.Token);
        Guid operationId = Guid.NewGuid();
        PendingRequest request = new(cts, invocation, operationId);
        if (!_requests.TryAdd(requestId, request))
        {
            cts.Dispose();
            if (invocation is not null && invocation.CompleteRequest()) FinalizeInvocation(invocation);
            throw new InvalidOperationException($"Duplicate host request '{requestId}'.");
        }

        Record(ScriptDiagnosticKind.HostRequestStarted, operationId, $"{operation}:{requestId}", invocation);
        _ = RunRequestAsync(operation, requestId, payload, request);
    }

    private async Task RunRequestAsync(string operation, string requestId, string payload, PendingRequest request)
    {
        try
        {
            object? result = await ExecuteAsync(
                operation,
                payload,
                request.Invocation,
                request.OperationId,
                request.Cancellation.Token).ConfigureAwait(false);
            if (!RemoveRequest(requestId, request)) return;
            if (Volatile.Read(ref _stopped) != 0 || !_dispatcher.IsAccepting) return;
            string json = JsonSerializer.Serialize(result, JsonOptions);
            await EnqueueWithInvocationAsync(
                request.Invocation,
                engine => engine.Invoke("__nexResolve", requestId, json)).ConfigureAwait(false);
            Record(ScriptDiagnosticKind.HostRequestCompleted, request.OperationId, operation, request.Invocation);
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested || _scope.IsCancellationRequested)
        {
            RemoveRequest(requestId, request);
            await RejectAsync(requestId, "CancelledError", "Host operation was cancelled.", request.Invocation).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            RemoveRequest(requestId, request);
            Record(ScriptDiagnosticKind.PermissionDenied, request.OperationId, operation, request.Invocation);
            await RejectAsync(requestId, "PermissionError", Sanitize(exception), request.Invocation).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RemoveRequest(requestId, request);
            await RejectAsync(requestId, "HostOperationError", Sanitize(exception), request.Invocation).ConfigureAwait(false);
        }
    }

    private async Task<object?> ExecuteAsync(
        string operation,
        string payload,
        InvocationContext? invocation,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
        JsonElement root = document.RootElement;
        switch (operation)
        {
            case "commands.send":
            {
                string command = GetString(root, "command") ?? string.Empty;
                string? reason = GetString(root, "reason");
                bool sensitive = GetBoolean(root, "sensitive");
                AutomationInvocationContext? automation = invocation?.AutomationContext;
                ScriptCommandResult result = await _host.Commands.SendAsync(
                    new ScriptCommandRequest(
                        command,
                        ScriptCommandOrigin.Script,
                        _host.ModuleId.Value,
                        _identity.Name,
                        reason,
                        sensitive,
                        ModuleId: _host.ModuleId.Value,
                        ScriptVersion: _identity.Version,
                        ScriptInstanceId: _identity.InstanceId,
                        InvocationId: invocation?.InvocationId,
                        EventId: invocation?.EventId,
                        ParentOperationId: invocation?.ParentOperationId ?? automation?.InvocationId ?? operationId,
                        AutomationId: GetString(root, "automationId") ?? automation?.SourceDefinitionId,
                        AutomationType: GetString(root, "automationType") ?? automation?.SourceKind.ToString(),
                        TriggerId: GetString(root, "triggerId") ?? automation?.SourceDefinitionId),
                    cancellationToken).ConfigureAwait(false);
                Record(ScriptDiagnosticKind.CommandEmitted, result.ActionId, command, invocation, invocation?.ParentOperationId ?? automation?.InvocationId ?? operationId);
                return result;
            }
            case "mapper.currentRoom":
                return await _host.Mapper.CurrentRoomAsync(cancellationToken).ConfigureAwait(false);
            case "mapper.findPath":
                return await _host.Mapper.FindPathAsync(RequireString(root, "destinationRoomId"), cancellationToken).ConfigureAwait(false);
            case "mapper.move":
            {
                Guid? routeExecutionId = Guid.TryParse(GetString(root, "routeExecutionId"), out Guid executionId)
                    ? executionId
                    : null;
                int? routeStep = GetOptionalInt32(root, "routeStep");
                int? routeTotalSteps = GetOptionalInt32(root, "routeTotalSteps");
                return await _host.Mapper.MoveAsync(
                    new ScriptMoveRequest(
                        RequireString(root, "direction"),
                        GetString(root, "fromRoomId"),
                        GetString(root, "expectedRoomId"),
                        routeExecutionId,
                        GetString(root, "routeId"),
                        routeStep,
                        routeTotalSteps,
                        GetString(root, "recovery")),
                    cancellationToken).ConfigureAwait(false);
            }
            case "codex.search":
                return await _host.Codex.SearchAsync(RequireString(root, "query"), GetInt32(root, "limit", 50, 1, 500), cancellationToken).ConfigureAwait(false);
            case "timers.delay":
            {
                double milliseconds = GetDouble(root, "milliseconds", 0, int.MaxValue);
                await _host.Timers.DelayAsync(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
                return null;
            }
            case "storage.get":
            {
                string? stored = await _host.Storage.GetAsync(RequireString(root, "key"), cancellationToken).ConfigureAwait(false);
                return ParseStoredJson(stored);
            }
            case "storage.set":
            {
                string key = RequireString(root, "key");
                if (!root.TryGetProperty("value", out JsonElement value)) value = JsonSerializer.SerializeToElement<object?>(null, JsonOptions);
                await _host.Storage.SetAsync(key, value.GetRawText(), cancellationToken).ConfigureAwait(false);
                return null;
            }
            case "storage.delete":
                _ = await _host.Storage.DeleteAsync(RequireString(root, "key"), cancellationToken).ConfigureAwait(false);
                return null;
            case "storage.has":
                return await _host.Storage.GetAsync(RequireString(root, "key"), cancellationToken).ConfigureAwait(false) is not null;
            case "storage.list":
            {
                string? prefix = GetString(root, "prefix");
                IReadOnlyList<ScriptStorageEntry> entries = await _host.Storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
                return entries.Select(entry => new { entry.Key, value = ParseStoredJson(entry.JsonValue) }).ToArray();
            }
            case "scripts.invoke":
            {
                ScriptFunctionRef functionRef = JsonSerializer.Deserialize<ScriptFunctionRef>(
                    root.GetProperty("functionRef").GetRawText(),
                    JsonOptions) ?? throw new ArgumentException("'functionRef' is required.");
                string sourceKindText = RequireString(root, "sourceKind");
                if (!Enum.TryParse(sourceKindText, true, out AutomationInvocationSourceKind sourceKind))
                    throw new ArgumentException($"Unsupported Automation source kind '{sourceKindText}'.");
                JsonElement arguments = root.TryGetProperty("arguments", out JsonElement argumentElement)
                    ? argumentElement.Clone()
                    : JsonSerializer.SerializeToElement(new { }, JsonOptions);
                IReadOnlyDictionary<string, string>? captures = root.TryGetProperty("captures", out JsonElement captureElement) && captureElement.ValueKind == JsonValueKind.Object
                    ? captureElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.ToString(), StringComparer.Ordinal)
                    : null;
                JsonElement? semanticEvent = root.TryGetProperty("semanticEvent", out JsonElement semanticElement) && semanticElement.ValueKind != JsonValueKind.Null
                    ? semanticElement.Clone()
                    : null;
                bool requireBoolean = root.TryGetProperty("requireBooleanResult", out JsonElement requireBooleanElement) && requireBooleanElement.ValueKind == JsonValueKind.True;
                return await _host.Functions.InvokeAsync(
                    new ScriptFunctionInvocationDescriptor(
                        functionRef,
                        sourceKind,
                        RequireString(root, "sourceDefinitionId"),
                        arguments,
                        captures,
                        semanticEvent,
                        requireBoolean,
                        invocation?.InvocationId),
                    cancellationToken).ConfigureAwait(false);
            }
            case "ui.notify":
            {
                string title = RequireString(root, "title");
                string message = RequireString(root, "message");
                string levelText = GetString(root, "level") ?? nameof(ScriptUiNotificationLevel.Information);
                _ = Enum.TryParse(levelText, true, out ScriptUiNotificationLevel level);
                await _host.Ui.NotifyAsync(new ScriptUiNotification(title, message, level), cancellationToken).ConfigureAwait(false);
                return true;
            }
            case "log.write":
            {
                string levelText = GetString(root, "level") ?? nameof(ScriptLogLevel.Information);
                _ = Enum.TryParse(levelText, true, out ScriptLogLevel level);
                string? data = BuildLogData(root, invocation);
                await _host.Log.WriteAsync(level, RequireString(root, "message"), data, cancellationToken).ConfigureAwait(false);
                return null;
            }
            default:
                throw new InvalidOperationException($"Unknown asynchronous host operation '{operation}'.");
        }
    }

    private bool RemoveRequest(string requestId, PendingRequest expected)
    {
        if (!_requests.TryRemove(requestId, out PendingRequest? removed) || !ReferenceEquals(removed, expected)) return false;
        expected.Cancellation.Dispose();
        if (expected.Invocation is not null && expected.Invocation.CompleteRequest()) FinalizeInvocation(expected.Invocation);
        return true;
    }

    private void CancelRequest(string requestId)
    {
        if (!_requests.TryRemove(requestId, out PendingRequest? request)) return;
        try { request.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        request.Cancellation.Dispose();
        if (request.Invocation is not null && request.Invocation.CompleteRequest()) FinalizeInvocation(request.Invocation);
    }

    private void Subscribe(string id, string eventTypesJson)
    {
        ThrowIfStopped();
        if (_subscriptions.ContainsKey(id)) throw new InvalidOperationException($"Duplicate event subscription '{id}'.");
        string[] eventTypes = JsonSerializer.Deserialize<string[]>(eventTypesJson, JsonOptions) ?? [];
        ScriptEventFilter filter = eventTypes.Length == 0 ? ScriptEventFilter.Any : ScriptEventFilter.For(eventTypes);
        IScriptEventSubscription subscription = _host.Events.Subscribe(
            _scope,
            filter,
            async (envelope, cancellationToken) =>
            {
                if (Volatile.Read(ref _stopped) != 0 || !_dispatcher.IsAccepting) return;
                Guid eventId = envelope.EventId ?? Guid.NewGuid();
                InvocationContext invocation = new(Guid.NewGuid(), eventId, envelope.CorrelationId, _scope.CancellationToken);
                if (!_invocations.TryAdd(invocation.InvocationId, invocation))
                {
                    invocation.Dispose();
                    throw new InvalidOperationException("Unable to register script invocation.");
                }

                Record(ScriptDiagnosticKind.InvocationStarted, Guid.NewGuid(), envelope.Type, invocation);
                string json = envelope.Payload.GetRawText();
                try
                {
                    invocation.MarkRunning();
                    await EnqueueWithInvocationAsync(
                        invocation,
                        engine => engine.Invoke("__nexDispatchEvent", id, json, invocation.InvocationId.ToString("D")),
                        cancellationToken).ConfigureAwait(false);
                    // Keep subscription delivery FIFO for the full async handler, not merely the
                    // initial JavaScript call. Host completions re-enter through the mailbox above.
                    await invocation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || invocation.Cancellation.IsCancellationRequested)
                {
                    CancelInvocation(invocation, "Invocation cancelled.");
                }
                catch (Exception exception)
                {
                    FaultInvocation(invocation, exception);
                    _fatalFault?.Invoke(exception);
                }
            });
        if (!_subscriptions.TryAdd(id, subscription))
        {
            subscription.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException($"Unable to register event subscription '{id}'.");
        }
    }

    private void CompleteInvocation(string invocationIdText, bool succeeded, string resultJson, string error)
    {
        if (!Guid.TryParse(invocationIdText, out Guid invocationId)) return;
        if (!_invocations.TryGetValue(invocationId, out InvocationContext? invocation)) return;
        Record(
            succeeded ? ScriptDiagnosticKind.InvocationCompleted : ScriptDiagnosticKind.InvocationFaulted,
            Guid.NewGuid(),
            succeeded ? "Invocation completed." : string.IsNullOrWhiteSpace(error) ? "Script handler failed." : error,
            invocation);
        if (invocation.CompleteHandler(
                succeeded ? ScriptInvocationStatus.Completed : ScriptInvocationStatus.Faulted,
                string.IsNullOrWhiteSpace(resultJson) ? null : resultJson,
                error))
            FinalizeInvocation(invocation);
    }

    private void FaultInvocation(InvocationContext invocation, Exception exception)
    {
        ScriptError error = Jint.Diagnostics.JintDiagnosticsAdapter.SanitizeRuntimeError(exception, _package);
        ScriptDiagnosticKind kind = error.Kind switch
        {
            ScriptErrorKind.ScriptTimeoutError => ScriptDiagnosticKind.Timeout,
            ScriptErrorKind.ScriptResourceLimitError => ScriptDiagnosticKind.ResourceLimitExceeded,
            ScriptErrorKind.CancelledError => ScriptDiagnosticKind.InvocationCancelled,
            _ => ScriptDiagnosticKind.InvocationFaulted
        };
        Record(kind, Guid.NewGuid(), error.Message, invocation, location: error.Location);
        invocation.Cancel();
        ScriptInvocationStatus status = error.Kind == ScriptErrorKind.CancelledError
            ? ScriptInvocationStatus.Cancelled
            : ScriptInvocationStatus.Faulted;
        if (invocation.CompleteHandler(status, error: error.Message)) FinalizeInvocation(invocation);
    }

    private void CancelInvocation(InvocationContext invocation, string message)
    {
        Record(ScriptDiagnosticKind.InvocationCancelled, Guid.NewGuid(), message, invocation);
        invocation.Cancel();
        if (invocation.CompleteHandler(ScriptInvocationStatus.Cancelled, error: message)) FinalizeInvocation(invocation);
    }

    public IReadOnlyList<ScriptInvocationSnapshot> SnapshotInvocations() => _invocations.Values
        .Select(invocation => invocation.Snapshot())
        .OrderBy(invocation => invocation.InvocationId)
        .ToArray();

    private void FinalizeInvocation(InvocationContext invocation)
    {
        if (_invocations.TryRemove(invocation.InvocationId, out InvocationContext? removed))
        {
            removed.Completion.TrySetResult(removed.Status);
            removed.ExternalCompletion?.TrySetResult(removed.BuildExternalResult());
            removed.Dispose();
        }
    }

    private async Task EnqueueWithInvocationAsync(
        InvocationContext? invocation,
        Action<Engine> action,
        CancellationToken cancellationToken = default)
    {
        await _dispatcher.EnqueueAsync(engine =>
        {
            InvocationContext? previous = _activeInvocation;
            _activeInvocation = invocation;
            try
            {
                action(engine);
                // Jint 4.16.x keeps RunAvailableContinuations internal. Enter through the public
                // Execute API instead: ScriptEvaluation drains the engine event loop before it
                // returns, so Promise reactions queued by host completions resume on this same
                // serialized mailbox turn without reaching into Jint internals.
                engine.Execute("void 0;", "<nexmud-continuation-pump>");
            }
            finally
            {
                _activeInvocation = previous;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Unsubscribe(string id)
    {
        if (_subscriptions.TryRemove(id, out IScriptEventSubscription? subscription))
            _ = subscription.DisposeAsync().AsTask();
    }

    private void Schedule(string id, double milliseconds, bool recurring)
    {
        ThrowIfStopped();
        if (!double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        TimeSpan interval = TimeSpan.FromMilliseconds(milliseconds);
        Func<CancellationToken, Task> callback = async cancellationToken =>
        {
            Guid eventId = Guid.NewGuid();
            InvocationContext invocation = new(Guid.NewGuid(), eventId, null, _scope.CancellationToken);
            if (!_invocations.TryAdd(invocation.InvocationId, invocation))
            {
                invocation.Dispose();
                throw new InvalidOperationException("Unable to register timer invocation.");
            }

            Record(ScriptDiagnosticKind.InvocationStarted, Guid.NewGuid(), $"timer:{id}", invocation);
            try
            {
                invocation.MarkRunning();
                await EnqueueWithInvocationAsync(
                    invocation,
                    engine => engine.Invoke("__nexDispatchTimer", id, invocation.InvocationId.ToString("D")),
                    cancellationToken).ConfigureAwait(false);
                await invocation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || invocation.Cancellation.IsCancellationRequested)
            {
                CancelInvocation(invocation, "Timer invocation cancelled.");
            }
            catch (Exception exception)
            {
                FaultInvocation(invocation, exception);
            }
            finally
            {
                if (!recurring) _timers.TryRemove(id, out _);
            }
        };
        IScriptScheduledTask timer = recurring
            ? _host.Timers.Every(_scope, interval, $"jint:{_host.ModuleId}:timer:{id}", callback)
            : _host.Timers.After(_scope, interval, $"jint:{_host.ModuleId}:timer:{id}", callback);
        if (!_timers.TryAdd(id, timer))
        {
            timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException($"Duplicate timer '{id}'.");
        }
    }

    private void CancelTimer(string id)
    {
        if (_timers.TryRemove(id, out IScriptScheduledTask? timer))
            _ = timer.DisposeAsync().AsTask();
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        foreach (InvocationContext invocation in _invocations.Values) invocation.Cancel();
        foreach ((_, PendingRequest request) in _requests)
        {
            try { request.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            request.Cancellation.Dispose();
        }
        _requests.Clear();
        foreach ((_, IScriptEventSubscription subscription) in _subscriptions)
            await subscription.DisposeAsync().ConfigureAwait(false);
        _subscriptions.Clear();
        foreach ((_, IScriptScheduledTask timer) in _timers)
            await timer.DisposeAsync().ConfigureAwait(false);
        _timers.Clear();
        foreach ((_, InvocationContext invocation) in _invocations)
        {
            invocation.ExternalCompletion?.TrySetResult(ScriptFunctionInvocationResult.Failed("CancelledError", "Script runtime stopped."));
            invocation.Dispose();
        }
        _invocations.Clear();
    }

    private async Task RejectAsync(string requestId, string code, string message, InvocationContext? invocation)
    {
        if (Volatile.Read(ref _stopped) != 0 || !_dispatcher.IsAccepting) return;
        string json = JsonSerializer.Serialize(new { code, message }, JsonOptions);
        try
        {
            await EnqueueWithInvocationAsync(invocation, engine => engine.Invoke("__nexReject", requestId, json)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
    }

    private void Record(
        ScriptDiagnosticKind kind,
        Guid operationId,
        string? message,
        InvocationContext? invocation = null,
        Guid? parentOperationId = null,
        ScriptSourceLocation? location = null) =>
        _diagnostics.Record(new ScriptDiagnosticRecord(
            kind,
            _host.Timers.UtcNow,
            _host.ModuleId,
            _identity.Version,
            _identity.InstanceId,
            InvocationId: invocation?.InvocationId,
            EventId: invocation?.EventId,
            OperationId: operationId,
            ParentOperationId: parentOperationId ?? invocation?.ParentOperationId ?? invocation?.AutomationContext?.InvocationId,
            Location: location,
            Message: message));

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);

    private static string? BuildLogData(JsonElement root, InvocationContext? invocation)
    {
        bool hasData = root.TryGetProperty("data", out JsonElement dataElement) && dataElement.ValueKind != JsonValueKind.Null;
        if (invocation is null) return hasData ? dataElement.GetRawText() : null;

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        if (hasData && dataElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in dataElement.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        else if (hasData)
        {
            values["data"] = dataElement.Clone();
        }
        values["invocationId"] = invocation.InvocationId;
        values["eventId"] = invocation.EventId;
        if (invocation.AutomationContext is { } automation)
        {
            values["automationInvocationId"] = automation.InvocationId;
            values["automationSourceKind"] = automation.SourceKind.ToString();
            values["automationSourceDefinitionId"] = automation.SourceDefinitionId;
        }
        return JsonSerializer.Serialize(values, JsonOptions);
    }

    private static string Sanitize(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Script operation is not permitted.",
        OperationCanceledException => "Script operation was cancelled.",
        ArgumentException argument => argument.Message,
        InvalidOperationException invalid => invalid.Message,
        MissingMethodException missing => missing.Message,
        FileNotFoundException missing => missing.Message,
        _ => "Host operation failed."
    };

    private static string RequireString(JsonElement root, string name) =>
        GetString(root, name) is { Length: > 0 } value ? value : throw new ArgumentException($"'{name}' is required.");

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) && value.GetBoolean();

    private static double GetDouble(JsonElement root, string name, double minimum, double maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || !value.TryGetDouble(out double parsed) ||
            !double.IsFinite(parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentOutOfRangeException(name);
        return parsed;
    }

    private static int? GetOptionalInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.TryGetInt32(out int parsed) ? parsed : null;
    }

    private static int GetInt32(JsonElement root, string name, int fallback, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int parsed)) return fallback;
        return Math.Clamp(parsed, minimum, maximum);
    }

    private static object? ParseStoredJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}

public readonly record struct ScriptManifestIdentity(string Name, string Version, Guid InstanceId);

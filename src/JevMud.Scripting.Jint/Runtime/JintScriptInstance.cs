using System.Text.Json;
using Jint;
using Jint.Native;
using JevMud.Scripting.Compilation;
using JevMud.Scripting.Diagnostics;
using JevMud.Scripting.Execution;
using JevMud.Scripting.Host;
using JevMud.Scripting.Jint.Bootstrap;
using JevMud.Scripting.Jint.Diagnostics;
using JevMud.Scripting.Jint.Host;
using JevMud.Scripting.Jint.Modules;
using JevMud.Scripting.Runtime;
using JevMud.Scripting.Scheduling;

namespace JevMud.Scripting.Jint.Runtime;

public sealed class JintScriptInstance : IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly CompiledScriptPackage _package;
    private readonly IScriptExecutionScope _scope;
    private readonly CancellationTokenSource _engineCancellation;
    private readonly JintScriptDispatcher _dispatcher;
    private readonly JintHostBridge _bridge;
    private readonly IScriptDiagnosticsSink _diagnostics;
    private readonly IScriptScheduler _clock;
    private readonly Guid _instanceId = Guid.NewGuid();
    private JsValue? _deactivate;
    private ScriptInstanceState _state = ScriptInstanceState.Created;
    private int _disposed;

    public JintScriptInstance(
        CompiledScriptPackage package,
        IScriptHost host,
        IScriptExecutionScope scope,
        JintEngineFactory engineFactory,
        IScriptDiagnosticsSink? diagnostics = null)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        ArgumentNullException.ThrowIfNull(host);
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        ArgumentNullException.ThrowIfNull(engineFactory);
        if (!host.ModuleId.Equals(package.Manifest.Id))
            throw new InvalidOperationException("Script host module identity does not match compiled package identity.");
        if (host.Permissions.Capabilities != package.Manifest.Permissions)
            throw new UnauthorizedAccessException(
                $"Resolved host capabilities '{host.Permissions.Capabilities}' do not exactly match manifest capabilities '{package.Manifest.Permissions}'.");

        _diagnostics = diagnostics ?? NullScriptDiagnosticsSink.Instance;
        _clock = host.Timers;
        Limits = ScriptResourceLimits.For(package.Manifest.RuntimeProfile);
        JintModuleLoader.Validate(package, Limits);
        _engineCancellation = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);
        Engine engine = engineFactory.Create(Limits, _engineCancellation.Token, _clock);
        _dispatcher = new JintScriptDispatcher(engine, Limits.MailboxCapacity);
        _bridge = new JintHostBridge(
            host,
            scope,
            _dispatcher,
            new ScriptManifestIdentity(package.Manifest.Name, package.Manifest.Version, _instanceId),
            package,
            _diagnostics,
            HandleFatalFault);
        SetState(ScriptInstanceState.Compiled);
    }

    public ScriptModuleId Id => _package.Manifest.Id;
    public string Name => _package.Manifest.Name;
    public string Version => _package.Manifest.Version;
    public Guid InstanceId => _instanceId;
    public ScriptResourceLimits Limits { get; }
    public ScriptInstanceState State { get { lock (_stateGate) return _state; } }
    public IScriptExecutionScope Scope => _scope;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State != ScriptInstanceState.Compiled)
            throw new InvalidOperationException($"Script '{Id}' cannot start from state '{State}'.");
        SetState(ScriptInstanceState.Loading);
        try
        {
            await _dispatcher.EnqueueAsync(engine =>
            {
                _bridge.Install(engine);
                JintModuleLoader.Register(engine, _package, NexMudJavaScriptBootstrap.Source);
                _ = engine.Modules.Import(NexMudJavaScriptBootstrap.ModuleName);
                var module = engine.Modules.Import(_package.Manifest.Entrypoint);
                JsValue activate = module.Get("activate");
                _deactivate = module.Get("deactivate");
                if (!activate.IsUndefined() && !activate.IsNull())
                {
                    string contextJson = JsonSerializer.Serialize(new
                    {
                        scriptId = Id.Value,
                        scriptVersion = Version,
                        scriptInstanceId = InstanceId,
                        apiVersion = ScriptApiVersion.Current
                    });
                    JsValue context = engine.Invoke("__nexParseHostJson", contextJson);
                    engine.Invoke(activate, context);
                }
            }, cancellationToken).ConfigureAwait(false);
            SetState(ScriptInstanceState.Running);
            Record(ScriptDiagnosticKind.ScriptLoaded, "Script loaded.");
        }
        catch (Exception exception)
        {
            SetState(ScriptInstanceState.Faulted);
            ScriptError error = JintDiagnosticsAdapter.SanitizeRuntimeError(exception, _package);
            Record(ScriptDiagnosticKind.UncaughtException, error.Message, error.Location);
            await StopCoreAsync(runDeactivate: false).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ScriptInstanceState state = State;
        if (state is ScriptInstanceState.Stopped or ScriptInstanceState.Disposed) return;
        if (state != ScriptInstanceState.Stopping) SetState(ScriptInstanceState.Stopping);
        await StopCoreAsync(runDeactivate: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopCoreAsync(bool runDeactivate, CancellationToken cancellationToken = default)
    {
        await _bridge.StopAsync().ConfigureAwait(false);
        if (runDeactivate && _deactivate is { } deactivate && !deactivate.IsUndefined() && !deactivate.IsNull() && _dispatcher.IsAccepting)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Limits.ExecutionTimeout);
            try
            {
                await _dispatcher.EnqueueAsync(engine => engine.Invoke(deactivate), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            catch (Exception exception)
            {
                ScriptError error = JintDiagnosticsAdapter.SanitizeRuntimeError(exception, _package);
                Record(ScriptDiagnosticKind.UncaughtException, error.Message, error.Location);
            }
        }

        try { _engineCancellation.Cancel(); } catch (ObjectDisposedException) { }
        _scope.Cancel();
        _dispatcher.StopAccepting();
        SetState(ScriptInstanceState.Stopped);
        Record(ScriptDiagnosticKind.ScriptUnloaded, "Script stopped.");
    }

    public ScriptModuleSnapshot Snapshot() => new(
        Id,
        Name,
        State is ScriptInstanceState.Loading or ScriptInstanceState.Running,
        _scope.IsCancellationRequested,
        _scope.Snapshot(),
        State switch
        {
            ScriptInstanceState.Loading => ScriptStatus.Loading,
            ScriptInstanceState.Running => ScriptStatus.Running,
            ScriptInstanceState.Faulted => ScriptStatus.Faulted,
            ScriptInstanceState.Stopping => ScriptStatus.Stopping,
            _ => ScriptStatus.Disabled
        },
        _bridge.SnapshotInvocations());

    private void SetState(ScriptInstanceState state)
    {
        lock (_stateGate) _state = state;
    }

    private void HandleFatalFault(Exception exception)
    {
        if (State is ScriptInstanceState.Stopping or ScriptInstanceState.Stopped or ScriptInstanceState.Disposed) return;
        SetState(ScriptInstanceState.Faulted);
        ScriptError error = JintDiagnosticsAdapter.SanitizeRuntimeError(exception, _package);
        Record(error.Kind == ScriptErrorKind.ScriptTimeoutError
            ? ScriptDiagnosticKind.Timeout
            : error.Kind == ScriptErrorKind.ScriptResourceLimitError
                ? ScriptDiagnosticKind.ResourceLimitExceeded
                : ScriptDiagnosticKind.UncaughtException, error.Message, error.Location);
        _scope.Cancel();
        try { _engineCancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void Record(ScriptDiagnosticKind kind, string message, ScriptSourceLocation? location = null) =>
        _diagnostics.Record(new ScriptDiagnosticRecord(
            kind,
            _clock.UtcNow,
            Id,
            Version,
            InstanceId,
            Location: location,
            Message: message));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync().ConfigureAwait(false); } catch { }
        await _bridge.DisposeAsync().ConfigureAwait(false);
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        await _scope.DisposeAsync().ConfigureAwait(false);
        _engineCancellation.Dispose();
        SetState(ScriptInstanceState.Disposed);
    }
}

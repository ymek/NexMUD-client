using System.Collections.Concurrent;
using JevMud.Scripting.Compilation;
using JevMud.Scripting.Diagnostics;
using JevMud.Scripting.Execution;
using JevMud.Scripting.Host;
using JevMud.Scripting.Runtime;

namespace JevMud.Scripting.Jint.Runtime;

public sealed class JintScriptRuntime : IJavaScriptRuntime
{
    private readonly IScriptExecutionSupervisor _supervisor;
    private readonly JintEngineFactory _engineFactory;
    private readonly IScriptDiagnosticsSink _diagnostics;
    private readonly ConcurrentDictionary<string, JintScriptInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public JintScriptRuntime(
        IScriptExecutionSupervisor supervisor,
        JintEngineFactory? engineFactory = null,
        IScriptDiagnosticsSink? diagnostics = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _engineFactory = engineFactory ?? new JintEngineFactory();
        _diagnostics = diagnostics ?? NullScriptDiagnosticsSink.Instance;
    }

    public string RuntimeName => "jint-4.16.3";

    public Task LoadAsync(IScriptModule module, IScriptHost host, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Jint executes compiled JavaScript packages, not managed script modules.");

    public async Task LoadAsync(CompiledScriptPackage package, IScriptHost host, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(host);
        ValidateHostBinding(package, host);
        IScriptExecutionScope scope = _supervisor.CreateOwner(package.Manifest.OwnerKind, package.Manifest.Name, package.Manifest.Id);
        JintScriptInstance instance;
        try
        {
            instance = new JintScriptInstance(package, host, scope, _engineFactory, _diagnostics);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!_instances.TryAdd(package.Manifest.Id.Value, instance))
        {
            await instance.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Script '{package.Manifest.Id}' is already loaded.");
        }

        try
        {
            await instance.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _instances.TryRemove(package.Manifest.Id.Value, out _);
            await instance.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }


    public async Task ReloadAsync(CompiledScriptPackage package, IScriptHost host, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(host);
        ValidateHostBinding(package, host);

        // Replacement, never mutation: fully initialize a fresh engine first. If initialization fails,
        // the existing instance remains active. Only after the replacement is running do we atomically swap.
        IScriptExecutionScope scope = _supervisor.CreateOwner(package.Manifest.OwnerKind, package.Manifest.Name, package.Manifest.Id);
        JintScriptInstance replacement;
        try
        {
            replacement = new JintScriptInstance(package, host, scope, _engineFactory, _diagnostics);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            await replacement.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        JintScriptInstance? previous = null;
        _instances.AddOrUpdate(
            package.Manifest.Id.Value,
            replacement,
            (_, existing) =>
            {
                previous = existing;
                return replacement;
            });

        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<bool> UnloadAsync(ScriptModuleId moduleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_instances.TryRemove(moduleId.Value, out JintScriptInstance? instance)) return false;
        await instance.StopAsync(cancellationToken).ConfigureAwait(false);
        await instance.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<ScriptModuleSnapshot> Snapshot() => _instances.Values
        .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
        .Select(instance => instance.Snapshot())
        .ToArray();


    private static void ValidateHostBinding(CompiledScriptPackage package, IScriptHost host)
    {
        if (!package.Manifest.Id.Equals(host.ModuleId))
            throw new InvalidOperationException("Script host module identity does not match the script manifest.");
        if (host.Permissions.Capabilities != package.Manifest.Permissions)
            throw new InvalidOperationException("Script host capability set does not exactly match the script manifest.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        JintScriptInstance[] instances = _instances.Values.ToArray();
        _instances.Clear();
        foreach (JintScriptInstance instance in instances)
            await instance.DisposeAsync().ConfigureAwait(false);
    }
}

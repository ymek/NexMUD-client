using System.Collections.Concurrent;
using JevMud.Scripting.Compilation;
using JevMud.Scripting.Execution;
using JevMud.Scripting.Host;
using JevMud.Scripting.Permissions;

namespace JevMud.Scripting.Runtime;

public interface IScriptContext
{
    IScriptHost Host { get; }
    IScriptExecutionScope Execution { get; }
}

public interface IScriptModule
{
    ScriptModuleId Id { get; }
    string Name { get; }
    ScriptOwnerKind OwnerKind => ScriptOwnerKind.UserScript;
    IScriptPermissionSet Permissions { get; }
    Task InitializeAsync(IScriptContext context, CancellationToken cancellationToken = default);
}

public enum ScriptStatus
{
    Disabled,
    Loading,
    Running,
    Faulted,
    Stopping
}

public enum ScriptInvocationStatus
{
    Queued,
    Running,
    WaitingOnHost,
    Completed,
    Faulted,
    Cancelled
}

public sealed record ScriptInvocationSnapshot(
    Guid InvocationId,
    Guid EventId,
    Guid? CorrelationId,
    ScriptInvocationStatus Status);

public sealed record ScriptModuleSnapshot(
    ScriptModuleId Id,
    string Name,
    bool Loaded,
    bool CancellationRequested,
    IReadOnlyList<ScriptTaskSnapshot> Tasks,
    ScriptStatus Status = ScriptStatus.Disabled,
    IReadOnlyList<ScriptInvocationSnapshot>? Invocations = null);

public interface IScriptRuntime : IAsyncDisposable
{
    string RuntimeName { get; }
    Task LoadAsync(IScriptModule module, IScriptHost host, CancellationToken cancellationToken = default);
    Task<bool> UnloadAsync(ScriptModuleId moduleId, CancellationToken cancellationToken = default);
    IReadOnlyList<ScriptModuleSnapshot> Snapshot();
}

/// <summary>
/// JavaScript execution specialization kept separate from the language-neutral runtime contract.
/// Jint and a future V8 adapter can implement this without forcing JavaScript artifacts into Lua
/// or other future runtime contracts.
/// </summary>
public interface IJavaScriptRuntime : IScriptRuntime
{
    Task LoadAsync(CompiledScriptPackage package, IScriptHost host, CancellationToken cancellationToken = default);
    Task ReloadAsync(CompiledScriptPackage package, IScriptHost host, CancellationToken cancellationToken = default);
}

/// <summary>
/// Managed bootstrap runtime for first-party policy modules. It exercises the same host,
/// permission, event, scheduling, storage and lifecycle contracts a future JavaScript/Lua
/// adapter will implement without selecting a user scripting language prematurely.
/// </summary>
public sealed class ManagedScriptRuntime : IScriptRuntime
{
    private sealed record LoadedModule(IScriptModule Module, IScriptExecutionScope Scope);
    private sealed record Context(IScriptHost Host, IScriptExecutionScope Execution) : IScriptContext;

    private readonly IScriptExecutionSupervisor _supervisor;
    private readonly ConcurrentDictionary<string, LoadedModule> _modules = new(StringComparer.OrdinalIgnoreCase);

    public ManagedScriptRuntime(IScriptExecutionSupervisor supervisor) =>
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    public string RuntimeName => "managed-bootstrap";

    public async Task LoadAsync(IScriptModule module, IScriptHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(host);
        if (!module.Id.Equals(host.ModuleId))
            throw new InvalidOperationException("Script host module identity does not match the module being loaded.");
        if (host.Permissions.Capabilities != module.Permissions.Capabilities)
            throw new InvalidOperationException("Script host capability set does not match the module declaration.");

        IScriptExecutionScope scope = _supervisor.CreateOwner(module.OwnerKind, module.Name, module.Id);
        LoadedModule loaded = new(module, scope);
        if (!_modules.TryAdd(module.Id.Value, loaded))
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Script module '{module.Id}' is already loaded.");
        }

        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                loaded.Scope.CancellationToken);
            await module.InitializeAsync(new Context(host, loaded.Scope), linked.Token).ConfigureAwait(false);
        }
        catch
        {
            _modules.TryRemove(module.Id.Value, out _);
            await loaded.Scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> UnloadAsync(ScriptModuleId moduleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_modules.TryRemove(moduleId.Value, out LoadedModule? loaded)) return false;
        await loaded.Scope.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<ScriptModuleSnapshot> Snapshot() => _modules.Values
        .OrderBy(value => value.Module.Name, StringComparer.OrdinalIgnoreCase)
        .Select(value => new ScriptModuleSnapshot(
            value.Module.Id,
            value.Module.Name,
            true,
            value.Scope.IsCancellationRequested,
            value.Scope.Snapshot(),
            ScriptStatus.Running,
            Array.Empty<ScriptInvocationSnapshot>()))
        .ToArray();

    public async ValueTask DisposeAsync()
    {
        LoadedModule[] modules = _modules.Values.ToArray();
        _modules.Clear();
        foreach (LoadedModule module in modules)
            await module.Scope.DisposeAsync().ConfigureAwait(false);
    }
}

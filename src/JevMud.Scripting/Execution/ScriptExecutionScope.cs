using System.Collections.Concurrent;
using JevMud.Scripting.Runtime;
using JevMud.Scripting.Time;

namespace JevMud.Scripting.Execution;

public sealed record ScriptTaskSnapshot(
    Guid OwnerId,
    ScriptOwnerKind OwnerKind,
    string OwnerName,
    string Operation,
    DateTimeOffset StartedAt,
    bool CancellationRequested);

public sealed record ScriptTaskFault(
    Guid OwnerId,
    ScriptOwnerKind OwnerKind,
    string OwnerName,
    string Operation,
    DateTimeOffset Timestamp,
    Exception Exception);

public interface IScriptExecutionScope : IAsyncDisposable
{
    ScriptOwner Owner { get; }
    CancellationToken CancellationToken { get; }
    bool IsCancellationRequested { get; }
    IScriptExecutionScope CreateChild(ScriptOwnerKind kind, string name, ScriptModuleId? moduleId = null);
    Task RunAsync(string operation, Func<CancellationToken, Task> work);
    void Cancel();
    IReadOnlyList<ScriptTaskSnapshot> Snapshot();
}

public sealed class ScriptExecutionScope : IScriptExecutionScope
{
    private sealed record RunningTask(string Operation, DateTimeOffset StartedAt, Task Task);

    private readonly CancellationTokenSource _cts;
    private readonly IScriptClock _clock;
    private readonly Action<ScriptTaskFault>? _faultSink;
    private readonly ConcurrentDictionary<Guid, ScriptExecutionScope> _children = new();
    private readonly ConcurrentDictionary<Guid, RunningTask> _tasks = new();
    private readonly object _disposeLock = new();
    private Action? _onDisposed;
    private bool _disposed;

    public ScriptExecutionScope(
        ScriptOwner owner,
        CancellationToken parentCancellation = default,
        IScriptClock? clock = null,
        Action<ScriptTaskFault>? faultSink = null)
    {
        Owner = owner;
        _clock = clock ?? SystemScriptClock.Instance;
        _faultSink = faultSink;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
    }

    public ScriptOwner Owner { get; }
    public CancellationToken CancellationToken => _cts.Token;
    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    public IScriptExecutionScope CreateChild(ScriptOwnerKind kind, string name, ScriptModuleId? moduleId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateChildKind(kind);
        ScriptOwner owner = ScriptOwner.Create(kind, name, moduleId ?? Owner.ModuleId, Owner.Id);
        ScriptExecutionScope child = new(owner, _cts.Token, _clock, _faultSink);
        if (!_children.TryAdd(owner.Id, child))
        {
            child.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("Unable to register script execution child scope.");
        }
        child._onDisposed = () => _children.TryRemove(owner.Id, out _);
        return child;
    }

    private void ValidateChildKind(ScriptOwnerKind childKind)
    {
        bool allowed = Owner.Kind switch
        {
            ScriptOwnerKind.Application => true,
            ScriptOwnerKind.AutomationRule => childKind is ScriptOwnerKind.AutomationRule or ScriptOwnerKind.AutomationWorkflow,
            ScriptOwnerKind.AutomationWorkflow => childKind == ScriptOwnerKind.AutomationWorkflow,
            ScriptOwnerKind.UserScript => childKind == ScriptOwnerKind.UserScript,
            ScriptOwnerKind.JevSession => childKind == ScriptOwnerKind.JevSession,
            ScriptOwnerKind.MapperRoute => childKind == ScriptOwnerKind.MapperRoute,
            ScriptOwnerKind.Plugin => childKind == ScriptOwnerKind.Plugin,
            ScriptOwnerKind.System => childKind == ScriptOwnerKind.System,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Execution owner '{Owner.Kind}' cannot create child owner '{childKind}'.");
    }

    public Task RunAsync(string operation, Func<CancellationToken, Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cts.Token.ThrowIfCancellationRequested();

        Task task = RunTrackedAsync(operation, work);
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private async Task RunTrackedAsync(string operation, Func<CancellationToken, Task> work)
    {
        Guid taskId = Guid.NewGuid();
        DateTimeOffset startedAt = _clock.UtcNow;
        Task task;
        try
        {
            task = work(_cts.Token);
        }
        catch (Exception exception)
        {
            ReportFault(operation, exception);
            throw;
        }

        _tasks[taskId] = new RunningTask(operation, startedAt, task);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFault(operation, exception);
            throw;
        }
        finally
        {
            _tasks.TryRemove(taskId, out _);
        }
    }

    private void ReportFault(string operation, Exception exception)
    {
        try
        {
            _faultSink?.Invoke(new ScriptTaskFault(
                Owner.Id,
                Owner.Kind,
                Owner.Name,
                operation,
                _clock.UtcNow,
                exception));
        }
        catch
        {
            // Observability must never replace the original task failure.
        }
    }

    public void Cancel()
    {
        if (_disposed) return;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public IReadOnlyList<ScriptTaskSnapshot> Snapshot()
    {
        List<ScriptTaskSnapshot> snapshots = _tasks.Values
            .Select(task => new ScriptTaskSnapshot(
                Owner.Id,
                Owner.Kind,
                Owner.Name,
                task.Operation,
                task.StartedAt,
                _cts.IsCancellationRequested))
            .ToList();
        foreach (ScriptExecutionScope child in _children.Values)
            snapshots.AddRange(child.Snapshot());
        return snapshots.OrderBy(snapshot => snapshot.StartedAt).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        ScriptExecutionScope[] children = _children.Values.ToArray();
        foreach (ScriptExecutionScope child in children)
        {
            try { await child.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        Task[] tasks = _tasks.Values.Select(value => value.Task).ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch { }
        }

        _cts.Dispose();
        _onDisposed?.Invoke();
    }
}

public interface IScriptExecutionSupervisor : IAsyncDisposable
{
    event Action<ScriptTaskFault>? TaskFaulted;
    IScriptExecutionScope Root { get; }
    IScriptExecutionScope CreateOwner(ScriptOwnerKind kind, string name, ScriptModuleId? moduleId = null);
    IReadOnlyList<ScriptTaskSnapshot> Snapshot();
}

public sealed class ScriptExecutionSupervisor : IScriptExecutionSupervisor
{
    public ScriptExecutionSupervisor(
        CancellationToken applicationCancellation = default,
        IScriptClock? clock = null)
    {
        Root = new ScriptExecutionScope(
            ScriptOwner.Create(ScriptOwnerKind.Application, "NexMUD"),
            applicationCancellation,
            clock,
            fault => TaskFaulted?.Invoke(fault));
    }

    public event Action<ScriptTaskFault>? TaskFaulted;
    public IScriptExecutionScope Root { get; }

    public IScriptExecutionScope CreateOwner(ScriptOwnerKind kind, string name, ScriptModuleId? moduleId = null) =>
        Root.CreateChild(kind, name, moduleId);

    public IReadOnlyList<ScriptTaskSnapshot> Snapshot() => Root.Snapshot();

    public ValueTask DisposeAsync() => Root.DisposeAsync();
}

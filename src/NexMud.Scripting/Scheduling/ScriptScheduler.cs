using NexMud.Scripting.Execution;
using NexMud.Scripting.Time;

namespace NexMud.Scripting.Scheduling;

public interface IScriptScheduledTask : IAsyncDisposable
{
    Guid Id { get; }
    void Cancel();
}

public interface IScriptScheduler
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
    IScriptScheduledTask After(
        IScriptExecutionScope owner,
        TimeSpan delay,
        string name,
        Func<CancellationToken, Task> callback);
    IScriptScheduledTask Every(
        IScriptExecutionScope owner,
        TimeSpan interval,
        string name,
        Func<CancellationToken, Task> callback);
}

public sealed class ScriptScheduler : IScriptScheduler
{
    private sealed class ScheduledTask : IScriptScheduledTask
    {
        private readonly CancellationTokenSource _cts;
        private int _disposed;

        public ScheduledTask(Guid id, CancellationToken parent)
        {
            Id = id;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public Guid Id { get; }
        public CancellationToken Token => _cts.Token;
        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { if (!_cts.IsCancellationRequested) _cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public void Complete()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _cts.Dispose();
        }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { if (!_cts.IsCancellationRequested) _cts.Cancel(); }
                catch (ObjectDisposedException) { }
                _cts.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }

    private readonly IScriptClock _clock;

    public ScriptScheduler(IScriptClock? clock = null) => _clock = clock ?? SystemScriptClock.Instance;

    public DateTimeOffset UtcNow => _clock.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        _clock.DelayAsync(delay, cancellationToken);

    public IScriptScheduledTask After(
        IScriptExecutionScope owner,
        TimeSpan delay,
        string name,
        Func<CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(callback);
        ScheduledTask scheduled = new(Guid.NewGuid(), owner.CancellationToken);
        _ = owner.RunAsync($"timer:{name}", async ownerToken =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, scheduled.Token);
            try
            {
                await _clock.DelayAsync(delay, linked.Token).ConfigureAwait(false);
                await callback(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { scheduled.Complete(); }
        });
        return scheduled;
    }

    public IScriptScheduledTask Every(
        IScriptExecutionScope owner,
        TimeSpan interval,
        string name,
        Func<CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(callback);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        ScheduledTask scheduled = new(Guid.NewGuid(), owner.CancellationToken);
        _ = owner.RunAsync($"timer:{name}", async ownerToken =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, scheduled.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await _clock.DelayAsync(interval, linked.Token).ConfigureAwait(false);
                    await callback(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { scheduled.Complete(); }
        });
        return scheduled;
    }
}

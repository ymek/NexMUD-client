using System.Threading.Channels;
using Jint;

namespace JevMud.Scripting.Jint.Runtime;

/// <summary>
/// Single-consumer mailbox. Every entry into the owning Jint engine goes through this dispatcher.
/// </summary>
public sealed class JintScriptDispatcher : IAsyncDisposable
{
    private interface IWorkItem
    {
        void Execute(Engine engine);
        void Cancel(CancellationToken cancellationToken);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<Engine, T> _work;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<Engine, T> work) => _work = work;
        public Task<T> Task => _completion.Task;

        public void Execute(Engine engine)
        {
            try { _completion.TrySetResult(_work(engine)); }
            catch (OperationCanceledException exception) { _completion.TrySetCanceled(exception.CancellationToken); }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }

        public void Cancel(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);
    }

    private readonly Engine _engine;
    private readonly Channel<IWorkItem> _mailbox;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumer;
    private int _accepting = 1;

    public JintScriptDispatcher(Engine engine, int capacity)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _mailbox = Channel.CreateBounded<IWorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _consumer = ConsumeAsync();
    }

    public bool IsAccepting => Volatile.Read(ref _accepting) != 0;

    public async Task<T> EnqueueAsync<T>(Func<Engine, T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(!IsAccepting, this);
        cancellationToken.ThrowIfCancellationRequested();
        WorkItem<T> item = new(work);
        await _mailbox.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        return await item.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task EnqueueAsync(Action<Engine> work, CancellationToken cancellationToken = default) =>
        EnqueueAsync(engine => { work(engine); return true; }, cancellationToken);

    public void StopAccepting()
    {
        Interlocked.Exchange(ref _accepting, 0);
        _mailbox.Writer.TryComplete();
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (IWorkItem item in _mailbox.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                item.Execute(_engine);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            while (_mailbox.Reader.TryRead(out IWorkItem? remaining))
                remaining.Cancel(_shutdown.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _accepting, 0);
        _mailbox.Writer.TryComplete();
        try { await _consumer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _shutdown.Cancel();
            try { await _consumer.ConfigureAwait(false); } catch { }
        }
        _shutdown.Dispose();
        _engine.Dispose();
    }
}

namespace NexMud.Gui.AutomationStudio;

internal sealed class StudioProfileTransitionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _generation;

    public long Request() => Interlocked.Increment(ref _generation);

    public bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation);

    public async Task<bool> EnterLatestAsync(long generation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (IsCurrent(generation)) return true;
        _gate.Release();
        return false;
    }

    public void Exit() => _gate.Release();
}

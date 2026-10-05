namespace NexMud.Gui.AutomationStudio;

internal sealed class RuntimeUiRefreshGate
{
    private readonly object _sync = new();
    private long _generation;
    private bool _pending;
    private bool _refreshRequestedWhilePending;

    public void Invalidate()
    {
        lock (_sync)
        {
            _generation++;
            _pending = false;
            _refreshRequestedWhilePending = false;
        }
    }

    public bool TryQueue(out long generation)
    {
        lock (_sync)
        {
            generation = _generation;
            if (_pending)
            {
                _refreshRequestedWhilePending = true;
                return false;
            }

            _pending = true;
            return true;
        }
    }

    public bool Complete(long generation)
    {
        lock (_sync)
        {
            if (generation != _generation) return false;
            if (_refreshRequestedWhilePending)
            {
                _refreshRequestedWhilePending = false;
                return true;
            }

            _pending = false;
            return false;
        }
    }
}

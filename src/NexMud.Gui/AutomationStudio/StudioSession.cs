namespace NexMud.Gui.AutomationStudio;

internal enum StudioActivity
{
    Automations,
    Scripts,
    Workflows,
    Search,
    Runtime
}

internal readonly record struct StudioSessionSnapshot(
    string ProfileId,
    long Generation,
    CancellationToken CancellationToken);

/// <summary>
/// Owns the profile-scoped lifetime of Studio operations. Switching profiles invalidates the
/// previous generation before new profile work is allowed to publish results.
/// </summary>
internal sealed class StudioSessionController : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource _scope = new();
    private string _profileId;
    private long _generation;
    private bool _disposed;

    public StudioSessionController(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        _profileId = profileId;
    }

    public StudioSessionSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return Snapshot();
            }
        }
    }

    public StudioSessionSnapshot SwitchProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        CancellationTokenSource previous;
        StudioSessionSnapshot next;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (string.Equals(_profileId, profileId, StringComparison.Ordinal))
                return Snapshot();

            previous = _scope;
            _scope = new CancellationTokenSource();
            _profileId = profileId;
            _generation++;
            next = Snapshot();
        }

        previous.Cancel();
        previous.Dispose();
        return next;
    }

    public bool IsCurrent(StudioSessionSnapshot snapshot)
    {
        lock (_gate)
        {
            return !_disposed
                && snapshot.Generation == _generation
                && string.Equals(snapshot.ProfileId, _profileId, StringComparison.Ordinal)
                && !snapshot.CancellationToken.IsCancellationRequested;
        }
    }

    private StudioSessionSnapshot Snapshot() =>
        new(_profileId, _generation, _scope.Token);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        CancellationTokenSource scope;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            scope = _scope;
        }

        scope.Cancel();
        scope.Dispose();
    }
}

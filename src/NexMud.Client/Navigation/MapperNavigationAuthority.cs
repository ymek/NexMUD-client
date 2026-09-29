namespace NexMud.Client.Navigation;

/// <summary>
/// Session-scoped authority marker for autonomous Mapper navigation. Manual user commands remain
/// authoritative; other autonomous producers may not issue competing movement while a route owns it.
/// </summary>
public sealed class MapperNavigationAuthority
{
    private readonly object _gate = new();
    private Guid? _routeExecutionId;
    private Func<CancellationToken, Task>? _manualMovementHandler;

    public Guid? CurrentRouteExecutionId
    {
        get { lock (_gate) return _routeExecutionId; }
    }

    public bool IsHeld => CurrentRouteExecutionId is not null;

    public bool TryAcquire(Guid routeExecutionId)
    {
        lock (_gate)
        {
            if (_routeExecutionId is not null && _routeExecutionId != routeExecutionId) return false;
            _routeExecutionId = routeExecutionId;
            return true;
        }
    }

    public void Release(Guid routeExecutionId)
    {
        lock (_gate)
        {
            if (_routeExecutionId == routeExecutionId) _routeExecutionId = null;
        }
    }

    public bool IsOwnedBy(Guid? routeExecutionId)
    {
        lock (_gate) return _routeExecutionId is not null && routeExecutionId is not null && _routeExecutionId == routeExecutionId;
    }

    public void SetManualMovementHandler(Func<CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _manualMovementHandler = handler;
    }

    public Task RequestManualMovementAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task>? handler;
        lock (_gate) handler = _routeExecutionId is null ? null : _manualMovementHandler;
        return handler is null ? Task.CompletedTask : handler(cancellationToken);
    }
}

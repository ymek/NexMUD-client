using System.Text.Json;
using System.Threading.Channels;
using JevMud.Client.Knowledge;
using JevMud.Client.Scripting;
using JevMud.Client.Settings;
using JevMud.Contracts.Actions;
using JevMud.Contracts.Events;
using JevMud.Contracts.Jev;
using JevMud.Contracts.State;
using JevMud.Core.Events;
using JevMud.Core.State;
using JevMud.Scripting.Compilation;
using JevMud.Scripting.Events;
using JevMud.Scripting.Host;
using JevMud.Scripting.Permissions;
using JevMud.Scripting.Runtime;
using JevMud.Scripting.Scheduling;

namespace JevMud.Client.Navigation;

/// <summary>
/// Public route-control facade. Route execution itself lives in the NexMUD-owned Jint Mapper
/// orchestration module; this service owns requests, authority, pause/resume/abort, and UI state.
/// </summary>
public sealed class AutoMoveService
{
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly StateReducer _state;
    private readonly MapperReadRepository _routes;
    private readonly IEventSink _eventSink;
    private readonly Func<MapperPreferences> _settings;
    private readonly ClientScriptPlatform _platform;
    private readonly IScriptMapper _mapper;
    private readonly MapperNavigationAuthority _authority;
    private readonly IScriptScheduler _scheduler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _routeSignalSequence;

    private MapperRouteExecutionContext? _execution;
    private AutoMoveStateChanged _current = new(AutoMoveStatus.Idle, null, null, 0, 0, null);

    public AutoMoveService(
        ChannelReader<EventEnvelope> events,
        StateReducer state,
        WorldKnowledgeStore knowledge,
        IEventSink eventSink,
        Func<MapperPreferences> settings,
        ClientScriptPlatform platform,
        IScriptMapper mapper,
        MapperNavigationAuthority authority,
        IScriptScheduler scheduler)
    {
        _events = events;
        _state = state;
        _routes = new MapperReadRepository(knowledge.DatabasePath);
        _eventSink = eventSink;
        _settings = settings;
        _platform = platform;
        _mapper = mapper;
        _authority = authority;
        _scheduler = scheduler;
        _authority.SetManualMovementHandler(PauseForManualMovementAsync);
    }

    public AutoMoveStateChanged Current => Volatile.Read(ref _current);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (envelope.Payload)
                {
                    case CombatStateChanged { Active: true } when _settings().StopAutoMoveOnCombat:
                        await PauseAsync("Combat started.", cancellationToken).ConfigureAwait(false);
                        break;
                    case CombatStateChanged { Active: false } when _settings().ResumeAutoMoveAfterCombat &&
                                                                Current.Status == AutoMoveStatus.Paused &&
                                                                string.Equals(Current.Reason, "Combat started.", StringComparison.Ordinal):
                        await ResumeAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case ConnectionStateChanged { Status: ConnectionStatus.Disconnected }:
                        await StopAsync("Disconnected.", cancellationToken).ConfigureAwait(false);
                        break;
                    case ScriptRuntimeDiagnosticEmitted diagnostic when IsActiveRouteDiagnostic(diagnostic):
                        await FailActiveRouteAsync($"Mapper route script faulted: {diagnostic.Message}", cancellationToken).ConfigureAwait(false);
                        break;
                    case ScriptRuntimeTaskFaulted fault when IsActiveRouteTaskFault(fault):
                        await FailActiveRouteAsync($"Mapper route runtime faulted: {fault.Message}", cancellationToken).ConfigureAwait(false);
                        break;
                    case ActionDispatching action when
                        action.Provenance?.Origin != CommandOrigin.Mapper &&
                        NavigationCommandClassifier.IsMovementCommand(action.Command):
                        await PauseAsync(
                            action.Source == DecisionSource.Human
                                ? "Paused for manual navigation."
                                : "Paused because another controller issued navigation.",
                            cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public Task StartAsync(
        string destinationRoomId,
        string? destinationLabel = null,
        CancellationToken cancellationToken = default) =>
        StartInternalAsync(destinationRoomId, destinationLabel, singleStep: false, cancellationToken);

    public Task StepAsync(
        string destinationRoomId,
        string? destinationLabel = null,
        CancellationToken cancellationToken = default) =>
        StartInternalAsync(destinationRoomId, destinationLabel, singleStep: true, cancellationToken);

    public async Task<bool> NavigateToQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        string? currentRoomId = _state.Current.Room.Id;
        if (string.IsNullOrWhiteSpace(currentRoomId)) return false;

        (MapperDestinationSearchResult Destination, KnowledgeRoute Route)? nearest = await _routes.FindNearestAsync(
            currentRoomId,
            query.Trim(),
            RoutePlanningOptions.From(_settings()),
            candidateLimit: 30,
            cancellationToken).ConfigureAwait(false);
        if (nearest is null) return false;

        await StartInternalAsync(
            nearest.Value.Destination.RoomId,
            nearest.Value.Destination.DisplayName,
            singleStep: false,
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutoMoveStateChanged current = Current;
            if (!string.Equals(current.DestinationRoomId, nearest.Value.Destination.RoomId, StringComparison.Ordinal)) return false;
            if (current.Status == AutoMoveStatus.Completed) return true;
            if (current.Status is AutoMoveStatus.Failed or AutoMoveStatus.Aborted or AutoMoveStatus.Stopped) return false;
            if (current.Status == AutoMoveStatus.Paused &&
                !(string.Equals(current.Reason, "Combat started.", StringComparison.Ordinal) && _settings().ResumeAutoMoveAfterCombat))
                return false;
            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PauseAsync(string reason = "Paused by user.", CancellationToken cancellationToken = default)
    {
        MapperRouteExecutionContext? execution;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            execution = _execution;
            if (execution is null || Current.Status is AutoMoveStatus.Completed or AutoMoveStatus.Failed or AutoMoveStatus.Aborted or AutoMoveStatus.Stopped)
                return;
            if (!execution.Control.Pause()) return;
        }
        finally
        {
            _gate.Release();
        }

        // A movement already in flight is allowed to resolve. RouteBoundScriptMapperHost publishes
        // the stable Paused state after that result. Otherwise pause is immediately stable.
        if (!execution.MoveInFlight)
        {
            (int completed, int total) = execution.Progress();
            await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
                MapperRouteLifecycleKind.Paused,
                AutoMoveStatus.Paused,
                reason,
                CompletedSteps: completed,
                TotalSteps: total), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PauseForManualMovementAsync(CancellationToken cancellationToken)
    {
        await PauseAsync("Paused for manual navigation.", cancellationToken).ConfigureAwait(false);
        while (true)
        {
            MapperRouteExecutionContext? execution = _execution;
            if (execution is null || !execution.MoveInFlight) return;
            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        MapperRouteExecutionContext? execution;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            execution = _execution;
            if (execution is null || !execution.Control.Resume()) return;
        }
        finally
        {
            _gate.Release();
        }

        (int completed, int total) = execution.Progress();
        await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
            MapperRouteLifecycleKind.Resumed,
            AutoMoveStatus.Replanning,
            "Route resumed; recomputing from the current room.",
            CompletedSteps: completed,
            TotalSteps: total), cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(string reason = "Stopped by user.", CancellationToken cancellationToken = default)
    {
        MapperRouteExecutionContext? execution;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            execution = _execution;
            if (execution is null || Current.Status is AutoMoveStatus.Idle or AutoMoveStatus.Aborted or AutoMoveStatus.Stopped) return;
            execution.Control.Abort();
            _authority.Release(execution.ExecutionId);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await _platform.JavaScriptRuntime.UnloadAsync(MapperRouteScriptCompiler.ModuleId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Route authority is already released and host work is cancelled through the route control.
        }

        (int completed, int total) = execution.Progress();
        await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
            MapperRouteLifecycleKind.Aborted,
            AutoMoveStatus.Aborted,
            reason,
            CompletedSteps: completed,
            TotalSteps: total), CancellationToken.None).ConfigureAwait(false);
    }

    private bool IsActiveRouteDiagnostic(ScriptRuntimeDiagnosticEmitted diagnostic)
    {
        MapperRouteExecutionContext? execution = _execution;
        return execution is not null &&
               !string.IsNullOrWhiteSpace(execution.ScriptVersion) &&
               string.Equals(diagnostic.ScriptId, MapperRouteScriptCompiler.ModuleId.Value, StringComparison.Ordinal) &&
               string.Equals(diagnostic.ScriptVersion, execution.ScriptVersion, StringComparison.Ordinal) &&
               diagnostic.Kind is "InvocationFaulted" or "Timeout" or "ResourceLimitExceeded" or "UncaughtException";
    }

    private bool IsActiveRouteTaskFault(ScriptRuntimeTaskFaulted fault)
    {
        MapperRouteExecutionContext? execution = _execution;
        return execution is not null &&
               !string.IsNullOrWhiteSpace(execution.RuntimeOwnerName) &&
               string.Equals(fault.OwnerKind, ScriptOwnerKind.MapperRoute.ToString(), StringComparison.Ordinal) &&
               string.Equals(fault.OwnerName, execution.RuntimeOwnerName, StringComparison.Ordinal);
    }

    private async Task FailActiveRouteAsync(string reason, CancellationToken cancellationToken)
    {
        MapperRouteExecutionContext? execution = _execution;
        if (execution is null) return;
        execution.Control.Abort();
        _authority.Release(execution.ExecutionId);
        (int completed, int total) = execution.Progress();
        await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
            MapperRouteLifecycleKind.Failed,
            AutoMoveStatus.Failed,
            reason,
            CompletedSteps: completed,
            TotalSteps: total,
            FailureReason: MapperRouteFailureReason.ScriptFault), cancellationToken).ConfigureAwait(false);
    }

    private async Task StartInternalAsync(
        string destinationRoomId,
        string? destinationLabel,
        bool singleStep,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoomId);
        MapperPreferences settings = _settings();
        if (!settings.Enabled || !settings.AutoMoveEnabled)
        {
            await PublishStandaloneFailureAsync(destinationRoomId, destinationLabel, "Auto-move is disabled in Mapper settings.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (_state.Current.Session.ConnectionStatus != ConnectionStatus.Connected)
        {
            await PublishStandaloneFailureAsync(destinationRoomId, destinationLabel, "Not connected.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(_state.Current.Room.Id))
        {
            await PublishStandaloneFailureAsync(destinationRoomId, destinationLabel, "Current room is not known to the mapper.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        MapperRouteExecutionContext? previous;
        MapperRouteExecutionContext execution;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            previous = _execution;
            previous?.Control.Abort();
            if (previous is not null) _authority.Release(previous.ExecutionId);

            Guid executionId = Guid.NewGuid();
            if (!_authority.TryAcquire(executionId))
                throw new InvalidOperationException("Mapper navigation authority is already owned by another route.");

            MapperRouteControl control = new();
            execution = new MapperRouteExecutionContext(
                executionId,
                destinationRoomId.Trim(),
                destinationLabel,
                singleStep,
                settings.AutoMoveMaximumReplans,
                control,
                (update, token) => ApplyRouteUpdateByIdAsync(executionId, update, token));
            _execution = execution;
        }
        finally
        {
            _gate.Release();
        }

        await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
            MapperRouteLifecycleKind.Started,
            AutoMoveStatus.Planning,
            "Planning route."), cancellationToken).ConfigureAwait(false);

        // Planning is Mapper domain work, not orchestration. Resolve the first immutable plan in C#
        // before loading the route module so a route cannot strand in Planning on the first async
        // SDK round-trip. The script consumes this plan and uses nex.mapper.findPath() only when
        // authoritative state changes or a replan is required.
        ScriptRoutePlan? initialPlan;
        try
        {
            initialPlan = await _mapper.FindPathAsync(destinationRoomId.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution.Control.Abort();
            _authority.Release(execution.ExecutionId);
            await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
                MapperRouteLifecycleKind.Failed,
                AutoMoveStatus.Failed,
                $"Route planning failed: {exception.Message}",
                FailureReason: MapperRouteFailureReason.MapperFault), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (initialPlan is null)
        {
            execution.Control.Abort();
            _authority.Release(execution.ExecutionId);
            await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
                MapperRouteLifecycleKind.Failed,
                AutoMoveStatus.Failed,
                "No known route to the destination.",
                FailureReason: MapperRouteFailureReason.NoPath), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        (int completed, int total) = execution.SetPlan(initialPlan.Steps.Count);
        await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
            MapperRouteLifecycleKind.Planned,
            initialPlan.Steps.Count == 0 ? AutoMoveStatus.Completed : AutoMoveStatus.Moving,
            initialPlan.Steps.Count == 0 ? "Already at destination." : null,
            initialPlan.RouteId,
            completed,
            total,
            initialPlan.Steps.Count > 0 ? initialPlan.Steps[0].Sequence : null,
            initialPlan.Steps.Count > 0 ? initialPlan.Steps[0].Direction : null), cancellationToken).ConfigureAwait(false);

        if (initialPlan.Steps.Count == 0)
            return;

        RouteBoundScriptMapperHost routeMapper = new(_mapper, execution);
        ScriptPermissionSet permissions = new(MapperRouteScriptCompiler.Permissions);
        CompiledScriptPackage package = MapperRouteScriptCompiler.Compile(
            execution.ExecutionId,
            destinationRoomId.Trim(),
            settings,
            initialPlan);
        execution.BindRuntimeIdentity(package.Manifest.Version, package.Manifest.Name);
        IScriptHost host = _platform.CreateHost(
            MapperRouteScriptCompiler.ModuleId,
            permissions,
            ScriptCommandOrigin.Mapper,
            package.Manifest.Name,
            mapperOverride: routeMapper);

        try
        {
            bool loaded = _platform.JavaScriptRuntime.Snapshot().Any(snapshot => snapshot.Id.Equals(MapperRouteScriptCompiler.ModuleId));
            if (loaded)
                await _platform.JavaScriptRuntime.ReloadAsync(package, host, cancellationToken).ConfigureAwait(false);
            else
                await _platform.JavaScriptRuntime.LoadAsync(package, host, cancellationToken).ConfigureAwait(false);

            // Do not rely on a zero-delay timer scheduled from inside module activation to begin
            // navigation. Publish the route-start signal only after the replacement module is fully
            // loaded and subscribed. This makes the production Go path deterministic and keeps the
            // route handler inside the normal script invocation/mailbox lifecycle.
            JsonElement startPayload = JsonSerializer.SerializeToElement(new
            {
                routeExecutionId = execution.ExecutionId.ToString("D")
            });
            await _platform.EventHub.PublishAsync(
                new ScriptEventEnvelope(
                    MapperRouteScriptCompiler.StartEventType,
                    Interlocked.Increment(ref _routeSignalSequence),
                    _scheduler.UtcNow,
                    "navigator",
                    startPayload,
                    EventId: Guid.NewGuid()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            execution.Control.Abort();
            _authority.Release(execution.ExecutionId);
            await ApplyRouteUpdateAsync(execution, new MapperRouteRuntimeUpdate(
                MapperRouteLifecycleKind.Failed,
                AutoMoveStatus.Failed,
                $"Mapper route script failed to start: {exception.Message}",
                FailureReason: MapperRouteFailureReason.ScriptFault), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task ApplyRouteUpdateByIdAsync(
        Guid executionId,
        MapperRouteRuntimeUpdate update,
        CancellationToken cancellationToken)
    {
        MapperRouteExecutionContext? execution = _execution;
        return execution is not null && execution.ExecutionId == executionId
            ? ApplyRouteUpdateAsync(execution, update, cancellationToken)
            : Task.CompletedTask;
    }

    private async Task ApplyRouteUpdateAsync(
        MapperRouteExecutionContext execution,
        MapperRouteRuntimeUpdate update,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_execution, execution)) return;
            AutoMoveStateChanged snapshot = new(
                update.Status,
                execution.DestinationRoomId,
                execution.DestinationLabel,
                update.CompletedSteps,
                update.TotalSteps,
                update.Direction,
                update.Reason);
            Volatile.Write(ref _current, snapshot);
            if (update.Status is AutoMoveStatus.Completed or AutoMoveStatus.Failed or AutoMoveStatus.Aborted or AutoMoveStatus.Stopped)
                _authority.Release(execution.ExecutionId);
        }
        finally
        {
            _gate.Release();
        }

        await _eventSink.PublishAsync(Current, "navigator", cancellationToken).ConfigureAwait(false);
        await _eventSink.PublishAsync(
            new MapperRouteLifecycleChanged(
                update.Kind,
                execution.ExecutionId,
                execution.DestinationRoomId,
                execution.DestinationLabel,
                update.RouteId,
                update.CompletedSteps,
                update.TotalSteps,
                update.RouteStep,
                update.Direction,
                update.Reason,
                update.FailureReason),
            "navigator",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishStandaloneFailureAsync(
        string destinationRoomId,
        string? destinationLabel,
        string reason,
        CancellationToken cancellationToken)
    {
        AutoMoveStateChanged snapshot = new(AutoMoveStatus.Failed, destinationRoomId, destinationLabel, 0, 0, null, reason);
        Volatile.Write(ref _current, snapshot);
        await _eventSink.PublishAsync(snapshot, "navigator", cancellationToken).ConfigureAwait(false);
    }
}

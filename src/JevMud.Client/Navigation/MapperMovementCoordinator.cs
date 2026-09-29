using System.Threading.Channels;
using JevMud.Client.Settings;
using JevMud.Contracts.Actions;
using JevMud.Contracts.Events;
using JevMud.Contracts.State;
using JevMud.Core.Events;
using JevMud.Core.State;
using JevMud.Scripting.Host;
using JevMud.Scripting.Scheduling;

namespace JevMud.Client.Navigation;

/// <summary>
/// Correlates one Mapper movement command at a time with semantic room/navigation events: one edge at a time.
/// It never reads transport output directly and never writes to Transport.
/// </summary>
public sealed class MapperMovementCoordinator
{
    private sealed record PendingMove(
        Guid ActionId,
        ScriptMoveRequest Request,
        string FromRoomId,
        TaskCompletionSource<ScriptMovementResult> Completion);

    private readonly ChannelReader<EventEnvelope> _events;
    private readonly StateReducer _state;
    private readonly IScriptCommands _commands;
    private readonly IScriptScheduler _scheduler;
    private readonly Func<MapperPreferences> _settings;
    private readonly SemaphoreSlim _movementGate = new(1, 1);
    private readonly object _pendingGate = new();
    private PendingMove? _pending;

    public MapperMovementCoordinator(
        ChannelReader<EventEnvelope> events,
        StateReducer state,
        IScriptCommands commands,
        IScriptScheduler scheduler,
        Func<MapperPreferences> settings)
    {
        _events = events;
        _state = state;
        _commands = commands;
        _scheduler = scheduler;
        _settings = settings;
    }

    public event Action<ActionDispatching>? ExternalMovementDispatched;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _state.WaitUntilProcessedAsync(envelope.Sequence, cancellationToken).ConfigureAwait(false);
                switch (envelope.Payload)
                {
                    case RoomObservationObserved room:
                        CompleteRoom(room.RoomId);
                        break;
                    case RoomChanged room when !string.IsNullOrWhiteSpace(room.Id):
                        CompleteRoom(room.Id!);
                        break;
                    case NavigationFailed failure:
                        CompleteFailure(failure);
                        break;
                    case ActionRejected rejected:
                        CompleteRejected(rejected);
                        break;
                    case ConnectionStateChanged { Status: ConnectionStatus.Disconnected }:
                        Complete(new ScriptMovementResult("disconnected"));
                        break;
                    case ActionDispatching action when
                        action.Provenance?.Origin != CommandOrigin.Mapper &&
                        NavigationCommandClassifier.IsMovementCommand(action.Command):
                        ExternalMovementDispatched?.Invoke(action);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(new ScriptMovementResult("cancelled"));
        }
    }

    public async Task<ScriptMovementResult> MoveAsync(
        ScriptMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Direction);
        if (!NavigationCommandClassifier.IsMovementCommand(request.Direction))
            return Blocked(null, ScriptMovementFailureReason.NoExit, "Mapper movement requires a directional command.", null);
        if (!_movementGate.Wait(0))
            return Blocked(_state.Current.Room.Id, ScriptMovementFailureReason.CannotMove, "Another Mapper movement is already in flight.", null);

        try
        {
            StateSnapshot state = _state.Current;
            if (state.Session.ConnectionStatus != ConnectionStatus.Connected)
                return new ScriptMovementResult("disconnected");
            if (string.IsNullOrWhiteSpace(state.Room.Id))
                return Blocked(null, ScriptMovementFailureReason.CannotMove, "Current room is unknown.", null);
            if (!string.IsNullOrWhiteSpace(request.FromRoomId) &&
                !string.Equals(request.FromRoomId, state.Room.Id, StringComparison.Ordinal))
            {
                return new ScriptMovementResult(
                    "unexpected",
                    FromRoomId: request.FromRoomId,
                    ExpectedRoomId: request.ExpectedRoomId,
                    ActualRoomId: state.Room.Id,
                    Message: "Route step is stale because the current room changed.");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(request.Recovery))
                {
                    ScriptMovementResult? recoveryFailure = await ExecuteRecoveryAsync(request, state, cancellationToken).ConfigureAwait(false);
                    if (recoveryFailure is not null) return recoveryFailure;
                    state = _state.Current;
                }

                Guid actionId = Guid.NewGuid();
                TaskCompletionSource<ScriptMovementResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                PendingMove pending = new(actionId, request, state.Room.Id!, completion);
                lock (_pendingGate)
                {
                    if (_pending is not null)
                        return Blocked(state.Room.Id, ScriptMovementFailureReason.CannotMove, "Another Mapper movement is already in flight.", null);
                    _pending = pending;
                }

                try
                {
                    ScriptCommandResult command = await _commands.SendAsync(
                        new ScriptCommandRequest(
                            request.Direction,
                            ScriptCommandOrigin.Mapper,
                            request.RouteExecutionId?.ToString("N") ?? "mapper-move",
                            "Mapper route",
                            "route movement",
                            ExpectedStateVersion: state.Version,
                            ActionId: actionId,
                            RouteExecutionId: request.RouteExecutionId,
                            RouteId: request.RouteId,
                            RouteStep: request.RouteStep,
                            RouteTotalSteps: request.RouteTotalSteps),
                        cancellationToken).ConfigureAwait(false);
                    if (!command.Accepted)
                        return Blocked(state.Room.Id, ScriptMovementFailureReason.CannotMove, command.Reason ?? "Movement command was rejected.", null);

                    int timeoutMs = Math.Clamp(_settings().AutoMoveStepTimeoutMilliseconds, 1000, 30000);
                    using CancellationTokenSource timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task timeout = _scheduler.DelayAsync(TimeSpan.FromMilliseconds(timeoutMs), timeoutCancellation.Token);
                    Task completed = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
                    if (completed == completion.Task)
                    {
                        timeoutCancellation.Cancel();
                        return await completion.Task.ConfigureAwait(false);
                    }

                    await timeout.ConfigureAwait(false);
                    return new ScriptMovementResult("timeout", FromRoomId: state.Room.Id, ExpectedRoomId: request.ExpectedRoomId);
                }
                finally
                {
                    lock (_pendingGate)
                    {
                        if (ReferenceEquals(_pending, pending)) _pending = null;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ScriptMovementResult("cancelled", FromRoomId: state.Room.Id, ExpectedRoomId: request.ExpectedRoomId);
            }
        }
        finally
        {
            _movementGate.Release();
        }
    }

    private async Task<ScriptMovementResult?> ExecuteRecoveryAsync(
        ScriptMoveRequest request,
        StateSnapshot state,
        CancellationToken cancellationToken)
    {
        string? recoveryCommand = request.Recovery?.Trim().ToLowerInvariant() switch
        {
            "stand" => "stand",
            "open-door" => BuildDoorOpenCommand(request.Direction),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(recoveryCommand))
            return Blocked(state.Room.Id, ScriptMovementFailureReason.CannotMove, "Unsupported Mapper recovery request.", null);

        ScriptCommandResult recovery = await _commands.SendAsync(
            new ScriptCommandRequest(
                recoveryCommand,
                ScriptCommandOrigin.Mapper,
                request.RouteExecutionId?.ToString("N") ?? "mapper-move",
                "Mapper route",
                $"route recovery: {request.Recovery}",
                ExpectedStateVersion: state.Version,
                RouteExecutionId: request.RouteExecutionId,
                RouteId: request.RouteId,
                RouteStep: request.RouteStep,
                RouteTotalSteps: request.RouteTotalSteps),
            cancellationToken).ConfigureAwait(false);
        if (!recovery.Accepted)
            return Blocked(state.Room.Id, ScriptMovementFailureReason.CannotMove, recovery.Reason ?? "Recovery command was rejected.", null);

        int recoveryDelay = Math.Clamp(_settings().AutoMoveStepDelayMilliseconds, 50, 1000);
        if (string.Equals(request.Recovery, "stand", StringComparison.OrdinalIgnoreCase))
        {
            DateTimeOffset deadline = _scheduler.UtcNow.AddMilliseconds(Math.Max(500, recoveryDelay * 4));
            while (_scheduler.UtcNow < deadline)
            {
                string? position = _state.Current.Character.Position;
                if (string.Equals(position, "standing", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(position, "stand", StringComparison.OrdinalIgnoreCase))
                    return null;
                await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(recoveryDelay), cancellationToken).ConfigureAwait(false);
        return null;
    }

    private void CompleteRoom(string roomId)
    {
        PendingMove? pending;
        lock (_pendingGate) pending = _pending;
        if (pending is null || string.Equals(roomId, pending.FromRoomId, StringComparison.Ordinal)) return;

        ScriptMovementResult result = string.IsNullOrWhiteSpace(pending.Request.ExpectedRoomId) ||
                                      string.Equals(roomId, pending.Request.ExpectedRoomId, StringComparison.Ordinal)
            ? new ScriptMovementResult("moved", pending.FromRoomId, roomId, pending.Request.ExpectedRoomId, roomId)
            : new ScriptMovementResult("unexpected", pending.FromRoomId, roomId, pending.Request.ExpectedRoomId, roomId);
        pending.Completion.TrySetResult(result);
    }

    private void CompleteFailure(NavigationFailed failure)
    {
        PendingMove? pending;
        lock (_pendingGate) pending = _pending;
        if (pending is null) return;
        if (!string.IsNullOrWhiteSpace(failure.Direction) &&
            !failure.Direction.Equals(pending.Request.Direction, StringComparison.OrdinalIgnoreCase)) return;

        ScriptMovementFailureReason reason = ClassifyFailure(failure.Reason);
        pending.Completion.TrySetResult(Blocked(
            pending.FromRoomId,
            reason,
            failure.Reason,
            RecoveryCommand(reason, pending.Request.Direction)));
    }

    private void CompleteRejected(ActionRejected rejected)
    {
        PendingMove? pending;
        lock (_pendingGate) pending = _pending;
        if (pending is null || pending.ActionId != rejected.ActionId) return;
        pending.Completion.TrySetResult(Blocked(
            pending.FromRoomId,
            ScriptMovementFailureReason.CannotMove,
            rejected.Reason,
            null));
    }

    private void Complete(ScriptMovementResult result)
    {
        PendingMove? pending;
        lock (_pendingGate) pending = _pending;
        pending?.Completion.TrySetResult(result);
    }

    private ScriptMovementResult Blocked(
        string? fromRoomId,
        ScriptMovementFailureReason reason,
        string? message,
        string? recoveryCommand) => new(
            "blocked",
            FromRoomId: fromRoomId,
            Reason: reason,
            Message: message,
            RecoveryCommand: recoveryCommand);

    private string? RecoveryCommand(ScriptMovementFailureReason reason, string direction) => reason switch
    {
        ScriptMovementFailureReason.StandingRequired => "stand",
        ScriptMovementFailureReason.ClosedDoor when _settings().AutoOpenDoors => BuildDoorOpenCommand(direction),
        _ => null
    };

    private string? BuildDoorOpenCommand(string direction)
    {
        string template = string.IsNullOrWhiteSpace(_settings().DoorOpenCommandTemplate)
            ? "open {direction}"
            : _settings().DoorOpenCommandTemplate;
        string command = template.Replace("{direction}", direction, StringComparison.OrdinalIgnoreCase).Trim();
        return command.Length == 0 ? null : command;
    }

    internal static ScriptMovementFailureReason ClassifyFailure(string? reason)
    {
        string text = reason ?? string.Empty;
        if (text.Contains("locked", StringComparison.OrdinalIgnoreCase)) return ScriptMovementFailureReason.LockedDoor;
        if (text.Contains("closed", StringComparison.OrdinalIgnoreCase)) return ScriptMovementFailureReason.ClosedDoor;
        if (text.Contains("stand", StringComparison.OrdinalIgnoreCase) || text.Contains("standing", StringComparison.OrdinalIgnoreCase))
            return ScriptMovementFailureReason.StandingRequired;
        if (text.Contains("combat", StringComparison.OrdinalIgnoreCase) || text.Contains("fighting", StringComparison.OrdinalIgnoreCase))
            return ScriptMovementFailureReason.CombatRestriction;
        if (text.Contains("no exit", StringComparison.OrdinalIgnoreCase) || text.Contains("can't go", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot go", StringComparison.OrdinalIgnoreCase)) return ScriptMovementFailureReason.NoExit;
        if (text.Contains("can't", StringComparison.OrdinalIgnoreCase) || text.Contains("cannot", StringComparison.OrdinalIgnoreCase))
            return ScriptMovementFailureReason.CannotMove;
        return ScriptMovementFailureReason.Unknown;
    }
}

using System.Text.Json;
using System.Threading.Channels;
using JevMud.Client.Knowledge;
using JevMud.Client.Navigation;
using JevMud.Client.Settings;
using JevMud.Contracts.Actions;
using JevMud.Contracts.Events;
using JevMud.Contracts.Jev;
using JevMud.Contracts.Gameplay;
using JevMud.Contracts.State;
using JevMud.Core.Actions;
using JevMud.Core.Events;
using JevMud.Core.State;
using JevMud.Scripting.Diagnostics;
using JevMud.Scripting.Events;
using JevMud.Scripting.Host;
using JevMud.Scripting.Permissions;
using JevMud.Scripting.Runtime;
using JevMud.Scripting.Scheduling;
using JevMud.Scripting.Storage;
using Microsoft.Data.Sqlite;

namespace JevMud.Client.Scripting;

public sealed class ClientScriptEventBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly StateReducer _state;
    private readonly IScriptEventPublisher _publisher;
    private string? _lastRoomId;

    public ClientScriptEventBridge(
        ChannelReader<EventEnvelope> events,
        StateReducer state,
        IScriptEventPublisher publisher)
    {
        _events = events;
        _state = state;
        _publisher = publisher;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // GameObservation is the immutable semantic source, not a second user-facing
                // raw-text event. Raw trigger compatibility remains GameTextReceived.
                if (envelope.Payload is GameObservationReceived)
                    continue;

                // Script handlers are allowed to read current state while processing an event.
                // Preserve the architectural ordering: semantic event -> state reduction -> script notification.
                await _state.WaitUntilProcessedAsync(envelope.Sequence, cancellationToken).ConfigureAwait(false);

                ScriptEventEnvelope projected = new(
                    ProjectEventType(envelope.Payload),
                    envelope.Sequence,
                    envelope.Timestamp,
                    envelope.Source,
                    ProjectPayload(envelope.Payload, envelope.Timestamp),
                    envelope.CorrelationId,
                    envelope.CausationId,
                    envelope.EventId);
                await _publisher.PublishAsync(projected, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private string ProjectEventType(IMudEvent mudEvent) => mudEvent switch
    {
        RoomChanged room => ProjectRoomEventType(room.Id),
        RoomObservationObserved room => ProjectRoomEventType(room.RoomId),
        CharacterVitalsChanged => ScriptEventTypes.CharacterVitalsChanged,
        CharacterPromptObserved => ScriptEventTypes.PromptReceived,
        CharacterPromptSnapshotObserved => ScriptEventTypes.CharacterPromptUpdated,
        CharacterStatusObserved => ScriptEventTypes.CharacterStatusChanged,
        GroupSnapshotObserved => ScriptEventTypes.GroupSnapshotUpdated,
        ActiveEffectsSnapshotObserved => ScriptEventTypes.EffectStatusSnapshot,
        CombatTargetConditionObserved => ScriptEventTypes.CombatTargetConditionUpdated,
        MovementObserved movement => ProjectMovementEventType(movement.Movement),
        ScanUpdated => ScriptEventTypes.ScanUpdated,
        ItemIdentified => ScriptEventTypes.ItemIdentified,
        GameCommandQueueCleared => ScriptEventTypes.GameCommandQueueCleared,
        CombatStateChanged { Active: true } => ScriptEventTypes.CombatStarted,
        CombatStateChanged { Active: false } => ScriptEventTypes.CombatEnded,
        EnemyKilled => ScriptEventTypes.EnemyKilled,
        ItemAcquired => ScriptEventTypes.ItemAcquired,
        ConnectionStateChanged => ScriptEventTypes.ConnectionStateChanged,
        AutoMoveStateChanged => ScriptEventTypes.MapperRouteStatusChanged,
        MapperRouteLifecycleChanged route => ProjectMapperRouteEventType(route.Kind),
        ActionDispatching => ScriptEventTypes.CommandSent,
        GameTextReceived => ScriptEventTypes.RawTextReceived,
        ScriptRuntimeTaskFaulted => ScriptEventTypes.RuntimeTaskFaulted,
        _ => mudEvent.GetType().Name
    };

    private static string ProjectMovementEventType(MovementObservation movement) => movement.Result switch
    {
        MovementResult.Blocked or MovementResult.CombatRestricted => ScriptEventTypes.MovementBlocked,
        MovementResult.SucceededKnownRoom => ScriptEventTypes.MovementSucceeded,
        MovementResult.SucceededUnknownRoom => ScriptEventTypes.MovementUnknownDestination,
        MovementResult.Forced => ScriptEventTypes.MovementForced,
        MovementResult.Teleported => ScriptEventTypes.MovementTeleported,
        _ => ScriptEventTypes.MovementObserved
    };

    private static string ProjectMapperRouteEventType(MapperRouteLifecycleKind kind) => kind switch
    {
        MapperRouteLifecycleKind.Started => ScriptEventTypes.MapperRouteStarted,
        MapperRouteLifecycleKind.Planned => ScriptEventTypes.MapperRoutePlanned,
        MapperRouteLifecycleKind.StepStarted => ScriptEventTypes.MapperRouteStepStarted,
        MapperRouteLifecycleKind.StepCompleted => ScriptEventTypes.MapperRouteStepCompleted,
        MapperRouteLifecycleKind.Blocked => ScriptEventTypes.MapperRouteBlocked,
        MapperRouteLifecycleKind.Replanning => ScriptEventTypes.MapperRouteReplanning,
        MapperRouteLifecycleKind.Paused => ScriptEventTypes.MapperRoutePaused,
        MapperRouteLifecycleKind.Resumed => ScriptEventTypes.MapperRouteResumed,
        MapperRouteLifecycleKind.Completed => ScriptEventTypes.MapperRouteCompleted,
        MapperRouteLifecycleKind.Aborted => ScriptEventTypes.MapperRouteAborted,
        MapperRouteLifecycleKind.Failed => ScriptEventTypes.MapperRouteFailed,
        _ => ScriptEventTypes.MapperRouteStatusChanged
    };

    private string ProjectRoomEventType(string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return ScriptEventTypes.RoomUpdated;
        bool entered = !string.Equals(_lastRoomId, roomId, StringComparison.Ordinal);
        _lastRoomId = roomId;
        return entered ? ScriptEventTypes.RoomEntered : ScriptEventTypes.RoomUpdated;
    }

    private static JsonElement ProjectPayload(IMudEvent mudEvent, DateTimeOffset timestamp) => mudEvent switch
    {
        RoomChanged room => JsonSerializer.SerializeToElement(new
        {
            id = room.Id,
            name = room.Name,
            exits = room.Exits
        }, JsonOptions),
        RoomObservationObserved room => JsonSerializer.SerializeToElement(new
        {
            id = room.RoomId,
            name = room.RoomName,
            exits = room.ExitDetails.Select(exit => new
            {
                direction = exit.Direction,
                doorState = exit.DoorState.ToString(),
                traversability = exit.Traversability.ToString(),
                blockReason = exit.BlockReason
            }).ToArray(),
            contents = room.Contents.Select(content => new
            {
                description = content.Description,
                canonicalName = content.CanonicalName,
                kind = content.Kind.ToString()
            }).ToArray()
        }, JsonOptions),
        CharacterVitalsChanged vitals => JsonSerializer.SerializeToElement(new
        {
            health = Resource(vitals.HitPoints, vitals.MaxHitPoints),
            mana = Resource(vitals.Mana, vitals.MaxMana),
            movement = Resource(vitals.Movement, vitals.MaxMovement),
            timestamp = timestamp.ToUnixTimeMilliseconds()
        }, JsonOptions),
        CharacterPromptObserved prompt => JsonSerializer.SerializeToElement(new
        {
            health = prompt.HitPoints,
            healthMaximum = prompt.MaxHitPoints,
            mana = prompt.Mana,
            manaMaximum = prompt.MaxMana,
            movement = prompt.Movement,
            movementMaximum = prompt.MaxMovement,
            experience = prompt.Experience,
            experienceToLevel = prompt.ExperienceToLevel,
            position = prompt.Position,
            roomName = prompt.RoomName,
            exits = prompt.Exits.Directions,
            terrain = prompt.Terrain,
            light = prompt.Light
        }, JsonOptions),
        CharacterPromptSnapshotObserved prompt => JsonSerializer.SerializeToElement(prompt.Snapshot, JsonOptions),
        CharacterStatusObserved status => JsonSerializer.SerializeToElement(new
        {
            status = status.Status,
            active = status.Active,
            sourceSequence = status.SourceSequence
        }, JsonOptions),
        GroupSnapshotObserved group => JsonSerializer.SerializeToElement(group.Snapshot, JsonOptions),
        ActiveEffectsSnapshotObserved effects => JsonSerializer.SerializeToElement(effects.Snapshot, JsonOptions),
        CombatTargetConditionObserved condition => JsonSerializer.SerializeToElement(new
        {
            target = condition.TargetName,
            descriptor = condition.Condition,
            range = condition.Range,
            sourceSequence = condition.SourceSequence
        }, JsonOptions),
        MovementObserved movement => JsonSerializer.SerializeToElement(movement.Movement, JsonOptions),
        ScanUpdated scan => JsonSerializer.SerializeToElement(scan.Scan, JsonOptions),
        ItemIdentified item => JsonSerializer.SerializeToElement(item.Item, JsonOptions),
        GameCommandQueueCleared cleared => JsonSerializer.SerializeToElement(new
        {
            sourceSequence = cleared.SourceSequence
        }, JsonOptions),
        CombatStateChanged combat => JsonSerializer.SerializeToElement(new
        {
            active = combat.Active,
            targetId = combat.TargetId
        }, JsonOptions),
        EnemyKilled killed => JsonSerializer.SerializeToElement(new { target = killed.TargetName }, JsonOptions),
        ItemAcquired item => JsonSerializer.SerializeToElement(new
        {
            item = item.ItemName,
            source = item.SourceDescription,
            sourceKind = item.SourceKind.ToString()
        }, JsonOptions),
        ConnectionStateChanged connection => JsonSerializer.SerializeToElement(new
        {
            status = connection.Status.ToString(),
            host = connection.Host,
            port = connection.Port,
            reason = connection.Reason
        }, JsonOptions),
        AutoMoveStateChanged route => JsonSerializer.SerializeToElement(new
        {
            status = route.Status.ToString(),
            destinationRoomId = route.DestinationRoomId,
            destinationLabel = route.DestinationLabel,
            completedSteps = route.CompletedSteps,
            totalSteps = route.TotalSteps,
            direction = route.CurrentDirection,
            reason = route.Reason
        }, JsonOptions),
        MapperRouteLifecycleChanged route => JsonSerializer.SerializeToElement(new
        {
            kind = route.Kind.ToString(),
            routeExecutionId = route.RouteExecutionId,
            destinationRoomId = route.DestinationRoomId,
            destinationLabel = route.DestinationLabel,
            routeId = route.RouteId,
            completedSteps = route.CompletedSteps,
            totalSteps = route.TotalSteps,
            routeStep = route.RouteStep,
            direction = route.Direction,
            reason = route.Reason,
            failureReason = route.FailureReason?.ToString()
        }, JsonOptions),
        ActionDispatching action => JsonSerializer.SerializeToElement(new
        {
            actionId = action.ActionId,
            command = action.Command,
            sensitive = action.Sensitive,
            source = action.Source.ToString(),
            provenance = action.Provenance is null ? null : new
            {
                origin = action.Provenance.Origin.ToString(),
                ownerId = action.Provenance.OwnerId,
                ownerName = action.Provenance.OwnerName,
                reason = action.Provenance.Reason,
                moduleId = action.Provenance.ModuleId,
                scriptVersion = action.Provenance.ScriptVersion,
                scriptInstanceId = action.Provenance.ScriptInstanceId,
                invocationId = action.Provenance.InvocationId,
                eventId = action.Provenance.EventId,
                parentOperationId = action.Provenance.ParentOperationId,
                automationId = action.Provenance.AutomationId,
                automationType = action.Provenance.AutomationType,
                triggerId = action.Provenance.TriggerId,
                routeExecutionId = action.Provenance.RouteExecutionId,
                routeId = action.Provenance.RouteId,
                routeStep = action.Provenance.RouteStep,
                routeTotalSteps = action.Provenance.RouteTotalSteps
            }
        }, JsonOptions),
        GameTextReceived text => JsonSerializer.SerializeToElement(new { text = text.Text }, JsonOptions),
        ScriptRuntimeTaskFaulted fault => JsonSerializer.SerializeToElement(new
        {
            ownerId = fault.OwnerId,
            ownerKind = fault.OwnerKind,
            ownerName = fault.OwnerName,
            operation = fault.Operation,
            errorType = fault.ErrorType,
            message = fault.Message
        }, JsonOptions),
        _ => JsonSerializer.SerializeToElement(new { }, JsonOptions)
    };

    private static object Resource(int? current, int? maximum) => new
    {
        current,
        maximum,
        percent = current is { } value && maximum is > 0
            ? Math.Clamp(value * 100.0 / maximum.Value, 0.0, 100.0)
            : (double?)null
    };
}

public sealed class ClientScriptStateHost : IScriptState
{
    private readonly StateReducer _state;

    public ClientScriptStateHost(StateReducer state) => _state = state;

    public ScriptStateSnapshot Snapshot()
    {
        StateSnapshot state = _state.Current;
        return new ScriptStateSnapshot(
            state.Version,
            state.Session.ConnectionStatus == ConnectionStatus.Connected,
            state.Session.InputMode.ToString(),
            new ScriptCharacterState(
                ToResource(state.Character.HitPoints.Current, state.Character.HitPoints.Maximum),
                ToResource(state.Character.Mana.Current, state.Character.Mana.Maximum),
                ToResource(state.Character.Movement.Current, state.Character.Movement.Maximum),
                state.Character.Position),
            new ScriptRoomState(
                state.Room.Id,
                state.Room.Name,
                state.Room.Exits.Directions.ToArray()),
            new ScriptCombatState(state.Combat.Active, state.Combat.TargetName ?? state.Combat.TargetId));
    }

    private static ScriptResourceState ToResource(int? current, int? maximum) => new(
        current,
        maximum,
        current is { } value && maximum is > 0
            ? Math.Clamp(value * 100.0 / maximum.Value, 0.0, 100.0)
            : null);
}

public sealed class ClientScriptCommands : IScriptCommands
{
    private readonly ActionProcessor _actions;
    private readonly StateReducer _state;
    private readonly IScriptScheduler _scheduler;
    private readonly Func<AutomationPreferences> _automationSettings;
    private readonly MapperNavigationAuthority? _navigationAuthority;
    private readonly SemaphoreSlim _automationRateGate = new(1, 1);
    private readonly Queue<DateTimeOffset> _recentAutomationCommands = new();
    private long _humanOverrideUntilUnixMilliseconds;

    public ClientScriptCommands(
        ActionProcessor actions,
        StateReducer state,
        IScriptScheduler? scheduler = null,
        Func<AutomationPreferences>? automationSettings = null,
        MapperNavigationAuthority? navigationAuthority = null)
    {
        _actions = actions;
        _state = state;
        _scheduler = scheduler ?? new ScriptScheduler();
        _automationSettings = automationSettings ?? (() => new AutomationPreferences());
        _navigationAuthority = navigationAuthority;
    }

    public async Task<ScriptCommandResult> SendAsync(
        ScriptCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Command);

        if (NavigationCommandClassifier.IsMovementCommand(request.Command))
        {
            if (request.Origin == ScriptCommandOrigin.Mapper)
            {
                if (_navigationAuthority is null || !_navigationAuthority.IsOwnedBy(request.RouteExecutionId))
                    return new ScriptCommandResult(request.ActionId ?? Guid.NewGuid(), false, "Mapper route does not own navigation authority.");
            }
            else if (_navigationAuthority?.IsHeld == true && request.Origin is not (ScriptCommandOrigin.User or ScriptCommandOrigin.Keybinding))
            {
                return new ScriptCommandResult(request.ActionId ?? Guid.NewGuid(), false, "Mapper route currently owns autonomous navigation.");
            }
        }

        if (request.Origin is ScriptCommandOrigin.User or ScriptCommandOrigin.Keybinding)
        {
            if (_navigationAuthority?.IsHeld == true && NavigationCommandClassifier.IsMovementCommand(request.Command))
                await _navigationAuthority.RequestManualMovementAsync(cancellationToken).ConfigureAwait(false);

            int milliseconds = Math.Clamp(_automationSettings().HumanOverrideMilliseconds, 0, 30_000);
            long until = _scheduler.UtcNow.AddMilliseconds(milliseconds).ToUnixTimeMilliseconds();
            Interlocked.Exchange(ref _humanOverrideUntilUnixMilliseconds, until);
        }
        else if (request.Origin == ScriptCommandOrigin.Automation)
        {
            await WaitForHumanOverrideAsync(cancellationToken).ConfigureAwait(false);
            await WaitForAutomationCommandSlotAsync(cancellationToken).ConfigureAwait(false);
        }

        long stateVersion = request.ExpectedStateVersion ?? _state.Current.Version;
        Guid actionId = request.ActionId ?? Guid.NewGuid();
        JevDomain? domain = null;
        DecisionSource source = request.Origin is ScriptCommandOrigin.User or ScriptCommandOrigin.Keybinding
            ? DecisionSource.Human
            : DecisionSource.Rules;
        if (request.Origin == ScriptCommandOrigin.Jev)
        {
            if (!Enum.TryParse(request.Domain, true, out JevDomain parsedDomain))
                return new ScriptCommandResult(actionId, false, "Jev commands require a valid domain.");
            domain = parsedDomain;
            source = DecisionSource.Jev;
        }

        MudActionEnvelope envelope = new(
            actionId,
            request.DecisionId,
            stateVersion,
            domain,
            source,
            request.UserApproved,
            _scheduler.UtcNow,
            new SendCommandAction(request.Command, request.Sensitive),
            new CommandProvenance(
                MapOrigin(request),
                request.OwnerId,
                request.OwnerName,
                request.Reason,
                request.ModuleId,
                request.ScriptVersion,
                request.ScriptInstanceId,
                request.InvocationId,
                request.EventId,
                request.ParentOperationId,
                request.AutomationId,
                request.AutomationType,
                request.TriggerId,
                request.RouteExecutionId,
                request.RouteId,
                request.RouteStep,
                request.RouteTotalSteps));
        await _actions.QueueAsync(envelope, cancellationToken).ConfigureAwait(false);
        return new ScriptCommandResult(actionId, true);
    }

    private async Task WaitForHumanOverrideAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            long remainingMilliseconds = Interlocked.Read(ref _humanOverrideUntilUnixMilliseconds) -
                                         _scheduler.UtcNow.ToUnixTimeMilliseconds();
            if (remainingMilliseconds <= 0) return;
            await _scheduler.DelayAsync(
                TimeSpan.FromMilliseconds(Math.Min(100, remainingMilliseconds)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForAutomationCommandSlotAsync(CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(_automationSettings().MaxCommandsPerSecond, 1, 50);
        while (true)
        {
            TimeSpan? wait = null;
            await _automationRateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTimeOffset now = _scheduler.UtcNow;
                while (_recentAutomationCommands.Count > 0 &&
                       now - _recentAutomationCommands.Peek() >= TimeSpan.FromSeconds(1))
                    _recentAutomationCommands.Dequeue();

                if (_recentAutomationCommands.Count < limit)
                {
                    _recentAutomationCommands.Enqueue(now);
                    return;
                }

                wait = TimeSpan.FromSeconds(1) - (now - _recentAutomationCommands.Peek());
            }
            finally
            {
                _automationRateGate.Release();
            }

            if (wait is { } delay && delay > TimeSpan.Zero)
                await _scheduler.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CommandOrigin MapOrigin(ScriptCommandRequest request)
    {
        if (request.Origin == ScriptCommandOrigin.Automation &&
            string.Equals(request.AutomationType, "Alias", StringComparison.OrdinalIgnoreCase))
            return CommandOrigin.Alias;

        return request.Origin switch
        {
            ScriptCommandOrigin.User => CommandOrigin.User,
            ScriptCommandOrigin.Alias => CommandOrigin.Alias,
            ScriptCommandOrigin.Keybinding => CommandOrigin.Keybinding,
            ScriptCommandOrigin.Automation => CommandOrigin.Automation,
            ScriptCommandOrigin.Jev => CommandOrigin.Jev,
            ScriptCommandOrigin.Mapper => CommandOrigin.Mapper,
            ScriptCommandOrigin.Script => CommandOrigin.Script,
            _ => CommandOrigin.System
        };
    }
}

public sealed class ClientScriptMapperHost : IScriptMapper
{
    private readonly MapperQueryService _queries;
    private readonly MapperMovementCoordinator _movement;

    public ClientScriptMapperHost(MapperQueryService queries, MapperMovementCoordinator movement)
    {
        _queries = queries;
        _movement = movement;
    }

    public Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default) =>
        _queries.CurrentRoomAsync(cancellationToken);

    public Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default) =>
        _queries.FindPathAsync(destinationRoomId, cancellationToken);

    public Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default) =>
        _movement.MoveAsync(request, cancellationToken);
}

public sealed class ClientScriptCodexHost : IScriptCodex
{
    private readonly WorldKnowledgeStore _knowledge;

    public ClientScriptCodexHost(WorldKnowledgeStore knowledge) => _knowledge = knowledge;

    public async Task<IReadOnlyList<ScriptCodexResult>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        IReadOnlyList<CodexEntrySummary> entries = await _knowledge.SearchCodexAsync(
            null,
            query.Trim(),
            Math.Clamp(limit, 1, 200),
            cancellationToken).ConfigureAwait(false);
        return entries.Select(entry => new ScriptCodexResult(
            entry.Kind.ToString(),
            entry.Key,
            entry.Title,
            entry.Subtitle)).ToArray();
    }
}

public sealed class SqliteScriptStorage : IScriptStorage
{
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _utcNow;

    public SqliteScriptStorage(
        ScriptModuleId moduleId,
        string? databasePath = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ModuleId = moduleId;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        DatabasePath = databasePath ?? GetDefaultPath();
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    public ScriptModuleId ModuleId { get; }
    public string DatabasePath { get; }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT json_value FROM script_storage WHERE module_id = $module AND storage_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$module", ModuleId.Value);
        command.Parameters.AddWithValue("$key", key.Trim());
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task SetAsync(string key, string jsonValue, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(jsonValue);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO script_storage(module_id, storage_key, json_value, updated_at)
            VALUES($module, $key, $value, $updatedAt)
            ON CONFLICT(module_id, storage_key) DO UPDATE SET
                json_value = excluded.json_value,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$module", ModuleId.Value);
        command.Parameters.AddWithValue("$key", key.Trim());
        command.Parameters.AddWithValue("$value", jsonValue);
        command.Parameters.AddWithValue("$updatedAt", _utcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM script_storage WHERE module_id = $module AND storage_key = $key;";
        command.Parameters.AddWithValue("$module", ModuleId.Value);
        command.Parameters.AddWithValue("$key", key.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<ScriptStorageEntry>> ListAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = string.IsNullOrEmpty(prefix)
            ? "SELECT storage_key, json_value, updated_at FROM script_storage WHERE module_id = $module ORDER BY storage_key;"
            : "SELECT storage_key, json_value, updated_at FROM script_storage WHERE module_id = $module AND storage_key LIKE $prefix ESCAPE '\\' ORDER BY storage_key;";
        command.Parameters.AddWithValue("$module", ModuleId.Value);
        if (!string.IsNullOrEmpty(prefix)) command.Parameters.AddWithValue("$prefix", EscapeLike(prefix) + "%");
        List<ScriptStorageEntry> results = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset.TryParse(reader.GetString(2), out DateTimeOffset updatedAt);
            results.Add(new ScriptStorageEntry(reader.GetString(0), reader.GetString(1), updatedAt));
        }
        return results;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS script_storage (
                module_id TEXT NOT NULL,
                storage_key TEXT NOT NULL,
                json_value TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(module_id, storage_key)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 256) throw new ArgumentOutOfRangeException(nameof(key), "Script storage keys are limited to 256 characters.");
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public static string GetDefaultPath() =>
        JevMud.Client.Paths.NexMudDataPaths.GetFilePath("script-state.db", includeSqliteSidecars: true);
}


public sealed record ScriptRuntimeDiagnosticEmitted(
    string ScriptId,
    string ScriptVersion,
    Guid ScriptInstanceId,
    string Kind,
    string? Message,
    Guid? InvocationId,
    Guid? EventId,
    Guid? OperationId,
    Guid? ParentOperationId,
    string? SourceFile,
    int? Line,
    int? Column) : IMudEvent;

public sealed class ClientScriptDiagnosticsSink : IScriptDiagnosticsSink
{
    private readonly IEventSink _events;

    public ClientScriptDiagnosticsSink(IEventSink events) =>
        _events = events ?? throw new ArgumentNullException(nameof(events));

    public void Record(ScriptDiagnosticRecord diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _ = PublishAsync(diagnostic);
    }

    private async Task PublishAsync(ScriptDiagnosticRecord diagnostic)
    {
        try
        {
            await _events.PublishAsync(
                new ScriptRuntimeDiagnosticEmitted(
                    diagnostic.ScriptId.Value,
                    diagnostic.ScriptVersion,
                    diagnostic.ScriptInstanceId,
                    diagnostic.Kind.ToString(),
                    diagnostic.Message,
                    diagnostic.InvocationId,
                    diagnostic.EventId,
                    diagnostic.OperationId,
                    diagnostic.ParentOperationId,
                    diagnostic.Location?.SourceFile,
                    diagnostic.Location?.Line,
                    diagnostic.Location?.Column),
                "scripting.runtime").ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics are observational and must never disturb runtime execution.
        }
    }
}

public sealed record ScriptUiNotificationRequested(
    string ModuleId,
    string Title,
    string Message,
    string Level) : IMudEvent;

public sealed class ClientScriptUiHost : IScriptUi
{
    private readonly ScriptModuleId _moduleId;
    private readonly IEventSink _events;

    public ClientScriptUiHost(ScriptModuleId moduleId, IEventSink events)
    {
        _moduleId = moduleId;
        _events = events;
    }

    public Task NotifyAsync(
        ScriptUiNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return _events.PublishAsync(
            new ScriptUiNotificationRequested(
                _moduleId.Value,
                notification.Title,
                notification.Message,
                notification.Level.ToString()),
            "scripting",
            cancellationToken).AsTask();
    }
}

public sealed record ScriptRuntimeTaskFaulted(
    Guid OwnerId,
    string OwnerKind,
    string OwnerName,
    string Operation,
    string ErrorType,
    string Message) : IMudEvent;

public sealed record ScriptLogEmitted(string ModuleId, string Level, string Message, string? DataJson = null) : IMudEvent;

public sealed class ClientScriptLogHost : IScriptLog
{
    private readonly ScriptModuleId _moduleId;
    private readonly IEventSink _events;

    public ClientScriptLogHost(ScriptModuleId moduleId, IEventSink events)
    {
        _moduleId = moduleId;
        _events = events;
    }

    public Task WriteAsync(ScriptLogLevel level, string message, CancellationToken cancellationToken = default) =>
        WriteAsync(level, message, null, cancellationToken);

    public Task WriteAsync(
        ScriptLogLevel level,
        string message,
        string? dataJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _events.PublishAsync(
            new ScriptLogEmitted(_moduleId.Value, level.ToString(), message, dataJson),
            "scripting",
            cancellationToken).AsTask();
    }
}

public sealed class CapabilityScriptHost : IScriptHost
{
    private sealed class CommandsGate : IScriptCommands
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptCommands _inner;
        private readonly ScriptModuleId _moduleId;
        private readonly ScriptCommandOrigin _origin;
        private readonly string _ownerName;

        public CommandsGate(
            IScriptPermissionSet permissions,
            IScriptCommands inner,
            ScriptModuleId moduleId,
            ScriptCommandOrigin origin,
            string ownerName)
        {
            _permissions = permissions;
            _inner = inner;
            _moduleId = moduleId;
            _origin = origin;
            _ownerName = ownerName;
        }

        public Task<ScriptCommandResult> SendAsync(ScriptCommandRequest request, CancellationToken cancellationToken = default)
        {
            _permissions.Demand(ScriptCapability.SendCommands);
            ArgumentNullException.ThrowIfNull(request);
            ScriptCommandRequest bound = request with
            {
                Origin = _origin,
                OwnerId = _moduleId.Value,
                OwnerName = _ownerName,
                ModuleId = _moduleId.Value,
                AutomationId = _origin == ScriptCommandOrigin.Automation ? request.AutomationId : null,
                AutomationType = _origin == ScriptCommandOrigin.Automation ? request.AutomationType : null,
                TriggerId = _origin == ScriptCommandOrigin.Automation ? request.TriggerId : null
            };
            return _inner.SendAsync(bound, cancellationToken);
        }
    }

    private sealed class StateGate : IScriptState
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptState _inner;
        public StateGate(IScriptPermissionSet permissions, IScriptState inner) { _permissions = permissions; _inner = inner; }
        public ScriptStateSnapshot Snapshot() { _permissions.Demand(ScriptCapability.ReadState); return _inner.Snapshot(); }
    }

    private sealed class MapperGate : IScriptMapper
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptMapper _inner;
        public MapperGate(IScriptPermissionSet permissions, IScriptMapper inner) { _permissions = permissions; _inner = inner; }
        public Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.ReadMapper); return _inner.CurrentRoomAsync(cancellationToken); }
        public Task<ScriptRoutePlan?> FindPathAsync(string destinationRoomId, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.ReadMapper | ScriptCapability.MapperPathfind); return _inner.FindPathAsync(destinationRoomId, cancellationToken); }
        public Task<ScriptMovementResult> MoveAsync(ScriptMoveRequest request, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.MapperMove); return _inner.MoveAsync(request, cancellationToken); }
    }

    private sealed class CodexGate : IScriptCodex
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptCodex _inner;
        public CodexGate(IScriptPermissionSet permissions, IScriptCodex inner) { _permissions = permissions; _inner = inner; }
        public Task<IReadOnlyList<ScriptCodexResult>> SearchAsync(string query, int limit = 50, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.ReadCodex); return _inner.SearchAsync(query, limit, cancellationToken); }
    }

    private sealed class StorageGate : IScriptStorage
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptStorage _inner;
        public StorageGate(IScriptPermissionSet permissions, IScriptStorage inner) { _permissions = permissions; _inner = inner; }
        public ScriptModuleId ModuleId => _inner.ModuleId;
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        { DemandRead(); return _inner.GetAsync(key, cancellationToken); }
        public Task SetAsync(string key, string jsonValue, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.WriteScriptStorage); return _inner.SetAsync(key, jsonValue, cancellationToken); }
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.WriteScriptStorage); return _inner.DeleteAsync(key, cancellationToken); }
        public Task<IReadOnlyList<ScriptStorageEntry>> ListAsync(string? prefix = null, CancellationToken cancellationToken = default)
        { DemandRead(); return _inner.ListAsync(prefix, cancellationToken); }
        private void DemandRead()
        {
            // Preserve compatibility for manifests which predate the dedicated read capability.
            if (_permissions.Allows(ScriptCapability.ReadScriptStorage) || _permissions.Allows(ScriptCapability.WriteScriptStorage)) return;
            _permissions.Demand(ScriptCapability.ReadScriptStorage);
        }
    }

    private sealed class SchedulerGate : IScriptScheduler
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptScheduler _inner;
        public SchedulerGate(IScriptPermissionSet permissions, IScriptScheduler inner) { _permissions = permissions; _inner = inner; }
        public DateTimeOffset UtcNow => _inner.UtcNow;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.CreateTimers); return _inner.DelayAsync(delay, cancellationToken); }
        public IScriptScheduledTask After(JevMud.Scripting.Execution.IScriptExecutionScope owner, TimeSpan delay, string name, Func<CancellationToken, Task> callback)
        { _permissions.Demand(ScriptCapability.CreateTimers); return _inner.After(owner, delay, name, callback); }
        public IScriptScheduledTask Every(JevMud.Scripting.Execution.IScriptExecutionScope owner, TimeSpan interval, string name, Func<CancellationToken, Task> callback)
        { _permissions.Demand(ScriptCapability.CreateTimers); return _inner.Every(owner, interval, name, callback); }
    }

    private sealed class UiGate : IScriptUi
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptUi _inner;
        public UiGate(IScriptPermissionSet permissions, IScriptUi inner) { _permissions = permissions; _inner = inner; }
        public Task NotifyAsync(ScriptUiNotification notification, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.EmitUiNotifications); return _inner.NotifyAsync(notification, cancellationToken); }
    }

    private sealed class LogGate : IScriptLog
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptLog _inner;
        public LogGate(IScriptPermissionSet permissions, IScriptLog inner) { _permissions = permissions; _inner = inner; }
        public Task WriteAsync(ScriptLogLevel level, string message, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.Log); return _inner.WriteAsync(level, message, cancellationToken); }
        public Task WriteAsync(ScriptLogLevel level, string message, string? dataJson, CancellationToken cancellationToken = default)
        { _permissions.Demand(ScriptCapability.Log); return _inner.WriteAsync(level, message, dataJson, cancellationToken); }
    }

    private sealed class EventGate : IScriptEvents
    {
        private readonly IScriptPermissionSet _permissions;
        private readonly IScriptEvents _inner;
        public EventGate(IScriptPermissionSet permissions, IScriptEvents inner) { _permissions = permissions; _inner = inner; }
        public IScriptEventSubscription Subscribe(JevMud.Scripting.Execution.IScriptExecutionScope owner, ScriptEventFilter filter, Func<ScriptEventEnvelope, CancellationToken, Task> handler, int capacity = 256)
        {
            _permissions.Demand(ScriptCapability.SubscribeEvents);
            if (filter.EventTypes?.Any(type => type.StartsWith("mapper.route", StringComparison.OrdinalIgnoreCase)) == true)
                _permissions.Demand(ScriptCapability.MapperRouteObserve);
            return _inner.Subscribe(owner, filter, handler, capacity);
        }
    }

    public CapabilityScriptHost(
        ScriptModuleId moduleId,
        IScriptPermissionSet permissions,
        IScriptEvents events,
        IScriptCommands commands,
        IScriptState state,
        IScriptMapper mapper,
        IScriptCodex codex,
        IScriptStorage storage,
        IScriptScheduler timers,
        IScriptUi ui,
        IScriptLog log,
        ScriptCommandOrigin commandOrigin = ScriptCommandOrigin.Script,
        string? ownerName = null)
    {
        ModuleId = moduleId;
        Permissions = permissions;
        Events = new EventGate(permissions, events);
        Commands = new CommandsGate(
            permissions,
            commands,
            moduleId,
            commandOrigin,
            string.IsNullOrWhiteSpace(ownerName) ? moduleId.Value : ownerName.Trim());
        State = new StateGate(permissions, state);
        Mapper = new MapperGate(permissions, mapper);
        Codex = new CodexGate(permissions, codex);
        Storage = new StorageGate(permissions, storage);
        Timers = new SchedulerGate(permissions, timers);
        Ui = new UiGate(permissions, ui);
        Log = new LogGate(permissions, log);
    }

    public ScriptModuleId ModuleId { get; }
    public IScriptPermissionSet Permissions { get; }
    public IScriptEvents Events { get; }
    public IScriptCommands Commands { get; }
    public IScriptState State { get; }
    public IScriptMapper Mapper { get; }
    public IScriptCodex Codex { get; }
    public IScriptStorage Storage { get; }
    public IScriptScheduler Timers { get; }
    public IScriptUi Ui { get; }
    public IScriptLog Log { get; }
}

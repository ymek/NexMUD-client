using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using JevMud.Scripting.Execution;

namespace JevMud.Scripting.Events;

public static class ScriptEventTypes
{
    public const string RoomEntered = "room.entered";
    public const string RoomUpdated = "room.updated";
    public const string CharacterVitalsChanged = "character.vitalsChanged";
    public const string PromptReceived = "character.promptReceived";
    public const string CharacterPromptUpdated = "character.promptUpdated";
    public const string CharacterStatusChanged = "character.statusChanged";
    public const string GroupSnapshotUpdated = "group.snapshotUpdated";
    public const string EffectStatusSnapshot = "effect.statusSnapshot";
    public const string MobObserved = "mob.observed";
    public const string MobConsidered = "mob.considered";
    public const string ItemObserved = "item.observed";
    public const string QuestObserved = "quest.observed";
    public const string QuestCompleted = "quest.completed";
    public const string CombatStarted = "combat.started";
    public const string CombatEnded = "combat.ended";
    public const string EnemyKilled = "combat.enemyKilled";
    public const string CombatTargetConditionUpdated = "combat.targetConditionUpdated";
    public const string MovementObserved = "movement.observed";
    public const string MovementSucceeded = "movement.succeeded";
    public const string MovementBlocked = "movement.blocked";
    public const string MovementUnknownDestination = "movement.unknownDestination";
    public const string MovementForced = "movement.forced";
    public const string MovementTeleported = "movement.teleported";
    public const string ScanUpdated = "scan.updated";
    public const string ItemIdentified = "item.identified";
    public const string GameCommandQueueCleared = "game.commandQueueCleared";
    public const string ItemAcquired = "item.acquired";
    public const string ConnectionStateChanged = "connection.stateChanged";
    public const string MapperRouteStatusChanged = "mapper.routeStatusChanged";
    public const string MapperRouteStarted = "mapper.routeStarted";
    public const string MapperRoutePlanned = "mapper.routePlanned";
    public const string MapperRouteStepStarted = "mapper.routeStepStarted";
    public const string MapperRouteStepCompleted = "mapper.routeStepCompleted";
    public const string MapperRouteBlocked = "mapper.routeBlocked";
    public const string MapperRouteReplanning = "mapper.routeReplanning";
    public const string MapperRoutePaused = "mapper.routePaused";
    public const string MapperRouteResumed = "mapper.routeResumed";
    public const string MapperRouteCompleted = "mapper.routeCompleted";
    public const string MapperRouteAborted = "mapper.routeAborted";
    public const string MapperRouteFailed = "mapper.routeFailed";
    public const string CommandSent = "command.sent";
    public const string RawTextReceived = "text.received";
    public const string RuntimeTaskFaulted = "runtime.taskFaulted";
    public const string AutomationAliasMatched = "automation.aliasMatched";
    public const string AutomationTextTriggerMatched = "automation.textTriggerMatched";
    public const string AutomationStateRuleMatched = "automation.stateRuleMatched";
}

public sealed record ScriptEventEnvelope(
    string Type,
    long Sequence,
    DateTimeOffset Timestamp,
    string Source,
    JsonElement Payload,
    Guid? CorrelationId = null,
    Guid? CausationId = null,
    Guid? EventId = null);

public sealed record ScriptEventFilter(IReadOnlySet<string>? EventTypes = null)
{
    public bool Matches(ScriptEventEnvelope envelope) =>
        EventTypes is null || EventTypes.Count == 0 || EventTypes.Contains(envelope.Type);

    public static ScriptEventFilter Any { get; } = new();
    public static ScriptEventFilter For(params string[] eventTypes) =>
        new(new HashSet<string>(eventTypes, StringComparer.OrdinalIgnoreCase));
}

public interface IScriptEventSubscription : IAsyncDisposable
{
    Guid Id { get; }
}

public interface IScriptEvents
{
    IScriptEventSubscription Subscribe(
        IScriptExecutionScope owner,
        ScriptEventFilter filter,
        Func<ScriptEventEnvelope, CancellationToken, Task> handler,
        int capacity = 256);
}

public interface IScriptEventPublisher
{
    ValueTask PublishAsync(ScriptEventEnvelope envelope, CancellationToken cancellationToken = default);
}

public sealed class ScriptEventHub : IScriptEvents, IScriptEventPublisher, IAsyncDisposable
{
    private sealed class Subscription : IScriptEventSubscription
    {
        private readonly ScriptEventHub _hub;
        private readonly Channel<ScriptEventEnvelope> _channel;
        private int _disposed;

        public Subscription(
            ScriptEventHub hub,
            ScriptEventFilter filter,
            int capacity)
        {
            _hub = hub;
            Filter = filter;
            Id = Guid.NewGuid();
            _channel = Channel.CreateBounded<ScriptEventEnvelope>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        }

        public void Start(
            IScriptExecutionScope scope,
            Func<ScriptEventEnvelope, CancellationToken, Task> handler)
        {
            _ = scope.RunAsync($"event-subscription:{Id:N}", async cancellationToken =>
            {
                try
                {
                    await foreach (ScriptEventEnvelope envelope in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        await handler(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                finally
                {
                    Close();
                }
            });
        }

        public Guid Id { get; }
        public ScriptEventFilter Filter { get; }

        public async ValueTask PublishAsync(
            ScriptEventEnvelope envelope,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                await _channel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Owner teardown closes the channel concurrently with publication.
            }
        }

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _channel.Writer.TryComplete();
            _hub.Remove(Id);
        }
    }

    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private int _disposed;

    public IScriptEventSubscription Subscribe(
        IScriptExecutionScope owner,
        ScriptEventFilter filter,
        Func<ScriptEventEnvelope, CancellationToken, Task> handler,
        int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Subscription subscription = new(this, filter, capacity);
        if (!_subscriptions.TryAdd(subscription.Id, subscription))
            throw new InvalidOperationException("Unable to register script event subscription.");
        try
        {
            subscription.Start(owner, handler);
            return subscription;
        }
        catch
        {
            _subscriptions.TryRemove(subscription.Id, out _);
            subscription.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public async ValueTask PublishAsync(ScriptEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Subscription[] subscriptions = _subscriptions.Values
            .Where(subscription => subscription.Filter.Matches(envelope))
            .ToArray();
        if (subscriptions.Length == 0) return;

        foreach (Subscription subscription in subscriptions)
            await subscription.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private void Remove(Guid id) => _subscriptions.TryRemove(id, out _);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Subscription[] subscriptions = _subscriptions.Values.ToArray();
        _subscriptions.Clear();
        foreach (Subscription subscription in subscriptions)
            await subscription.DisposeAsync().ConfigureAwait(false);
    }
}

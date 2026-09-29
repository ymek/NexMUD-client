using System.Collections.Concurrent;
using System.Threading.Channels;
using JevMud.Adapters.Avendar;
using JevMud.Client.Knowledge;
using JevMud.Contracts.Events;
using JevMud.Contracts.Jev;
using JevMud.Contracts.State;
using JevMud.Core.Events;
using JevMud.Core.Jev;
using JevMud.Core.State;
using JevMud.Scripting.Execution;
using JevMud.Scripting.Host;
using JevMud.Scripting.Runtime;
using JevMud.Scripting.Scheduling;
using JevMud.Jev;

namespace JevMud.Client.Runtime;

/// <summary>
/// Supervises Jev's typed decisions. Jev chooses within a bounded domain; deterministic policy
/// owns sequencing, stale-state protection, action outcome gating, recovery posture, and route
/// loop prevention. Only one autonomous action may be in flight at a time.
/// </summary>
public sealed class JevDecisionCoordinator
{
    private sealed record PendingApproval(JevDecisionTrace Decision);
    private sealed record EvaluationSignal(JevDomain Domain, long EventSequence);
    private sealed record PendingAutonomyAction(
        Guid ActionId,
        Guid DecisionId,
        JevDomain Domain,
        string SemanticAction,
        string Command,
        DateTimeOffset QueuedAt);

    private static readonly JevDomain[] AutonomyPriority =
    [
        JevDomain.Combat,
        JevDomain.Recovery,
        JevDomain.Navigation
    ];

    private readonly ChannelReader<EventEnvelope> _events;
    private readonly StateReducer _state;
    private readonly JevAuthorityService _authority;
    private readonly IScriptCommands _commands;
    private readonly IScriptScheduler _scheduler;
    private readonly IScriptExecutionSupervisor _execution;
    private IScriptExecutionScope _jevScope;
    private readonly IEventSink _sink;
    private readonly Func<IJevDecisionEngine?> _engine;
    private readonly Func<StateSnapshot, CancellationToken, Task<object?>> _contextProvider;
    private readonly ConcurrentDictionary<Guid, PendingApproval> _approvals = new();
    private readonly Channel<EvaluationSignal> _evaluationSignals = Channel.CreateUnbounded<EvaluationSignal>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly object _autonomyGate = new();
    private readonly Dictionary<string, DateTimeOffset> _actionCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<JevDomain, DateTimeOffset> _domainCooldowns = new();
    private readonly List<string> _recentRoomIds = [];
    private PendingAutonomyAction? _pendingAction;
    private string? _lastObservedRoomId;
    private string? _lastMoveOriginRoomId;
    private string? _lastMoveDirection;
    private string? _lastMoveDestinationRoomId;
    private bool _recoveryPhaseActive;

    public JevDecisionCoordinator(
        ChannelReader<EventEnvelope> events,
        StateReducer state,
        JevAuthorityService authority,
        IScriptCommands commands,
        IScriptScheduler scheduler,
        IScriptExecutionSupervisor execution,
        IEventSink sink,
        Func<IJevDecisionEngine?> engine,
        Func<StateSnapshot, CancellationToken, Task<object?>>? contextProvider = null)
    {
        _events = events;
        _state = state;
        _authority = authority;
        _commands = commands;
        _scheduler = scheduler;
        _execution = execution;
        _jevScope = execution.CreateOwner(ScriptOwnerKind.JevSession, "Jev session");
        _sink = sink;
        _engine = engine;
        _contextProvider = contextProvider ?? ((_, _) => Task.FromResult<object?>(null));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task evaluationWorker = EvaluationLoopAsync(cancellationToken);
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                ObserveAutonomyEvent(envelope);
                if (envelope.Payload is JevEnabledChanged enabled)
                {
                    if (enabled.Enabled) ReplaceJevScope();
                    else CancelJevScope();
                }

                IReadOnlyList<JevDomain> triggered = JevDecisionTriggerPolicy.GetTriggeredDomains(envelope.Payload);
                foreach (JevDomain domain in triggered)
                {
                    if (!AutonomyPriority.Contains(domain))
                    {
                        continue;
                    }

                    _evaluationSignals.Writer.TryWrite(new EvaluationSignal(domain, envelope.Sequence));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _evaluationSignals.Writer.TryComplete();
            try
            {
                await evaluationWorker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    public Task<JevDecisionTrace?> EvaluateCombatNowAsync(CancellationToken cancellationToken = default) =>
        EvaluateDomainNowAsync(JevDomain.Combat, cancellationToken);

    public Task<JevDecisionTrace?> EvaluateNavigationNowAsync(CancellationToken cancellationToken = default) =>
        EvaluateDomainNowAsync(JevDomain.Navigation, cancellationToken);

    public Task<JevDecisionTrace?> EvaluateRecoveryNowAsync(CancellationToken cancellationToken = default) =>
        EvaluateDomainNowAsync(JevDomain.Recovery, cancellationToken);

    public async Task<JevDecisionTrace?> EvaluateDomainNowAsync(
        JevDomain domain,
        CancellationToken cancellationToken = default)
    {
        IJevDecisionEngine? engine = _engine();
        if (engine is null || !_authority.Enabled)
        {
            return null;
        }

        await _evaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StateSnapshot snapshot = _state.Current;
            object? persistentContext = await _contextProvider(snapshot, cancellationToken).ConfigureAwait(false);
            if (!TryCreateRequest(
                    domain,
                    snapshot,
                    persistentContext,
                    AvendarAutonomyConstraints.Empty,
                    out JevChoiceRequest? request) || request is null)
            {
                return null;
            }

            JevDecisionTrace decision = await engine.EvaluateChoiceAsync(request, cancellationToken).ConfigureAwait(false);
            await ApplyAuthorityAsync(decision, cancellationToken).ConfigureAwait(false);
            return decision;
        }
        finally
        {
            _evaluationGate.Release();
        }
    }

    public async Task<bool> ApproveAsync(Guid decisionId, CancellationToken cancellationToken = default)
    {
        if (!_approvals.TryRemove(decisionId, out PendingApproval? pending))
        {
            return false;
        }

        try
        {
            bool executed = await ExecuteDecisionAsync(pending.Decision, cancellationToken).ConfigureAwait(false);
            await _sink.PublishAsync(
                new JevApprovalResolved(decisionId, executed, executed ? null : "Decision could not be revalidated."),
                "jev.coordinator",
                cancellationToken).ConfigureAwait(false);
            return executed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _sink.PublishAsync(
                new JevApprovalResolved(decisionId, false, exception.Message),
                "jev.coordinator",
                CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<bool> RejectAsync(Guid decisionId, CancellationToken cancellationToken = default)
    {
        if (!_approvals.TryRemove(decisionId, out _))
        {
            return false;
        }

        await _sink.PublishAsync(
            new JevApprovalResolved(decisionId, false, "Rejected by user."),
            "jev.coordinator",
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task EvaluationLoopAsync(CancellationToken cancellationToken)
    {
        while (await _evaluationSignals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            HashSet<JevDomain> domains = [];
            long maxSequence = 0;
            DrainSignals(domains, ref maxSequence);

            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            DrainSignals(domains, ref maxSequence);

            await WaitForAuthoritativeStateAsync(maxSequence, cancellationToken).ConfigureAwait(false);

            if (_engine() is null || !_authority.Enabled || !domains.Any(domain => _authority.Get(domain) != JevAuthority.Off))
            {
                continue;
            }

            await _evaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EvaluateAutonomyCycleAsync(domains, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await _sink.PublishAsync(
                    new ComponentError("Jev coordinator", exception.Message),
                    "jev.coordinator",
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _evaluationGate.Release();
            }
        }
    }

    private void DrainSignals(HashSet<JevDomain> domains, ref long maxSequence)
    {
        while (_evaluationSignals.Reader.TryRead(out EvaluationSignal? signal))
        {
            if (signal is null)
            {
                continue;
            }

            domains.Add(signal.Domain);
            maxSequence = Math.Max(maxSequence, signal.EventSequence);
        }
    }

    private async Task WaitForAuthoritativeStateAsync(long eventSequence, CancellationToken cancellationToken)
    {
        if (eventSequence <= 0)
        {
            return;
        }

        DateTimeOffset deadline = _scheduler.UtcNow.AddSeconds(1);
        while (_state.LastProcessedSequence < eventSequence && _scheduler.UtcNow < deadline)
        {
            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JevDecisionTrace?> EvaluateAutonomyCycleAsync(
        HashSet<JevDomain> triggeredDomains,
        CancellationToken cancellationToken)
    {
        IJevDecisionEngine? engine = _engine();
        if (engine is null)
        {
            return null;
        }

        StateSnapshot snapshot = _state.Current;
        RefreshRoomHistory(snapshot);

        if (HasPendingAction(_scheduler.UtcNow))
        {
            return null;
        }

        object? persistentContext = await _contextProvider(snapshot, cancellationToken).ConfigureAwait(false);

        // Recovery is the only prompt-driven domain. Track whether we actually entered recovery
        // and hand control back to navigation once when that phase completes. Healthy prompts by
        // themselves never become a navigation metronome.
        if (triggeredDomains.Contains(JevDomain.Recovery) && !snapshot.Combat.Active)
        {
            bool needsRecovery = AvendarJevRequestFactory.NeedsRecoveryForTravel(snapshot);
            lock (_autonomyGate)
            {
                if (needsRecovery)
                {
                    _recoveryPhaseActive = true;
                }
                else if (_recoveryPhaseActive &&
                         string.Equals(snapshot.Character.Position, "standing", StringComparison.OrdinalIgnoreCase) &&
                         _authority.Get(JevDomain.Navigation) != JevAuthority.Off)
                {
                    _recoveryPhaseActive = false;
                    triggeredDomains.Add(JevDomain.Navigation);
                }
            }
        }

        foreach (JevDomain domain in AutonomyPriority)
        {
            if (!triggeredDomains.Contains(domain) || _authority.Get(domain) == JevAuthority.Off)
            {
                continue;
            }

            DateTimeOffset now = _scheduler.UtcNow;
            if (DomainIsCoolingDown(domain, now))
            {
                continue;
            }

            AvendarAutonomyConstraints constraints = BuildConstraints(snapshot, domain, now, persistentContext);
            if (!TryCreateRequest(domain, snapshot, persistentContext, constraints, out JevChoiceRequest? request) || request is null)
            {
                continue;
            }

            if (domain == JevDomain.Recovery)
            {
                lock (_autonomyGate)
                {
                    _recoveryPhaseActive = true;
                }
            }

            JevDecisionTrace decision = await engine.EvaluateChoiceAsync(request, cancellationToken).ConfigureAwait(false);
            RegisterDecisionCooldown(decision, _scheduler.UtcNow);
            await ApplyAuthorityAsync(decision, cancellationToken).ConfigureAwait(false);

            if (domain == JevDomain.Combat &&
                !snapshot.Combat.Active &&
                _authority.Get(domain) == JevAuthority.Auto &&
                AvendarJevRequestFactory.IsNoOpAction(decision.Selected.Action))
            {
                continue;
            }

            return decision;
        }

        return null;
    }

    private static bool TryCreateRequest(
        JevDomain domain,
        StateSnapshot snapshot,
        object? persistentContext,
        AvendarAutonomyConstraints constraints,
        out JevChoiceRequest? request) =>
        domain switch
        {
            JevDomain.Combat => AvendarJevRequestFactory.TryCreateCombatRequest(snapshot, persistentContext, constraints, out request),
            JevDomain.Recovery => AvendarJevRequestFactory.TryCreateRecoveryRequest(snapshot, persistentContext, constraints, out request),
            JevDomain.Navigation => AvendarJevRequestFactory.TryCreateNavigationRequest(snapshot, persistentContext, constraints, out request),
            _ => NoRequest(out request)
        };

    private static bool NoRequest(out JevChoiceRequest? request)
    {
        request = null;
        return false;
    }

    private async Task ApplyAuthorityAsync(JevDecisionTrace decision, CancellationToken cancellationToken)
    {
        JevAuthority authority = _authority.Get(decision.Domain);
        switch (authority)
        {
            case JevAuthority.Off:
                await _sink.PublishAsync(
                    new JevDecisionExecutionSkipped(decision.DecisionId, "Authority changed to Off before the decision completed."),
                    "jev.coordinator",
                    cancellationToken).ConfigureAwait(false);
                return;
            case JevAuthority.Observe:
            case JevAuthority.Suggest:
                return;
            case JevAuthority.Approve:
                if (AvendarJevRequestFactory.IsNoOpAction(decision.Selected.Action))
                {
                    await _sink.PublishAsync(
                        new JevDecisionExecutionSkipped(decision.DecisionId, "Jev selected a no-op action; no command is required."),
                        "jev.coordinator",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                StateSnapshot approvalState = _state.Current;
                AvendarJevRequestFactory.TryMaterializeAction(
                    approvalState,
                    decision.Domain,
                    decision.Selected.Action,
                    out string? preview,
                    out _);
                _approvals[decision.DecisionId] = new PendingApproval(decision);
                await _sink.PublishAsync(
                    new JevApprovalRequested(decision.DecisionId, decision.Domain, decision.Selected.Action, preview),
                    "jev.coordinator",
                    cancellationToken).ConfigureAwait(false);
                return;
            case JevAuthority.Auto:
                await ExecuteDecisionAsync(decision, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(authority), authority, "Unknown Jev authority.");
        }
    }

    private async Task<bool> ExecuteDecisionAsync(JevDecisionTrace decision, CancellationToken cancellationToken)
    {
        if (AvendarJevRequestFactory.IsNoOpAction(decision.Selected.Action))
        {
            await _sink.PublishAsync(
                new JevDecisionExecutionSkipped(decision.DecisionId, "Jev selected a no-op action; no command is required."),
                "jev.coordinator",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        StateSnapshot current = _state.Current;
        if (current.Session.InputMode != SessionInputMode.Normal)
        {
            await _sink.PublishAsync(
                new JevDecisionExecutionSkipped(decision.DecisionId, "The session is no longer accepting normal game commands."),
                "jev.coordinator",
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (ActionIsCoolingDown(decision.Domain, decision.Selected.Action, _scheduler.UtcNow))
        {
            await _sink.PublishAsync(
                new JevDecisionExecutionSkipped(decision.DecisionId, "The selected action is temporarily suppressed while its previous outcome settles."),
                "jev.coordinator",
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!AvendarJevRequestFactory.TryMaterializeAction(
                current,
                decision.Domain,
                decision.Selected.Action,
                out string? command,
                out string? reason) || string.IsNullOrWhiteSpace(command))
        {
            await _sink.PublishAsync(
                new JevDecisionExecutionSkipped(decision.DecisionId, reason ?? "The selected action is no longer valid."),
                "jev.coordinator",
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        Guid actionId = Guid.NewGuid();
        PendingAutonomyAction pending = new(
            actionId,
            decision.DecisionId,
            decision.Domain,
            decision.Selected.Action,
            command,
            _scheduler.UtcNow);

        lock (_autonomyGate)
        {
            _pendingAction = pending;
            if (decision.Domain == JevDomain.Navigation && decision.Selected.Action.StartsWith("move:", StringComparison.OrdinalIgnoreCase))
            {
                _lastMoveOriginRoomId = current.Room.Id;
                _lastMoveDirection = decision.Selected.Action["move:".Length..].Trim().ToLowerInvariant();
                _lastMoveDestinationRoomId = null;
            }
        }

        try
        {
            ScriptCommandResult result = await _commands.SendAsync(
                new ScriptCommandRequest(
                    command,
                    ScriptCommandOrigin.Jev,
                    decision.DecisionId.ToString("N"),
                    $"Jev {decision.Domain}",
                    decision.Selected.Action,
                    ExpectedStateVersion: current.Version,
                    ActionId: actionId,
                    Domain: decision.Domain.ToString(),
                    DecisionId: decision.DecisionId,
                    UserApproved: true),
                cancellationToken).ConfigureAwait(false);
            if (!result.Accepted)
            {
                lock (_autonomyGate)
                {
                    if (_pendingAction?.ActionId == actionId) _pendingAction = null;
                }
                await _sink.PublishAsync(
                    new JevDecisionExecutionSkipped(decision.DecisionId, result.Reason ?? "Command dispatch rejected the Jev action."),
                    "jev.coordinator",
                    cancellationToken).ConfigureAwait(false);
                return false;
            }

            StartJevTask($"outcome:{decision.Domain}:{actionId:N}", token => WatchPendingOutcomeAsync(pending, token));
            return true;
        }
        catch
        {
            lock (_autonomyGate)
            {
                if (_pendingAction?.ActionId == actionId)
                {
                    _pendingAction = null;
                }
            }
            throw;
        }
    }

    private async Task WatchPendingOutcomeAsync(
        PendingAutonomyAction pending,
        CancellationToken cancellationToken)
    {
        try
        {
            await _scheduler.DelayAsync(
                    PendingTimeout(pending.Domain) + TimeSpan.FromMilliseconds(100),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        long sequence;
        lock (_autonomyGate)
        {
            if (_pendingAction?.ActionId != pending.ActionId)
            {
                return;
            }

            SetActionCooldownUnsafe(pending.Domain, pending.SemanticAction, TimeSpan.FromSeconds(5));
            _pendingAction = null;
            sequence = _state.LastProcessedSequence;
        }

        // A missing semantic outcome must not permanently freeze Auto mode. Reconsider the domain
        // with the timed-out action temporarily excluded, allowing another legal recovery/move or
        // a safe no-op instead of blindly repeating the same command.
        _evaluationSignals.Writer.TryWrite(new EvaluationSignal(pending.Domain, sequence));
    }

    private void ObserveAutonomyEvent(EventEnvelope envelope)
    {
        lock (_autonomyGate)
        {
            PruneCooldowns(_scheduler.UtcNow);
            PendingAutonomyAction? pending = _pendingAction;
            if (pending is null)
            {
                return;
            }

            if (envelope.Payload is ActionRejected rejected && rejected.ActionId == pending.ActionId)
            {
                SetActionCooldownUnsafe(pending.Domain, pending.SemanticAction, TimeSpan.FromSeconds(2));
                _pendingAction = null;
                _evaluationSignals.Writer.TryWrite(new EvaluationSignal(pending.Domain, envelope.Sequence));
                return;
            }

            if (!IsOutcomeEvent(pending.Domain, envelope.Payload))
            {
                return;
            }

            TimeSpan cooldown = OutcomeCooldown(pending.Domain, pending.SemanticAction);
            SetActionCooldownUnsafe(pending.Domain, pending.SemanticAction, cooldown);
            _pendingAction = null;
        }
    }

    private void StartJevTask(string operation, Func<CancellationToken, Task> work)
    {
        IScriptExecutionScope scope = _jevScope;
        if (scope.IsCancellationRequested) return;
        try
        {
            _ = scope.RunAsync(operation, work);
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // The owner was replaced concurrently; the replacement owns subsequent work.
        }
    }

    private void ReplaceJevScope()
    {
        IScriptExecutionScope previous = _jevScope;
        previous.Cancel();
        _ = previous.DisposeAsync().AsTask();
        _jevScope = _execution.CreateOwner(ScriptOwnerKind.JevSession, "Jev session");
    }

    private void CancelJevScope()
    {
        _jevScope.Cancel();
        lock (_autonomyGate) _pendingAction = null;
        _approvals.Clear();
    }

    private static bool IsOutcomeEvent(JevDomain domain, IMudEvent mudEvent) => domain switch
    {
        JevDomain.Navigation => mudEvent is RoomObservationObserved or NavigationFailed,
        JevDomain.Recovery => mudEvent is CharacterPromptObserved or CharacterPositionObserved,
        JevDomain.Combat => mudEvent is CombatStateChanged or CombatDamageObserved or CombatAttackObserved or
            CombatTargetConditionObserved or EnemyKilled or CharacterPromptObserved,
        _ => false
    };

    private bool HasPendingAction(DateTimeOffset now)
    {
        lock (_autonomyGate)
        {
            PruneCooldowns(now);
            if (_pendingAction is null)
            {
                return false;
            }

            if (now - _pendingAction.QueuedAt <= PendingTimeout(_pendingAction.Domain))
            {
                return true;
            }

            SetActionCooldownUnsafe(_pendingAction.Domain, _pendingAction.SemanticAction, TimeSpan.FromSeconds(5));
            _pendingAction = null;
            return false;
        }
    }

    private AvendarAutonomyConstraints BuildConstraints(
        StateSnapshot snapshot,
        JevDomain domain,
        DateTimeOffset now,
        object? persistentContext)
    {
        lock (_autonomyGate)
        {
            PruneCooldowns(now);
            string prefix = $"{domain}:";
            HashSet<string> suppressed = _actionCooldowns
                .Where(pair => pair.Value > now && pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key[prefix.Length..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string? backtrack = null;
            if (!string.IsNullOrWhiteSpace(_lastMoveDirection) &&
                !string.IsNullOrWhiteSpace(_lastMoveDestinationRoomId) &&
                string.Equals(snapshot.Room.Id, _lastMoveDestinationRoomId, StringComparison.Ordinal))
            {
                backtrack = OppositeDirection(_lastMoveDirection!);
            }

            string[] recent = _recentRoomIds.TakeLast(6).ToArray();
            HashSet<string> protectedTargets = new(StringComparer.OrdinalIgnoreCase);
            if (persistentContext is JevPersistentContext knowledge)
            {
                DateTimeOffset speakerCutoff = now.AddMinutes(-2);
                CommunicationKnowledge[] localSpeakers = knowledge.RecentCommunications
                    .Where(message => message.Channel.Equals("say", StringComparison.OrdinalIgnoreCase) &&
                                      !string.IsNullOrWhiteSpace(message.Speaker) &&
                                      (message.ObservedAt is null || message.ObservedAt >= speakerCutoff))
                    .ToArray();

                foreach (CommunicationKnowledge message in localSpeakers)
                {
                    protectedTargets.Add(message.Speaker!.Trim());
                }

                // Avendar may list an NPC by description ("an exotic young woman") and later
                // reveal the proper name only when they speak. When there is exactly one occupant,
                // bind an otherwise-unresolved local speaker to that occupant for target safety.
                if (snapshot.Room.Occupants.Count == 1 && localSpeakers.Any(message =>
                        !OccupantMatchesSpeaker(snapshot.Room.Occupants[0], message.Speaker!)))
                {
                    RoomContentObservation onlyOccupant = snapshot.Room.Occupants[0];
                    string? target = onlyOccupant.TargetKeywords?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                                     onlyOccupant.CanonicalName;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        protectedTargets.Add(target.Trim());
                    }
                }
            }
            return new AvendarAutonomyConstraints(suppressed, backtrack, recent, protectedTargets);
        }
    }

    private void RefreshRoomHistory(StateSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Room.Id))
        {
            return;
        }

        lock (_autonomyGate)
        {
            string roomId = snapshot.Room.Id!;
            if (string.Equals(roomId, _lastObservedRoomId, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_lastMoveOriginRoomId) &&
                string.Equals(_lastObservedRoomId, _lastMoveOriginRoomId, StringComparison.Ordinal))
            {
                _lastMoveDestinationRoomId = roomId;
            }

            _lastObservedRoomId = roomId;
            _recentRoomIds.Add(roomId);
            if (_recentRoomIds.Count > 12)
            {
                _recentRoomIds.RemoveRange(0, _recentRoomIds.Count - 12);
            }
        }
    }

    private bool DomainIsCoolingDown(JevDomain domain, DateTimeOffset now)
    {
        lock (_autonomyGate)
        {
            return _domainCooldowns.TryGetValue(domain, out DateTimeOffset until) && until > now;
        }
    }

    private bool ActionIsCoolingDown(JevDomain domain, string action, DateTimeOffset now)
    {
        lock (_autonomyGate)
        {
            return _actionCooldowns.TryGetValue(ActionCooldownKey(domain, action), out DateTimeOffset until) && until > now;
        }
    }

    private void RegisterDecisionCooldown(JevDecisionTrace decision, DateTimeOffset now)
    {
        // An executed Auto action is sequenced by _pendingAction, not a time-based debounce.
        // A short domain cooldown here can consume the command's immediate server response and
        // strand the supervisor (notably after rest/stand). Cooldowns are only for non-executing
        // observation modes and deliberate no-op decisions.
        if (decision.Authority == JevAuthority.Auto &&
            !AvendarJevRequestFactory.IsNoOpAction(decision.Selected.Action))
        {
            return;
        }

        TimeSpan duration = decision.Authority switch
        {
            JevAuthority.Observe or JevAuthority.Suggest => TimeSpan.FromMilliseconds(750),
            _ when AvendarJevRequestFactory.IsNoOpAction(decision.Selected.Action) => decision.Domain switch
            {
                JevDomain.Navigation => TimeSpan.FromSeconds(5),
                JevDomain.Recovery => TimeSpan.FromSeconds(2),
                JevDomain.Combat => TimeSpan.FromSeconds(3),
                _ => TimeSpan.FromSeconds(1)
            },
            _ => TimeSpan.FromMilliseconds(250)
        };

        lock (_autonomyGate)
        {
            _domainCooldowns[decision.Domain] = now + duration;
        }
    }

    private void SetActionCooldownUnsafe(JevDomain domain, string action, TimeSpan duration) =>
        _actionCooldowns[ActionCooldownKey(domain, action)] = _scheduler.UtcNow + duration;

    private void PruneCooldowns(DateTimeOffset now)
    {
        foreach (string key in _actionCooldowns.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _actionCooldowns.Remove(key);
        }
        foreach (JevDomain key in _domainCooldowns.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _domainCooldowns.Remove(key);
        }
    }

    private static string ActionCooldownKey(JevDomain domain, string action) => $"{domain}:{action}";

    private static TimeSpan PendingTimeout(JevDomain domain) => domain switch
    {
        JevDomain.Navigation => TimeSpan.FromSeconds(6),
        JevDomain.Recovery => TimeSpan.FromSeconds(5),
        JevDomain.Combat => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(4)
    };

    private static TimeSpan OutcomeCooldown(JevDomain domain, string action) => domain switch
    {
        JevDomain.Recovery when action.Equals("stand", StringComparison.OrdinalIgnoreCase) => TimeSpan.FromSeconds(10),
        JevDomain.Recovery => TimeSpan.FromSeconds(2),
        JevDomain.Navigation => TimeSpan.Zero,
        JevDomain.Combat => TimeSpan.FromSeconds(1),
        _ => TimeSpan.FromSeconds(1)
    };

    private static bool OccupantMatchesSpeaker(RoomContentObservation occupant, string speaker)
    {
        string name = speaker.Trim();
        if (!string.IsNullOrWhiteSpace(occupant.CanonicalName) &&
            occupant.CanonicalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return occupant.Description.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private static string? OppositeDirection(string direction) => direction.Trim().ToLowerInvariant() switch
    {
        "north" => "south",
        "south" => "north",
        "east" => "west",
        "west" => "east",
        "up" => "down",
        "down" => "up",
        _ => null
    };
}

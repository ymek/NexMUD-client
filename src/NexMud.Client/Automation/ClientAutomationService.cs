using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NexMud.Client.Settings;
using NexMud.Contracts.Events;
using NexMud.Contracts.Jev;
using NexMud.Contracts.State;
using NexMud.Core.Events;
using NexMud.Core.State;
using NexMud.Scripting.Scheduling;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.Host;
using NexMud.Scripting.Execution;

namespace NexMud.Client.Automation;

/// <summary>
/// Legacy workflow executor retained during the Automation strangler migration. Aliases, text
/// triggers, state rules, and timers execute through AutomationRuntimeCompiler/Jint; this service
/// temporarily owns only workflow behavior whose mapper/Jev actions are outside the current SDK.
/// </summary>
public sealed class ClientAutomationService
{
    private sealed record EventWaiter(string EventType, TaskCompletionSource<IMudEvent> Completion);

    private sealed class WorkflowContext
    {
        public WorkflowContext(IMudEvent? triggerEvent) => Event = triggerEvent;
        public IMudEvent? Event { get; set; }
    }

    private sealed record ActiveWorkflow(string Name, IScriptExecutionScope Scope);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly StateReducer _state;
    private readonly IScriptCommands _commands;
    private readonly IScriptScheduler _scheduler;
    private readonly IScriptExecutionScope _automationScope;
    private readonly IEventSink _eventSink;
    private readonly Func<ClientSettings> _settings;
    private readonly Func<JevDomain, CancellationToken, Task<bool>> _jevEscalation;
    private readonly Func<string, CancellationToken, Task<bool>> _navigate;
    private readonly AutomationStateStore _stateStore;
    private readonly AutomationRuntimeCompiler? _compiledAutomation;
    private readonly ConcurrentDictionary<string, byte> _completedOneShotWorkflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _lastWorkflowCondition = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastWorkflowFire = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveWorkflow> _activeWorkflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _workflowStartLock = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _actionCompletions = new();
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EventWaiter> _eventWaiters = [];
    private readonly object _eventWaiterLock = new();
    private readonly SemaphoreSlim _variableGate = new(1, 1);
    private long _lastCompiledStateVersion = -1;
    private long _lastWorkflowStateVersion = -1;

    public ClientAutomationService(
        ChannelReader<EventEnvelope> events,
        StateReducer state,
        IScriptCommands commands,
        IScriptScheduler scheduler,
        IScriptExecutionSupervisor execution,
        IEventSink eventSink,
        Func<ClientSettings> settings,
        Func<JevDomain, CancellationToken, Task<bool>>? jevEscalation = null,
        Func<string, CancellationToken, Task<bool>>? navigate = null,
        AutomationStateStore? stateStore = null,
        AutomationRuntimeCompiler? compiledAutomation = null)
    {
        _events = events;
        _state = state;
        _commands = commands;
        _scheduler = scheduler;
        _automationScope = execution.CreateOwner(ScriptOwnerKind.AutomationRule, "Automation runtime");
        _eventSink = eventSink;
        _settings = settings;
        _jevEscalation = jevEscalation ?? ((_, _) => Task.FromResult(false));
        _navigate = navigate ?? ((_, _) => Task.FromResult(false));
        _stateStore = stateStore ?? new AutomationStateStore();
        _compiledAutomation = compiledAutomation;
    }

    public IReadOnlyDictionary<string, string> Variables
    {
        get
        {
            lock (_variables) return new Dictionary<string, string>(_variables, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> ActiveWorkflowNames => _activeWorkflows.Values
        .Select(workflow => workflow.Name)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public Task<bool> RunWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId)) return Task.FromResult(false);
        ClientSettings settings = _settings();
        AutomationPreferences preferences = settings.Automation ?? new AutomationPreferences();
        AutomationWorkflow? workflow = (settings.Workflows ?? Array.Empty<AutomationWorkflow>())
            .FirstOrDefault(candidate => WorkflowId(candidate).Equals(workflowId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (workflow is null || !workflow.Enabled || !workflow.AllowManualRun || !preferences.Enabled ||
            preferences.GetDisabledGroupSet().Contains(workflow.Group) || !CanAutomate(_state.Current))
            return Task.FromResult(false);
        return Task.FromResult(StartWorkflow(workflow, null, cancellationToken));
    }

    public bool CancelWorkflow(string workflowId)
    {
        if (!_activeWorkflows.TryGetValue(workflowId, out ActiveWorkflow? workflow)) return false;
        workflow.Scope.Cancel();
        return true;
    }

    public void CancelAllWorkflows()
    {
        foreach (ActiveWorkflow workflow in _activeWorkflows.Values) workflow.Scope.Cancel();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await LoadVariablesAsync(cancellationToken).ConfigureAwait(false);
        await RunEventsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                CompleteActionWaiter(envelope.Payload);

                // Source observations are evidence for parsers/display, not a second Automation
                // event surface. Raw trigger compatibility continues through GameTextReceived.
                if (envelope.Payload is GameObservationReceived)
                {
                    continue;
                }

                SignalEventWaiters(envelope.Payload);

                // Keep Automation's domain matching aligned with the same semantic-event -> state-reduction
                // ordering used by the scripting event bridge. Matchers may read state/template values, so they
                // must not observe a snapshot which predates the event currently being processed.
                await _state.WaitUntilProcessedAsync(envelope.Sequence, cancellationToken).ConfigureAwait(false);
                StateSnapshot current = _state.Current;
                if (_compiledAutomation is not null && current.Version != _lastCompiledStateVersion)
                {
                    _lastCompiledStateVersion = current.Version;
                    await _compiledAutomation.ProcessStateChangedAsync(
                        current,
                        envelope.Payload,
                        Variables,
                        cancellationToken).ConfigureAwait(false);
                }

                bool stateChanged = current.Version != _lastWorkflowStateVersion;
                if (stateChanged) _lastWorkflowStateVersion = current.Version;
                await EvaluateWorkflowTriggersAsync(current, envelope.Payload, stateChanged, cancellationToken).ConfigureAwait(false);

                if (_compiledAutomation is not null && envelope.Payload is GameTextReceived game)
                {
                    await _compiledAutomation.ProcessGameTextAsync(
                        game.Text,
                        Variables,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task EvaluateWorkflowTriggersAsync(
        StateSnapshot state,
        IMudEvent currentEvent,
        bool stateChanged,
        CancellationToken cancellationToken)
    {
        if (!CanAutomate(state)) return Task.CompletedTask;
        ClientSettings settings = _settings();
        AutomationPreferences preferences = settings.Automation ?? new AutomationPreferences();
        IReadOnlySet<string> disabledGroups = preferences.GetDisabledGroupSet();
        int maximum = Math.Clamp(preferences.MaxConcurrentWorkflows, 1, 16);
        if (_activeWorkflows.Count >= maximum) return Task.CompletedTask;

        foreach (AutomationWorkflow workflow in (settings.Workflows ?? Array.Empty<AutomationWorkflow>())
                     .Where(workflow => workflow.Enabled && !disabledGroups.Contains(workflow.Group))
                     .OrderByDescending(workflow => workflow.Priority)
                     .ThenBy(workflow => workflow.Name, StringComparer.OrdinalIgnoreCase))
        {
            string workflowId = WorkflowId(workflow);
            if (string.IsNullOrWhiteSpace(workflow.Name) ||
                (string.IsNullOrEmpty(workflow.Steps) && workflow.Actions is not { Count: > 0 }) ||
                (workflow.OneShot && _completedOneShotWorkflows.ContainsKey(workflowId)) ||
                _activeWorkflows.ContainsKey(workflowId)) continue;

            bool eventMatches = string.IsNullOrWhiteSpace(workflow.TriggerEvent)
                ? true
                : workflow.TriggerEvent == "*"
                    ? currentEvent is not AutomationRuleMatched &&
                      currentEvent is not AutomationVariableChanged &&
                      currentEvent is not AutomationWorkflowStateChanged
                    : currentEvent.GetType().Name.Equals(workflow.TriggerEvent, StringComparison.OrdinalIgnoreCase);
            if (!eventMatches) continue;

            bool hasCondition = !string.IsNullOrWhiteSpace(workflow.TriggerCondition);
            bool condition = !hasCondition || GameRuleEvaluator.Evaluate(workflow.TriggerCondition!, state, Variables, currentEvent);
            bool previous = _lastWorkflowCondition.TryGetValue(workflowId, out bool prior) && prior;
            if (stateChanged || hasCondition) _lastWorkflowCondition[workflowId] = condition;

            // State-only workflows are edge-triggered. Event-triggered workflows fire for each matching event
            // while their optional state guard is true.
            bool shouldFire = string.IsNullOrWhiteSpace(workflow.TriggerEvent)
                ? hasCondition && condition && !previous
                : condition;
            if (!shouldFire) continue;

            DateTimeOffset now = _scheduler.UtcNow;
            int cooldown = Math.Clamp(workflow.CooldownMilliseconds, 0, 600_000);
            if (_lastWorkflowFire.TryGetValue(workflowId, out DateTimeOffset last) && now - last < TimeSpan.FromMilliseconds(cooldown)) continue;

            _lastWorkflowFire[workflowId] = now;
            StartWorkflow(workflow, currentEvent, cancellationToken);
            if (_activeWorkflows.Count >= maximum) break;
        }
        return Task.CompletedTask;
    }

    private bool StartWorkflow(AutomationWorkflow workflow, IMudEvent? triggerEvent, CancellationToken cancellationToken)
    {
        string workflowId = WorkflowId(workflow);
        IScriptExecutionScope scope = _automationScope.CreateChild(
            ScriptOwnerKind.AutomationWorkflow,
            $"Automation: {workflow.Name}");
        lock (_workflowStartLock)
        {
            int maximum = Math.Clamp((_settings().Automation ?? new AutomationPreferences()).MaxConcurrentWorkflows, 1, 16);
            if ((workflow.OneShot && _completedOneShotWorkflows.ContainsKey(workflowId)) ||
                _activeWorkflows.Count >= maximum || !_activeWorkflows.TryAdd(workflowId, new ActiveWorkflow(workflow.Name, scope)))
            {
                _ = scope.DisposeAsync().AsTask();
                return false;
            }
            if (workflow.OneShot) _completedOneShotWorkflows.TryAdd(workflowId, 0);
        }

        _ = RunWorkflowScopeAsync(workflow, workflowId, triggerEvent, scope, cancellationToken);
        return true;
    }

    private async Task RunWorkflowScopeAsync(
        AutomationWorkflow workflow,
        string workflowId,
        IMudEvent? triggerEvent,
        IScriptExecutionScope scope,
        CancellationToken callerCancellation)
    {
        using CancellationTokenRegistration registration = callerCancellation.CanBeCanceled
            ? callerCancellation.Register(scope.Cancel)
            : default;
        try
        {
            await scope.RunAsync(
                $"workflow:{workflow.Name}",
                token => ExecuteWorkflowAsync(workflow, workflowId, triggerEvent, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scope.IsCancellationRequested)
        {
            await PublishWorkflowAsync(
                workflow.Name,
                AutomationWorkflowStatus.Cancelled,
                0,
                0,
                "Cancelled",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _activeWorkflows.TryRemove(workflowId, out _);
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteWorkflowAsync(AutomationWorkflow workflow, string workflowId, IMudEvent? triggerEvent, CancellationToken cancellationToken)
    {
        WorkflowContext context = new(triggerEvent);
        string[] steps = workflow.Steps
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();
        await PublishWorkflowAsync(workflow.Name, AutomationWorkflowStatus.Started, 0, steps.Length, "Triggered", cancellationToken).ConfigureAwait(false);

        bool skipStructuredActions = false;
        if (workflow.Actions is { Count: > 0 })
        {
            bool ready;
            string? preparationError = null;
            try
            {
                ready = _compiledAutomation is not null &&
                        await _compiledAutomation.PrepareWorkflowActionsAsync(workflow, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ready = false;
                preparationError = exception.Message;
            }

            if (!ready)
            {
                string detail = preparationError is null
                    ? "Workflow action dispatcher is unavailable."
                    : $"Workflow action dispatcher could not be prepared: {preparationError}";
                if (workflow.FailureMode == AutomationWorkflowFailureMode.Stop)
                {
                    await PublishWorkflowAsync(
                        workflow.Name,
                        AutomationWorkflowStatus.Failed,
                        0,
                        steps.Length,
                        $"{detail} No workflow steps were executed.",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Continue mode deliberately skips the unavailable tail and records recovery.
                skipStructuredActions = true;
                await PublishWorkflowAsync(
                    workflow.Name,
                    AutomationWorkflowStatus.Running,
                    0,
                    steps.Length,
                    $"Action dispatch unavailable; continuing without structured actions: {detail}",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        for (int index = 0; index < steps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string step = steps[index];
            try
            {
                await PublishWorkflowAsync(workflow.Name, AutomationWorkflowStatus.Running, index, steps.Length, step, cancellationToken).ConfigureAwait(false);
                bool keepGoing = await ExecuteWorkflowStepAsync(workflowId, workflow.Name, step, index, steps.Length, context, cancellationToken).ConfigureAwait(false);
                if (!keepGoing)
                {
                    await PublishWorkflowAsync(workflow.Name, AutomationWorkflowStatus.Completed, index + 1, steps.Length, "Stopped by workflow.", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (workflow.FailureMode == AutomationWorkflowFailureMode.Stop)
                {
                    await PublishWorkflowAsync(workflow.Name, AutomationWorkflowStatus.Failed, index, steps.Length, exception.Message, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await PublishWorkflowAsync(
                    workflow.Name,
                    AutomationWorkflowStatus.Running,
                    index,
                    steps.Length,
                    $"Step failed; continuing: {exception.Message}",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (!skipStructuredActions && workflow.Actions is { Count: > 0 })
        {
            bool dispatched = false;
            string? dispatchError = null;
            try
            {
                dispatched = _compiledAutomation is not null && await _compiledAutomation.RunWorkflowActionsAsync(
                    workflow,
                    context.Event,
                    Variables,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                dispatchError = exception.Message;
            }

            if (!dispatched)
            {
                string detail = dispatchError is null
                    ? "Structured workflow actions could not be dispatched."
                    : $"Structured workflow actions could not be dispatched: {dispatchError}";
                if (workflow.FailureMode == AutomationWorkflowFailureMode.Stop)
                {
                    await PublishWorkflowAsync(
                        workflow.Name,
                        AutomationWorkflowStatus.Failed,
                        steps.Length,
                        steps.Length,
                        detail,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                await PublishWorkflowAsync(
                    workflow.Name,
                    AutomationWorkflowStatus.Running,
                    steps.Length,
                    steps.Length,
                    $"Action dispatch failed; continuing: {detail}",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await PublishWorkflowAsync(workflow.Name, AutomationWorkflowStatus.Completed, steps.Length, steps.Length, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteWorkflowStepAsync(
        string workflowId,
        string workflowName,
        string step,
        int index,
        int total,
        WorkflowContext context,
        CancellationToken cancellationToken)
    {
        if (TryConditionalStep(step, "if", out string condition, out string conditionalStep))
        {
            if (!GameRuleEvaluator.Evaluate(condition, _state.Current, Variables, context.Event)) return true;
            return await ExecuteWorkflowStepAsync(workflowId, workflowName, conditionalStep, index, total, context, cancellationToken).ConfigureAwait(false);
        }
        if (TryConditionalStep(step, "unless", out string unlessCondition, out string unlessStep))
        {
            if (GameRuleEvaluator.Evaluate(unlessCondition, _state.Current, Variables, context.Event)) return true;
            return await ExecuteWorkflowStepAsync(workflowId, workflowName, unlessStep, index, total, context, cancellationToken).ConfigureAwait(false);
        }
        if (TryRetryStep(step, out int retries, out int retryDelayMilliseconds, out string retryStep))
        {
            Exception? lastFailure = null;
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await ExecuteWorkflowStepAsync(workflowId, workflowName, retryStep, index, total, context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastFailure = exception;
                    if (attempt >= retries) break;
                    await PublishWorkflowAsync(
                        workflowName,
                        AutomationWorkflowStatus.Waiting,
                        index,
                        total,
                        $"Retry {attempt + 1}/{retries} after failure: {exception.Message}",
                        cancellationToken).ConfigureAwait(false);
                    if (retryDelayMilliseconds > 0)
                        await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException(
                $"Workflow step failed after {retries + 1} attempts: {retryStep}",
                lastFailure ?? new InvalidOperationException("Unknown retry failure."));
        }
        if (TryVerb(step, "assert", out string assertion))
        {
            if (!GameRuleEvaluator.Evaluate(assertion, _state.Current, Variables, context.Event))
                throw new InvalidOperationException($"Workflow assertion failed: {assertion}");
            return true;
        }
        if (TryVerb(step, "send", out string command))
        {
            bool succeeded = await QueueRuleCommandAsync(
                ExpandTemplate(command, _state.Current, context.Event),
                awaitDispatch: true,
                cancellationToken,
                GetWorkflowScope(workflowId)).ConfigureAwait(false);
            if (!succeeded) throw new InvalidOperationException($"Command rejected: {command}");
            return true;
        }
        if (TryVerb(step, "delay", out string delayText))
        {
            if (!int.TryParse(delayText, out int milliseconds)) throw new FormatException("delay requires milliseconds.");
            await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, 600_000)), cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (TryVerb(step, "wait-event", out string eventSpec))
        {
            (string eventType, int timeout) = ParseWaitSpec(eventSpec);
            await PublishWorkflowAsync(workflowName, AutomationWorkflowStatus.Waiting, index, total, $"Waiting for {eventType}", cancellationToken).ConfigureAwait(false);
            context.Event = await WaitForEventAsync(eventType, timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (TryVerb(step, "wait", out string conditionSpec))
        {
            (string waitCondition, int timeout) = ParseWaitSpec(conditionSpec);
            await PublishWorkflowAsync(workflowName, AutomationWorkflowStatus.Waiting, index, total, $"Waiting for {waitCondition}", cancellationToken).ConfigureAwait(false);
            await WaitForStateAsync(waitCondition, timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (TryVerb(step, "set", out string assignment))
        {
            int equals = assignment.IndexOf('=');
            if (equals <= 0) throw new FormatException("set requires name=value.");
            string name = assignment[..equals].Trim();
            string value = ExpandTemplate(assignment[(equals + 1)..].Trim(), _state.Current, context.Event);
            await SetVariableAsync(name, value, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (TryVerb(step, "unset", out string variable))
        {
            await SetVariableAsync(variable.Trim(), null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (TryVerb(step, "navigate", out string destinationQuery))
        {
            string query = ExpandTemplate(destinationQuery, _state.Current, context.Event).Trim();
            if (query.Length == 0) throw new FormatException("navigate requires a room, MOB, fixture, or item-source query.");
            await PublishWorkflowAsync(workflowName, AutomationWorkflowStatus.Waiting, index, total, $"Navigating to {query}", cancellationToken).ConfigureAwait(false);
            if (!await _navigate(query, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"No reachable mapped destination matched: {query}");
            return true;
        }
        if (TryVerb(step, "jev", out string domainText))
        {
            if (!Enum.TryParse(domainText.Trim(), true, out JevDomain domain) || domain is not (JevDomain.Combat or JevDomain.Navigation or JevDomain.Recovery))
                throw new FormatException("jev step supports combat, navigation, or recovery.");
            if (!await _jevEscalation(domain, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"Jev did not produce a {domain} decision.");
            return true;
        }
        if (step.Equals("stop", StringComparison.OrdinalIgnoreCase)) return false;
        throw new FormatException($"Unknown workflow step: {step}");
    }

    private bool CanAutomate(StateSnapshot state)
    {
        AutomationPreferences preferences = _settings().Automation ?? new AutomationPreferences();
        return preferences.Enabled && state.Session.InputMode == SessionInputMode.Normal && state.Session.ConnectionStatus == ConnectionStatus.Connected;
    }

    private async Task<bool> QueueRuleCommandAsync(
        string command,
        bool awaitDispatch,
        CancellationToken cancellationToken,
        IScriptExecutionScope? ownerScope = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (!CanAutomate(_state.Current)) return false;
        StateSnapshot current = _state.Current;
        IScriptExecutionScope owner = ownerScope ?? _automationScope;
        Guid actionId = Guid.NewGuid();
        TaskCompletionSource<bool>? completion = null;
        if (awaitDispatch)
        {
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _actionCompletions[actionId] = completion;
        }

        ScriptCommandResult result = await _commands.SendAsync(
            new ScriptCommandRequest(
                command,
                ScriptCommandOrigin.Automation,
                owner.Owner.Id.ToString("N"),
                owner.Owner.Name,
                "Automation command",
                ExpectedStateVersion: current.Version,
                ActionId: actionId),
            cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
        {
            _actionCompletions.TryRemove(actionId, out _);
            return false;
        }
        if (completion is null) return true;

        using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _actionCompletions.TryRemove(actionId, out _);
            return false;
        }
    }

    private IScriptExecutionScope? GetWorkflowScope(string workflowId) =>
        _activeWorkflows.TryGetValue(workflowId, out ActiveWorkflow? workflow) ? workflow.Scope : null;

    private static string WorkflowId(AutomationWorkflow workflow) =>
        AutomationRuntimeCompiler.WorkflowProgramId(workflow);

    private void CompleteActionWaiter(IMudEvent mudEvent)
    {
        switch (mudEvent)
        {
            case ActionExecuted executed when _actionCompletions.TryRemove(executed.ActionId, out TaskCompletionSource<bool>? success):
                success.TrySetResult(true);
                break;
            case ActionRejected rejected when _actionCompletions.TryRemove(rejected.ActionId, out TaskCompletionSource<bool>? failure):
                failure.TrySetResult(false);
                break;
        }
    }

    private async Task WaitForStateAsync(string condition, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        try
        {
            while (!GameRuleEvaluator.Evaluate(condition, _state.Current, Variables))
                await _scheduler.DelayAsync(TimeSpan.FromMilliseconds(75), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for state: {condition}");
        }
    }

    private async Task<IMudEvent> WaitForEventAsync(string eventType, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IMudEvent> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventWaiter waiter = new(eventType, completion);
        lock (_eventWaiterLock) _eventWaiters.Add(waiter);
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out waiting for event: {eventType}");
        }
        finally
        {
            lock (_eventWaiterLock) _eventWaiters.Remove(waiter);
        }
    }

    private void SignalEventWaiters(IMudEvent mudEvent)
    {
        string eventType = mudEvent.GetType().Name;
        EventWaiter[] matches;
        lock (_eventWaiterLock)
            matches = _eventWaiters.Where(waiter => waiter.EventType == "*" || waiter.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (EventWaiter waiter in matches) waiter.Completion.TrySetResult(mudEvent);
    }

    private async Task SetVariableAsync(string name, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Variable name is required.", nameof(name));
        string normalized = name.Trim();
        await _variableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_variables)
            {
                if (value is null) _variables.Remove(normalized);
                else _variables[normalized] = value;
            }
            if ((_settings().Automation ?? new AutomationPreferences()).PersistVariables)
                await _stateStore.SaveAsync(Variables, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _variableGate.Release();
        }
        await _eventSink.PublishAsync(new AutomationVariableChanged(normalized, value), "automation", cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadVariablesAsync(CancellationToken cancellationToken)
    {
        if (!(_settings().Automation ?? new AutomationPreferences()).PersistVariables) return;
        Dictionary<string, string> loaded = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_variables)
        {
            _variables.Clear();
            foreach ((string key, string value) in loaded) _variables[key] = value;
        }
    }

    private string ExpandTemplate(string text, StateSnapshot state, IMudEvent? currentEvent = null)
    {
        return Regex.Replace(text, @"\$\{([^}]+)\}", match =>
        {
            string key = match.Groups[1].Value.Trim();
            string? value = GameRuleEvaluator.ResolveText(key.StartsWith("var.", StringComparison.OrdinalIgnoreCase) ? key : "var." + key, state, Variables, currentEvent)
                ?? GameRuleEvaluator.ResolveText(key, state, Variables, currentEvent);
            return value ?? match.Value;
        }, RegexOptions.CultureInvariant, RegexTimeout);
    }

    private Task PublishWorkflowAsync(
        string name,
        AutomationWorkflowStatus status,
        int step,
        int total,
        string? detail,
        CancellationToken cancellationToken) =>
        _eventSink.PublishAsync(new AutomationWorkflowStateChanged(name, status, step, total, detail), "automation", cancellationToken).AsTask();

    private static (string Value, int TimeoutMilliseconds) ParseWaitSpec(string spec)
    {
        const int DefaultTimeout = 30_000;
        Match timeout = Regex.Match(spec, @"(?:^|\s)timeout=(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        if (!timeout.Success) return (spec.Trim(), DefaultTimeout);
        int milliseconds = int.TryParse(timeout.Groups[1].Value, out int parsed) ? Math.Clamp(parsed, 100, 600_000) : DefaultTimeout;
        return (spec[..timeout.Index].Trim(), milliseconds);
    }

    private static bool TryRetryStep(string line, out int retries, out int delayMilliseconds, out string nestedStep)
    {
        retries = 0;
        delayMilliseconds = 0;
        nestedStep = string.Empty;
        if (!TryVerb(line, "retry", out string remainder)) return false;

        int separator = remainder.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0 || separator >= remainder.Length - 2)
            throw new FormatException("retry requires '<count> [delay=ms] :: <step>'.");

        string options = remainder[..separator].Trim();
        nestedStep = remainder[(separator + 2)..].Trim();
        string[] tokens = options.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !int.TryParse(tokens[0], out retries) || retries is < 0 or > 10)
            throw new FormatException("retry count must be between 0 and 10.");

        foreach (string token in tokens.Skip(1))
        {
            if (!token.StartsWith("delay=", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(token[6..], out int parsedDelay))
                throw new FormatException("retry options support only delay=<milliseconds>.");
            delayMilliseconds = Math.Clamp(parsedDelay, 0, 60_000);
        }

        if (nestedStep.Length == 0) throw new FormatException("retry requires a nested step.");
        return true;
    }

    private static bool TryConditionalStep(string line, string verb, out string condition, out string nestedStep)
    {
        condition = string.Empty;
        nestedStep = string.Empty;
        if (!TryVerb(line, verb, out string remainder)) return false;
        int separator = remainder.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0 || separator >= remainder.Length - 2)
            throw new FormatException($"{verb} requires '<condition> :: <step>'.");
        condition = remainder[..separator].Trim();
        nestedStep = remainder[(separator + 2)..].Trim();
        return condition.Length > 0 && nestedStep.Length > 0;
    }

    private static bool TryVerb(string line, string verb, out string remainder)
    {
        remainder = string.Empty;
        if (!line.StartsWith(verb, StringComparison.OrdinalIgnoreCase)) return false;
        if (line.Length == verb.Length) return true;
        if (!char.IsWhiteSpace(line[verb.Length])) return false;
        remainder = line[(verb.Length + 1)..].Trim();
        return true;
    }

    private static bool TryMatch(TriggerRule rule, string text, out string command)
    {
        command = rule.Command;
        if (rule.MatchMode == HighlightMatchMode.Literal)
        {
            StringComparison comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return text.Contains(rule.Pattern, comparison);
        }

        try
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!rule.CaseSensitive) options |= RegexOptions.IgnoreCase;
            Match match = Regex.Match(text, rule.Pattern, options, RegexTimeout);
            if (!match.Success) return false;
            for (int index = 1; index < Math.Min(match.Groups.Count, 10); index++)
                command = command.Replace($"${index}", match.Groups[index].Value, StringComparison.Ordinal);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

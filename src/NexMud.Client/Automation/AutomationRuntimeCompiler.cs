using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexMud.Client.Commands;
using NexMud.Client.Scripting;
using NexMud.Client.Settings;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;
using NexMud.Core.State;
using NexMud.Scripting.Events;
using NexMud.Scripting.Host;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.Scheduling;
using NexMud.Transport.Text;

namespace NexMud.Client.Automation;

/// <summary>
/// Strangler migration boundary for Automation. Definitions are normalized into Automation IR,
/// compiled to deterministic JavaScript, and executed by the shared Jint runtime. Matching which
/// is intrinsically domain-specific (aliases, text, legacy state expressions) remains in C# and
/// publishes immutable match DTOs into the script event hub.
/// </summary>
public sealed class AutomationRuntimeCompiler
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex TemplateRegex = new(@"\$\{([^}]+)\}", RegexOptions.CultureInvariant, RegexTimeout);
    private readonly ClientScriptPlatform _platform;
    private readonly StateReducer _state;
    private readonly IScriptScheduler _scheduler;
    private readonly IAutomationCompiler _compiler;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly AnsiTextParser _ansi = new();
    private readonly Dictionary<string, DateTimeOffset> _lastFire = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _lastCondition = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completedOneShot = new(StringComparer.OrdinalIgnoreCase);
    private AutomationProgram[] _programs = [];
    private long _internalSequence;
    private bool _loaded;
    private string _lineBuffer = string.Empty;
    private string _rollingBuffer = string.Empty;
    private int _rollingBufferCharacters = 8192;
    private string? _activeGeneration;
    private ClientSettings? _lastSettings;

    public AutomationRuntimeCompiler(
        ClientScriptPlatform platform,
        StateReducer state,
        IScriptScheduler scheduler,
        IAutomationCompiler? compiler = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _compiler = compiler ?? new AutomationJavaScriptCompiler();
    }

    public IReadOnlyList<AutomationProgram> Programs => Volatile.Read(ref _programs);
    public IReadOnlyList<AutomationCompilerDiagnostic> LastDiagnostics { get; private set; } = [];
    public string? ContentHash { get; private set; }

    public async Task ReloadAsync(ClientSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _lastSettings = settings;
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AutomationPreferences preferences = settings.Automation ?? new AutomationPreferences();
            AutomationProgram[] candidatePrograms = BuildPrograms(settings, preferences).ToArray();
            _rollingBufferCharacters = Math.Clamp(preferences.RollingBufferCharacters, 1024, 65536);

            if (!preferences.Enabled || candidatePrograms.Length == 0)
            {
                if (_loaded)
                {
                    await _platform.JavaScriptRuntime.UnloadAsync(new ScriptModuleId("automation.profile"), cancellationToken)
                        .ConfigureAwait(false);
                    _loaded = false;
                }
                SwapPrograms([]);
                ResetMatcherBuffers();
                LastDiagnostics = [];
                ContentHash = null;
                _activeGeneration = null;
                return;
            }

            CompiledAutomationArtifact artifact;
            try
            {
                artifact = _compiler.Compile(candidatePrograms, ScriptApiVersion.Current);
            }
            catch (Exception exception)
            {
                LastDiagnostics = [new AutomationCompilerDiagnostic("AUTOMATION_COMPILE", exception.Message)];
                return; // replacement semantics: keep the old runtime active
            }

            if (_loaded &&
                string.Equals(ContentHash, artifact.ContentHash, StringComparison.Ordinal) &&
                IsRuntimeRunning())
            {
                SwapPrograms(artifact.Programs);
                LastDiagnostics = artifact.Diagnostics;
                _activeGeneration = artifact.RuntimeGeneration;
                return;
            }

            IScriptHost host = _platform.CreateHost(
                artifact.Package.Manifest.Id,
                new NexMud.Scripting.Permissions.ScriptPermissionSet(artifact.Package.Manifest.Permissions),
                ScriptCommandOrigin.Automation,
                "Automation runtime");
            try
            {
                if (_loaded)
                    await _platform.JavaScriptRuntime.ReloadAsync(artifact.Package, host, cancellationToken).ConfigureAwait(false);
                else
                    await _platform.JavaScriptRuntime.LoadAsync(artifact.Package, host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LastDiagnostics = [new AutomationCompilerDiagnostic("AUTOMATION_LOAD", exception.Message)];
                return; // ReloadAsync preserves the prior running instance on failure.
            }

            bool generationChanged = !string.Equals(_activeGeneration, artifact.RuntimeGeneration, StringComparison.Ordinal);
            _loaded = true;
            _activeGeneration = artifact.RuntimeGeneration;
            SwapPrograms(artifact.Programs);
            if (generationChanged) ResetMatcherBuffers();
            LastDiagnostics = artifact.Diagnostics;
            ContentHash = artifact.ContentHash;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private void ResetMatcherBuffers()
    {
        _lineBuffer = string.Empty;
        _rollingBuffer = string.Empty;
    }

    private bool IsRuntimeRunning() =>
        _platform.JavaScriptRuntime.Snapshot().Any(snapshot =>
            snapshot.Id.Value.Equals("automation.profile", StringComparison.OrdinalIgnoreCase) &&
            snapshot.Status == ScriptStatus.Running);

    private async Task<bool> EnsureRuntimeRunningAsync(CancellationToken cancellationToken)
    {
        if (IsRuntimeRunning()) return true;
        ClientSettings? settings = _lastSettings;
        if (settings is null) return false;

        await ReloadAsync(settings, cancellationToken).ConfigureAwait(false);
        return IsRuntimeRunning();
    }

    public async Task<bool> TryHandleAliasAsync(
        string input,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!CanAutomate(_state.Current)) return false;
        if (!await EnsureRuntimeRunningAsync(cancellationToken).ConfigureAwait(false)) return false;
        foreach (AutomationProgram program in Programs
                     .Where(program => program.Trigger is AliasAutomationTrigger)
                     .OrderByDescending(program => program.Priority)
                     .ThenBy(program => program.Order)
                     .ThenBy(program => program.Id, StringComparer.Ordinal))
        {
            AliasAutomationTrigger trigger = (AliasAutomationTrigger)program.Trigger;
            if (!TryMatchAlias(trigger.Pattern, input, out string tail, out string[] captures, out Dictionary<string, string> named))
                continue;

            Dictionary<string, string?> values = ResolveTemplateValues(program, _state.Current, variables, null);
            foreach ((string key, string value) in named) values[key] = value;
            await PublishMatchAsync(
                ScriptEventTypes.AutomationAliasMatched,
                program,
                new
                {
                    automationId = program.Id,
                    runtimeGeneration = _activeGeneration,
                    triggerId = $"alias:{program.Id}",
                    input,
                    tail,
                    captures,
                    values
                },
                cancellationToken).ConfigureAwait(false);
            await PublishObservedMatchAsync(program, cancellationToken).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<bool> TryHandleKeybindingAsync(
        CommandKeyBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!CanAutomate(_state.Current)) return false;
        if (!await EnsureRuntimeRunningAsync(cancellationToken).ConfigureAwait(false)) return false;

        AutomationProgram? program = Programs.FirstOrDefault(candidate =>
            candidate.Trigger is KeybindingAutomationTrigger trigger &&
            trigger.Gesture.Equals(binding.Gesture, StringComparison.OrdinalIgnoreCase));
        if (program is null) return false;

        await PublishMatchAsync(
            "automation.keybindingMatched",
            program,
            new
            {
                automationId = program.Id,
                runtimeGeneration = _activeGeneration,
                triggerId = $"keybinding:{program.Id}",
                captures = Array.Empty<string>(),
                values = new Dictionary<string, string?>()
            },
            cancellationToken).ConfigureAwait(false);
        await PublishObservedMatchAsync(program, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> PrepareWorkflowActionsAsync(
        AutomationWorkflow workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (!CanAutomate(_state.Current)) return false;
        if (!await EnsureRuntimeRunningAsync(cancellationToken).ConfigureAwait(false)) return false;

        return FindWorkflowProgram(Programs, workflow) is not null;
    }

    public async Task<bool> RunWorkflowActionsAsync(
        AutomationWorkflow workflow,
        IMudEvent? currentEvent,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        if (!await PrepareWorkflowActionsAsync(workflow, cancellationToken).ConfigureAwait(false)) return false;

        AutomationProgram? program = FindWorkflowProgram(Programs, workflow);
        if (program is null) return false;

        Dictionary<string, string?> values = ResolveTemplateValues(program, _state.Current, variables, currentEvent);
        await PublishMatchAsync(
            "automation.workflowMatched",
            program,
            new
            {
                automationId = program.Id,
                runtimeGeneration = _activeGeneration,
                triggerId = $"workflow:{program.Id}",
                conditionPassed = true,
                eventType = currentEvent?.GetType().Name,
                values
            },
            cancellationToken).ConfigureAwait(false);
        await PublishObservedMatchAsync(program, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task ProcessGameTextAsync(
        string text,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanAutomate(_state.Current)) return;
        if (!await EnsureRuntimeRunningAsync(cancellationToken).ConfigureAwait(false)) return;
        string plain = string.Concat(_ansi.Process(text).Select(segment => segment.Text));
        if (plain.Length == 0) return;

        _lineBuffer += plain;
        _rollingBuffer += plain;
        if (_rollingBuffer.Length > _rollingBufferCharacters) _rollingBuffer = _rollingBuffer[^_rollingBufferCharacters..];

        int lineEnd;
        while ((lineEnd = _lineBuffer.IndexOf('\n')) >= 0)
        {
            string line = _lineBuffer[..lineEnd].TrimEnd('\r');
            _lineBuffer = _lineBuffer[(lineEnd + 1)..];
            await ProcessTextLineAsync(line, variables, cancellationToken).ConfigureAwait(false);
        }

        if (_lineBuffer.Length > 8192)
        {
            string overflow = _lineBuffer;
            _lineBuffer = string.Empty;
            await ProcessTextLineAsync(overflow, variables, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ProcessStateChangedAsync(
        StateSnapshot state,
        IMudEvent currentEvent,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanAutomate(state)) return;
        if (!await EnsureRuntimeRunningAsync(cancellationToken).ConfigureAwait(false)) return;
        DateTimeOffset now = _scheduler.UtcNow;
        foreach (AutomationProgram program in Programs
                     .Where(program => program.Trigger is StateAutomationTrigger)
                     .OrderByDescending(program => program.Priority)
                     .ThenBy(program => program.Order)
                     .ThenBy(program => program.Id, StringComparer.Ordinal))
        {
            StateAutomationTrigger trigger = (StateAutomationTrigger)program.Trigger;
            bool current = GameRuleEvaluator.Evaluate(trigger.Expression, state, variables, currentEvent);
            bool previous = _lastCondition.TryGetValue(program.Id, out bool prior) && prior;
            _lastCondition[program.Id] = current;
            bool shouldFire = trigger.Activation switch
            {
                GameRuleActivation.OnEnter => current && !previous,
                GameRuleActivation.OnExit => !current && previous,
                _ => current
            };
            if (!shouldFire || (trigger.OneShot && _completedOneShot.Contains(program.Id))) continue;
            if (!CooldownElapsed(program.Id, trigger.CooldownMilliseconds, now)) continue;

            _lastFire[program.Id] = now;
            if (trigger.OneShot) _completedOneShot.Add(program.Id);
            Dictionary<string, string?> values = ResolveTemplateValues(program, state, variables, currentEvent);
            await PublishMatchAsync(
                ScriptEventTypes.AutomationStateRuleMatched,
                program,
                new
                {
                    automationId = program.Id,
                    runtimeGeneration = _activeGeneration,
                    triggerId = $"state:{program.Id}",
                    conditionPassed = true,
                    eventType = currentEvent.GetType().Name,
                    values
                },
                cancellationToken).ConfigureAwait(false);
            await PublishObservedMatchAsync(program, cancellationToken).ConfigureAwait(false);
            if (trigger.StopProcessing) break;
        }
    }

    private async Task ProcessTextLineAsync(
        string line,
        IReadOnlyDictionary<string, string>? variables,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _scheduler.UtcNow;
        foreach (AutomationProgram program in Programs
                     .Where(program => program.Trigger is TextAutomationTrigger)
                     .OrderByDescending(program => program.Priority)
                     .ThenBy(program => program.Order)
                     .ThenBy(program => program.Id, StringComparer.Ordinal))
        {
            TextAutomationTrigger trigger = (TextAutomationTrigger)program.Trigger;
            if (trigger.OneShot && _completedOneShot.Contains(program.Id)) continue;
            if (!CooldownElapsed(program.Id, trigger.CooldownMilliseconds, now)) continue;
            string candidate = trigger.Scope == TriggerScope.RollingBuffer ? _rollingBuffer : line;
            if (!TryMatchText(trigger, candidate, out string[] captures)) continue;

            _lastFire[program.Id] = now;
            if (trigger.OneShot) _completedOneShot.Add(program.Id);
            Dictionary<string, string?> values = ResolveTemplateValues(program, _state.Current, variables, null);
            await PublishMatchAsync(
                ScriptEventTypes.AutomationTextTriggerMatched,
                program,
                new
                {
                    automationId = program.Id,
                    runtimeGeneration = _activeGeneration,
                    triggerId = $"text:{program.Id}",
                    line,
                    captures,
                    values
                },
                cancellationToken).ConfigureAwait(false);
            await PublishObservedMatchAsync(program, cancellationToken).ConfigureAwait(false);
            if (trigger.StopProcessing) break;
        }
    }


    private static bool CanAutomate(StateSnapshot state) =>
        state.Session.InputMode == SessionInputMode.Normal &&
        state.Session.ConnectionStatus == ConnectionStatus.Connected;

    private bool CooldownElapsed(string id, int milliseconds, DateTimeOffset now)
    {
        int cooldown = Math.Clamp(milliseconds, 0, 600_000);
        return !_lastFire.TryGetValue(id, out DateTimeOffset last) || now - last >= TimeSpan.FromMilliseconds(cooldown);
    }

    private async Task PublishMatchAsync(
        string type,
        AutomationProgram program,
        object payload,
        CancellationToken cancellationToken)
    {
        JsonElement dto = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await _platform.EventHub.PublishAsync(
            new ScriptEventEnvelope(
                type,
                Interlocked.Increment(ref _internalSequence),
                _scheduler.UtcNow,
                "automation.matcher",
                dto,
                EventId: Guid.NewGuid()),
            cancellationToken).ConfigureAwait(false);
    }

    private Task PublishObservedMatchAsync(
        AutomationProgram program,
        CancellationToken cancellationToken) =>
        _platform.EventSink.PublishAsync(
            new AutomationRuleMatched(program.Name, program.Type.ToString()),
            "automation",
            cancellationToken).AsTask();

    private void SwapPrograms(IReadOnlyList<AutomationProgram> programs)
    {
        AutomationProgram[] snapshot = programs.ToArray();
        Volatile.Write(ref _programs, snapshot);
        HashSet<string> active = snapshot.Select(program => program.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string key in _lastFire.Keys.Where(key => !active.Contains(key)).ToArray()) _lastFire.Remove(key);
        foreach (string key in _lastCondition.Keys.Where(key => !active.Contains(key)).ToArray()) _lastCondition.Remove(key);
        _completedOneShot.RemoveWhere(key => !active.Contains(key));
    }

    private static IEnumerable<AutomationProgram> BuildPrograms(ClientSettings settings, AutomationPreferences preferences)
    {
        IReadOnlySet<string> disabledGroups = preferences.GetDisabledGroupSet();
        string separator = settings.CommandSeparator;

        foreach ((CommandAlias alias, int index) in (settings.Aliases ?? Array.Empty<CommandAlias>()).Select((value, index) => (value, index)))
        {
            IReadOnlyList<AutomationAction> actions = ResolveActions(alias.Actions, alias.Expansion, separator);
            if (!alias.Enabled || string.IsNullOrWhiteSpace(alias.Name) || actions.Count == 0) continue;
            yield return new AutomationProgram(
                ResolveId(alias.Id, "alias", $"{alias.Name}\n{alias.Expansion}"),
                alias.Name,
                AutomationProgramType.Alias,
                new AliasAutomationTrigger(alias.Name),
                alias.Conditions?.ToArray() ?? [],
                actions,
                Order: index);
        }

        foreach ((TriggerRule rule, int index) in (settings.Triggers ?? Array.Empty<TriggerRule>()).Select((value, index) => (value, index)))
        {
            IReadOnlyList<AutomationAction> actions = ResolveActions(rule.Actions, rule.Command, separator);
            if (!rule.Enabled || disabledGroups.Contains(rule.Group) || string.IsNullOrWhiteSpace(rule.Pattern) || actions.Count == 0) continue;
            yield return new AutomationProgram(
                ResolveId(rule.Id, "trigger", $"{rule.Pattern}\n{rule.Command}\n{rule.MatchMode}\n{rule.CaseSensitive}\n{rule.Scope}\n{rule.Group}"),
                rule.Pattern,
                AutomationProgramType.TextTrigger,
                new TextAutomationTrigger(
                    rule.Pattern,
                    rule.MatchMode,
                    rule.CaseSensitive,
                    rule.Scope,
                    rule.CooldownMilliseconds,
                    rule.StopProcessing,
                    rule.OneShot),
                rule.Conditions?.ToArray() ?? [],
                actions,
                Group: rule.Group,
                Priority: rule.Priority,
                Order: index);
        }

        foreach ((SemanticTriggerRule rule, int index) in (settings.SemanticTriggers ?? Array.Empty<SemanticTriggerRule>()).Select((value, index) => (value, index)))
        {
            AutomationAction[] actions = rule.Actions?.ToArray() ?? [];
            if (!rule.Enabled || disabledGroups.Contains(rule.Group) || string.IsNullOrWhiteSpace(rule.EventName) || actions.Length == 0) continue;
            yield return new AutomationProgram(
                ResolveId(rule.Id, "semantic", $"{rule.Name}\n{rule.EventName}\n{rule.Group}"),
                rule.Name,
                AutomationProgramType.SemanticTrigger,
                new SemanticAutomationTrigger(rule.EventName),
                rule.Conditions?.ToArray() ?? [],
                actions,
                Group: rule.Group,
                Priority: rule.Priority,
                Order: index);
        }

        foreach ((GameRule rule, int index) in (settings.GameRules ?? Array.Empty<GameRule>()).Select((value, index) => (value, index)))
        {
            IReadOnlyList<AutomationAction> actions = ResolveActions(rule.Actions, rule.Command, separator);
            if (!rule.Enabled || disabledGroups.Contains(rule.Group) || string.IsNullOrWhiteSpace(rule.Condition) || actions.Count == 0) continue;
            yield return new AutomationProgram(
                ResolveId(rule.Id, "rule", $"{rule.Name}\n{rule.Condition}\n{rule.Command}\n{rule.Activation}\n{rule.Group}"),
                rule.Name,
                AutomationProgramType.StateRule,
                new StateAutomationTrigger(
                    rule.Condition,
                    rule.Activation,
                    rule.CooldownMilliseconds,
                    rule.StopProcessing,
                    rule.OneShot),
                rule.Conditions?.ToArray() ?? [new StateExpressionAutomationCondition(rule.Condition)],
                actions,
                Group: rule.Group,
                Priority: rule.Priority,
                Order: index);
        }

        foreach ((CommandTimer timer, int index) in (settings.Timers ?? Array.Empty<CommandTimer>()).Select((value, index) => (value, index)))
        {
            IReadOnlyList<AutomationAction> actions = ResolveActions(timer.Actions, timer.Command, separator);
            if (!timer.Enabled || disabledGroups.Contains(timer.Group) || timer.IntervalSeconds <= 0 || actions.Count == 0) continue;
            yield return new AutomationProgram(
                ResolveId(timer.Id, "timer", $"{timer.Name}\n{timer.IntervalSeconds}\n{timer.Repeat}\n{timer.Command}\n{timer.Group}"),
                timer.Name,
                AutomationProgramType.Timer,
                new TimerAutomationTrigger(Math.Min(timer.IntervalSeconds, int.MaxValue / 1000) * 1000, timer.Repeat),
                timer.Conditions?.ToArray() ?? [],
                actions,
                Group: timer.Group,
                Order: index);
        }

        foreach ((CommandKeyBinding binding, int index) in (settings.KeyBindings ?? Array.Empty<CommandKeyBinding>()).Select((value, index) => (value, index)))
        {
            IReadOnlyList<AutomationAction> actions = binding.Actions is { Count: > 0 }
                ? binding.Actions.ToArray()
                : binding.ScriptAction is not null ? [binding.ScriptAction] : [];
            if (!binding.Enabled || actions.Count == 0) continue;
            yield return new AutomationProgram(
                ResolveId(binding.Id, "keybinding", $"{binding.Gesture}\n{binding.Name}\n{binding.Priority}"),
                string.IsNullOrWhiteSpace(binding.Name) ? binding.Gesture : binding.Name,
                AutomationProgramType.Keybinding,
                new KeybindingAutomationTrigger(binding.Gesture),
                binding.Conditions?.ToArray() ?? [],
                actions,
                Priority: binding.Priority,
                Order: index);
        }

        foreach ((AutomationWorkflow workflow, int index) in (settings.Workflows ?? Array.Empty<AutomationWorkflow>()).Select((value, index) => (value, index)))
        {
            AutomationAction[] actions = workflow.Actions?.ToArray() ?? [];
            if (!workflow.Enabled || disabledGroups.Contains(workflow.Group) || actions.Length == 0) continue;
            yield return new AutomationProgram(
                WorkflowProgramId(workflow),
                workflow.Name,
                AutomationProgramType.Workflow,
                new WorkflowAutomationTrigger(workflow.TriggerEvent, workflow.TriggerCondition),
                [],
                actions,
                Group: workflow.Group,
                Priority: workflow.Priority,
                Order: index);
        }
    }

    internal static AutomationProgram? FindWorkflowProgram(
        IReadOnlyList<AutomationProgram> programs,
        AutomationWorkflow workflow)
    {
        string id = WorkflowProgramId(workflow);
        return programs.FirstOrDefault(candidate =>
            candidate.Type == AutomationProgramType.Workflow &&
            candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    internal static string WorkflowProgramId(AutomationWorkflow workflow)
    {
        if (!string.IsNullOrWhiteSpace(workflow.Id)) return workflow.Id.Trim();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(workflow)));
        return $"automation.workflow.{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static IReadOnlyList<AutomationAction> ResolveActions(
        IReadOnlyList<AutomationAction>? actions,
        string command,
        string? separator)
    {
        if (actions is { Count: > 0 }) return actions.ToArray();
        return string.IsNullOrWhiteSpace(command) ? [] : [ToCommandAction(command, separator)];
    }

    private static string ResolveId(string? explicitId, string type, string key) =>
        string.IsNullOrWhiteSpace(explicitId) ? StableId(type, key) : explicitId.Trim();

    private static AutomationAction ToCommandAction(string command, string? separator)
    {
        IReadOnlyList<string> commands = CommandBatchSplitter.Split(command, separator);
        return commands.Count == 1
            ? new SendCommandAutomationAction(commands[0])
            : new SendCommandsAutomationAction(commands);
    }

    private static string StableId(string type, string key)
    {
        string canonical = $"{type}\n{key.Trim().ToLowerInvariant()}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..12];
        return $"automation.{type}.{hash}";
    }

    private static bool TryMatchAlias(
        string pattern,
        string input,
        out string tail,
        out string[] captures,
        out Dictionary<string, string> named)
    {
        tail = string.Empty;
        captures = [];
        named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] patternParts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] inputParts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patternParts.Length == 0 || inputParts.Length == 0) return false;

        bool structured = patternParts.Any(part => part.Length >= 3 && part[0] == '{' && part[^1] == '}');
        if (!structured)
        {
            if (!pattern.Equals(inputParts[0], StringComparison.OrdinalIgnoreCase)) return false;
            int firstSpace = input.IndexOf(' ');
            tail = firstSpace < 0 ? string.Empty : input[(firstSpace + 1)..];
            string[] args = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            captures = new string[Math.Min(args.Length + 1, 10)];
            captures[0] = input;
            for (int index = 0; index < Math.Min(args.Length, 9); index++) captures[index + 1] = args[index];
            return true;
        }

        int inputIndex = 0;
        List<string> captureList = [input];
        for (int patternIndex = 0; patternIndex < patternParts.Length; patternIndex++)
        {
            string token = patternParts[patternIndex];
            bool placeholder = token.Length >= 3 && token[0] == '{' && token[^1] == '}';
            if (!placeholder)
            {
                if (inputIndex >= inputParts.Length || !token.Equals(inputParts[inputIndex], StringComparison.OrdinalIgnoreCase)) return false;
                inputIndex++;
                continue;
            }

            string name = token[1..^1].Trim();
            if (name.Length == 0 || inputIndex >= inputParts.Length) return false;
            string value = patternIndex == patternParts.Length - 1
                ? string.Join(' ', inputParts[inputIndex..])
                : inputParts[inputIndex];
            named[name] = value;
            captureList.Add(value);
            inputIndex += patternIndex == patternParts.Length - 1 ? inputParts.Length - inputIndex : 1;
        }
        if (inputIndex != inputParts.Length) return false;
        tail = string.Join(' ', inputParts.Skip(1));
        captures = captureList.Take(10).ToArray();
        return true;
    }

    private static bool TryMatchText(TextAutomationTrigger trigger, string text, out string[] captures)
    {
        captures = [];
        if (trigger.MatchMode == HighlightMatchMode.Literal)
        {
            StringComparison comparison = trigger.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!text.Contains(trigger.Pattern, comparison)) return false;
            captures = [trigger.Pattern];
            return true;
        }

        try
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!trigger.CaseSensitive) options |= RegexOptions.IgnoreCase;
            Match match = Regex.Match(text, trigger.Pattern, options, RegexTimeout);
            if (!match.Success) return false;
            captures = match.Groups.Cast<Group>().Take(10).Select(group => group.Value).ToArray();
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

    private static Dictionary<string, string?> ResolveTemplateValues(
        AutomationProgram program,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables,
        IMudEvent? currentEvent)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string template in ActionTemplates(program.Actions))
        {
            foreach (Match match in TemplateRegex.Matches(template))
            {
                string key = match.Groups[1].Value.Trim();
                if (key.Length == 0 || values.ContainsKey(key)) continue;
                values[key] = GameRuleEvaluator.ResolveText(
                    key.StartsWith("var.", StringComparison.OrdinalIgnoreCase) ? key : "var." + key,
                    state,
                    variables,
                    currentEvent)
                    ?? GameRuleEvaluator.ResolveText(key, state, variables, currentEvent);
            }
        }
        return values;
    }

    private static IEnumerable<string> ActionTemplates(IEnumerable<AutomationAction> actions)
    {
        foreach (AutomationAction action in actions)
        {
            switch (action)
            {
                case SendCommandAutomationAction send:
                    yield return send.Command;
                    break;
                case SendCommandsAutomationAction batch:
                    foreach (string command in batch.Commands) yield return command;
                    break;
                case LogAutomationAction log:
                    yield return log.Message;
                    break;
            }
        }
    }
}

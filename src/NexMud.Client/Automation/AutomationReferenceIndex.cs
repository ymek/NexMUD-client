using NexMud.Client.Settings;
using NexMud.Scripting.Runtime;

namespace NexMud.Client.Automation;

public sealed record AutomationFunctionReference(
    ScriptFunctionRef FunctionRef,
    AutomationInvocationSourceKind SourceKind,
    string DefinitionId,
    string DefinitionName,
    string UsageKind,
    int? Index = null);

/// <summary>
/// Canonical ScriptFunctionRef dependency index used by Studio navigation, deletion safety and
/// coordinated refactors. References never depend on display names or source line numbers.
/// </summary>
public sealed class AutomationReferenceIndex
{
    public IReadOnlyList<AutomationFunctionReference> Build(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<AutomationFunctionReference> references = [];
        AddActions(references, settings.Aliases ?? [], x => x.Actions, x => x.Name, AutomationInvocationSourceKind.Alias);
        AddActions(references, settings.Triggers ?? [], x => x.Actions, x => x.Pattern, AutomationInvocationSourceKind.TextTrigger);
        AddConditions(references, settings.Triggers ?? [], x => x.Conditions, x => x.Pattern, AutomationInvocationSourceKind.TextTrigger);
        AddActions(references, settings.SemanticTriggers ?? [], x => x.Actions, x => x.Name, AutomationInvocationSourceKind.SemanticTrigger);
        AddConditions(references, settings.SemanticTriggers ?? [], x => x.Conditions, x => x.Name, AutomationInvocationSourceKind.SemanticTrigger);
        AddActions(references, settings.GameRules ?? [], x => x.Actions, x => x.Name, AutomationInvocationSourceKind.StateRule);
        AddConditions(references, settings.GameRules ?? [], x => x.Conditions, x => x.Name, AutomationInvocationSourceKind.StateRule);
        AddActions(references, settings.Timers ?? [], x => x.Actions, x => x.Name, AutomationInvocationSourceKind.Timer);
        AddActions(references, settings.Workflows ?? [], x => x.Actions, x => x.Name, AutomationInvocationSourceKind.Workflow);

        foreach ((CommandKeyBinding binding, int index) in (settings.KeyBindings ?? []).Select((value, index) => (value, index)))
        {
            if (binding.ScriptAction is not { } action) continue;
            references.Add(new AutomationFunctionReference(
                action.FunctionRef,
                AutomationInvocationSourceKind.Keybinding,
                $"keybinding:{index}",
                binding.Name ?? binding.Gesture,
                "Action",
                0));
        }
        return references;
    }

    public IReadOnlyList<AutomationFunctionReference> FindUses(ClientSettings settings, ScriptFunctionRef functionRef) =>
        Build(settings).Where(reference => Equals(reference.FunctionRef, functionRef)).ToArray();

    public ClientSettings RewriteFunctionRef(
        ClientSettings settings,
        ScriptFunctionRef source,
        ScriptFunctionRef destination)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return settings with
        {
            Aliases = (settings.Aliases ?? []).Select(value => value with { Actions = RewriteActions(value.Actions, source, destination) }).ToArray(),
            Triggers = (settings.Triggers ?? []).Select(value => value with
            {
                Actions = RewriteActions(value.Actions, source, destination),
                Conditions = RewriteConditions(value.Conditions, source, destination)
            }).ToArray(),
            SemanticTriggers = (settings.SemanticTriggers ?? []).Select(value => value with
            {
                Actions = RewriteActions(value.Actions, source, destination),
                Conditions = RewriteConditions(value.Conditions, source, destination)
            }).ToArray(),
            GameRules = (settings.GameRules ?? []).Select(value => value with
            {
                Actions = RewriteActions(value.Actions, source, destination),
                Conditions = RewriteConditions(value.Conditions, source, destination)
            }).ToArray(),
            Timers = (settings.Timers ?? []).Select(value => value with { Actions = RewriteActions(value.Actions, source, destination) }).ToArray(),
            Workflows = (settings.Workflows ?? []).Select(value => value with { Actions = RewriteActions(value.Actions, source, destination) }).ToArray(),
            KeyBindings = (settings.KeyBindings ?? []).Select(value => value.ScriptAction is { } action && Equals(action.FunctionRef, source)
                ? value with { ScriptAction = action with { FunctionRef = destination } }
                : value).ToArray()
        };
    }

    public ClientSettings RewriteModulePath(
        ClientSettings settings,
        string packageId,
        string sourceModulePath,
        string destinationModulePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModulePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationModulePath);
        ClientSettings current = settings;
        foreach (ScriptFunctionRef functionRef in Build(settings)
                     .Select(reference => reference.FunctionRef)
                     .Where(reference => reference.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
                                         reference.ModulePath.Equals(sourceModulePath, StringComparison.Ordinal))
                     .Distinct())
        {
            current = RewriteFunctionRef(
                current,
                functionRef,
                new ScriptFunctionRef(functionRef.PackageId, destinationModulePath, functionRef.ExportName));
        }
        return current;
    }

    private static void AddActions<T>(
        List<AutomationFunctionReference> target,
        IReadOnlyList<T> values,
        Func<T, IReadOnlyList<AutomationAction>?> actions,
        Func<T, string> name,
        AutomationInvocationSourceKind sourceKind)
    {
        for (int definitionIndex = 0; definitionIndex < values.Count; definitionIndex++)
        {
            IReadOnlyList<AutomationAction> list = actions(values[definitionIndex]) ?? [];
            for (int actionIndex = 0; actionIndex < list.Count; actionIndex++)
            {
                if (list[actionIndex] is not RunScriptFunctionAutomationAction script) continue;
                target.Add(new AutomationFunctionReference(
                    script.FunctionRef,
                    sourceKind,
                    $"{sourceKind}:{definitionIndex}",
                    name(values[definitionIndex]),
                    "Action",
                    actionIndex));
            }
        }
    }

    private static void AddConditions<T>(
        List<AutomationFunctionReference> target,
        IReadOnlyList<T> values,
        Func<T, IReadOnlyList<AutomationCondition>?> conditions,
        Func<T, string> name,
        AutomationInvocationSourceKind sourceKind)
    {
        for (int definitionIndex = 0; definitionIndex < values.Count; definitionIndex++)
        {
            int predicateIndex = 0;
            foreach (ScriptFunctionRef functionRef in EnumeratePredicates(conditions(values[definitionIndex]) ?? []))
            {
                target.Add(new AutomationFunctionReference(
                    functionRef,
                    sourceKind,
                    $"{sourceKind}:{definitionIndex}",
                    name(values[definitionIndex]),
                    "Predicate",
                    predicateIndex++));
            }
        }
    }

    private static IEnumerable<ScriptFunctionRef> EnumeratePredicates(IEnumerable<AutomationCondition> conditions)
    {
        foreach (AutomationCondition condition in conditions)
        {
            switch (condition)
            {
                case ScriptPredicateAutomationCondition predicate:
                    yield return predicate.FunctionRef;
                    break;
                case AllAutomationCondition all:
                    foreach (ScriptFunctionRef nested in EnumeratePredicates(all.Conditions)) yield return nested;
                    break;
                case AnyAutomationCondition any:
                    foreach (ScriptFunctionRef nested in EnumeratePredicates(any.Conditions)) yield return nested;
                    break;
                case NotAutomationCondition not:
                    foreach (ScriptFunctionRef nested in EnumeratePredicates([not.Condition])) yield return nested;
                    break;
            }
        }
    }

    private static IReadOnlyList<AutomationAction>? RewriteActions(
        IReadOnlyList<AutomationAction>? actions,
        ScriptFunctionRef source,
        ScriptFunctionRef destination) =>
        actions?.Select(action => action is RunScriptFunctionAutomationAction script && Equals(script.FunctionRef, source)
            ? script with { FunctionRef = destination }
            : action).ToArray();

    private static IReadOnlyList<AutomationCondition>? RewriteConditions(
        IReadOnlyList<AutomationCondition>? conditions,
        ScriptFunctionRef source,
        ScriptFunctionRef destination) =>
        conditions?.Select(condition => RewriteCondition(condition, source, destination)).ToArray();

    private static AutomationCondition RewriteCondition(
        AutomationCondition condition,
        ScriptFunctionRef source,
        ScriptFunctionRef destination) => condition switch
    {
        ScriptPredicateAutomationCondition predicate when Equals(predicate.FunctionRef, source) =>
            predicate with { FunctionRef = destination },
        AllAutomationCondition all => all with { Conditions = all.Conditions.Select(item => RewriteCondition(item, source, destination)).ToArray() },
        AnyAutomationCondition any => any with { Conditions = any.Conditions.Select(item => RewriteCondition(item, source, destination)).ToArray() },
        NotAutomationCondition not => not with { Condition = RewriteCondition(not.Condition, source, destination) },
        _ => condition
    };
}

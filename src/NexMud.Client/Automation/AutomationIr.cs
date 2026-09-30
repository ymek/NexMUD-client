using System.Text.Json;
using System.Text.Json.Serialization;
using NexMud.Client.Settings;
using NexMud.Scripting.Runtime;

namespace NexMud.Client.Automation;

public enum AutomationProgramType
{
    Alias,
    Keybinding,
    TextTrigger,
    SemanticTrigger,
    Timer,
    StateRule,
    Workflow
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AliasAutomationTrigger), "alias")]
[JsonDerivedType(typeof(KeybindingAutomationTrigger), "keybinding")]
[JsonDerivedType(typeof(TextAutomationTrigger), "text")]
[JsonDerivedType(typeof(SemanticAutomationTrigger), "semantic")]
[JsonDerivedType(typeof(TimerAutomationTrigger), "timer")]
[JsonDerivedType(typeof(StateAutomationTrigger), "state")]
[JsonDerivedType(typeof(WorkflowAutomationTrigger), "workflow")]
public abstract record AutomationTrigger;

public sealed record AliasAutomationTrigger(string Pattern) : AutomationTrigger;
public sealed record KeybindingAutomationTrigger(string Gesture) : AutomationTrigger;

public sealed record TextAutomationTrigger(
    string Pattern,
    HighlightMatchMode MatchMode,
    bool CaseSensitive,
    TriggerScope Scope,
    int CooldownMilliseconds,
    bool StopProcessing,
    bool OneShot) : AutomationTrigger;

public sealed record SemanticAutomationTrigger(string EventName) : AutomationTrigger;

public sealed record TimerAutomationTrigger(int IntervalMilliseconds, bool Repeat) : AutomationTrigger;

public sealed record StateAutomationTrigger(
    string Expression,
    GameRuleActivation Activation,
    int CooldownMilliseconds,
    bool StopProcessing,
    bool OneShot) : AutomationTrigger;

public sealed record WorkflowAutomationTrigger(string? EventName, string? Condition) : AutomationTrigger;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StateExpressionAutomationCondition), "stateExpression")]
[JsonDerivedType(typeof(EventFieldComparisonAutomationCondition), "eventFieldComparison")]
[JsonDerivedType(typeof(RegexAutomationCondition), "regex")]
[JsonDerivedType(typeof(ContainsAutomationCondition), "contains")]
[JsonDerivedType(typeof(StorageValueAutomationCondition), "storageValue")]
[JsonDerivedType(typeof(ScriptPredicateAutomationCondition), "scriptPredicate")]
[JsonDerivedType(typeof(AllAutomationCondition), "all")]
[JsonDerivedType(typeof(AnyAutomationCondition), "any")]
[JsonDerivedType(typeof(NotAutomationCondition), "not")]
public abstract record AutomationCondition;

/// <summary>
/// Compatibility bridge for the existing deterministic GameRule expression language. Matching
/// remains in C# while the migrated action execution runs in Jint. New conditions should prefer
/// the structured primitives below.
/// </summary>
public sealed record StateExpressionAutomationCondition(string Expression) : AutomationCondition;
public sealed record EventFieldComparisonAutomationCondition(string Field, string Operator, object? Value) : AutomationCondition;
public sealed record RegexAutomationCondition(string Field, string Pattern, bool CaseSensitive = false) : AutomationCondition;
public sealed record ContainsAutomationCondition(string Field, string Value, bool CaseSensitive = false) : AutomationCondition;
public sealed record StorageValueAutomationCondition(string Key, string Operator, object? Value) : AutomationCondition;
public sealed record ScriptPredicateAutomationCondition(
    ScriptFunctionRef FunctionRef,
    JsonElement Arguments) : AutomationCondition;
public sealed record AllAutomationCondition(IReadOnlyList<AutomationCondition> Conditions) : AutomationCondition;
public sealed record AnyAutomationCondition(IReadOnlyList<AutomationCondition> Conditions) : AutomationCondition;
public sealed record NotAutomationCondition(AutomationCondition Condition) : AutomationCondition;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SendCommandAutomationAction), "sendCommand")]
[JsonDerivedType(typeof(SendCommandsAutomationAction), "sendCommands")]
[JsonDerivedType(typeof(DelayAutomationAction), "delay")]
[JsonDerivedType(typeof(LogAutomationAction), "log")]
[JsonDerivedType(typeof(SetStorageAutomationAction), "setStorage")]
[JsonDerivedType(typeof(DeleteStorageAutomationAction), "deleteStorage")]
[JsonDerivedType(typeof(RunScriptFunctionAutomationAction), "runScriptFunction")]
public abstract record AutomationAction;

public sealed record SendCommandAutomationAction(string Command) : AutomationAction;
public sealed record SendCommandsAutomationAction(IReadOnlyList<string> Commands) : AutomationAction;
public sealed record DelayAutomationAction(int Milliseconds) : AutomationAction;
public sealed record LogAutomationAction(string Level, string Message) : AutomationAction;
public sealed record SetStorageAutomationAction(string Key, object? Value) : AutomationAction;
public sealed record DeleteStorageAutomationAction(string Key) : AutomationAction;
public sealed record RunScriptFunctionAutomationAction(
    ScriptFunctionRef FunctionRef,
    JsonElement Arguments,
    ScriptFunctionFailurePolicy FailurePolicy = ScriptFunctionFailurePolicy.StopCurrentAutomation) : AutomationAction;

public sealed record AutomationProgram(
    string Id,
    string Name,
    AutomationProgramType Type,
    AutomationTrigger Trigger,
    IReadOnlyList<AutomationCondition> Conditions,
    IReadOnlyList<AutomationAction> Actions,
    bool Enabled = true,
    string Group = "Default",
    int Priority = 0,
    int Order = 0);

public sealed record AutomationSourceMapEntry(
    string AutomationId,
    string AutomationName,
    AutomationProgramType AutomationType,
    int? ConditionIndex = null,
    int? ActionIndex = null);

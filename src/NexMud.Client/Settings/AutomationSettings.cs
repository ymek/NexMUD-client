using NexMud.Client.Automation;
using NexMud.Client.Interaction;

namespace NexMud.Client.Settings;

public enum TranscriptLogFormat
{
    PlainText,
    AnsiText,
    JsonLines
}

public enum TriggerScope
{
    Line,
    RollingBuffer
}

public enum GameRuleActivation
{
    WhileTrue,
    OnEnter,
    OnExit
}

public enum AutomationWorkflowFailureMode
{
    Stop,
    Continue
}

public sealed record CommandAlias(
    string Name,
    string Expansion,
    bool Enabled = true,
    IReadOnlyList<AutomationAction>? Actions = null,
    string? Id = null,
    IReadOnlyList<AutomationCondition>? Conditions = null);

public sealed record TriggerRule(
    string Pattern,
    string Command,
    HighlightMatchMode MatchMode = HighlightMatchMode.Literal,
    bool CaseSensitive = false,
    bool Enabled = true,
    string Group = "Default",
    int Priority = 0,
    int CooldownMilliseconds = 250,
    bool StopProcessing = false,
    TriggerScope Scope = TriggerScope.Line,
    bool OneShot = false,
    IReadOnlyList<AutomationCondition>? Conditions = null,
    IReadOnlyList<AutomationAction>? Actions = null,
    string? Id = null);

public sealed record SemanticTriggerRule(
    string Name,
    string EventName,
    bool Enabled = true,
    string Group = "Default",
    int Priority = 0,
    IReadOnlyList<AutomationCondition>? Conditions = null,
    IReadOnlyList<AutomationAction>? Actions = null,
    string? Id = null);

public sealed record GameRule(
    string Name,
    string Condition,
    string Command,
    bool Enabled = true,
    string Group = "Default",
    int CooldownMilliseconds = 1000,
    int Priority = 0,
    bool StopProcessing = false,
    GameRuleActivation Activation = GameRuleActivation.OnEnter,
    bool OneShot = false,
    IReadOnlyList<AutomationCondition>? Conditions = null,
    IReadOnlyList<AutomationAction>? Actions = null,
    string? Id = null);

/// <summary>
/// A first-party deterministic workflow. Steps use a deliberately small declarative DSL and may
/// finish with typed Automation actions. Script calls use the same RunScriptFunction action model
/// as aliases, keybindings, triggers, timers and state rules.
/// </summary>
public sealed record AutomationWorkflow(
    string Name,
    string Steps,
    string? TriggerCondition = null,
    string? TriggerEvent = null,
    bool Enabled = true,
    string Group = "Default",
    int Priority = 0,
    int CooldownMilliseconds = 1000,
    AutomationWorkflowFailureMode FailureMode = AutomationWorkflowFailureMode.Stop,
    bool OneShot = false,
    IReadOnlyList<AutomationAction>? Actions = null,
    string? Id = null);

public sealed record CommandTimer(
    string Name,
    int IntervalSeconds,
    string Command,
    bool Repeat = true,
    bool Enabled = false,
    string Group = "Default",
    IReadOnlyList<AutomationAction>? Actions = null,
    string? Id = null,
    IReadOnlyList<AutomationCondition>? Conditions = null);

public sealed record CommandKeyBinding(
    string Gesture,
    string Command,
    bool Enabled = true,
    string? Name = null,
    KeybindingContext Context = KeybindingContext.Global,
    KeybindingActionKind Action = KeybindingActionKind.SendCommand,
    int Priority = 0,
    RunScriptFunctionAutomationAction? ScriptAction = null,
    string? Id = null,
    IReadOnlyList<AutomationCondition>? Conditions = null,
    IReadOnlyList<AutomationAction>? Actions = null);

public sealed record AutomationPreferences(
    bool Enabled = true,
    int MaxCommandsPerSecond = 8,
    int RollingBufferCharacters = 8192,
    IReadOnlyList<string>? DisabledGroups = null,
    int HumanOverrideMilliseconds = 1500,
    int MaxConcurrentWorkflows = 2,
    bool PersistVariables = true)
{
    public IReadOnlySet<string> GetDisabledGroupSet() => new HashSet<string>(
        DisabledGroups ?? Array.Empty<string>(),
        StringComparer.OrdinalIgnoreCase);
}

public sealed record ProtocolPreferences(
    bool Naws = true,
    bool Gmcp = true,
    bool Msdp = true,
    bool Mssp = true,
    bool Mccp2 = true,
    bool Charset = true,
    bool NewEnvironment = true,
    bool Mtts = true,
    bool Eor = true);

public sealed record MapperPreferences(
    bool Enabled = true,
    bool AutoMap = true,
    bool PersistKnowledge = true,
    int MaximumRouteDepth = 250,
    bool AvoidBlockedExits = true,
    bool AllowUnknownTraversability = true,
    bool AutoMoveEnabled = true,
    bool StopAutoMoveOnCombat = true,
    bool ResumeAutoMoveAfterCombat = false,
    bool ReplanAutoMoveOnDeviation = true,
    int AutoMoveStepDelayMilliseconds = 120,
    int AutoMoveStepTimeoutMilliseconds = 8000,
    int AutoMoveMaximumReplans = 3,
    int VisualMapDepth = 8,
    int VisualMapMaximumRooms = 120,
    IReadOnlyList<string>? AvoidAreas = null,
    IReadOnlyList<string>? AvoidTerrains = null,
    IReadOnlyList<string>? AvoidMobNames = null,
    bool AvoidClosedDoors = false,
    bool PreferKnownTraversableExits = true,
    bool AutoOpenDoors = false,
    string DoorOpenCommandTemplate = "open {direction}")
{
    public IReadOnlySet<string> GetAvoidAreaSet() => new HashSet<string>(
        AvoidAreas ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> GetAvoidTerrainSet() => new HashSet<string>(
        AvoidTerrains ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> GetAvoidMobSet() => new HashSet<string>(
        AvoidMobNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
}

public sealed record WorkspacePreferences(
    double WindowWidth = 1500,
    double WindowHeight = 960,
    bool DockVisible = true,
    double DockWidth = 360,
    string DockView = "Context",
    double LiveSplitHeight = 190);

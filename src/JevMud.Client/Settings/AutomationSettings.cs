namespace JevMud.Client.Settings;

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
    bool Enabled = true);

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
    bool OneShot = false);

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
    bool OneShot = false);

/// <summary>
/// A first-party deterministic workflow. Steps use a deliberately small declarative DSL:
/// send &lt;command&gt;
/// delay &lt;milliseconds&gt;
/// wait &lt;state expression&gt; [timeout=&lt;milliseconds&gt;]
/// wait-event &lt;event type&gt; [timeout=&lt;milliseconds&gt;]
/// if &lt;state expression&gt; :: &lt;step&gt;
/// unless &lt;state expression&gt; :: &lt;step&gt;
/// retry &lt;count&gt; [delay=&lt;milliseconds&gt;] :: &lt;step&gt;
/// assert &lt;state expression&gt;
/// navigate &lt;room/MOB/object/item-source query&gt;
/// set &lt;name&gt;=&lt;value&gt;
/// unset &lt;name&gt;
/// jev combat|navigation|recovery
/// stop
///
/// Step arguments support ${...} templates using workflow variables, state expressions, and
/// fields from the triggering/waited event such as ${event.target} and ${event.item}.
///
/// TriggerCondition uses the same state expression language as GameRule. TriggerEvent may be
/// an IMudEvent type name (for example EnemyKilled) or "*". If both are supplied both must match.
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
    bool OneShot = false);

public sealed record CommandTimer(
    string Name,
    int IntervalSeconds,
    string Command,
    bool Repeat = true,
    bool Enabled = false,
    string Group = "Default");

public sealed record CommandKeyBinding(
    string Gesture,
    string Command,
    bool Enabled = true);

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

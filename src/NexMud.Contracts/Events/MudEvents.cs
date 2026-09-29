using NexMud.Contracts.Actions;
using NexMud.Contracts.Jev;
using NexMud.Contracts.Gameplay;
using NexMud.Contracts.State;

namespace NexMud.Contracts.Events;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected
}

public sealed record ConnectionStateChanged(
    ConnectionStatus Status,
    string? Host,
    int? Port,
    string? Reason = null) : IMudEvent;

public sealed record SessionInputModeChanged(SessionInputMode Mode) : IMudEvent;
public sealed record ResponseCaptureChanged(ResponseCaptureKind Capture) : IMudEvent;

public sealed record TextReceived(string Text) : IMudEvent;
public sealed record GameTextReceived(string Text) : IMudEvent;
public sealed record GameObservationReceived(GameObservation Observation) : IMudEvent;

public sealed record GmcpMessageReceived(string Module, string Payload) : IMudEvent;
public sealed record MsdpMessageReceived(IReadOnlyDictionary<string, NexMud.Contracts.Transport.MsdpValue> Values) : IMudEvent;
public sealed record MsspMessageReceived(IReadOnlyDictionary<string, IReadOnlyList<string>> Values) : IMudEvent;
public sealed record ProtocolStateChanged(string Protocol, bool Enabled, string? Detail = null) : IMudEvent;
public sealed record ProtocolPromptBoundaryReceived(string Kind) : IMudEvent;
public sealed record ProtocolError(string Protocol, string Message, bool Fatal = false) : IMudEvent;
public sealed record TransportError(string Message) : IMudEvent;
public sealed record ComponentError(string Component, string Message) : IMudEvent;

public sealed record CharacterVitalsChanged(
    int? HitPoints,
    int? MaxHitPoints,
    int? Mana,
    int? MaxMana,
    int? Movement,
    int? MaxMovement) : IMudEvent;

public sealed record CharacterPositionObserved(string Position) : IMudEvent;
public sealed record CharacterPromptSnapshotObserved(CharacterPromptSnapshot Snapshot) : IMudEvent;
public sealed record GroupSnapshotObserved(GroupSnapshot Snapshot) : IMudEvent;
public sealed record ActiveEffectsSnapshotObserved(ActiveEffectsSnapshot Snapshot) : IMudEvent;

public sealed record CharacterPromptObserved(
    int HitPoints,
    int MaxHitPoints,
    int Mana,
    int MaxMana,
    int Movement,
    int MaxMovement,
    long Experience,
    long ExperienceToLevel,
    string Position,
    string RoomName,
    ExitState Exits,
    string Terrain,
    string Light) : IMudEvent
{
    public long SourceSequence { get; init; }
}

public sealed record CharacterScoreObserved(
    CharacterProfileState Profile,
    IReadOnlyDictionary<string, AttributeScore> Attributes,
    VitalState HitPoints,
    VitalState Mana,
    VitalState Movement,
    long? Experience,
    long? ExperienceToLevel,
    int? Exploration,
    CharacterCombatStats CombatStats,
    InventorySummary Inventory,
    IReadOnlyList<string>? Effects,
    IReadOnlyList<string>? Conditions = null) : IMudEvent;

public sealed record SkillsSnapshotObserved(
    IReadOnlyList<SkillState> Skills,
    ObservationCompleteness Completeness = ObservationCompleteness.Complete) : IMudEvent;

public sealed record SpellsSnapshotObserved(
    IReadOnlyList<SpellState> Spells,
    ObservationCompleteness Completeness = ObservationCompleteness.Complete) : IMudEvent;
public sealed record SkillImproved(string SkillName, int? ExperienceAmount = null) : IMudEvent;
public sealed record SkillPracticeSucceeded(string SkillName) : IMudEvent;

public sealed record EquipmentChanged(string Slot, string? Item) : IMudEvent;
public sealed record EquipmentSnapshotObserved(
    IReadOnlyList<EquipmentSlotState> Slots,
    ObservationCompleteness Completeness = ObservationCompleteness.Complete) : IMudEvent;
public sealed record InventorySnapshotObserved(
    IReadOnlyList<string> Items,
    ObservationCompleteness Completeness = ObservationCompleteness.Complete) : IMudEvent;
public sealed record ItemIdentified(ItemIdentification Item) : IMudEvent;
public enum ItemAcquisitionSourceKind
{
    Unknown,
    Corpse,
    MobDrop
}

public sealed record ItemAcquired(
    string ItemName,
    string? SourceDescription,
    ItemAcquisitionSourceKind SourceKind,
    string RawText) : IMudEvent;

public sealed record AbilityHelpObserved(AbilityHelpDocument Help) : IMudEvent;
public sealed record CharacterConditionChanged(string Condition, bool Active) : IMudEvent;

public sealed record AreaObserved(string Area) : IMudEvent;

public sealed record RoomChanged(
    string? Id,
    string? Name,
    IReadOnlyList<string> Exits) : IMudEvent;

public sealed record RoomObservationObserved(
    string RoomId,
    string RoomName,
    string DescriptionFingerprint,
    string Description,
    IReadOnlyList<RoomExitObservation> ExitDetails,
    IReadOnlyList<RoomContentObservation> Contents,
    ObservationCompleteness ContentsCompleteness) : IMudEvent
{
    public IReadOnlyList<string> RecentObservations { get; init; } = Array.Empty<string>();
    public Guid ObservationId { get; init; }
    public RoomVisibilityQuality VisibilityQuality { get; init; } = RoomVisibilityQuality.Normal;
    public long SourceSequence { get; init; }
}

// Retained for compatibility with focused tests and callers which only have post-exit contents.
public sealed record RoomContentsObserved(
    string RoomName,
    IReadOnlyList<RoomContentObservation> Occupants,
    IReadOnlyList<RoomContentObservation> Objects,
    IReadOnlyList<RoomContentObservation> UnknownContents) : IMudEvent;

public sealed record RoomOccupantDeparted(string TargetName, string? Direction) : IMudEvent;
public sealed record MovementObserved(MovementObservation Movement) : IMudEvent;
public sealed record ScanUpdated(ScanObservation Scan) : IMudEvent;
public sealed record GameCommandQueueCleared(long SourceSequence) : IMudEvent;
public sealed record NavigationAttempted(string Direction, Guid? ActionId = null) : IMudEvent;
public sealed record NavigationResponseCompleted(Guid ActionId, string Direction, bool RoomObserved) : IMudEvent;
public sealed record NavigationFailed(string? Direction, string Reason) : IMudEvent;
public sealed record RoomExitStateChanged(
    string Direction,
    ExitDoorState DoorState,
    ExitTraversability Traversability,
    string? BlockReason = null) : IMudEvent;

public enum AutoMoveStatus
{
    Idle,
    Planning,
    Moving,
    Paused,
    Recovering,
    Replanning,
    Completed,
    Aborted,
    Stopped,
    Failed
}

public sealed record AutoMoveStateChanged(
    AutoMoveStatus Status,
    string? DestinationRoomId,
    string? DestinationLabel,
    int CompletedSteps,
    int TotalSteps,
    string? CurrentDirection,
    string? Reason = null) : IMudEvent;

public enum MapperRouteLifecycleKind
{
    Started,
    Planned,
    StepStarted,
    StepCompleted,
    Blocked,
    Replanning,
    Paused,
    Resumed,
    Completed,
    Aborted,
    Failed
}

public enum MapperRouteFailureReason
{
    NoCurrentRoom,
    NoPath,
    MovementBlocked,
    MovementTimeout,
    Disconnected,
    RecoveryExhausted,
    MapperFault,
    ScriptFault,
    Cancelled
}

public sealed record MapperRouteLifecycleChanged(
    MapperRouteLifecycleKind Kind,
    Guid RouteExecutionId,
    string DestinationRoomId,
    string? DestinationLabel,
    string? RouteId = null,
    int CompletedSteps = 0,
    int TotalSteps = 0,
    int? RouteStep = null,
    string? Direction = null,
    string? Reason = null,
    MapperRouteFailureReason? FailureReason = null) : IMudEvent;

public enum AutomationWorkflowStatus
{
    Started,
    Waiting,
    Running,
    Completed,
    Cancelled,
    Failed
}

public sealed record AutomationRuleMatched(string RuleName, string RuleKind, string? Command = null) : IMudEvent;
public sealed record AutomationVariableChanged(string Name, string? Value) : IMudEvent;
public sealed record AutomationWorkflowStateChanged(
    string WorkflowName,
    AutomationWorkflowStatus Status,
    int StepIndex,
    int TotalSteps,
    string? Detail = null) : IMudEvent;

public sealed record CombatStateChanged(bool Active, string? TargetId) : IMudEvent;
public sealed record CombatDamageObserved(DamageObservation Damage) : IMudEvent;
public sealed record CombatAttackObserved(CombatAttackObservation Attack) : IMudEvent;
public sealed record CombatTargetConditionObserved(string TargetName, string Condition) : IMudEvent
{
    public ConditionRange? Range { get; init; }
    public long SourceSequence { get; init; }
}
public sealed record EnemyKilled(string TargetName) : IMudEvent;
public sealed record CorpseDestroyed(string TargetName) : IMudEvent;
public sealed record EffectStateChanged(string Effect, bool Active) : IMudEvent;

public sealed record ExperienceGained(int Amount) : IMudEvent;
public sealed record ExplorationGained(int Amount, int ExperienceAmount) : IMudEvent;
public sealed record CurrencyGained(string Currency, int Amount) : IMudEvent;
public sealed record CharacterPromoted(int HitPointGain, int ManaGain, int MovementGain) : IMudEvent;
public sealed record CorpseHarvested(string ItemName, string CorpseDescription, long SourceSequence) : IMudEvent;
public sealed record CorpseSacrificed(string ItemName, string? Deity, long SourceSequence) : IMudEvent;
public sealed record CharacterStatusObserved(string Status, bool Active, long SourceSequence) : IMudEvent;
public sealed record PagerPromptObserved : IMudEvent;

public sealed record CommunicationObserved(
    string Channel,
    string? Speaker,
    string Message) : IMudEvent;

public sealed record JevAuthorityProfileChanged(JevAuthoritySnapshot Snapshot) : IMudEvent;
public sealed record JevEnabledChanged(bool Enabled) : IMudEvent;
public sealed record JevEvaluationStarted(Guid DecisionId, JevDomain Domain, long StateVersion) : IMudEvent;
public sealed record JevEvaluationCompleted(Guid DecisionId, TimeSpan Latency) : IMudEvent;
public sealed record JevEvaluationFailed(Guid DecisionId, TimeSpan Latency, string Message) : IMudEvent;
public sealed record JevDecisionProduced(JevDecisionTrace Decision) : IMudEvent;
public sealed record JevApprovalRequested(Guid DecisionId, JevDomain Domain, string Action, string? Command) : IMudEvent;
public sealed record JevApprovalResolved(Guid DecisionId, bool Approved, string? Reason = null) : IMudEvent;
public sealed record JevDecisionExecutionSkipped(Guid DecisionId, string Reason) : IMudEvent;

public sealed record ActionValidated(
    Guid ActionId,
    long DecisionStateVersion,
    long CurrentStateVersion) : IMudEvent;

public sealed record ActionDispatching(
    Guid ActionId,
    string Command,
    bool Sensitive = false,
    DecisionSource Source = DecisionSource.Human,
    CommandProvenance? Provenance = null) : IMudEvent;
public sealed record ActionExecuted(
    Guid ActionId,
    string Command,
    bool Sensitive = false,
    DecisionSource Source = DecisionSource.Human,
    CommandProvenance? Provenance = null) : IMudEvent;
public sealed record ActionRejected(Guid ActionId, string Reason) : IMudEvent;

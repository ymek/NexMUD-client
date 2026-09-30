using System.Collections.ObjectModel;
using NexMud.Contracts.Events;
using NexMud.Contracts.Jev;
using NexMud.Contracts.Gameplay;

namespace NexMud.Contracts.State;

public enum SessionInputMode
{
    Unknown,
    LoginName,
    LoginPassword,
    Normal,
    Pager,
    Editor
}

public enum ResponseCaptureKind
{
    None,
    Score,
    Skills,
    Spells,
    Equipment,
    Inventory,
    ItemIdentification,
    AbilityHelp,
    Where,
    Group,
    Effects,
    Scan
}

public sealed record SessionState(
    ConnectionStatus ConnectionStatus,
    string? Host,
    int? Port,
    string? LastError,
    SessionInputMode InputMode)
{
    public ResponseCaptureKind ActiveCapture { get; init; } = ResponseCaptureKind.None;
}

public sealed record VitalState(int? Current, int? Maximum);

public sealed record CharacterProfileState(
    string? Name,
    string? Title,
    string? Lineage,
    string? ClassName,
    int? Level,
    string? Gender,
    int? Age,
    string? AgeDescriptor,
    int? Hours,
    string? Resonance,
    string? Alignment);

public sealed record AttributeScore(int Current, int Maximum)
{
    /// <summary>Avendar's parenthesized/base attribute value. Maximum is retained for compatibility.</summary>
    public int BaseValue => Maximum;
}

public sealed record CharacterCombatStats(
    int? Hitroll,
    int? Damroll,
    int? Saves,
    int? ArmorClass,
    string? ArmorClassDescriptor);

public sealed record InventorySummary(
    int? Items,
    int? MaxItems,
    int? Weight,
    int? MaxWeight,
    int? Copper,
    int? Silver = null,
    int? Gold = null);

public enum SkillAvailability
{
    Available,
    Unavailable
}

public enum AbilityDomain
{
    Unknown,
    Combat,
    Recovery,
    Navigation,
    Inventory,
    Loot,
    Social,
    Training,
    Utility,
    Quest
}

public sealed record SkillState(
    string Name,
    int RequiredLevel,
    SkillAvailability Availability,
    int? ProficiencyPercent,
    bool IsFresh = true,
    AbilityDomain Domain = AbilityDomain.Unknown);

public sealed record SpellState(
    string Name,
    int RequiredLevel,
    SkillAvailability Availability,
    int? ProficiencyPercent,
    bool IsFresh = true,
    AbilityDomain Domain = AbilityDomain.Unknown);

public sealed record EquipmentSlotState(
    string Slot,
    int Ordinal,
    string? Item)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Item);
}

public sealed record EquipmentState(
    string? MainHand,
    IReadOnlyDictionary<string, string> Worn)
{
    public IReadOnlyList<EquipmentSlotState> Slots { get; init; } = Array.Empty<EquipmentSlotState>();
    public ObservationCompleteness Completeness { get; init; } = ObservationCompleteness.Unknown;
    public IReadOnlyDictionary<string, ItemIdentification> IdentifiedItems { get; init; } =
        new ReadOnlyDictionary<string, ItemIdentification>(new Dictionary<string, ItemIdentification>(StringComparer.OrdinalIgnoreCase));
}

public enum AbilityHelpKind
{
    Unknown,
    Skill,
    Spell
}

public sealed record ItemProperty(string Name, string Value);

public sealed record ItemIdentification(
    string Name,
    IReadOnlyList<string> Flags,
    decimal? Weight,
    IReadOnlyList<string> WearLocations,
    int? Level,
    string? Material,
    string? ItemType,
    string? WeaponType,
    IReadOnlyList<string> WeaponFlags,
    string? DamageType,
    string? DamageDice,
    decimal? DamageAverage,
    IReadOnlyDictionary<string, string> ExtraFields,
    string RawText)
{
    public string DisplayName => Name;
    public string? Size { get; init; }
    public IReadOnlyList<string> Spells { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ItemProperty> Properties { get; init; } = Array.Empty<ItemProperty>();
    public IReadOnlyDictionary<string, string> RawFields => ExtraFields;
    public long SourceSequence { get; init; }
}

public sealed record AbilityHelpDocument(
    string Name,
    AbilityHelpKind Kind,
    decimal? ActivationLagRounds,
    int? ActivationManaCost,
    string? Syntax,
    string Description,
    IReadOnlyDictionary<string, string> Fields,
    string RawText);

public sealed record CharacterState(
    VitalState HitPoints,
    VitalState Mana,
    VitalState Movement,
    long? Experience,
    long? ExperienceToLevel,
    int? Exploration,
    string? Position,
    CharacterProfileState Profile,
    IReadOnlyDictionary<string, AttributeScore> Attributes,
    CharacterCombatStats CombatStats,
    InventorySummary Inventory,
    IReadOnlyList<string> Effects,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<SkillState> Skills,
    EquipmentState Equipment)
{
    public IReadOnlyList<string> CarriedItems { get; init; } = Array.Empty<string>();
    public ObservationCompleteness InventoryCompleteness { get; init; } = ObservationCompleteness.Unknown;
    public IReadOnlyList<SpellState> Spells { get; init; } = Array.Empty<SpellState>();
    public ObservationCompleteness SkillsCompleteness { get; init; } = ObservationCompleteness.Unknown;
    public ObservationCompleteness SpellsCompleteness { get; init; } = ObservationCompleteness.Unknown;
    public DateTimeOffset? SkillsObservedAt { get; init; }
    public DateTimeOffset? SpellsObservedAt { get; init; }
    public CharacterPromptSnapshot? PromptSnapshot { get; init; }
    public GroupSnapshot? Group { get; init; }
    public IReadOnlyList<ActiveEffect> ActiveEffects { get; init; } = Array.Empty<ActiveEffect>();
    public long LastObservedSequence { get; init; }
}


public enum ExitDoorState
{
    Unknown,
    Open,
    Closed,
    Locked
}

public enum ExitTraversability
{
    Unknown,
    Traversable,
    Blocked
}

public sealed record RoomExitObservation(
    string Direction,
    bool Exists,
    ExitDoorState DoorState,
    ExitTraversability Traversability,
    string? BlockReason = null)
{
    public string? RawToken { get; init; }
    public IReadOnlyList<string> Qualifiers { get; init; } = Array.Empty<string>();
}

public sealed record ExitState(
    bool IsKnown,
    IReadOnlyList<string> Directions,
    IReadOnlyList<RoomExitObservation>? Details = null);

public enum ObservationCompleteness
{
    Unknown,
    Partial,
    Complete
}

public enum RoomEntityKind
{
    Occupant,
    Object,
    Fixture,
    Corpse,
    Unknown
}

[Flags]
public enum RoomEntityTraits
{
    None = 0,
    Mobile = 1 << 0,
    Readable = 1 << 1,
    Examinable = 1 << 2,
    Container = 1 << 3,
    Drinkable = 1 << 4,
    Lootable = 1 << 5,
    Sacrificable = 1 << 6,
    Fixture = 1 << 7,
    Corpse = 1 << 8
}

public sealed record RoomContentObservation(
    string Description,
    string? CanonicalName = null,
    RoomEntityKind Kind = RoomEntityKind.Unknown,
    RoomEntityTraits Traits = RoomEntityTraits.None,
    IReadOnlyList<string>? TargetKeywords = null,
    IReadOnlyList<string>? Decorators = null)
{
    public int Count { get; init; } = 1;
    public Guid OccurrenceId { get; init; }
    public IReadOnlyList<string> StateFlags { get; init; } = Array.Empty<string>();
}

public sealed record RoomState(
    string? Id,
    string? Name,
    string? DescriptionFingerprint,
    string? Description,
    ExitState Exits,
    string? Terrain,
    string? Light,
    ObservationCompleteness ContentsCompleteness,
    IReadOnlyList<RoomContentObservation> Contents)
{
    public IReadOnlyList<string> RecentObservations { get; init; } = Array.Empty<string>();
    public RoomVisibilityQuality VisibilityQuality { get; init; } = RoomVisibilityQuality.Partial;
    public long LastObservedSequence { get; init; }
    public DateTimeOffset LastObservedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public ResolutionQuality ResolutionQuality { get; init; } = ResolutionQuality.Unknown;

    public IReadOnlyList<RoomContentObservation> Occupants =>
        Contents.Where(content => content.Kind == RoomEntityKind.Occupant).ToArray();

    public IReadOnlyList<RoomContentObservation> Objects =>
        Contents.Where(content => content.Kind == RoomEntityKind.Object).ToArray();

    public IReadOnlyList<RoomContentObservation> Interactables =>
        Contents.Where(content => content.Kind == RoomEntityKind.Fixture).ToArray();

    public IReadOnlyList<RoomContentObservation> Corpses =>
        Contents.Where(content => content.Kind == RoomEntityKind.Corpse).ToArray();

    public IReadOnlyList<RoomContentObservation> UnknownContents =>
        Contents.Where(content => content.Kind == RoomEntityKind.Unknown).ToArray();
}

public sealed record RoomNodeState(
    string Id,
    string Name,
    string? DescriptionFingerprint,
    string? Description,
    IReadOnlyList<RoomExitObservation> Exits,
    DateTimeOffset LastObservedAt);

public sealed record RoomEdgeState(
    string FromRoomId,
    string Direction,
    string ToRoomId,
    DateTimeOffset ObservedAt);

public sealed record WorldMapState(
    IReadOnlyDictionary<string, RoomNodeState> Rooms,
    IReadOnlyList<RoomEdgeState> Edges,
    IReadOnlyList<string> PendingDirections)
{
    public string? PendingDirection => PendingDirections.Count == 0 ? null : PendingDirections[0];
}

public enum CombatActor
{
    Player,
    Opponent
}

public sealed record DamageObservation(
    CombatActor Source,
    string SourceName,
    string TargetName,
    string DamageType,
    string? AbsoluteTerm,
    int? AbsoluteTier,
    string RelativeTerm,
    int RelativeTier);

public enum AttackOutcome
{
    Miss,
    Dodged
}

public sealed record CombatAttackObservation(
    CombatActor Source,
    string SourceName,
    string TargetName,
    string? AttackType,
    AttackOutcome Outcome);

public sealed record CombatState(
    bool Active,
    string? TargetId,
    string? TargetName,
    string? TargetCondition,
    DamageObservation? LastPlayerDamage,
    DamageObservation? LastOpponentDamage,
    CombatAttackObservation? LastAvoidedAttack)
{
    public ConditionRange? TargetConditionRange { get; init; }
    public long LastObservedSequence { get; init; }
    public DateTimeOffset LastObservedAt { get; init; } = DateTimeOffset.UnixEpoch;
    public ResolutionQuality ResolutionQuality { get; init; } = ResolutionQuality.Unknown;
}

public sealed record StateSnapshot(
    long Version,
    DateTimeOffset Timestamp,
    SessionState Session,
    CharacterState Character,
    RoomState Room,
    WorldMapState World,
    CombatState Combat,
    JevAuthoritySnapshot JevAuthority)
{
    public ScanObservation? LastScan { get; init; }
    public MovementObservation? LastMovement { get; init; }
    public long LastSourceSequence { get; init; }

    public static StateSnapshot Initial { get; } = new(
        Version: 0,
        Timestamp: DateTimeOffset.UnixEpoch,
        Session: new SessionState(ConnectionStatus.Disconnected, null, null, null, SessionInputMode.Unknown),
        Character: new CharacterState(
            new VitalState(null, null),
            new VitalState(null, null),
            new VitalState(null, null),
            null,
            null,
            null,
            null,
            new CharacterProfileState(null, null, null, null, null, null, null, null, null, null, null),
            EmptyAttributes(),
            new CharacterCombatStats(null, null, null, null, null),
            new InventorySummary(null, null, null, null, null, null, null),
            EmptyStrings(),
            EmptyStrings(),
            EmptySkills(),
            new EquipmentState(null, EmptyStringDictionary())),
        Room: new RoomState(
            null,
            null,
            null,
            null,
            new ExitState(false, EmptyStrings()),
            null,
            null,
            ObservationCompleteness.Unknown,
            EmptyRoomContents()),
        World: new WorldMapState(EmptyRooms(), EmptyRoomEdges(), EmptyStrings()),
        Combat: new CombatState(false, null, null, null, null, null, null),
        JevAuthority: JevAuthoritySnapshot.Create(
            JevPreset.Off,
            Enum.GetValues<JevDomain>().ToDictionary(domain => domain, _ => global::NexMud.Contracts.Jev.JevAuthority.Off)));

    private static IReadOnlyList<string> EmptyStrings() =>
        new ReadOnlyCollection<string>(Array.Empty<string>());

    private static IReadOnlyList<SkillState> EmptySkills() =>
        new ReadOnlyCollection<SkillState>(Array.Empty<SkillState>());

    private static IReadOnlyList<RoomContentObservation> EmptyRoomContents() =>
        new ReadOnlyCollection<RoomContentObservation>(Array.Empty<RoomContentObservation>());

    private static IReadOnlyList<RoomEdgeState> EmptyRoomEdges() =>
        new ReadOnlyCollection<RoomEdgeState>(Array.Empty<RoomEdgeState>());

    private static IReadOnlyDictionary<string, AttributeScore> EmptyAttributes() =>
        new ReadOnlyDictionary<string, AttributeScore>(
            new Dictionary<string, AttributeScore>(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string> EmptyStringDictionary() =>
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, RoomNodeState> EmptyRooms() =>
        new ReadOnlyDictionary<string, RoomNodeState>(
            new Dictionary<string, RoomNodeState>(StringComparer.Ordinal));
}

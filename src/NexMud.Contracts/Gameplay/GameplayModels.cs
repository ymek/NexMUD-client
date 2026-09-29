using System.Collections.ObjectModel;

namespace NexMud.Contracts.Gameplay;

public enum ObservationKind
{
    Text,
    PromptCandidate,
    ProtocolEvent,
    LocalCommandEcho,
    ConnectionEvent,
    ReplayMarker
}

public sealed record GameAnsiColor(byte Red, byte Green, byte Blue);

public sealed record GameAnsiStyle(
    GameAnsiColor? Foreground,
    GameAnsiColor? Background,
    bool Bold,
    bool Faint,
    bool Italic,
    bool Underline,
    bool Blink,
    bool Reverse,
    bool Strikethrough)
{
    public static GameAnsiStyle Default { get; } =
        new(null, null, false, false, false, false, false, false, false);
}

public sealed record GameAnsiRun(string Text, GameAnsiStyle Style);

public sealed record ObservationMetadata(
    bool IsPrompt = false,
    bool IsLocal = false,
    string? Protocol = null,
    IReadOnlyDictionary<string, string>? Fields = null)
{
    public IReadOnlyDictionary<string, string> EffectiveFields =>
        Fields ?? new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record GameObservation(
    long Sequence,
    DateTimeOffset ReceivedAt,
    string SessionId,
    ObservationKind Kind,
    string RawText,
    string PlainText,
    IReadOnlyList<GameAnsiRun> AnsiRuns,
    ObservationMetadata Metadata);

public sealed record ResourceValue(int Current, int Maximum);

public sealed record GameClock(int Hour, int Minute)
{
    public override string ToString() => $"{Hour}:{Minute:00}";
}

public sealed record CharacterPromptSnapshot(
    ResourceValue? Health,
    ResourceValue? Mana,
    ResourceValue? Movement,
    long? Experience = null,
    long? ExperienceToLevel = null,
    long? ExplorationPoints = null,
    GameClock? GameClock = null,
    string? Position = null,
    string? RoomName = null,
    string? Terrain = null,
    string? Light = null,
    IReadOnlyList<string>? UnknownFields = null,
    long SourceSequence = 0)
{
    public IReadOnlyList<string> EffectiveUnknownFields =>
        UnknownFields ?? Array.Empty<string>();
}

public sealed record GroupMemberSnapshot(
    string DisplayName,
    int Level,
    string ClassCode,
    ResourceValue Health,
    ResourceValue Mana,
    ResourceValue Movement,
    long ObservedSequence);

public sealed record GroupSnapshot(
    string? LeaderName,
    IReadOnlyList<GroupMemberSnapshot> Members,
    long SourceSequence);

public enum ActiveEffectKind
{
    Unknown,
    Spell,
    Skill
}

public enum EffectDurationKind
{
    Unknown,
    Hours,
    Permanent
}

public sealed record EffectDuration(EffectDurationKind Kind, decimal? Hours = null)
{
    public static EffectDuration Unknown { get; } = new(EffectDurationKind.Unknown);
    public static EffectDuration Permanent { get; } = new(EffectDurationKind.Permanent);
}

public sealed record EffectModifier(string Attribute, string Value);

public sealed record ActiveEffect(
    string Name,
    ActiveEffectKind Kind,
    EffectDuration Duration,
    IReadOnlyList<EffectModifier> Modifiers,
    long SourceSequence);

public sealed record ActiveEffectsSnapshot(
    IReadOnlyList<ActiveEffect> Effects,
    long SourceSequence);

public sealed record ConditionRange(
    int MinPercent,
    int MaxPercent,
    string Descriptor);

public enum EntityObservationKind
{
    Player,
    Mob,
    Pet,
    Item,
    Corpse,
    Interactable,
    Unknown
}

public sealed record EntityObservation(
    Guid ObservationId,
    EntityObservationKind Kind,
    string DisplayText,
    string? CanonicalCandidateName,
    IReadOnlyList<string> Qualifiers,
    int Count,
    IReadOnlyList<string> StateFlags,
    Guid? RoomObservationId = null);

public enum RoomVisibilityQuality
{
    Unknown,
    Normal,
    Opaque
}

public enum MovementCause
{
    ManualDirection,
    MapperRoute,
    Follow,
    Flee,
    Crawl,
    Portal,
    Teleport,
    Summon,
    Forced,
    Unknown
}

public enum MovementResult
{
    SucceededKnownRoom,
    SucceededUnknownRoom,
    Blocked,
    CombatRestricted,
    Forced,
    Teleported,
    Disconnected,
    Unknown
}

public sealed record MovementObservation(
    MovementCause Cause,
    MovementResult Result,
    string? Direction,
    string? DestinationRoomId,
    string? Detail,
    long SourceSequence,
    Guid? CommandId = null);

public enum ScanVisibility
{
    Unknown,
    Visible,
    Opaque
}

public sealed record ScanTierObservation(
    int Distance,
    IReadOnlyList<EntityObservation> Entities,
    ScanVisibility Visibility);

public sealed record ScanObservation(
    string Direction,
    IReadOnlyList<ScanTierObservation> Tiers,
    long SourceSequence);

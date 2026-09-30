using System.Collections.ObjectModel;

namespace NexMud.Contracts.Gameplay;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId Empty { get; } = new(Guid.Empty);

    public static SessionId New() => new(Guid.NewGuid());

    public static bool TryParse(string? value, out SessionId sessionId)
    {
        if (Guid.TryParse(value, out Guid parsed))
        {
            sessionId = new SessionId(parsed);
            return true;
        }

        sessionId = Empty;
        return false;
    }

    public override string ToString() => Value.ToString("N");
}

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
    SessionId SessionId,
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
    int? Level,
    string? ClassCode,
    ResourceValue Health,
    ResourceValue Mana,
    ResourceValue Movement,
    long ObservedSequence);

public sealed record GroupSnapshot(
    string? LeaderDisplayName,
    IReadOnlyList<GroupMemberSnapshot> Members,
    long SourceSequence);

public enum EffectKind
{
    Unknown,
    Spell,
    Skill
}

public enum EffectDurationKind
{
    Timed,
    Permanent,
    Unknown
}

public sealed record EffectDuration(EffectDurationKind Kind, decimal? Hours = null)
{
    public static EffectDuration Unknown { get; } = new(EffectDurationKind.Unknown);
    public static EffectDuration Permanent { get; } = new(EffectDurationKind.Permanent);
}

public sealed record EffectModifier(string Attribute, string Value);

public sealed record ActiveEffect(
    string Name,
    EffectKind Kind,
    EffectDuration Duration,
    IReadOnlyList<EffectModifier> Modifiers,
    long SourceSequence);

public sealed record EffectSnapshot(
    IReadOnlyList<ActiveEffect> Effects,
    long SourceSequence);

public sealed record ConditionRange(
    int MinPercentInclusive,
    int MaxPercentInclusive,
    string Descriptor)
{
    public int MinPercent => MinPercentInclusive;
    public int MaxPercent => MaxPercentInclusive;
}

public enum ResolutionQuality
{
    Authoritative,
    Observed,
    Provisional,
    Unknown
}

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
    Guid? RoomObservationId = null,
    long SourceSequence = 0);

public sealed record ExitObservation(
    string Direction,
    string RawToken,
    IReadOnlyList<string> Qualifiers);

public sealed record RoomObservation(
    Guid ObservationId,
    string? Name,
    string? Description,
    IReadOnlyList<ExitObservation> Exits,
    IReadOnlyList<EntityObservation> Entities,
    RoomVisibilityQuality VisibilityQuality,
    long SourceSequenceStart,
    long SourceSequenceEnd,
    string? RoomId = null);

public enum RoomVisibilityQuality
{
    Visible,
    Partial,
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

public enum MovementResultKind
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

public enum MovementBlockReason
{
    None,
    NoExit,
    CombatRestriction,
    DoorClosed,
    DoorLocked,
    Unknown
}

public sealed record MovementObservation(
    MovementCause Cause,
    MovementResultKind Result,
    string? Direction,
    string? DestinationRoomId,
    string? Detail,
    long SourceSequence,
    Guid? CommandId = null,
    MovementBlockReason BlockReason = MovementBlockReason.None);

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

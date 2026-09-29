using JevMud.Client.Knowledge;
using JevMud.Contracts.Events;
using JevMud.Contracts.State;

namespace JevMud.Gui;

internal sealed record VitalMeterViewModel(string Label, int? Current, int? Maximum)
{
    public double MaximumValue => Maximum is > 0 ? Maximum.Value : 1;
    public double CurrentValue => Math.Clamp(Current ?? 0, 0, MaximumValue);
    public string Display => Current is null && Maximum is null ? "-" : $"{Current?.ToString() ?? "-"}/{Maximum?.ToString() ?? "-"}";
}

internal sealed record CharacterAttributeViewModel(string Name, int Current, int BaseValue)
{
    public string Display => Current == BaseValue ? Current.ToString() : $"{Current} ({BaseValue})";
}

internal sealed record CharacterEquipmentViewModel(string Slot, string Item);

internal sealed record CharacterHudViewModel(
    string Name,
    string Identity,
    string? Alignment,
    string Level,
    VitalMeterViewModel HitPoints,
    VitalMeterViewModel Mana,
    VitalMeterViewModel Movement,
    string ExperienceText,
    string ExperienceRemainingText,
    string Position,
    string Combat,
    bool CombatActive,
    string Target,
    string Jev,
    bool JevEnabled,
    IReadOnlyList<CharacterAttributeViewModel> Attributes,
    string Hitroll,
    string Damroll,
    string Saves,
    string ArmorClass,
    string Exploration,
    string Wealth,
    string Items,
    string Weight,
    string EquipmentSummary,
    IReadOnlyList<CharacterEquipmentViewModel> Equipment,
    IReadOnlyList<string> Conditions)
{
    public static CharacterHudViewModel From(StateSnapshot state, bool jevEnabled, string jevText)
    {
        CharacterProfileState profile = state.Character.Profile;
        string displayName = BuildDisplayName(profile);
        string identity = string.Join(" • ", new[]
        {
            TitleCase(profile.Lineage),
            TitleCase(profile.ClassName),
            profile.Level is null ? null : $"Level {profile.Level}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        string experienceText = state.Character.Experience is long xp
            ? $"{xp:N0} XP"
            : "XP not observed";
        string remainingText = state.Character.ExperienceToLevel is long remaining
            ? $"{remaining:N0} XP"
            : "—";

        string[] preferred = ["STR", "DEX", "CON", "INT", "WIS", "CHR"];
        Dictionary<string, AttributeScore> attributes = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, AttributeScore value) in state.Character.Attributes)
        {
            attributes[NormalizeAttributeName(key)] = value;
        }
        List<CharacterAttributeViewModel> projectedAttributes = [];
        foreach (string name in preferred)
        {
            if (!attributes.TryGetValue(name, out AttributeScore? score) || score is null) continue;
            projectedAttributes.Add(new CharacterAttributeViewModel(name, score.Current, score.BaseValue));
            attributes.Remove(name);
        }
        projectedAttributes.AddRange(attributes
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new CharacterAttributeViewModel(pair.Key.ToUpperInvariant(), pair.Value.Current, pair.Value.BaseValue)));

        List<string> conditions = [];
        conditions.AddRange(state.Character.Conditions);
        conditions.AddRange(state.Character.Effects);

        EquipmentState equipment = state.Character.Equipment;
        List<CharacterEquipmentViewModel> projectedEquipment = [];
        if (equipment.Slots.Count > 0)
        {
            projectedEquipment.AddRange(equipment.Slots
                .Where(slot => !slot.IsEmpty)
                .Select(slot => new CharacterEquipmentViewModel(CleanSlot(slot.Slot), slot.Item!)));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(equipment.MainHand))
            {
                projectedEquipment.Add(new CharacterEquipmentViewModel("Wielded", equipment.MainHand!));
            }
            projectedEquipment.AddRange(equipment.Worn
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new CharacterEquipmentViewModel(CleanSlot(pair.Key), pair.Value)));
        }

        int observedSlots = equipment.Slots.Count;
        string equipmentSummary = equipment.Completeness == ObservationCompleteness.Unknown && observedSlots == 0
            ? "Equipment not observed"
            : observedSlots > 0
                ? $"{projectedEquipment.Count} equipped • {observedSlots} slots • {TitleCase(equipment.Completeness.ToString())}"
                : $"{projectedEquipment.Count} equipped";

        CharacterCombatStats combat = state.Character.CombatStats;
        return new CharacterHudViewModel(
            displayName,
            identity,
            profile.Alignment,
            profile.Level is null ? "-" : profile.Level.Value.ToString(),
            new VitalMeterViewModel("HP", state.Character.HitPoints.Current, state.Character.HitPoints.Maximum),
            new VitalMeterViewModel("MA", state.Character.Mana.Current, state.Character.Mana.Maximum),
            new VitalMeterViewModel("MV", state.Character.Movement.Current, state.Character.Movement.Maximum),
            experienceText,
            remainingText,
            state.Character.Position ?? "-",
            state.Combat.Active ? "ACTIVE" : "CLEAR",
            state.Combat.Active,
            state.Combat.TargetName ?? state.Combat.TargetId ?? "-",
            jevText,
            jevEnabled,
            projectedAttributes,
            FormatSigned(combat.Hitroll),
            FormatSigned(combat.Damroll),
            FormatSigned(combat.Saves),
            FormatArmor(combat),
            state.Character.Exploration?.ToString("N0") ?? "-",
            FormatWealth(state.Character.Inventory),
            FormatCapacity(state.Character.Inventory.Items, state.Character.Inventory.MaxItems),
            FormatCapacity(state.Character.Inventory.Weight, state.Character.Inventory.MaxWeight),
            equipmentSummary,
            projectedEquipment,
            conditions);
    }

    private static string BuildDisplayName(CharacterProfileState profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) return "Character not identified";
        return string.IsNullOrWhiteSpace(profile.Title)
            ? profile.Name!
            : $"{profile.Name} {profile.Title}";
    }

    private static string? TitleCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        return trimmed.Length == 1
            ? trimmed.ToUpperInvariant()
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static string NormalizeAttributeName(string name) => name.Trim().ToUpperInvariant() switch
    {
        "CHA" => "CHR",
        _ => name.Trim().ToUpperInvariant()
    };

    private static string FormatSigned(int? value) => value is null ? "-" : value.Value >= 0 ? $"+{value}" : value.Value.ToString();

    private static string FormatArmor(CharacterCombatStats stats)
    {
        if (stats.ArmorClass is null && string.IsNullOrWhiteSpace(stats.ArmorClassDescriptor)) return "-";
        string value = stats.ArmorClass?.ToString() ?? "-";
        return string.IsNullOrWhiteSpace(stats.ArmorClassDescriptor)
            ? value
            : $"{value} / {TitleCase(stats.ArmorClassDescriptor)}";
    }

    private static string FormatWealth(InventorySummary inventory)
    {
        List<string> parts = [];
        if (inventory.Gold is > 0) parts.Add($"{inventory.Gold}g");
        if (inventory.Silver is > 0) parts.Add($"{inventory.Silver}s");
        if (inventory.Copper is > 0) parts.Add($"{inventory.Copper}c");
        if (parts.Count > 0) return string.Join(" ", parts);
        return inventory.Gold is null && inventory.Silver is null && inventory.Copper is null ? "-" : "0c";
    }

    private static string FormatCapacity(int? current, int? maximum)
    {
        if (current is null && maximum is null) return "-";
        if (maximum is null) return current?.ToString("N0") ?? "-";
        return $"{current?.ToString("N0") ?? "-"} / {maximum.Value:N0}";
    }

    private static string CleanSlot(string slot)
    {
        string normalized = slot
            .Replace("<", string.Empty, StringComparison.Ordinal)
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Trim();
        return normalized.ToLowerInvariant() switch
        {
            "worn on finger" => "Finger",
            "worn around neck" => "Neck",
            "worn on torso" => "Torso",
            "worn on head" => "Head",
            "worn on legs" => "Legs",
            "worn on feet" => "Feet",
            "worn on hands" => "Hands",
            "worn on arms" => "Arms",
            "worn as shield" => "Shield",
            "worn about body" => "Body",
            "worn about waist" => "Waist",
            "worn around wrist" => "Wrist",
            "wielded" => "Wielded",
            "dual wielded" => "Dual wielded",
            "branded" => "Brand",
            _ => normalized
        };
    }
}

internal sealed record RoomExitViewModel(
    string Direction,
    ExitDoorState DoorState,
    ExitTraversability Traversability,
    string? BlockReason);

internal sealed record RoomEntityViewModel(string Description, RoomEntityKind Kind);

internal sealed record RoomContextViewModel(
    string Name,
    string Metadata,
    string? Area,
    IReadOnlyList<RoomExitViewModel> Exits,
    IReadOnlyList<RoomEntityViewModel> People,
    IReadOnlyList<RoomEntityViewModel> Objects,
    IReadOnlyList<RoomEntityViewModel> Fixtures,
    IReadOnlyList<RoomEntityViewModel> Corpses,
    IReadOnlyList<string> RecentObservations,
    int? VisitCount,
    int? KnownRoutes,
    int? RecurringEntities,
    DateTimeOffset? LastSeenAt)
{
    public static RoomContextViewModel From(
        StateSnapshot state,
        RoomKnowledge? memory,
        MapperRoomMetadata? metadata)
    {
        IReadOnlyList<RoomExitObservation> exitDetails = state.Room.Exits.Details ?? Array.Empty<RoomExitObservation>();
        Dictionary<string, RoomExitObservation> detailByDirection = exitDetails
            .GroupBy(exit => exit.Direction, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(exit => exit.Direction, StringComparer.OrdinalIgnoreCase);

        List<RoomExitViewModel> exits = [];
        foreach (string direction in state.Room.Exits.Directions)
        {
            detailByDirection.TryGetValue(direction, out RoomExitObservation? detail);
            exits.Add(new RoomExitViewModel(
                direction,
                detail?.DoorState ?? ExitDoorState.Unknown,
                detail?.Traversability ?? ExitTraversability.Unknown,
                detail?.BlockReason));
        }

        static IReadOnlyList<RoomEntityViewModel> Project(IEnumerable<RoomContentObservation> entities) => entities
            .Select(entity => new RoomEntityViewModel(
                string.IsNullOrWhiteSpace(entity.CanonicalName) ? entity.Description : entity.CanonicalName!,
                entity.Kind))
            .ToArray();

        List<string> roomMeta = [];
        if (!string.IsNullOrWhiteSpace(metadata?.Label) &&
            !string.Equals(metadata.Label, state.Room.Name, StringComparison.OrdinalIgnoreCase))
        {
            roomMeta.Add(metadata.Label!);
        }
        if (!string.IsNullOrWhiteSpace(metadata?.Area)) roomMeta.Add(metadata.Area!);
        if (!string.IsNullOrWhiteSpace(state.Room.Terrain)) roomMeta.Add(state.Room.Terrain!);
        if (!string.IsNullOrWhiteSpace(state.Room.Light)) roomMeta.Add(state.Room.Light!);

        IReadOnlyList<string> observations = state.Room.RecentObservations
            .Take(5)
            .ToArray();

        return new RoomContextViewModel(
            state.Room.Name ?? metadata?.Label ?? "Room not observed",
            string.Join(" • ", roomMeta),
            metadata?.Area,
            exits,
            Project(state.Room.Occupants),
            Project(state.Room.Objects),
            Project(state.Room.Interactables),
            Project(state.Room.Corpses),
            observations,
            memory?.VisitCount,
            memory?.Exits.Count,
            memory?.FrequentEntities.Count,
            memory?.LastSeenAt);
    }
}

internal sealed record GameplayShellViewModel(
    bool Connected,
    string ConnectionText,
    CharacterHudViewModel Character,
    RoomContextViewModel Room)
{
    public static GameplayShellViewModel From(
        StateSnapshot state,
        bool jevEnabled,
        string jevText,
        RoomKnowledge? memory,
        MapperRoomMetadata? metadata,
        string configuredHost,
        int configuredPort)
    {
        bool connected = state.Session.ConnectionStatus != ConnectionStatus.Disconnected;
        string host = connected ? state.Session.Host ?? configuredHost : configuredHost;
        int port = connected ? state.Session.Port ?? configuredPort : configuredPort;
        return new GameplayShellViewModel(
            connected,
            connected ? $"Connected · {host}:{port}" : $"Disconnected · {host}:{port}",
            CharacterHudViewModel.From(state, jevEnabled, jevText),
            RoomContextViewModel.From(state, memory, metadata));
    }
}

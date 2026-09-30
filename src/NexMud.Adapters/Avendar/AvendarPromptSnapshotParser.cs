using System.Collections.ObjectModel;
using NexMud.Contracts.Gameplay;

namespace NexMud.Adapters.Avendar;

/// <summary>
/// Composable parser for prompt fragments visible in historical and live Avendar output.
/// It deliberately accepts signed and over-max resources without normalization.
/// </summary>
public static partial class AvendarPromptSnapshotParser
{
    public static bool TryParseResourceLine(
        string line,
        long sourceSequence,
        out CharacterPromptSnapshot? snapshot)
    {
        snapshot = null;
        if (!ResourceSegmentParser.TryParse(line, out AvendarResourceSegments? resources) || resources is null)
        {
            return false;
        }

        PromptTail tail = ParseTail(resources.Tail);

        snapshot = new CharacterPromptSnapshot(
            resources.Health,
            resources.Mana,
            resources.Movement,
            ExperienceToLevel: tail.ExperienceToLevel,
            ExplorationPoints: tail.ExplorationPoints,
            Terrain: tail.Terrain,
            Light: tail.Light,
            UnknownFields: tail.UnknownFields,
            SourceSequence: sourceSequence);
        return true;
    }

    public static bool TryExtractGameClockPrefix(
        string line,
        long sourceSequence,
        out CharacterPromptSnapshot? snapshot,
        out string remainder)
    {
        snapshot = null;
        if (!GameClockParser.TryParsePrefix(line, out GameClock? clock, out remainder) || clock is null)
        {
            return false;
        }

        snapshot = new CharacterPromptSnapshot(
            null,
            null,
            null,
            GameClock: clock,
            SourceSequence: sourceSequence);
        return true;
    }

    public static CharacterPromptSnapshot FromTelemetry(
        NexMud.Contracts.Events.CharacterPromptObserved prompt,
        long sourceSequence) =>
        new(
            new ResourceValue(prompt.HitPoints, prompt.MaxHitPoints),
            new ResourceValue(prompt.Mana, prompt.MaxMana),
            new ResourceValue(prompt.Movement, prompt.MaxMovement),
            prompt.Experience,
            prompt.ExperienceToLevel,
            GameClock: null,
            Position: prompt.Position,
            RoomName: prompt.RoomName,
            Terrain: prompt.Terrain,
            Light: prompt.Light,
            SourceSequence: sourceSequence);

    private static PromptTail ParseTail(string value)
    {
        string tail = value.Trim();
        if (tail.EndsWith('>'))
        {
            tail = tail[..^1].TrimEnd();
        }

        UnknownPromptFieldCollector unknown = new();
        string? terrain = null;
        string? light = null;

        int pipeStart = tail.IndexOf('|');
        string numericSection = pipeStart >= 0 ? tail[..pipeStart].Trim() : tail;
        if (pipeStart >= 0)
        {
            string[] fields = tail[pipeStart..].Split('|', StringSplitOptions.None);
            string[] values = fields.Skip(1).Select(field => field.Trim()).ToArray();
            if (values.Length > 0) terrain = TerrainParser.Parse(values[0]);
            if (values.Length > 1) light = LightParser.Parse(values[1]);
            foreach (string extra in values.Skip(2)) unknown.Add(extra);
        }

        long? tnl = null;
        long? exploration = null;
        foreach (string token in numericSection.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TnlParser.TryParse(token, out long parsedTnl))
            {
                tnl = parsedTnl;
            }
            else if (ExplorationPointsParser.TryParse(token, out long parsedExploration))
            {
                exploration = parsedExploration;
            }
            else
            {
                unknown.Add(token);
            }
        }

        return new PromptTail(tnl, exploration, terrain, light, unknown.Snapshot());
    }

    private sealed record PromptTail(
        long? ExperienceToLevel,
        long? ExplorationPoints,
        string? Terrain,
        string? Light,
        IReadOnlyList<string> UnknownFields);
}

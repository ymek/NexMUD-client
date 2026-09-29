using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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
        Match resources = ResourcePrefixRegex().Match(line.Trim());
        if (!resources.Success)
        {
            return false;
        }

        ResourceValue health = Pair(resources, "hp", "maxhp");
        ResourceValue mana = Pair(resources, "mana", "maxmana");
        ResourceValue movement = Pair(resources, "move", "maxmove");
        PromptTail tail = ParseTail(resources.Groups["rest"].Value);

        snapshot = new CharacterPromptSnapshot(
            health,
            mana,
            movement,
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
        remainder = line;
        Match match = GameClockRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        int hour = int.Parse(match.Groups["hour"].Value);
        int minute = int.Parse(match.Groups["minute"].Value);
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        snapshot = new CharacterPromptSnapshot(
            null,
            null,
            null,
            GameClock: new GameClock(hour, minute),
            SourceSequence: sourceSequence);
        remainder = match.Groups["rest"].Value;
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

        string? terrain = null;
        string? light = null;
        List<string> unknown = [];

        int pipeStart = tail.IndexOf('|');
        string numericSection = pipeStart >= 0 ? tail[..pipeStart].Trim() : tail;
        if (pipeStart >= 0)
        {
            string pipeSection = tail[pipeStart..];
            string[] fields = pipeSection.Split('|', StringSplitOptions.None);
            List<string> values = fields
                .Skip(1)
                .Where((field, index) => index < fields.Length - 2 || field.Length > 0)
                .Select(field => field.Trim())
                .ToList();
            if (values.Count > 0 && values[0].Length > 0)
            {
                terrain = values[0];
            }
            if (values.Count > 1 && values[1].Length > 0)
            {
                light = values[1];
            }
            if (values.Count > 2)
            {
                unknown.AddRange(values.Skip(2).Where(field => field.Length > 0));
            }
        }

        long? tnl = null;
        long? exploration = null;
        foreach (string token in numericSection.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match known = KnownNumericFieldRegex().Match(token);
            if (!known.Success)
            {
                unknown.Add(token);
                continue;
            }

            long parsed = long.Parse(known.Groups["value"].Value);
            switch (known.Groups["kind"].Value.ToLowerInvariant())
            {
                case "tnl":
                    tnl = parsed;
                    break;
                case "ep":
                    exploration = parsed;
                    break;
                default:
                    unknown.Add(token);
                    break;
            }
        }

        return new PromptTail(
            tnl,
            exploration,
            terrain,
            light,
            new ReadOnlyCollection<string>(unknown.ToArray()));
    }

    private static ResourceValue Pair(Match match, string current, string maximum) =>
        new(int.Parse(match.Groups[current].Value), int.Parse(match.Groups[maximum].Value));

    private sealed record PromptTail(
        long? ExperienceToLevel,
        long? ExplorationPoints,
        string? Terrain,
        string? Light,
        IReadOnlyList<string> UnknownFields);

    [GeneratedRegex(
        @"^<\s*(?<hp>-?\d+)\s*\(\s*(?<maxhp>-?\d+)\s*\)hp\s+" +
        @"(?<mana>-?\d+)\s*\(\s*(?<maxmana>-?\d+)\s*\)m\s+" +
        @"(?<move>-?\d+)\s*\(\s*(?<maxmove>-?\d+)\s*\)mv(?<rest>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePrefixRegex();

    [GeneratedRegex(@"^(?<value>-?\d+)(?<kind>tnl|ep)$", RegexOptions.IgnoreCase)]
    private static partial Regex KnownNumericFieldRegex();

    [GeneratedRegex(@"^\s*(?<hour>\d{1,2}):(?<minute>\d{2})>\s?(?<rest>.*)$")]
    private static partial Regex GameClockRegex();
}

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using JevMud.Contracts.Gameplay;

namespace JevMud.Adapters.Avendar;

public static partial class AvendarScanParser
{
    public static bool TryParse(
        IReadOnlyList<string> lines,
        long sourceSequence,
        out ScanObservation? scan)
    {
        scan = null;
        string? direction = null;
        List<ScanTierObservation> tiers = [];
        int? distance = null;
        List<EntityObservation> entities = [];
        ScanVisibility visibility = ScanVisibility.Visible;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            Match start = StartRegex().Match(line);
            if (start.Success)
            {
                direction = NormalizeDirection(start.Groups["direction"].Value);
                continue;
            }

            Match tier = TierRegex().Match(line);
            if (tier.Success)
            {
                FlushTier();
                distance = int.Parse(tier.Groups["distance"].Value);
                direction ??= NormalizeDirection(tier.Groups["direction"].Value);
                visibility = ScanVisibility.Visible;
                continue;
            }

            if (distance is null || line.Length == 0)
            {
                continue;
            }

            if (line.Equals("It's too dark to see clearly.", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("It is too dark to see clearly.", StringComparison.OrdinalIgnoreCase))
            {
                visibility = ScanVisibility.Opaque;
                continue;
            }

            if (IsPromptLike(line))
            {
                break;
            }

            entities.Add(AvendarEntityObservationParser.CreateScanEntity(
                line,
                sourceSequence,
                direction ?? string.Empty,
                distance.Value,
                entities.Count));
        }

        FlushTier();
        if (direction is null || tiers.Count == 0)
        {
            return false;
        }

        scan = new ScanObservation(
            direction,
            new ReadOnlyCollection<ScanTierObservation>(tiers.ToArray()),
            sourceSequence);
        return true;

        void FlushTier()
        {
            if (distance is null)
            {
                return;
            }

            tiers.Add(new ScanTierObservation(
                distance.Value,
                new ReadOnlyCollection<EntityObservation>(entities.ToArray()),
                visibility));
            entities.Clear();
            distance = null;
        }
    }

    private static string NormalizeDirection(string value) => value.Trim().ToLowerInvariant() switch
    {
        "n" or "north" => "north",
        "e" or "east" => "east",
        "s" or "south" => "south",
        "w" or "west" => "west",
        "u" or "up" => "up",
        "d" or "down" => "down",
        var direction => direction
    };

    private static bool IsPromptLike(string line) =>
        line.StartsWith('<') && line.Contains("hp", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(line, @"^\d{1,2}:\d{2}>");

    [GeneratedRegex(@"^You peer intently (?<direction>north|east|south|west|up|down)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"^===\s*(?<distance>\d+)\s+(?<direction>north|east|south|west|up|down)\s*===$", RegexOptions.IgnoreCase)]
    private static partial Regex TierRegex();
}

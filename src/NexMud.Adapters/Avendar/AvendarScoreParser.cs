using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;

namespace NexMud.Adapters.Avendar;

public static class AvendarScoreParser
{
    private static readonly Regex AttributeRegex = new(
        @"\b(?<name>Str|Int|Wis|Dex|Con|Chr):\s*(?<current>-?\d+)\((?<max>-?\d+)\)",
        RegexOptions.Compiled);

    public static bool TryParse(IReadOnlyList<string> lines, out CharacterScoreObserved? observed)
    {
        observed = null;
        if (lines.Count == 0)
        {
            return false;
        }

        string joined = string.Join('\n', lines);
        CharacterProfileState profile = new(
            ParseName(lines, out string? title),
            title,
            Capture(joined, @"\bLineage:\s*(?<value>[^\s|]+)"),
            Capture(joined, @"\bClass:\s*(?<value>[^\s|]+)"),
            CaptureInt(joined, @"\bLevel:\s*(?<value>\d+)"),
            Capture(joined, @"\bGender\s*:\s*(?<value>[^\s|]+)"),
            CaptureInt(joined, @"\bAge\s*:\s*(?<value>\d+)"),
            Capture(joined, @"\bAge\s*:\s*\d+\s*\((?<value>[^)]+)\)"),
            CaptureInt(joined, @"\bHours:\s*(?<value>\d+)"),
            Capture(joined, @"\bResonance:\s*(?<value>[^|\r\n]+)"),
            Capture(joined, @"\bAlignment:\s*(?<value>[^|\r\n]+)"));

        Dictionary<string, AttributeScore> attributes = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(joined))
        {
            attributes[match.Groups["name"].Value] = new AttributeScore(
                int.Parse(match.Groups["current"].Value),
                int.Parse(match.Groups["max"].Value));
        }

        VitalState hp = ParseVital(joined, "Hit");
        VitalState mana = ParseVital(joined, "Mana");
        VitalState move = ParseVital(joined, "Move");

        Match armor = Regex.Match(
            joined,
            @"\bTotal AC\s*:\s*(?<descriptor>[^(|\r\n]+?)\s*\((?<value>-?\d+)\)",
            RegexOptions.IgnoreCase);

        CharacterCombatStats combatStats = new(
            CaptureInt(joined, @"\bHitroll:\s*(?<value>-?\d+)"),
            CaptureInt(joined, @"\bDamroll:\s*(?<value>-?\d+)"),
            CaptureInt(joined, @"\bSaves\s*:\s*(?<value>-?\d+)"),
            armor.Success ? int.Parse(armor.Groups["value"].Value) : null,
            armor.Success ? armor.Groups["descriptor"].Value.Trim() : null);

        (int? copper, int? silver, int? gold) = ParseWealth(joined);
        (int? items, int? maxItems) = ParseInventoryPair(joined, "Items", "Max Items");
        (int? weight, int? maxWeight) = ParseInventoryPair(joined, "Weight", "Max Weight");
        InventorySummary inventory = new(
            items,
            maxItems,
            weight,
            maxWeight,
            copper,
            silver,
            gold);

        IReadOnlyList<string>? effects = null;
        if (joined.Contains("You are not affected by any spells.", StringComparison.OrdinalIgnoreCase))
        {
            effects = new ReadOnlyCollection<string>(Array.Empty<string>());
        }

        List<string> conditions = [];
        if (joined.Contains("You are getting hungry.", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add("hungry");
        }

        long? experience = CaptureLong(joined, @"\bExperience\s*:\s*(?<value>\d+)");
        long? experienceToLevel = CaptureLong(joined, @"\bExper/level:\s*(?<value>\d+)");
        int? exploration = CaptureInt(joined, @"\bExploration:\s*(?<value>\d+)");

        bool recognized = profile.Name is not null ||
                          hp.Current is not null ||
                          mana.Current is not null ||
                          move.Current is not null ||
                          experience is not null ||
                          attributes.Count > 0;
        if (!recognized)
        {
            return false;
        }

        observed = new CharacterScoreObserved(
            profile,
            new ReadOnlyDictionary<string, AttributeScore>(attributes),
            hp,
            mana,
            move,
            experience,
            experienceToLevel,
            exploration,
            combatStats,
            inventory,
            effects,
            new ReadOnlyCollection<string>(conditions));
        return true;
    }


    private static (int? Copper, int? Silver, int? Gold) ParseWealth(string text)
    {
        Match wealth = Regex.Match(text, @"\bWealth:\s*(?<value>[^|\r\n]+)", RegexOptions.IgnoreCase);
        if (!wealth.Success)
        {
            return (null, null, null);
        }

        int? copper = null;
        int? silver = null;
        int? gold = null;
        foreach (Match amount in Regex.Matches(wealth.Groups["value"].Value, @"(?<amount>\d+)\s*(?<unit>[csg])", RegexOptions.IgnoreCase))
        {
            int parsed = int.Parse(amount.Groups["amount"].Value);
            switch (amount.Groups["unit"].Value.ToLowerInvariant())
            {
                case "c":
                    copper = parsed;
                    break;
                case "s":
                    silver = parsed;
                    break;
                case "g":
                    gold = parsed;
                    break;
            }
        }

        return (copper ?? 0, silver ?? 0, gold ?? 0);
    }

    private static string? ParseName(IReadOnlyList<string> lines, out string? title)
    {
        title = null;

        foreach (string line in lines)
        {
            Match explicitName = Regex.Match(line, @"^\s*(?:\|\s*)?Name\s*:\s*(?<value>[^|\r\n]+)", RegexOptions.IgnoreCase);
            if (explicitName.Success)
            {
                return SplitNameAndTitle(explicitName.Groups["value"].Value.Trim(), out title);
            }
        }

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("| ", StringComparison.Ordinal) ||
                !trimmed.EndsWith(" |", StringComparison.Ordinal) ||
                trimmed.Contains(':', StringComparison.Ordinal) ||
                trimmed.Contains("You ", StringComparison.Ordinal))
            {
                continue;
            }

            string content = trimmed[2..^2].Trim();
            if (content.Length == 0 || content.All(character => character is '-' or '\\' or '/'))
            {
                continue;
            }

            return SplitNameAndTitle(content, out title);
        }

        return null;
    }

    private static string SplitNameAndTitle(string value, out string? title)
    {
        int space = value.IndexOf(' ');
        if (space < 0)
        {
            title = null;
            return value;
        }

        title = value[(space + 1)..].Trim();
        return value[..space];
    }

    private static (int? Current, int? Maximum) ParseInventoryPair(string text, string label, string maximumLabel)
    {
        Match slash = Regex.Match(
            text,
            $@"\b{Regex.Escape(label)}\s*:\s*(?<current>\d+)\s*/\s*(?<maximum>\d+)",
            RegexOptions.IgnoreCase);
        if (slash.Success)
        {
            return (
                int.Parse(slash.Groups["current"].Value),
                int.Parse(slash.Groups["maximum"].Value));
        }

        return (
            CaptureInt(text, $@"\b{Regex.Escape(label)}\s*:\s*(?<value>\d+)"),
            CaptureInt(text, $@"\b{Regex.Escape(maximumLabel)}\s*:\s*(?<value>\d+)"));
    }

    private static VitalState ParseVital(string text, string name)
    {
        Match match = Regex.Match(
            text,
            $@"\b{Regex.Escape(name)}\s*:\s*(?<current>\d+)\s*/\s*(?<max>\d+)",
            RegexOptions.IgnoreCase);
        return match.Success
            ? new VitalState(
                int.Parse(match.Groups["current"].Value),
                int.Parse(match.Groups["max"].Value))
            : new VitalState(null, null);
    }

    private static string? Capture(string text, string pattern)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static int? CaptureInt(string text, string pattern)
    {
        string? value = Capture(text, pattern);
        return value is not null && int.TryParse(value, out int parsed) ? parsed : null;
    }

    private static long? CaptureLong(string text, string pattern)
    {
        string? value = Capture(text, pattern);
        return value is not null && long.TryParse(value, out long parsed) ? parsed : null;
    }
}

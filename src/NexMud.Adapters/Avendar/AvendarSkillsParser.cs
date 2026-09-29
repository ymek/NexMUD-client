using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;

namespace NexMud.Adapters.Avendar;

public static class AvendarSkillsParser
{
    private static readonly Regex LevelRegex = new(
        @"^\s*Level\s+(?<level>\d+):\s*(?<content>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SkillRegex = new(
        @"(?<name>[A-Za-z][A-Za-z0-9 \'\-]*?)\s+(?<value>\d+%|n/a)(?=\s{2,}|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParse(IReadOnlyList<string> lines, out SkillsSnapshotObserved? observed)
    {
        observed = null;
        if (lines.Count == 0)
        {
            return false;
        }

        List<SkillState> skills = [];
        int? currentLevel = null;

        foreach (string rawLine in lines)
        {
            if (rawLine.Contains("[Hit Return to continue]", StringComparison.Ordinal))
            {
                continue;
            }

            string content = rawLine;
            Match levelMatch = LevelRegex.Match(rawLine);
            if (levelMatch.Success)
            {
                currentLevel = int.Parse(levelMatch.Groups["level"].Value);
                content = levelMatch.Groups["content"].Value;
            }

            if (currentLevel is null)
            {
                continue;
            }

            foreach (Match skillMatch in SkillRegex.Matches(content))
            {
                string name = Regex.Replace(skillMatch.Groups["name"].Value.Trim(), @"\s+", " ");
                string value = skillMatch.Groups["value"].Value;
                bool available = !value.Equals("n/a", StringComparison.OrdinalIgnoreCase);
                int? proficiency = available
                    ? int.Parse(value.TrimEnd('%'))
                    : null;

                skills.Add(new SkillState(
                    name,
                    currentLevel.Value,
                    available ? SkillAvailability.Available : SkillAvailability.Unavailable,
                    proficiency,
                    true,
                    AvendarAbilityClassifier.Classify(name)));
            }
        }

        if (skills.Count == 0)
        {
            return false;
        }

        observed = new SkillsSnapshotObserved(
            new ReadOnlyCollection<SkillState>(skills));
        return true;
    }
}

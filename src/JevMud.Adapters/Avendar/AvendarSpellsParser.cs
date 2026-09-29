using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using JevMud.Contracts.Events;
using JevMud.Contracts.State;

namespace JevMud.Adapters.Avendar;

/// <summary>
/// Parses the spell-list shape Avendar exposes when it uses level headings and
/// percentage/n-a entries. An explicit "No spells found." response is a complete
/// empty snapshot. Unknown formats are left unmodeled rather than guessed.
/// </summary>
public static class AvendarSpellsParser
{
    private static readonly Regex LevelRegex = new(
        @"^\s*Level\s+(?<level>\d+):\s*(?<content>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpellRegex = new(
        @"(?<name>[A-Za-z][A-Za-z '\-]*?)\s+(?<value>\d+%|n/a)(?=\s{2,}|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryParse(IReadOnlyList<string> lines, out SpellsSnapshotObserved? observed)
    {
        observed = null;
        if (lines.Count == 0)
        {
            return false;
        }

        if (lines.Any(line => line.Trim().Equals("No spells found.", StringComparison.OrdinalIgnoreCase)))
        {
            observed = new SpellsSnapshotObserved(Array.Empty<SpellState>());
            return true;
        }

        List<SpellState> spells = [];
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

            foreach (Match spellMatch in SpellRegex.Matches(content))
            {
                string name = Regex.Replace(spellMatch.Groups["name"].Value.Trim(), @"\s+", " ");
                string value = spellMatch.Groups["value"].Value;
                bool available = !value.Equals("n/a", StringComparison.OrdinalIgnoreCase);
                int? proficiency = available ? int.Parse(value.TrimEnd('%')) : null;

                spells.Add(new SpellState(
                    name,
                    currentLevel.Value,
                    available ? SkillAvailability.Available : SkillAvailability.Unavailable,
                    proficiency,
                    true,
                    AvendarAbilityClassifier.Classify(name)));
            }
        }

        if (spells.Count == 0)
        {
            return false;
        }

        observed = new SpellsSnapshotObserved(new ReadOnlyCollection<SpellState>(spells));
        return true;
    }
}

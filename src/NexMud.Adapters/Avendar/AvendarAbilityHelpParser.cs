using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;

namespace NexMud.Adapters.Avendar;

public static partial class AvendarAbilityHelpParser
{
    public static bool TryParse(IReadOnlyList<string> lines, out AbilityHelpObserved? observed)
    {
        observed = null;
        if (lines.Count == 0)
        {
            return false;
        }

        string? name = null;
        AbilityHelpKind kind = AbilityHelpKind.Unknown;
        int titleIndex = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            Match title = TitleRegex().Match(lines[index]);
            if (!title.Success)
            {
                continue;
            }

            name = Regex.Replace(title.Groups["name"].Value.Trim(), @"\s+", " ");
            kind = title.Groups["kind"].Value.Equals("skill", StringComparison.OrdinalIgnoreCase)
                ? AbilityHelpKind.Skill
                : AbilityHelpKind.Spell;
            titleIndex = index;
            break;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        string? syntax = null;
        int syntaxIndex = -1;
        for (int index = titleIndex + 1; index < lines.Count; index++)
        {
            string trimmed = lines[index].Trim();
            if (trimmed.StartsWith("Syntax:", StringComparison.OrdinalIgnoreCase))
            {
                syntax = NormalizeWhitespace(trimmed["Syntax:".Length..]);
                syntaxIndex = index;
                continue;
            }

            Match field = FieldRegex().Match(lines[index]);
            if (field.Success)
            {
                string key = NormalizeKey(field.Groups["key"].Value);
                string value = NormalizeWhitespace(field.Groups["value"].Value);
                if (key.Length > 0 && value.Length > 0)
                {
                    fields[key] = value;
                }
            }
        }

        decimal? lag = ParseLeadingDecimal(Value(fields, "activation lag"));
        int? manaCost = ParseManaCost(Value(fields, "activation cost"));
        string description = BuildDescription(lines, syntaxIndex >= 0 ? syntaxIndex + 1 : titleIndex + 1);

        observed = new AbilityHelpObserved(new AbilityHelpDocument(
            name,
            kind,
            lag,
            manaCost,
            syntax,
            description,
            new ReadOnlyDictionary<string, string>(fields),
            string.Join("\n", lines)));
        return true;
    }

    private static string BuildDescription(IReadOnlyList<string> lines, int start)
    {
        List<string> description = [];
        for (int index = Math.Max(start, 0); index < lines.Count; index++)
        {
            string line = lines[index].TrimEnd();
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                if (description.Count > 0 && description[^1].Length > 0)
                {
                    description.Add(string.Empty);
                }
                continue;
            }

            if (IsDecoration(trimmed) || FieldRegex().IsMatch(line) || trimmed.StartsWith("Syntax:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            description.Add(trimmed);
        }

        while (description.Count > 0 && description[^1].Length == 0)
        {
            description.RemoveAt(description.Count - 1);
        }

        // Avendar hard-wraps help prose for terminal presentation. Store semantic prose, not
        // terminal line wrapping, so UI reference text and Jev context receive contiguous strings.
        return NormalizeWhitespace(string.Join(" ", description));
    }

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");

    private static bool IsDecoration(string value) =>
        value.StartsWith("/---", StringComparison.Ordinal) ||
        value.StartsWith("\\---", StringComparison.Ordinal) ||
        value.All(character => character is '-' or '/' or '\\' or '|' or ' ');

    private static string NormalizeKey(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string? Value(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) ? value : null;

    private static decimal? ParseLeadingDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = LeadingDecimalRegex().Match(value);
        return match.Success && decimal.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out decimal result)
            ? result
            : null;
    }

    private static int? ParseManaCost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = ManaCostRegex().Match(value);
        return match.Success && int.TryParse(match.Groups["value"].Value, out int result)
            ? result
            : null;
    }

    [GeneratedRegex(@"^\s*\|\s*(?<name>.+?)\s+\((?<kind>Skill|Spell)\)\s*\|\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z][A-Za-z ]+?)\s*:\s*(?<value>.+?)\s*$")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"(?<value>\d+(?:\.\d+)?)")]
    private static partial Regex LeadingDecimalRegex();

    [GeneratedRegex(@"^(?<value>\d+)\s+mana\b", RegexOptions.IgnoreCase)]
    private static partial Regex ManaCostRegex();
}

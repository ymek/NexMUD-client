using System.Text.RegularExpressions;

namespace NexMud.Adapters.Avendar;

/// <summary>
/// Small deterministic heuristics for Avendar room-line entity classes. Keep these
/// rules shared between live parsing and knowledge repair so persisted semantics do
/// not drift from the adapter.
/// </summary>
public static partial class AvendarRoomEntityClassifier
{
    private static readonly string[] FixtureKeywords =
    [
        "sign", "board", "plaque", "note", "fountain", "bin", "bins", "volume"
    ];

    public static bool IsFixtureSubject(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        string subject = StripDecorators(description.Trim());
        if (!FixtureKeywords.Any(keyword => ContainsWord(subject, keyword))) return false;
        Match match = FixtureSubjectRegex().Match(subject);
        return match.Success && FixtureKeywords.Any(keyword => ContainsWord(match.Groups["subject"].Value, keyword));
    }

    public static string? ExtractFixtureCanonicalName(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        string subject = StripDecorators(description.Trim());
        Match match = FixtureSubjectRegex().Match(subject);
        return match.Success ? match.Value.Trim() : null;
    }

    public static string? ExtractOccupantCanonicalName(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        string subject = StripDecorators(description.Trim());
        Match match = OccupantSubjectRegex().Match(subject);
        return match.Success ? match.Groups["subject"].Value.Trim() : null;
    }

    private static string StripDecorators(string description)
    {
        string remaining = description;
        while (true)
        {
            Match match = DecoratorRegex().Match(remaining);
            if (!match.Success) return remaining;
            remaining = remaining[match.Length..].TrimStart();
        }
    }

    private static bool ContainsWord(string description, string word) =>
        Regex.IsMatch(description, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"^(?<decorator>\([^)]*\))\s*")]
    private static partial Regex DecoratorRegex();

    [GeneratedRegex(@"^(?:(?:a|an|the|some|several)\s+)?(?<subject>(?:[\w'-]+[ ,.-]*){0,5}(?:sign|board|plaque|note|fountain|bin|bins|volume))\b", RegexOptions.IgnoreCase)]
    private static partial Regex FixtureSubjectRegex();

    [GeneratedRegex(@"^(?<subject>(?:(?:a|an|the|some|several)\s+)?[\w'-]+(?:\s+[\w'-]+){0,11}?)\s+(?:looks?|lays?|stands?|sits?|rests?|waits?|watches?|paces?|walks?|patrols?|guards?|directs?|polishes?|gazes?|leans?|looms?|hovers?|floats?|wanders?|scratches?|stares?|kneels?|crouches?|slouches?|smiles?|frowns?|nods?|shifts?|tends?|reads?|holds?|wears?|carries?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OccupantSubjectRegex();
}

namespace NexMud.Gui.AutomationStudio;

internal static class StudioSearchMatcher
{
    public static bool Contains(string? value, string? term, bool matchCase, bool wholeWord)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        if (string.IsNullOrEmpty(value)) return false;
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int offset = 0;
        while (offset < value.Length)
        {
            int index = value.IndexOf(term, offset, comparison);
            if (index < 0) return false;
            int end = index + term.Length;
            if (!wholeWord || (IsBoundary(value, index - 1) && IsBoundary(value, end))) return true;
            offset = index + 1;
        }
        return false;
    }

    public static bool MatchesFilters(
        string? searchableText,
        IEnumerable<string> includedTerms,
        IEnumerable<string> excludedTerms,
        bool matchCase,
        bool wholeWord)
    {
        ArgumentNullException.ThrowIfNull(includedTerms);
        ArgumentNullException.ThrowIfNull(excludedTerms);
        return includedTerms.All(term => Contains(searchableText, term, matchCase, wholeWord)) &&
               !excludedTerms.Any(term => Contains(searchableText, term, matchCase, wholeWord));
    }

    public static string[] ParseTerms(string? value) => (value ?? string.Empty)
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(term => term.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static int Rank(string? title, string query, bool matchCase)
    {
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(title?.Trim(), query.Trim(), comparison)) return 0;
        if (title?.StartsWith(query, comparison) == true) return 1;
        if (Contains(title, query, matchCase, wholeWord: false)) return 2;
        return 3;
    }

    private static bool IsBoundary(string value, int index) =>
        index < 0 || index >= value.Length || !IsWordCharacter(value[index]);

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_' ||
        char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.SpacingCombiningMark or
            System.Globalization.UnicodeCategory.EnclosingMark;
}

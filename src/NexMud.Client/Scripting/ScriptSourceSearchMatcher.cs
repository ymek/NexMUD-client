using System.Buffers;
using System.Text;

namespace NexMud.Client.Scripting;

internal static class ScriptSourceSearchMatcher
{
    public static int FindFirst(string line, string query, ScriptSourceSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(options);

        StringComparison comparison = options.MatchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        int searchFrom = 0;
        while (searchFrom <= line.Length - query.Length)
        {
            int match = line.IndexOf(query, searchFrom, comparison);
            if (match < 0) return -1;

            int afterMatch = match + query.Length;
            bool startsAtWordBoundary = IsBoundaryBefore(line, match);
            bool endsAtWordBoundary = IsBoundaryAfter(line, afterMatch);
            if (!options.WholeWord || (startsAtWordBoundary && endsAtWordBoundary)) return match;

            searchFrom = match + 1;
        }

        return -1;
    }

    public static bool MatchesFilters(string searchableText, ScriptSourceSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(searchableText);
        ArgumentNullException.ThrowIfNull(options);
        foreach (string term in options.IncludedTerms ?? [])
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (FindFirst(searchableText, term, options) < 0) return false;
        }
        foreach (string term in options.ExcludedTerms ?? [])
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (FindFirst(searchableText, term, options) >= 0) return false;
        }
        return true;
    }

    private static bool IsBoundaryBefore(string value, int index)
    {
        if (!IsRuneBoundary(value, index)) return false;
        return index == 0 ||
               Rune.DecodeLastFromUtf16(value.AsSpan(0, index), out Rune previous, out _) != OperationStatus.Done ||
               !IsWordCharacter(previous);
    }

    private static bool IsBoundaryAfter(string value, int index)
    {
        if (!IsRuneBoundary(value, index)) return false;
        return index == value.Length ||
               Rune.DecodeFromUtf16(value.AsSpan(index), out Rune next, out _) != OperationStatus.Done ||
               !IsWordCharacter(next);
    }

    private static bool IsRuneBoundary(string value, int index) =>
        index <= 0 || index >= value.Length ||
        !char.IsHighSurrogate(value[index - 1]) || !char.IsLowSurrogate(value[index]);

    private static bool IsWordCharacter(Rune value) =>
        Rune.IsLetterOrDigit(value) || value.Value == '_' ||
        Rune.GetUnicodeCategory(value) is System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.SpacingCombiningMark or
            System.Globalization.UnicodeCategory.EnclosingMark;
}

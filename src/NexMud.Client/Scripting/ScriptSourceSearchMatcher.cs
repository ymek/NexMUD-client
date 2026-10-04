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
            bool startsAtWordBoundary = match == 0 || !IsWordCharacter(line[match - 1]);
            bool endsAtWordBoundary = afterMatch == line.Length || !IsWordCharacter(line[afterMatch]);
            if (!options.WholeWord || (startsAtWordBoundary && endsAtWordBoundary)) return match;

            searchFrom = match + query.Length;
        }

        return -1;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}

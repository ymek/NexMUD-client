using System.Text.RegularExpressions;

namespace JevMud.Adapters.Avendar;

/// <summary>
/// Parses Avendar's carried-inventory response into canonical item names while preserving
/// the game's textual item identity. Presentation code consumes state produced from this parser;
/// it never reparses transcript text.
/// </summary>
public static partial class AvendarInventoryParser
{
    public static bool TryParse(IReadOnlyList<string> lines, out IReadOnlyList<string>? items)
    {
        items = null;
        int header = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            string candidate = lines[index].Trim();
            if (candidate.Equals("You are carrying:", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("You are carrying", StringComparison.OrdinalIgnoreCase))
            {
                header = index;
                break;
            }
        }

        if (header < 0)
        {
            return false;
        }

        List<string> parsed = [];
        for (int index = header + 1; index < lines.Count; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsEmptyInventory(line))
            {
                items = Array.Empty<string>();
                return true;
            }

            string normalized = LeadingStatusFlagsRegex().Replace(line, string.Empty).Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            parsed.Add(normalized);
        }

        items = parsed;
        return true;
    }

    private static bool IsEmptyInventory(string line) =>
        line.Equals("Nothing.", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("You are carrying nothing.", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("You are carrying nothing", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?:\([^)]*\)\s*)+")]
    private static partial Regex LeadingStatusFlagsRegex();
}

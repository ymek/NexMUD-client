using System.Text.RegularExpressions;
using JevMud.Contracts.State;

namespace JevMud.Adapters.Avendar;

public static partial class AvendarEquipmentParser
{
    public static bool TryParse(
        IReadOnlyList<string> lines,
        out IReadOnlyList<EquipmentSlotState>? slots)
    {
        slots = null;
        int header = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].Trim().Equals("You are using:", StringComparison.OrdinalIgnoreCase))
            {
                header = index;
                break;
            }
        }

        if (header < 0)
        {
            return false;
        }

        Dictionary<string, int> ordinals = new(StringComparer.OrdinalIgnoreCase);
        List<EquipmentSlotState> parsed = [];
        for (int index = header + 1; index < lines.Count; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            Match match = SlotRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string slot = NormalizeSlot(match.Groups["slot"].Value);
            int ordinal = ordinals.TryGetValue(slot, out int current) ? current + 1 : 1;
            ordinals[slot] = ordinal;

            string itemText = match.Groups["item"].Value.Trim();
            string? item = itemText.Equals("[nothing]", StringComparison.OrdinalIgnoreCase)
                ? null
                : itemText;
            parsed.Add(new EquipmentSlotState(slot, ordinal, item));
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        slots = parsed;
        return true;
    }

    public static string NormalizeSlot(string slot) =>
        Regex.Replace(slot.Trim().ToLowerInvariant(), @"\s+", " ");

    [GeneratedRegex(@"^\s*<(?<slot>[^>]+)>\s+(?<item>.+?)\s*$")]
    private static partial Regex SlotRegex();
}

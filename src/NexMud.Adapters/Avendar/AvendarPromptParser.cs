using System.Collections.ObjectModel;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;

namespace NexMud.Adapters.Avendar;

public static class AvendarPromptParser
{
    public static bool TryParse(string prompt, out CharacterPromptObserved? observed)
    {
        observed = null;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        string normalized = prompt.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (!normalized.StartsWith("[J|", StringComparison.Ordinal) ||
            !normalized.EndsWith(']'))
        {
            return false;
        }

        string[] fields = normalized[1..^1].Split('|');
        if (fields.Length != 11 || fields[0] != "J")
        {
            return false;
        }

        if (!TryParsePair(fields[1], out int hp, out int maxHp) ||
            !TryParsePair(fields[2], out int mana, out int maxMana) ||
            !TryParsePair(fields[3], out int move, out int maxMove) ||
            !long.TryParse(fields[4], out long experience) ||
            !long.TryParse(fields[5], out long experienceToLevel))
        {
            return false;
        }

        ExitState exits = ParseExits(fields[8]);
        observed = new CharacterPromptObserved(
            hp,
            maxHp,
            mana,
            maxMana,
            move,
            maxMove,
            experience,
            experienceToLevel,
            fields[6].Trim(),
            fields[7].Trim(),
            exits,
            fields[9].Trim(),
            fields[10].Trim());
        return true;
    }

    private static bool TryParsePair(string value, out int current, out int maximum)
    {
        current = 0;
        maximum = 0;
        string[] parts = value.Split('/', 2);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out current) &&
               int.TryParse(parts[1], out maximum);
    }

    private static ExitState ParseExits(string value)
    {
        string trimmed = value.Trim();
        if (trimmed == "???")
        {
            return new ExitState(false, EmptyStrings());
        }

        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) || trimmed.Length == 0)
        {
            return new ExitState(true, EmptyStrings());
        }

        List<string> directions = [];
        foreach (char valueChar in trimmed)
        {
            string? direction = char.ToUpperInvariant(valueChar) switch
            {
                'N' => "north",
                'E' => "east",
                'S' => "south",
                'W' => "west",
                'U' => "up",
                'D' => "down",
                _ => null
            };

            if (direction is null)
            {
                return new ExitState(false, EmptyStrings());
            }

            directions.Add(direction);
        }

        return new ExitState(
            true,
            new ReadOnlyCollection<string>(directions));
    }

    private static IReadOnlyList<string> EmptyStrings() =>
        new ReadOnlyCollection<string>(Array.Empty<string>());
}

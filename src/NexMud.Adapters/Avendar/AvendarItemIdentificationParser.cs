using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;

namespace NexMud.Adapters.Avendar;

public static partial class AvendarItemIdentificationParser
{
    public static bool TryParse(
        IReadOnlyList<string> lines,
        out ItemIdentified? observed,
        long sourceSequence = 0)
    {
        observed = null;
        if (lines.Count == 0)
        {
            return false;
        }

        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        List<string> spells = [];
        foreach (string rawLine in lines)
        {
            Match affect = AffectRegex().Match(rawLine);
            if (affect.Success)
            {
                string stat = NormalizeKey(affect.Groups["stat"].Value);
                string amount = affect.Groups["amount"].Value.Trim();
                if (stat.Length > 0 && amount.Length > 0)
                {
                    fields[$"affect {stat}"] = amount;
                }
                continue;
            }

            Match effectRow = EffectRowRegex().Match(rawLine);
            if (effectRow.Success)
            {
                string stat = NormalizeKey(effectRow.Groups["stat"].Value);
                string amount = effectRow.Groups["amount"].Value.Trim();
                if (stat.Length > 0 && amount.Length > 0)
                {
                    fields[$"effect {stat}"] = amount;
                }
                continue;
            }

            Match match = FieldRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            string key = NormalizeKey(match.Groups["key"].Value);
            string value = match.Groups["value"].Value.Trim();
            if (key.Length > 0 && value.Length > 0)
            {
                if (key.Equals("spell", StringComparison.OrdinalIgnoreCase))
                {
                    spells.Add(value);
                }
                fields[key] = value;
            }
        }

        if (!fields.TryGetValue("object", out string? name) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        decimal? weight = TryDecimal(fields, "weight");
        int? level = TryInt(fields, "level");
        decimal? damageAverage = null;
        string? damageDice = Value(fields, "damage dice") ?? Value(fields, "damage");
        if (!string.IsNullOrWhiteSpace(damageDice))
        {
            Match damage = DamageDiceRegex().Match(damageDice);
            if (damage.Success)
            {
                damageDice = damage.Groups["dice"].Value;
                if (decimal.TryParse(
                        damage.Groups["average"].Value,
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out decimal parsedAverage))
                {
                    damageAverage = parsedAverage;
                }
            }
        }

        HashSet<string> knownKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "object", "flags", "weight", "wear", "level", "material", "type",
            "weapon type", "weapon flags", "damage type", "damage dice", "damage"
        };
        Dictionary<string, string> extra = fields
            .Where(pair => !knownKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        ItemProperty[] properties = fields
            .Where(pair => pair.Key.StartsWith("affect ", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.StartsWith("effect ", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new ItemProperty(pair.Key, pair.Value))
            .ToArray();

        ItemIdentification item = new(
            name.Trim(),
            SplitFlags(Value(fields, "flags")),
            weight,
            SplitList(Value(fields, "wear")),
            level,
            Value(fields, "material"),
            Value(fields, "type"),
            Value(fields, "weapon type"),
            SplitFlags(Value(fields, "weapon flags")),
            Value(fields, "damage type"),
            damageDice,
            damageAverage,
            new ReadOnlyDictionary<string, string>(extra),
            string.Join("\n", lines))
        {
            Spells = new ReadOnlyCollection<string>(spells.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            Size = Value(fields, "size"),
            Properties = new ReadOnlyCollection<ItemProperty>(properties),
            SourceSequence = sourceSequence
        };

        observed = new ItemIdentified(item);
        return true;
    }

    private static string NormalizeKey(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string? Value(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) ? value : null;

    private static decimal? TryDecimal(IReadOnlyDictionary<string, string> fields, string key)
    {
        string? value = Value(fields, key);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result)
            ? result
            : null;
    }

    private static int? TryInt(IReadOnlyDictionary<string, string> fields, string key)
    {
        string? value = Value(fields, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;
    }

    private static IReadOnlyList<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        return new ReadOnlyCollection<string>(
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static IReadOnlyList<string> SplitFlags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        string[] flags = Regex.Split(value.Trim(), @"[\s,]+")
            .Where(flag => flag.Length > 0)
            .ToArray();
        return new ReadOnlyCollection<string>(flags);
    }

    [GeneratedRegex(@"^\s*\|\s*Affects\s+(?<stat>.+?)\s+by\s+(?<amount>[+-]?\d+(?:\.\d+)?)\s*\|\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AffectRegex();

    [GeneratedRegex(@"^\s*\|\s*-\s+(?<stat>.+?)\s+(?<amount>[+-]?\d+(?:\.\d+)?)\s*\|\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EffectRowRegex();

    [GeneratedRegex(@"^\s*\|\s*(?<key>[^:|]+?)\s*:\s*(?<value>.*?)\s*\|\s*$")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"^(?<dice>\d+d\d+)\s*\(average\s+(?<average>\d+(?:\.\d+)?)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex DamageDiceRegex();
}

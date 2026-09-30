using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using NexMud.Contracts.Gameplay;

namespace NexMud.Adapters.Avendar;

public sealed record AvendarResourceSegments(
    ResourceValue Health,
    ResourceValue Mana,
    ResourceValue Movement,
    string Tail);

public static partial class ResourceSegmentParser
{
    public static bool TryParse(string text, out AvendarResourceSegments? segments)
    {
        ArgumentNullException.ThrowIfNull(text);
        Match match = ResourcePrefixRegex().Match(text.Trim());
        if (!match.Success)
        {
            segments = null;
            return false;
        }

        segments = new AvendarResourceSegments(
            Pair(match, "hp", "maxhp"),
            Pair(match, "mana", "maxmana"),
            Pair(match, "move", "maxmove"),
            match.Groups["rest"].Value);
        return true;
    }

    private static ResourceValue Pair(Match match, string current, string maximum) =>
        new(int.Parse(match.Groups[current].Value), int.Parse(match.Groups[maximum].Value));

    [GeneratedRegex(
        @"^<\s*(?<hp>-?\d+)\s*\(\s*(?<maxhp>-?\d+)\s*\)hp\s+" +
        @"(?<mana>-?\d+)\s*\(\s*(?<maxmana>-?\d+)\s*\)m\s+" +
        @"(?<move>-?\d+)\s*\(\s*(?<maxmove>-?\d+)\s*\)mv(?<rest>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ResourcePrefixRegex();
}

public static partial class TnlParser
{
    public static bool TryParse(string token, out long value) => TryParseKind(token, "tnl", out value);

    private static bool TryParseKind(string token, string kind, out long value)
    {
        Match match = NumericFieldRegex().Match(token);
        if (match.Success && match.Groups["kind"].Value.Equals(kind, StringComparison.OrdinalIgnoreCase))
        {
            value = long.Parse(match.Groups["value"].Value);
            return true;
        }
        value = default;
        return false;
    }

    [GeneratedRegex(@"^(?<value>-?\d+)(?<kind>tnl|ep)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericFieldRegex();
}

public static partial class ExplorationPointsParser
{
    public static bool TryParse(string token, out long value)
    {
        Match match = ExplorationRegex().Match(token);
        if (match.Success)
        {
            value = long.Parse(match.Groups["value"].Value);
            return true;
        }
        value = default;
        return false;
    }

    [GeneratedRegex(@"^(?<value>-?\d+)ep$", RegexOptions.IgnoreCase)]
    private static partial Regex ExplorationRegex();
}

public static partial class GameClockParser
{
    public static bool TryParsePrefix(string line, out GameClock? clock, out string remainder)
    {
        ArgumentNullException.ThrowIfNull(line);
        Match match = GameClockRegex().Match(line);
        if (!match.Success)
        {
            clock = null;
            remainder = line;
            return false;
        }

        int hour = int.Parse(match.Groups["hour"].Value);
        int minute = int.Parse(match.Groups["minute"].Value);
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            clock = null;
            remainder = line;
            return false;
        }

        clock = new GameClock(hour, minute);
        remainder = match.Groups["rest"].Value;
        return true;
    }

    [GeneratedRegex(@"^\s*(?<hour>\d{1,2}):(?<minute>\d{2})>\s?(?<rest>.*)$")]
    private static partial Regex GameClockRegex();
}

public static class TerrainParser
{
    public static string? Parse(string? token) => Normalize(token);

    private static string? Normalize(string? token)
    {
        string? value = token?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public static class LightParser
{
    public static string? Parse(string? token)
    {
        string? value = token?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public sealed class UnknownPromptFieldCollector
{
    private readonly List<string> _fields = [];

    public void Add(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _fields.Add(value.Trim());
        }
    }

    public IReadOnlyList<string> Snapshot() =>
        new ReadOnlyCollection<string>(_fields.ToArray());
}

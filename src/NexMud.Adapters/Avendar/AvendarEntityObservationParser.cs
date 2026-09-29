using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NexMud.Contracts.Gameplay;

namespace NexMud.Adapters.Avendar;

internal static partial class AvendarEntityObservationParser
{
    private static readonly HashSet<string> KnownStateQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "White Aura",
        "Translucent",
        "Hide",
        "Charmed",
        "Plagued",
        "Glowing",
        "Humming",
        "Invis"
    };

    public static ParsedEntityText ParseDecorators(string text)
    {
        string remaining = text.Trim();
        int count = 1;
        Match countMatch = CountRegex().Match(remaining);
        if (countMatch.Success)
        {
            count = Math.Max(1, int.Parse(countMatch.Groups["count"].Value));
            remaining = remaining[countMatch.Length..].TrimStart();
        }

        List<string> qualifiers = [];
        while (true)
        {
            Match qualifier = QualifierRegex().Match(remaining);
            if (!qualifier.Success)
            {
                break;
            }

            qualifiers.Add(qualifier.Groups["qualifier"].Value.Trim());
            remaining = remaining[qualifier.Length..].TrimStart();
        }

        string[] stateFlags = qualifiers
            .Where(KnownStateQualifiers.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ParsedEntityText(
            count,
            remaining,
            new ReadOnlyCollection<string>(qualifiers.ToArray()),
            new ReadOnlyCollection<string>(stateFlags));
    }

    public static EntityObservation CreateScanEntity(
        string line,
        long sourceSequence,
        string direction,
        int distance,
        int occurrenceIndex)
    {
        ParsedEntityText parsed = ParseDecorators(line);
        EntityObservationKind kind = parsed.Undecorated.Contains("corpse", StringComparison.OrdinalIgnoreCase)
            ? EntityObservationKind.Corpse
            : EntityObservationKind.Unknown;
        return new EntityObservation(
            CreateObservationId($"scan|{sourceSequence}|{direction}|{distance}|{occurrenceIndex}|{line.Trim()}"),
            kind,
            line.Trim(),
            null,
            parsed.Qualifiers,
            parsed.Count,
            parsed.StateFlags);
    }

    private static Guid CreateObservationId(string evidenceKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(evidenceKey));
        return new Guid(hash.AsSpan(0, 16));
    }

    internal sealed record ParsedEntityText(
        int Count,
        string Undecorated,
        IReadOnlyList<string> Qualifiers,
        IReadOnlyList<string> StateFlags);

    [GeneratedRegex(@"^\(\s*(?<count>\d+)\s*\)\s+")]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"^\((?<qualifier>[^)]+)\)\s*")]
    private static partial Regex QualifierRegex();
}

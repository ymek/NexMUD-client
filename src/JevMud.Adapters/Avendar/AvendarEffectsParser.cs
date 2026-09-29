using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using JevMud.Contracts.Gameplay;

namespace JevMud.Adapters.Avendar;

public static partial class AvendarEffectsParser
{
    public static bool TryParse(
        IReadOnlyList<string> lines,
        long sourceSequence,
        out ActiveEffectsSnapshot? snapshot)
    {
        snapshot = null;
        List<EffectBuilder> builders = [];
        EffectBuilder? current = null;

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            Match effect = EffectRegex().Match(line);
            if (effect.Success)
            {
                current = new EffectBuilder(
                    effect.Groups["name"].Value.Trim(),
                    ParseKind(effect.Groups["kind"].Value),
                    ParseDuration(effect.Groups["duration"].Value),
                    sourceSequence);
                AddModifier(current, effect.Groups["body"].Value);
                builders.Add(current);
                continue;
            }

            Match continuation = ContinuationRegex().Match(line);
            if (continuation.Success && current is not null)
            {
                AddModifier(current, continuation.Groups["body"].Value);
            }
        }

        if (builders.Count == 0)
        {
            bool explicitEmpty = lines.Any(line =>
                line.Trim().Equals("You are not affected by any spells.", StringComparison.OrdinalIgnoreCase));
            if (!explicitEmpty)
            {
                return false;
            }

            snapshot = new ActiveEffectsSnapshot(Array.Empty<ActiveEffect>(), sourceSequence);
            return true;
        }

        ActiveEffect[] effects = builders.Select(builder => builder.Build()).ToArray();
        snapshot = new ActiveEffectsSnapshot(
            new ReadOnlyCollection<ActiveEffect>(effects),
            sourceSequence);
        return true;
    }

    private static void AddModifier(EffectBuilder builder, string body)
    {
        string normalized = body.Trim();
        if (!normalized.StartsWith("modifies ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string modifier = normalized["modifies ".Length..].Trim();
        int by = modifier.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (by < 0)
        {
            builder.Modifiers.Add(new EffectModifier(modifier, string.Empty));
            return;
        }

        builder.Modifiers.Add(new EffectModifier(
            modifier[..by].Trim(),
            modifier[(by + 4)..].Trim()));
    }

    private static ActiveEffectKind ParseKind(string value) => value.ToLowerInvariant() switch
    {
        "spell" => ActiveEffectKind.Spell,
        "skill" => ActiveEffectKind.Skill,
        _ => ActiveEffectKind.Unknown
    };

    private static EffectDuration ParseDuration(string value)
    {
        string normalized = value.Trim();
        if (normalized.Equals("permanently", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("permanent", StringComparison.OrdinalIgnoreCase))
        {
            return EffectDuration.Permanent;
        }

        Match hours = HoursRegex().Match(normalized);
        if (hours.Success && decimal.TryParse(
                hours.Groups["hours"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal parsed))
        {
            return new EffectDuration(EffectDurationKind.Hours, parsed);
        }

        return EffectDuration.Unknown;
    }

    private sealed class EffectBuilder
    {
        public EffectBuilder(string name, ActiveEffectKind kind, EffectDuration duration, long sourceSequence)
        {
            Name = name;
            Kind = kind;
            Duration = duration;
            SourceSequence = sourceSequence;
        }

        public string Name { get; }
        public ActiveEffectKind Kind { get; }
        public EffectDuration Duration { get; }
        public long SourceSequence { get; }
        public List<EffectModifier> Modifiers { get; } = [];

        public ActiveEffect Build() => new(
            Name,
            Kind,
            Duration,
            new ReadOnlyCollection<EffectModifier>(Modifiers.ToArray()),
            SourceSequence);
    }

    [GeneratedRegex(
        @"^\s*(?<kind>Spell|Skill):\s*(?<name>.+?)\s*:\s*(?<body>modifies\s+.+?)\s+(?:for\s+)?(?<duration>-?\d+(?:\.\d+)?\s+hours?|permanently|permanent)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EffectRegex();

    [GeneratedRegex(
        @"^\s*:\s*(?<body>modifies\s+.+?)\s+(?:for\s+)?(?<duration>-?\d+(?:\.\d+)?\s+hours?|permanently|permanent)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationRegex();

    [GeneratedRegex(@"^(?<hours>-?\d+(?:\.\d+)?)\s+hours?$", RegexOptions.IgnoreCase)]
    private static partial Regex HoursRegex();
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JevMud.Contracts.Events;
using JevMud.Contracts.State;

namespace JevMud.Adapters.Avendar;

public sealed partial class AvendarRoomContentsParser
{
    private const int MaxBufferedLines = 512;
    private readonly List<CapturedLine> _lines = [];

    public void ObserveLine(string line, bool claimedByAnotherParser = false)
    {
        ArgumentNullException.ThrowIfNull(line);

        _lines.Add(new CapturedLine(line, claimedByAnotherParser));
        if (_lines.Count > MaxBufferedLines)
        {
            _lines.RemoveRange(0, _lines.Count - MaxBufferedLines);
        }
    }

    public bool TryComplete(string roomName, out RoomObservationObserved? observed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomName);

        try
        {
            int titleIndex = FindLastRoomTitle(roomName);
            if (titleIndex < 0)
            {
                observed = null;
                return false;
            }

            int exitsIndex = FindExitsIndex(titleIndex + 1);
            if (exitsIndex < 0)
            {
                observed = null;
                return false;
            }

            IReadOnlyList<IReadOnlyList<string>> paragraphs = SplitParagraphs(titleIndex + 1, exitsIndex);
            IReadOnlyList<string> descriptionLines = paragraphs.Count > 0
                ? paragraphs[0]
                : Array.Empty<string>();

            string description = NormalizeDescription(descriptionLines);
            IReadOnlyList<RoomExitObservation> exitDetails = ParseExitDetails(_lines[exitsIndex].Text);
            string fingerprint = ComputeFingerprint(roomName, description, exitDetails);
            List<RoomContentObservation> contents = [];
            List<string> recentObservations = [];

            for (int paragraphIndex = 1; paragraphIndex < paragraphs.Count; paragraphIndex++)
            {
                string fixtureDescription = NormalizeDescription(paragraphs[paragraphIndex]);
                if (fixtureDescription.Length > 0)
                {
                    contents.Add(CreateFixture(fixtureDescription));
                }
            }

            for (int index = exitsIndex + 1; index < _lines.Count; index++)
            {
                CapturedLine captured = _lines[index];
                if (captured.ClaimedByAnotherParser || IsNonRoomMessage(captured.Text))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(captured.Text))
                {
                    continue;
                }

                string observation = captured.Text.Trim();
                recentObservations.Add(observation);
                contents.Add(ClassifyPostExitContent(captured.Text));
            }

            observed = new RoomObservationObserved(
                RoomId: $"avendar:{fingerprint[..16]}",
                RoomName: roomName,
                DescriptionFingerprint: fingerprint,
                Description: description,
                ExitDetails: exitDetails,
                Contents: new ReadOnlyCollection<RoomContentObservation>(contents.ToArray()),
                ContentsCompleteness: ObservationCompleteness.Complete)
            {
                RecentObservations = new ReadOnlyCollection<string>(recentObservations.ToArray())
            };
            return true;
        }
        finally
        {
            Reset();
        }
    }

    public void Reset() => _lines.Clear();

    private int FindLastRoomTitle(string roomName)
    {
        for (int index = _lines.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_lines[index].Text.Trim(), roomName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private int FindExitsIndex(int start)
    {
        for (int index = start; index < _lines.Count; index++)
        {
            if (ExitsRegex().IsMatch(_lines[index].Text))
            {
                return index;
            }
        }

        return -1;
    }

    private IReadOnlyList<IReadOnlyList<string>> SplitParagraphs(int start, int endExclusive)
    {
        List<IReadOnlyList<string>> paragraphs = [];
        List<string> current = [];

        for (int index = start; index < endExclusive; index++)
        {
            string line = _lines[index].Text;
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    paragraphs.Add(new ReadOnlyCollection<string>(current.ToArray()));
                    current.Clear();
                }
                continue;
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            paragraphs.Add(new ReadOnlyCollection<string>(current.ToArray()));
        }

        return new ReadOnlyCollection<IReadOnlyList<string>>(paragraphs.ToArray());
    }

    private static string NormalizeDescription(IReadOnlyList<string> lines) =>
        string.Join(
            " ",
            lines.Select(line => WhitespaceRegex().Replace(line.Trim(), " ")))
            .Trim();

    private static string ComputeFingerprint(
        string roomName,
        string description,
        IReadOnlyList<RoomExitObservation> exitDetails)
    {
        string structuralExits = string.Join(",", exitDetails
            .Where(exit => exit.Exists)
            .Select(exit => exit.Direction.ToLowerInvariant())
            .OrderBy(direction => direction, StringComparer.Ordinal));
        string normalized = $"{roomName.Trim()}\n{description.Trim()}\n{structuralExits}".ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IReadOnlyList<RoomExitObservation> ParseExitDetails(string exitsLine)
    {
        Match match = ExitsRegex().Match(exitsLine);
        if (!match.Success)
        {
            return Array.Empty<RoomExitObservation>();
        }

        List<RoomExitObservation> exits = [];
        string body = match.Groups["body"].Value;
        foreach (string rawToken in body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool closed = rawToken.StartsWith('(') && rawToken.EndsWith(')');
            string token = closed ? rawToken[1..^1] : rawToken;
            string? direction = NormalizeDirection(token);
            if (direction is null)
            {
                continue;
            }

            exits.Add(new RoomExitObservation(
                direction,
                Exists: true,
                DoorState: closed ? ExitDoorState.Closed : ExitDoorState.Unknown,
                Traversability: closed ? ExitTraversability.Blocked : ExitTraversability.Traversable,
                BlockReason: closed ? "closed" : null));
        }

        return new ReadOnlyCollection<RoomExitObservation>(exits.ToArray());
    }

    private static RoomContentObservation ClassifyPostExitContent(string line)
    {
        string description = line.Trim();
        if (LooksLikeCorpse(description))
        {
            return new RoomContentObservation(
                description,
                Kind: RoomEntityKind.Corpse,
                Traits: RoomEntityTraits.Examinable |
                        RoomEntityTraits.Container |
                        RoomEntityTraits.Lootable |
                        RoomEntityTraits.Sacrificable |
                        RoomEntityTraits.Corpse,
                TargetKeywords: Keywords("corpse"));
        }

        if (AvendarRoomEntityClassifier.IsFixtureSubject(description))
        {
            return CreateFixture(description);
        }

        if (char.IsWhiteSpace(line[0]))
        {
            return new RoomContentObservation(
                description,
                Kind: RoomEntityKind.Object,
                Traits: RoomEntityTraits.Examinable | ReadableTrait(description),
                TargetKeywords: ExtractKnownKeywords(description));
        }

        IReadOnlyList<string> decorators = StripDecorators(description).Decorators;
        return new RoomContentObservation(
            description,
            CanonicalName: AvendarRoomEntityClassifier.ExtractOccupantCanonicalName(description),
            Kind: RoomEntityKind.Occupant,
            Traits: RoomEntityTraits.Mobile | RoomEntityTraits.Examinable,
            TargetKeywords: null,
            Decorators: decorators.Count == 0 ? null : decorators);
    }

    private static RoomContentObservation CreateFixture(string description)
    {
        RoomEntityTraits traits = RoomEntityTraits.Fixture | RoomEntityTraits.Examinable | ReadableTrait(description);
        if (ContainsWord(description, "fountain"))
        {
            traits |= RoomEntityTraits.Drinkable;
        }
        if (ContainsWord(description, "bin") || ContainsWord(description, "bins"))
        {
            traits |= RoomEntityTraits.Container;
        }

        return new RoomContentObservation(
            description,
            CanonicalName: AvendarRoomEntityClassifier.ExtractFixtureCanonicalName(description),
            Kind: RoomEntityKind.Fixture,
            Traits: traits,
            TargetKeywords: ExtractKnownKeywords(description));
    }

    private static RoomEntityTraits ReadableTrait(string description) =>
        KnownReadableKeywords.Any(keyword => ContainsWord(description, keyword))
            ? RoomEntityTraits.Readable
            : RoomEntityTraits.None;

    private static bool LooksLikeFixture(string description) =>
        KnownFixtureKeywords.Any(keyword => ContainsWord(description, keyword));

    private static bool LooksLikeCorpse(string description) =>
        CorpseRegex().IsMatch(description);

    private static IReadOnlyList<string>? ExtractKnownKeywords(string description)
    {
        string[] keywords = KnownTargetKeywords
            .Where(keyword => ContainsWord(description, keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return keywords.Length == 0 ? null : new ReadOnlyCollection<string>(keywords);
    }

    private static IReadOnlyList<string> Keywords(params string[] values) =>
        new ReadOnlyCollection<string>(values);

    private static bool ContainsWord(string description, string word) =>
        Regex.IsMatch(description, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);

    private static (string Undecorated, IReadOnlyList<string> Decorators) StripDecorators(string description)
    {
        List<string> decorators = [];
        string remaining = description;
        while (true)
        {
            Match match = DecoratorRegex().Match(remaining);
            if (!match.Success)
            {
                break;
            }

            decorators.Add(match.Groups["decorator"].Value);
            remaining = remaining[match.Length..].TrimStart();
        }

        return (remaining, new ReadOnlyCollection<string>(decorators.ToArray()));
    }

    private static bool IsNonRoomMessage(string line)
    {
        string text = line.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.StartsWith("You ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Your ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[TIPS]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[OOC]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Obvious exits:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Total found:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Players found:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("There is ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("There are ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SpeechStartRegex().IsMatch(text);
    }

    private static string? NormalizeDirection(string value) => value.Trim().ToLowerInvariant() switch
    {
        "north" => "north",
        "east" => "east",
        "south" => "south",
        "west" => "west",
        "up" => "up",
        "down" => "down",
        _ => null
    };

    private static readonly string[] KnownFixtureKeywords =
    [
        "sign", "board", "plaque", "note", "fountain", "bin", "bins", "volume"
    ];

    private static readonly string[] KnownReadableKeywords =
    [
        "sign", "board", "plaque", "note", "book", "volume", "scroll", "tablet", "tablets"
    ];

    private static readonly string[] KnownTargetKeywords =
    [
        "sign", "board", "plaque", "note", "fountain", "bin", "bins", "book", "volume", "scroll", "tablet", "tablets", "corpse"
    ];

    private sealed record CapturedLine(string Text, bool ClaimedByAnotherParser);

    [GeneratedRegex(@"^\s*\[Exits:\s*(?<body>.*)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ExitsRegex();

    [GeneratedRegex(@"^(?<decorator>\([^)]*\))\s*")]
    private static partial Regex DecoratorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^.+?\s+(?:tells you|says),\s*'", RegexOptions.IgnoreCase)]
    private static partial Regex SpeechStartRegex();

    [GeneratedRegex(@"^(?:(?:\([^)]*\)|[!?])\s*)*(?:(?:a|an|the)\s+)?corpse\b|\bcorpse of\b", RegexOptions.IgnoreCase)]
    private static partial Regex CorpseRegex();
}

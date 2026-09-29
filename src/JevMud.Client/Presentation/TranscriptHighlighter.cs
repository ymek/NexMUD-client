using System.Text.RegularExpressions;
using JevMud.Client.Settings;
using JevMud.Transport.Text;

namespace JevMud.Client.Presentation;

public sealed record TranscriptPresentationSegment(
    string Text,
    AnsiTextStyle AnsiStyle,
    TranscriptHighlightRule? Highlight);

public sealed class TranscriptHighlighter
{
    private sealed record RuleMatcher(TranscriptHighlightRule Rule, Regex? Regex);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private readonly IReadOnlyList<RuleMatcher> _matchers;

    public TranscriptHighlighter(IEnumerable<TranscriptHighlightRule>? rules)
    {
        _matchers = (rules ?? Array.Empty<TranscriptHighlightRule>())
            .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(CreateMatcher)
            .Where(matcher => matcher is not null)
            .Cast<RuleMatcher>()
            .ToArray();
    }

    public IReadOnlyList<TranscriptPresentationSegment> Apply(AnsiTextSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        List<TranscriptPresentationSegment> pieces = [new(segment.Text, segment.Style, null)];
        foreach (RuleMatcher matcher in _matchers)
        {
            List<TranscriptPresentationSegment> next = [];
            foreach (TranscriptPresentationSegment piece in pieces)
            {
                if (piece.Highlight is not null || piece.Text.Length == 0)
                {
                    next.Add(piece);
                    continue;
                }

                Split(piece, matcher, next);
            }
            pieces = next;
        }

        return pieces;
    }

    private static RuleMatcher? CreateMatcher(TranscriptHighlightRule rule)
    {
        if (rule.MatchMode == HighlightMatchMode.Literal)
        {
            return new RuleMatcher(rule, null);
        }

        try
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!rule.CaseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }
            return new RuleMatcher(rule, new Regex(rule.Pattern, options, RegexTimeout));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void Split(
        TranscriptPresentationSegment piece,
        RuleMatcher matcher,
        List<TranscriptPresentationSegment> output)
    {
        if (matcher.Regex is not null)
        {
            SplitRegex(piece, matcher, output);
            return;
        }

        StringComparison comparison = matcher.Rule.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        int cursor = 0;
        while (cursor < piece.Text.Length)
        {
            int index = piece.Text.IndexOf(matcher.Rule.Pattern, cursor, comparison);
            if (index < 0)
            {
                break;
            }

            if (index > cursor)
            {
                output.Add(new(piece.Text[cursor..index], piece.AnsiStyle, null));
            }
            int end = index + matcher.Rule.Pattern.Length;
            output.Add(new(piece.Text[index..end], piece.AnsiStyle, matcher.Rule));
            cursor = end;
        }

        if (cursor < piece.Text.Length)
        {
            output.Add(new(piece.Text[cursor..], piece.AnsiStyle, null));
        }
        else if (cursor == 0)
        {
            output.Add(piece);
        }
    }

    private static void SplitRegex(
        TranscriptPresentationSegment piece,
        RuleMatcher matcher,
        List<TranscriptPresentationSegment> output)
    {
        MatchCollection matches;
        try
        {
            matches = matcher.Regex!.Matches(piece.Text);
        }
        catch (RegexMatchTimeoutException)
        {
            output.Add(piece);
            return;
        }

        int cursor = 0;
        bool matched = false;
        foreach (Match match in matches)
        {
            if (!match.Success || match.Length == 0 || match.Index < cursor)
            {
                continue;
            }

            matched = true;
            if (match.Index > cursor)
            {
                output.Add(new(piece.Text[cursor..match.Index], piece.AnsiStyle, null));
            }
            output.Add(new(piece.Text.Substring(match.Index, match.Length), piece.AnsiStyle, matcher.Rule));
            cursor = match.Index + match.Length;
        }

        if (!matched)
        {
            output.Add(piece);
            return;
        }
        if (cursor < piece.Text.Length)
        {
            output.Add(new(piece.Text[cursor..], piece.AnsiStyle, null));
        }
    }
}

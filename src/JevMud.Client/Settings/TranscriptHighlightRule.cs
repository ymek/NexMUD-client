namespace JevMud.Client.Settings;

public enum HighlightMatchMode
{
    Literal,
    Regex
}

public sealed record TranscriptHighlightRule(
    string Pattern,
    string Foreground,
    HighlightMatchMode MatchMode = HighlightMatchMode.Literal,
    bool Bold = true,
    bool Underline = false,
    bool CaseSensitive = false,
    bool Enabled = true);

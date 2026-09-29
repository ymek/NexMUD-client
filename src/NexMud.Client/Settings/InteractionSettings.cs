namespace NexMud.Client.Settings;

public sealed record InputPreferences(
    int HistoryMaximumEntries = 500,
    bool PersistHistory = true,
    bool DeduplicateConsecutiveHistory = true,
    bool CompletionEnabled = true,
    int CompletionTokenLimit = 6_000,
    bool LocalEcho = true);

public sealed record OutputPreferences(
    TimestampRenderMode TimestampMode = TimestampRenderMode.Off,
    int ScrollbackMaximumEntries = 5_000,
    bool SplitOutputEnabled = true,
    bool NotifyWhenUnfocused = true);

public enum TimestampRenderMode
{
    Off,
    Time,
    TimeWithMilliseconds,
    DateTime
}

public enum OutputRuleMatchType
{
    Substring,
    Regex
}

public enum OutputRuleActionKind
{
    Highlight,
    Gag,
    Substitute,
    Capture,
    Notify,
    Beep,
    LogMarker
}

public sealed record OutputRuleAction(
    OutputRuleActionKind Kind,
    string? Text = null,
    string? Foreground = null,
    string? Background = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false);

public sealed record OutputTransformationRule(
    string Id,
    string Name,
    string Pattern,
    OutputRuleMatchType MatchType = OutputRuleMatchType.Substring,
    bool CaseSensitive = false,
    int Priority = 0,
    bool Enabled = true,
    IReadOnlyList<OutputRuleAction>? Actions = null)
{
    public IReadOnlyList<OutputRuleAction> EffectiveActions => Actions ?? Array.Empty<OutputRuleAction>();
}

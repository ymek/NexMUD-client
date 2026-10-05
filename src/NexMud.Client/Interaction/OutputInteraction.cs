using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NexMud.Adapters.Avendar;
using NexMud.Client.Settings;
using NexMud.Client.Presentation;
using NexMud.Contracts.Actions;
using NexMud.Contracts.Events;
using NexMud.Contracts.Gameplay;
using NexMud.Contracts.Jev;
using NexMud.Core.Events;
using NexMud.Transport.Text;

namespace NexMud.Client.Interaction;

public enum OutputFrameSource
{
    Server,
    LocalEcho,
    System,
    Replay
}

public sealed record OutputFrame(
    Guid FrameId,
    DateTimeOffset Timestamp,
    string RawText,
    string PlainText,
    IReadOnlyList<AnsiTextSegment> AnsiRuns,
    bool IsPrompt,
    OutputFrameSource Source,
    long Sequence,
    bool IsReplay = false);

public sealed record OutputPresentationSeed(
    string PlainText,
    IReadOnlyList<AnsiTextSegment> AnsiRuns,
    bool IsPrompt);

public sealed record OutputCapture(
    string RuleId,
    IReadOnlyDictionary<string, string> Values);

public sealed record OutputNotification(
    string RuleId,
    string Message,
    bool Beep);

public sealed record OutputRuleDiagnostic(
    string RuleId,
    string Message,
    bool TimedOut = false);

public sealed record OutputPresentationStyle(
    string? Foreground = null,
    string? Background = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false);

public sealed record WorldStyledRun(
    string Text,
    AnsiTextStyle AnsiStyle,
    OutputPresentationStyle? Override = null);

public sealed record WorldBufferEntry(
    Guid EntryId,
    DateTimeOffset Timestamp,
    Guid SourceFrameId,
    string RenderedText,
    IReadOnlyList<WorldStyledRun> StyledRuns,
    bool IsPrompt,
    bool IsLocalEcho,
    bool IsGagged,
    OutputFrameSource Source,
    IReadOnlyList<OutputCapture> Captures,
    IReadOnlyList<OutputNotification> Notifications);


public sealed record WorldBufferSearchOptions(
    bool CaseSensitive = false,
    bool Regex = false,
    int Limit = 100);

public sealed record WorldBufferSearchResult(Guid EntryId, int Index, string Text, DateTimeOffset Timestamp);

/// <summary>
/// Bounded source-frame journal. Display transformations never mutate these frames, so semantic-preserving
/// logging/replay can remain independent from the rendered WorldBuffer.
/// </summary>
public sealed class OutputFrameJournal
{
    private readonly object _sync = new();
    private readonly List<OutputFrame> _frames = [];
    private int _maximumEntries;

    public OutputFrameJournal(int maximumEntries = 5_000) => Configure(maximumEntries);

    public void Configure(int maximumEntries)
    {
        lock (_sync)
        {
            _maximumEntries = Math.Clamp(maximumEntries, 250, 100_000);
            Trim();
        }
    }

    public void Append(OutputFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sync)
        {
            _frames.Add(frame);
            Trim();
        }
    }

    public IReadOnlyList<OutputFrame> Snapshot()
    {
        lock (_sync) return _frames.ToArray();
    }

    public OutputFrame? Find(Guid frameId)
    {
        lock (_sync) return _frames.LastOrDefault(frame => frame.FrameId == frameId);
    }

    private void Trim()
    {
        if (_frames.Count > _maximumEntries)
            _frames.RemoveRange(0, _frames.Count - _maximumEntries);
    }
}

/// <summary>Bounded logical scrollback store. Avalonia is a projection over this buffer, not its source of truth.</summary>
public sealed class WorldBuffer
{
    private readonly object _sync = new();
    private readonly List<WorldBufferEntry> _entries = [];
    private int _maximumEntries;

    public WorldBuffer(int maximumEntries = 5_000) => Configure(maximumEntries);

    public event Action<WorldBufferEntry>? Appended;

    public void Configure(int maximumEntries)
    {
        lock (_sync)
        {
            _maximumEntries = Math.Clamp(maximumEntries, 250, 100_000);
            Trim();
        }
    }

    public void Append(WorldBufferEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _entries.Add(entry);
            Trim();
        }
        PublishAppended(entry);
    }

    public IReadOnlyList<WorldBufferEntry> Snapshot(bool includeGagged = false)
    {
        lock (_sync)
            return _entries.Where(entry => includeGagged || !entry.IsGagged).ToArray();
    }

    public IReadOnlyList<WorldBufferEntry> Tail(int count, bool includeGagged = false)
    {
        int bounded = Math.Max(0, count);
        lock (_sync)
        {
            IEnumerable<WorldBufferEntry> source = _entries.Where(entry => includeGagged || !entry.IsGagged);
            int total = source.Count();
            return source.Skip(Math.Max(0, total - bounded)).ToArray();
        }
    }

    public IReadOnlyList<WorldBufferSearchResult> Search(string query, WorldBufferSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        options ??= new WorldBufferSearchOptions();
        int limit = Math.Clamp(options.Limit, 1, 1_000);
        List<WorldBufferSearchResult> results = [];
        Regex? regex = null;
        if (options.Regex && query.Length > 0)
        {
            RegexOptions regexOptions = RegexOptions.CultureInvariant;
            if (!options.CaseSensitive) regexOptions |= RegexOptions.IgnoreCase;
            try
            {
                regex = new Regex(query, regexOptions, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return results;
            }
        }

        lock (_sync)
        {
            for (int index = 0; index < _entries.Count && results.Count < limit; index++)
            {
                WorldBufferEntry entry = _entries[index];
                if (entry.IsGagged) continue;
                bool match;
                try
                {
                    match = regex is not null
                        ? regex.IsMatch(entry.RenderedText)
                        : entry.RenderedText.Contains(query, options.CaseSensitive
                            ? StringComparison.Ordinal
                            : StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
                if (match)
                    results.Add(new WorldBufferSearchResult(entry.EntryId, index, entry.RenderedText, entry.Timestamp));
            }
        }
        return results;
    }

    private void PublishAppended(WorldBufferEntry entry)
    {
        Delegate[] subscribers = Appended?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Delegate subscriber in subscribers)
        {
            try { ((Action<WorldBufferEntry>)subscriber)(entry); }
            catch { /* A view subscriber must never fault the logical output pipeline. */ }
        }
    }

    private void Trim()
    {
        if (_entries.Count > _maximumEntries)
            _entries.RemoveRange(0, _entries.Count - _maximumEntries);
    }
}

public sealed class OutputFrameFactory
{
    private readonly AnsiTextParser _sourceAnsi = new();
    private readonly DisplayLineEndingNormalizer _sourceLineEndings = new();
    private readonly AvendarPromptDisplayFilter _promptFilter = new();
    private readonly AnsiTextParser _presentationAnsi = new();
    private readonly DisplayLineEndingNormalizer _presentationLineEndings = new();
    private long _sequence;
    private bool _slurpPrompt;

    public void Configure(bool slurpPrompt)
    {
        if (_slurpPrompt == slurpPrompt) return;
        _slurpPrompt = slurpPrompt;
        _promptFilter.Reset();
        _presentationLineEndings.Reset();
        _presentationAnsi.Reset();
    }

    public void Reset()
    {
        _promptFilter.Reset();
        _sourceLineEndings.Reset();
        _presentationLineEndings.Reset();
        _sourceAnsi.Reset();
        _presentationAnsi.Reset();
    }

    /// <summary>
    /// Creates the immutable source representation before any prompt slurp or user transformation.
    /// Even frames with no visible glyphs are journaled so raw logging/replay can remain lossless.
    /// </summary>
    public OutputFrame CreateServerFrame(string rawText, DateTimeOffset timestamp, bool replay = false)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        string normalized = _sourceLineEndings.Process(rawText);
        IReadOnlyList<AnsiTextSegment> runs = _sourceAnsi.Process(normalized).ToArray();
        string plain = string.Concat(runs.Select(run => run.Text));
        return new OutputFrame(
            Guid.NewGuid(),
            timestamp,
            rawText,
            plain,
            runs,
            IsPrompt: plain.TrimStart().StartsWith("[J|", StringComparison.Ordinal),
            replay ? OutputFrameSource.Replay : OutputFrameSource.Server,
            Interlocked.Increment(ref _sequence),
            replay);
    }

    /// <summary>Builds a display seed from the source stream without mutating the source frame.</summary>
    public OutputPresentationSeed? CreatePresentation(OutputFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Source is not (OutputFrameSource.Server or OutputFrameSource.Replay))
            return new OutputPresentationSeed(frame.PlainText, frame.AnsiRuns, frame.IsPrompt);

        string displayRaw = _slurpPrompt ? _promptFilter.Process(frame.RawText) : frame.RawText;
        string normalized = _presentationLineEndings.Process(displayRaw);
        IReadOnlyList<AnsiTextSegment> runs = _presentationAnsi.Process(normalized).ToArray();
        if (runs.Count == 0) return null;
        string plain = string.Concat(runs.Select(run => run.Text));
        if (plain.Length == 0) return null;
        return new OutputPresentationSeed(
            plain,
            runs,
            plain.TrimStart().StartsWith("[J|", StringComparison.Ordinal));
    }

    public OutputFrame CreateLocalEcho(string text, DateTimeOffset timestamp, AnsiTextStyle? style = null) =>
        new(
            Guid.NewGuid(),
            timestamp,
            text,
            text,
            [new AnsiTextSegment(text, style ?? AnsiTextStyle.Default)],
            false,
            OutputFrameSource.LocalEcho,
            Interlocked.Increment(ref _sequence));
}

public sealed record OutputTransformResult(WorldBufferEntry Entry, IReadOnlyList<OutputNotification> Notifications);

/// <summary>
/// Deterministic display-only transformation. Matching is bounded and failures never alter the source frame.
/// </summary>
public sealed class OutputTransformationService
{
    private sealed record Matcher(OutputTransformationRule Rule, Regex? Regex);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private IReadOnlyList<Matcher> _matchers = Array.Empty<Matcher>();

    public event Action<OutputRuleDiagnostic>? Diagnostic;

    public void Configure(
        IEnumerable<OutputTransformationRule>? rules,
        IEnumerable<TranscriptHighlightRule>? legacyHighlights)
    {
        List<OutputTransformationRule> combined = [];
        if (rules is not null) combined.AddRange(rules);
        int legacyIndex = 0;
        foreach (TranscriptHighlightRule legacy in legacyHighlights ?? Array.Empty<TranscriptHighlightRule>())
        {
            combined.Add(new OutputTransformationRule(
                $"legacy-highlight-{legacyIndex++}",
                $"Highlight: {legacy.Pattern}",
                legacy.Pattern,
                legacy.MatchMode == HighlightMatchMode.Regex ? OutputRuleMatchType.Regex : OutputRuleMatchType.Substring,
                legacy.CaseSensitive,
                Priority: -1000,
                Enabled: legacy.Enabled,
                Actions:
                [
                    new OutputRuleAction(
                        OutputRuleActionKind.Highlight,
                        Foreground: legacy.Foreground,
                        Bold: legacy.Bold,
                        Underline: legacy.Underline)
                ]));
        }

        List<Matcher> matchers = [];
        foreach (OutputTransformationRule rule in combined
                     .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.Pattern))
                     .OrderByDescending(rule => rule.Priority)
                     .ThenBy(rule => rule.Id, StringComparer.Ordinal))
        {
            Matcher? matcher = CreateMatcher(rule);
            if (matcher is not null) matchers.Add(matcher);
        }
        _matchers = matchers;
    }

    public OutputTransformResult Transform(OutputFrame frame, OutputPresentationSeed? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        presentation ??= new OutputPresentationSeed(frame.PlainText, frame.AnsiRuns, frame.IsPrompt);
        List<(Matcher Matcher, MatchData Match)> sourceMatches = [];
        foreach (Matcher matcher in _matchers)
        {
            foreach (MatchData match in FindMatches(matcher, presentation.PlainText))
                sourceMatches.Add((matcher, match));
        }

        List<OutputCapture> captures = [];
        List<OutputNotification> notifications = [];
        bool gagged = false;
        string rendered = presentation.PlainText;

        foreach ((Matcher matcher, MatchData match) in sourceMatches)
        {
            foreach (OutputRuleAction action in matcher.Rule.EffectiveActions)
            {
                switch (action.Kind)
                {
                    case OutputRuleActionKind.Capture:
                        captures.Add(new OutputCapture(matcher.Rule.Id, match.Captures));
                        break;
                    case OutputRuleActionKind.Gag:
                        gagged = true;
                        break;
                    case OutputRuleActionKind.Notify:
                    case OutputRuleActionKind.Beep:
                        notifications.Add(new OutputNotification(
                            matcher.Rule.Id,
                            ExpandTemplate(action.Text ?? matcher.Rule.Name, match.Captures),
                            action.Kind == OutputRuleActionKind.Beep));
                        break;
                }
            }
        }

        // Substitutions intentionally re-match the current rendered text in deterministic rule order.
        foreach (Matcher matcher in _matchers)
        {
            foreach (OutputRuleAction action in matcher.Rule.EffectiveActions.Where(action => action.Kind == OutputRuleActionKind.Substitute))
                rendered = Substitute(matcher, rendered, action.Text ?? string.Empty);
        }

        IReadOnlyList<WorldStyledRun> runs = BuildStyledRuns(presentation, rendered);
        foreach (Matcher matcher in _matchers)
        {
            OutputRuleAction? highlight = matcher.Rule.EffectiveActions.FirstOrDefault(action => action.Kind == OutputRuleActionKind.Highlight);
            if (highlight is null) continue;
            MatchData[] highlightMatches = FindMatches(matcher, rendered).ToArray();
            if (highlightMatches.Length == 0) continue;
            runs = ApplyHighlight(runs, highlightMatches, highlight);
        }

        WorldBufferEntry entry = new(
            Guid.NewGuid(),
            frame.Timestamp,
            frame.FrameId,
            rendered,
            runs,
            presentation.IsPrompt,
            frame.Source == OutputFrameSource.LocalEcho,
            gagged,
            frame.Source,
            captures,
            notifications);
        return new OutputTransformResult(entry, notifications);
    }

    private Matcher? CreateMatcher(OutputTransformationRule rule)
    {
        if (rule.MatchType == OutputRuleMatchType.Substring) return new Matcher(rule, null);
        if (rule.Pattern.Length > 4_096)
        {
            Diagnostic?.Invoke(new OutputRuleDiagnostic(rule.Id, "Regex pattern exceeds 4096 characters."));
            return null;
        }
        try
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!rule.CaseSensitive) options |= RegexOptions.IgnoreCase;
            return new Matcher(rule, new Regex(rule.Pattern, options, RegexTimeout));
        }
        catch (ArgumentException exception)
        {
            Diagnostic?.Invoke(new OutputRuleDiagnostic(rule.Id, $"Invalid regex: {exception.Message}"));
            return null;
        }
    }

    private sealed record MatchData(int Index, int Length, IReadOnlyDictionary<string, string> Captures);

    private IEnumerable<MatchData> FindMatches(Matcher matcher, string text)
    {
        if (matcher.Regex is null)
        {
            StringComparison comparison = matcher.Rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int cursor = 0;
            while (cursor <= text.Length - matcher.Rule.Pattern.Length)
            {
                int index = text.IndexOf(matcher.Rule.Pattern, cursor, comparison);
                if (index < 0) yield break;
                yield return new MatchData(index, matcher.Rule.Pattern.Length, new Dictionary<string, string>
                {
                    ["0"] = text.Substring(index, matcher.Rule.Pattern.Length)
                });
                cursor = index + Math.Max(1, matcher.Rule.Pattern.Length);
            }
            yield break;
        }

        MatchCollection matches;
        try { matches = matcher.Regex.Matches(text); }
        catch (RegexMatchTimeoutException)
        {
            Diagnostic?.Invoke(new OutputRuleDiagnostic(matcher.Rule.Id, "Regex match timed out.", TimedOut: true));
            yield break;
        }
        foreach (Match match in matches.Cast<Match>())
        {
            if (!match.Success) continue;
            Dictionary<string, string> captures = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < match.Groups.Count; index++) captures[index.ToString()] = match.Groups[index].Value;
            foreach (string name in matcher.Regex.GetGroupNames()) captures[name] = match.Groups[name].Value;
            yield return new MatchData(match.Index, match.Length, captures);
        }
    }

    private string Substitute(Matcher matcher, string text, string replacement)
    {
        try
        {
            if (matcher.Regex is not null)
                return matcher.Regex.Replace(text, replacement);

            StringComparison comparison = matcher.Rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (matcher.Rule.Pattern.Length == 0) return text;
            StringBuilder output = new(text.Length);
            int cursor = 0;
            while (cursor < text.Length)
            {
                int index = text.IndexOf(matcher.Rule.Pattern, cursor, comparison);
                if (index < 0) break;
                output.Append(text, cursor, index - cursor);
                output.Append(replacement);
                cursor = index + matcher.Rule.Pattern.Length;
            }
            output.Append(text, cursor, text.Length - cursor);
            return output.ToString();
        }
        catch (RegexMatchTimeoutException)
        {
            Diagnostic?.Invoke(new OutputRuleDiagnostic(matcher.Rule.Id, "Regex substitution timed out.", TimedOut: true));
            return text;
        }
    }

    private static string ExpandTemplate(string text, IReadOnlyDictionary<string, string> captures)
    {
        string result = text;
        foreach ((string key, string value) in captures)
            result = result.Replace($"${{{key}}}", value, StringComparison.Ordinal);
        return result;
    }

    private static IReadOnlyList<WorldStyledRun> BuildStyledRuns(OutputPresentationSeed presentation, string rendered)
    {
        if (string.Equals(rendered, presentation.PlainText, StringComparison.Ordinal))
            return presentation.AnsiRuns.Select(run => new WorldStyledRun(run.Text, run.Style)).ToArray();

        // A substitution creates display text that did not exist in the source ANSI stream. Keep the source
        // immutable and render the substituted projection with a neutral base style; subsequent user highlights
        // still apply deterministically.
        return [new WorldStyledRun(rendered, AnsiTextStyle.Default)];
    }

    private static IReadOnlyList<WorldStyledRun> ApplyHighlight(
        IReadOnlyList<WorldStyledRun> input,
        IReadOnlyList<MatchData> matches,
        OutputRuleAction action)
    {
        List<WorldStyledRun> output = [];
        int absoluteOffset = 0;
        foreach (WorldStyledRun run in input)
        {
            int runStart = absoluteOffset;
            int runEnd = runStart + run.Text.Length;
            absoluteOffset = runEnd;

            if (run.Text.Length == 0 || run.Override is not null)
            {
                output.Add(run);
                continue;
            }

            int cursor = 0;
            foreach (MatchData match in matches)
            {
                int matchStart = match.Index;
                int matchEnd = match.Index + match.Length;
                int overlapStart = Math.Max(runStart, matchStart);
                int overlapEnd = Math.Min(runEnd, matchEnd);
                if (overlapStart >= overlapEnd) continue;

                int localStart = overlapStart - runStart;
                int localEnd = overlapEnd - runStart;
                if (localStart > cursor)
                    output.Add(run with { Text = run.Text[cursor..localStart] });
                output.Add(run with
                {
                    Text = run.Text[localStart..localEnd],
                    Override = new OutputPresentationStyle(
                        action.Foreground,
                        action.Background,
                        action.Bold,
                        action.Italic,
                        action.Underline)
                });
                cursor = localEnd;
            }

            if (cursor < run.Text.Length)
                output.Add(run with { Text = run.Text[cursor..] });
        }
        return output;
    }

}

public sealed class ClientInteractionRuntime
{
    private readonly Func<ClientSettings> _settings;
    private readonly ChannelReader<EventEnvelope>? _events;
    private readonly object _processingGate = new();

    public ClientInteractionRuntime(
        Func<ClientSettings> settings,
        ChannelReader<EventEnvelope>? events = null,
        IInputAliasResolver? aliasResolver = null,
        IInputCommandDispatcher? commandDispatcher = null,
        ICommandHistoryPersistence? historyPersistence = null,
        Func<NexMud.Contracts.State.StateSnapshot>? state = null)
    {
        _settings = settings;
        _events = events;
        History = new CommandHistoryService();
        Completion = new CompletionService();
        List<ICompletionProvider> completionProviders =
        [
            new StaticCompletionProvider(
            [
                "look", "consider", "scan", "where", "who", "score", "inventory", "equipment",
                "skills", "spells", "flee", "get", "drop", "wear", "remove", "open", "close",
                "lock", "unlock", "read", "examine", "drink", "sacrifice", "say", "tell"
            ]),
            new AliasCompletionProvider(settings)
        ];
        if (state is not null) completionProviders.Add(new StateCompletionProvider(state));
        Completion.SetProviders(completionProviders);
        Input = new InputPipeline(
            settings,
            History,
            Completion,
            new CommandTokenizer(),
            aliasResolver,
            commandDispatcher,
            historyPersistence);
        Editing = new InputEditingService(History, Completion);
        Keybindings = new KeybindingService();
        Frames = new OutputFrameFactory();
        Transformations = new OutputTransformationService();
        Transformations.Diagnostic += PublishRuleDiagnostic;
        SourceFrames = new OutputFrameJournal();
        World = new WorldBuffer();
        Configure(settings());
    }

    public ICommandHistory History { get; }
    public CompletionService Completion { get; }
    public InputPipeline Input { get; }
    public InputEditingService Editing { get; }
    public KeybindingService Keybindings { get; }
    public OutputFrameFactory Frames { get; }
    public OutputTransformationService Transformations { get; }
    public OutputFrameJournal SourceFrames { get; }
    public WorldBuffer World { get; }

    public event Action<OutputNotification>? NotificationRequested;
    public event Action<OutputRuleDiagnostic>? RuleDiagnostic;

    public void Configure(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InputPreferences input = settings.Input ?? new InputPreferences();
        OutputPreferences output = settings.Output ?? new OutputPreferences();
        if (History is CommandHistoryService history)
            history.Configure(input.HistoryMaximumEntries, input.DeduplicateConsecutiveHistory);
        Completion.Configure(input.CompletionTokenLimit);
        Keybindings.Configure(settings.KeyBindings, settings.Workflows, settings.Automation);
        lock (_processingGate)
        {
            Frames.Configure(settings.SlurpTelemetryPrompt);
            Transformations.Configure(settings.OutputRules, settings.HighlightRules);
            SourceFrames.Configure(output.ScrollbackMaximumEntries);
            World.Configure(output.ScrollbackMaximumEntries);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_events is null) return;
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (envelope.Payload)
                {
                    case GameObservationReceived observation when
                        !observation.Observation.Metadata.IsLocal &&
                        observation.Observation.Kind is ObservationKind.Text or ObservationKind.ReplayMarker:
                        ProcessServerOutput(
                            observation.Observation.RawText,
                            observation.Observation.ReceivedAt,
                            replay: observation.Observation.Kind == ObservationKind.ReplayMarker);
                        break;
                    case ActionDispatching action when CommandInputPolicy.ShouldEchoToTranscript(action.Sensitive):
                    {
                        ClientSettings settings = _settings();
                        OutputPreferences output = settings.Output ?? new OutputPreferences();
                        if (output.ShowCommandEcho && (settings.Input ?? new InputPreferences()).LocalEcho)
                            AppendCommandEcho(action, envelope.Timestamp, output.ShowCommandProvenance);
                        break;
                    }
                    case ConnectionStateChanged connection when connection.Status == ConnectionStatus.Disconnected:
                        lock (_processingGate) Frames.Reset();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public WorldBufferEntry? ProcessServerOutput(string rawText, DateTimeOffset timestamp, bool replay = false)
    {
        OutputTransformResult? result;
        lock (_processingGate)
        {
            OutputFrame frame = Frames.CreateServerFrame(rawText, timestamp, replay);
            SourceFrames.Append(frame);
            OutputPresentationSeed? presentation = Frames.CreatePresentation(frame);
            if (presentation is null) return null;
            Completion.IndexText(presentation.PlainText);
            result = Transformations.Transform(frame, presentation);
        }
        World.Append(result.Entry);
        foreach (OutputNotification notification in result.Notifications)
            PublishNotification(notification);
        return result.Entry;
    }

    public WorldBufferEntry AppendLocalEcho(string text, DateTimeOffset timestamp, AnsiTextStyle? style = null)
    {
        OutputTransformResult result;
        lock (_processingGate)
        {
            OutputFrame frame = Frames.CreateLocalEcho(text, timestamp, style);
            SourceFrames.Append(frame);
            result = Transformations.Transform(frame);
        }
        World.Append(result.Entry);
        foreach (OutputNotification notification in result.Notifications)
            PublishNotification(notification);
        return result.Entry;
    }

    private void PublishNotification(OutputNotification notification)
    {
        Delegate[] subscribers = NotificationRequested?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Delegate subscriber in subscribers)
        {
            try { ((Action<OutputNotification>)subscriber)(notification); }
            catch { /* Notification delivery is isolated from output processing. */ }
        }
    }

    private void PublishRuleDiagnostic(OutputRuleDiagnostic diagnostic)
    {
        Delegate[] subscribers = RuleDiagnostic?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Delegate subscriber in subscribers)
        {
            try { ((Action<OutputRuleDiagnostic>)subscriber)(diagnostic); }
            catch { /* Diagnostics are observability; they cannot break output processing. */ }
        }
    }

    private void AppendCommandEcho(ActionDispatching action, DateTimeOffset timestamp, bool showProvenance)
    {
        string command = action.Command.Length == 0 ? "<enter>" : action.Command;
        CommandOrigin origin = action.Provenance?.Origin ?? action.Source switch
        {
            DecisionSource.Human => CommandOrigin.User,
            DecisionSource.Jev or DecisionSource.Hybrid => CommandOrigin.Jev,
            _ => CommandOrigin.System
        };
        string sourcePrefix = !showProvenance ? "> " : origin switch
        {
            CommandOrigin.User => "> ",
            CommandOrigin.Alias => "[Alias] > ",
            CommandOrigin.Keybinding => "[Key] > ",
            CommandOrigin.Automation => "[Automation] > ",
            CommandOrigin.Jev => "[Jev] > ",
            CommandOrigin.Mapper => "[Mapper] > ",
            CommandOrigin.Script => "[Script] > ",
            _ => "[System] > "
        };

        AnsiColor echoColor = origin switch
        {
            CommandOrigin.Jev => new AnsiColor(255, 205, 92),
            CommandOrigin.Alias => new AnsiColor(150, 220, 255),
            CommandOrigin.Keybinding => new AnsiColor(100, 230, 210),
            CommandOrigin.Automation => new AnsiColor(178, 156, 255),
            CommandOrigin.Mapper => new AnsiColor(105, 200, 255),
            CommandOrigin.Script => new AnsiColor(210, 150, 255),
            CommandOrigin.System => new AnsiColor(255, 224, 128),
            _ => new AnsiColor(70, 220, 255)
        };
        AppendLocalEcho(
            $"{sourcePrefix}{command}\n",
            timestamp,
            AnsiTextStyle.Default with { Foreground = echoColor, Bold = true, Faint = false });
    }
}

internal sealed class DisplayLineEndingNormalizer
{
    private bool _pendingCarriageReturn;
    private bool _lastOutputWasLineFeed;

    public string Process(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        StringBuilder output = new(text.Length);
        int index = 0;
        if (_pendingCarriageReturn)
        {
            if (text[0] == '\n')
            {
                AppendLineFeed(output);
                index = 1;
            }
            else if (text[0] == '\0')
            {
                index = 1;
                _lastOutputWasLineFeed = false;
            }
            else
            {
                // A bare CR is a carriage-return operation, not a vertical line
                // advance. Static transcript rendering cannot overprint an existing
                // row, so consuming it is closer to terminal semantics than letting
                // Avalonia interpret it as another line break.
                _lastOutputWasLineFeed = false;
            }
            _pendingCarriageReturn = false;
        }
        for (; index < text.Length; index++)
        {
            char value = text[index];
            if (value == '\n')
            {
                AppendLineFeed(output);
                continue;
            }

            if (value != '\r')
            {
                output.Append(value);
                _lastOutputWasLineFeed = false;
                continue;
            }

            // Diku/Merc-derived MUDs commonly emit LFCR ("\n\r") rather than
            // conventional CRLF. A terminal treats the CR as returning to column
            // zero on the row already advanced by LF. A text control treats both
            // characters as line breaks, which creates an invented blank row after
            // every server line. Consume the CR when it follows an emitted LF.
            if (_lastOutputWasLineFeed)
            {
                _lastOutputWasLineFeed = false;
                continue;
            }

            if (index + 1 >= text.Length)
            {
                _pendingCarriageReturn = true;
                continue;
            }

            if (text[index + 1] == '\n')
            {
                AppendLineFeed(output);
                index++;
                continue;
            }

            if (text[index + 1] == '\0')
            {
                // NVT CR NUL is a carriage return with no vertical movement.
                index++;
                _lastOutputWasLineFeed = false;
                continue;
            }

            _lastOutputWasLineFeed = false;
        }
        return output.ToString();
    }

    public void Reset()
    {
        _pendingCarriageReturn = false;
        _lastOutputWasLineFeed = false;
    }

    private void AppendLineFeed(StringBuilder output)
    {
        output.Append('\n');
        _lastOutputWasLineFeed = true;
    }
}

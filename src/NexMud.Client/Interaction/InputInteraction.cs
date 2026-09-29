using NexMud.Client.Commands;
using NexMud.Client.Presentation;
using NexMud.Client.Settings;
using NexMud.Contracts.State;

namespace NexMud.Client.Interaction;

public enum InputSourceKind
{
    Keyboard,
    Paste,
    History,
    Keybinding,
    UiAction,
    Automation,
    Script,
    Mapper,
    Jev
}

public sealed record InputRequest(
    InputSourceKind SourceKind,
    string Text,
    DateTimeOffset Timestamp,
    Guid CorrelationId,
    string SessionId = "primary");

/// <summary>
/// Immutable editable-buffer projection. Avalonia/Terminal.Gui own native editing and IME behavior;
/// application services consume and return this DTO for history/completion operations.
/// </summary>
public sealed record InputBufferSnapshot(
    string Text,
    int CaretIndex,
    int SelectionStart = 0,
    int SelectionEnd = 0);

public sealed record InputEditResult(string Text, int CaretIndex);

public interface ICommandHistory
{
    void Add(string command);
    string? Previous(string currentBuffer);
    string? Next();
    IReadOnlyList<string> Search(string query, int limit = 50);
    IReadOnlyList<string> Snapshot();
    void Restore(IEnumerable<string> commands);
    void ResetNavigation();
}

/// <summary>
/// Session-local command history. It preserves the buffer which existed before history navigation
/// so Down after the newest history item restores the user's in-progress text.
/// </summary>
public sealed class CommandHistoryService : ICommandHistory
{
    private readonly object _sync = new();
    private readonly List<string> _commands = [];
    private int _cursor;
    private string _inProgress = string.Empty;
    private int _maximumEntries;
    private bool _deduplicateConsecutive;

    public CommandHistoryService(int maximumEntries = 500, bool deduplicateConsecutive = true)
    {
        Configure(maximumEntries, deduplicateConsecutive);
    }

    public void Configure(int maximumEntries, bool deduplicateConsecutive)
    {
        lock (_sync)
        {
            _maximumEntries = Math.Clamp(maximumEntries, 1, 10_000);
            _deduplicateConsecutive = deduplicateConsecutive;
            Trim();
            _cursor = _commands.Count;
        }
    }

    public void Add(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0) return;
        lock (_sync)
        {
            if (!_deduplicateConsecutive || _commands.Count == 0 ||
                !string.Equals(_commands[^1], command, StringComparison.Ordinal))
            {
                _commands.Add(command);
                Trim();
            }
            _cursor = _commands.Count;
            _inProgress = string.Empty;
        }
    }

    public string? Previous(string currentBuffer)
    {
        ArgumentNullException.ThrowIfNull(currentBuffer);
        lock (_sync)
        {
            if (_commands.Count == 0) return null;
            if (_cursor == _commands.Count)
                _inProgress = currentBuffer;

            _cursor = Math.Max(0, _cursor - 1);
            return _commands[_cursor];
        }
    }

    public string? Next()
    {
        lock (_sync)
        {
            if (_commands.Count == 0) return null;
            _cursor = Math.Min(_commands.Count, _cursor + 1);
            return _cursor == _commands.Count ? _inProgress : _commands[_cursor];
        }
    }

    public IReadOnlyList<string> Search(string query, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(query);
        int bounded = Math.Clamp(limit, 1, 500);
        lock (_sync)
        {
            return _commands
                .AsEnumerable()
                .Reverse()
                .Where(command => command.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(bounded)
                .ToArray();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync) return _commands.ToArray();
    }

    public void Restore(IEnumerable<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        lock (_sync)
        {
            _commands.Clear();
            foreach (string command in commands.Where(command => !string.IsNullOrEmpty(command)))
            {
                if (_deduplicateConsecutive && _commands.Count > 0 &&
                    string.Equals(_commands[^1], command, StringComparison.Ordinal))
                    continue;

                _commands.Add(command);
            }
            Trim();
            _cursor = _commands.Count;
            _inProgress = string.Empty;
        }
    }

    public void ResetNavigation()
    {
        lock (_sync)
        {
            _cursor = _commands.Count;
            _inProgress = string.Empty;
        }
    }

    private void Trim()
    {
        if (_commands.Count > _maximumEntries)
            _commands.RemoveRange(0, _commands.Count - _maximumEntries);
    }
}

public sealed record CompletionCandidate(string Value, long Recency = 0, int SourcePriority = 0);

public sealed record CompletionContext(string Prefix, string SessionId = "primary");

public interface ICompletionProvider
{
    IEnumerable<CompletionCandidate> GetCandidates(CompletionContext context);
}

public sealed class StaticCompletionProvider : ICompletionProvider
{
    private readonly IReadOnlyList<string> _values;
    private readonly int _priority;

    public StaticCompletionProvider(IEnumerable<string> values, int priority = 10)
    {
        _values = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _priority = priority;
    }

    public IEnumerable<CompletionCandidate> GetCandidates(CompletionContext context) =>
        _values
            .Where(value => value.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(value => new CompletionCandidate(value, SourcePriority: _priority));
}

public sealed class AliasCompletionProvider : ICompletionProvider
{
    private readonly Func<ClientSettings> _settings;

    public AliasCompletionProvider(Func<ClientSettings> settings) => _settings = settings;

    public IEnumerable<CompletionCandidate> GetCandidates(CompletionContext context) =>
        (_settings().Aliases ?? Array.Empty<CommandAlias>())
            .Where(alias => alias.Enabled && alias.Name.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(alias => new CompletionCandidate(alias.Name, SourcePriority: 30));
}

public sealed class StateCompletionProvider : ICompletionProvider
{
    private readonly Func<StateSnapshot> _state;

    public StateCompletionProvider(Func<StateSnapshot> state) => _state = state;

    public IEnumerable<CompletionCandidate> GetCandidates(CompletionContext context)
    {
        StateSnapshot state = _state();
        HashSet<string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string exit in state.Room.Exits.Directions) Add(exit);
        foreach (RoomContentObservation entity in state.Room.Contents)
        {
            Add(entity.CanonicalName);
            foreach (string keyword in entity.TargetKeywords ?? Array.Empty<string>()) Add(keyword);
        }
        foreach (string item in state.Character.CarriedItems) Add(item);
        foreach (EquipmentSlotState slot in state.Character.Equipment.Slots)
            if (!slot.IsEmpty && !string.IsNullOrWhiteSpace(slot.Item)) Add(slot.Item!);
        foreach (SkillState skill in state.Character.Skills) Add(skill.Name);
        foreach (SpellState spell in state.Character.Spells) Add(spell.Name);

        return values
            .Where(value => value.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(value => new CompletionCandidate(value, SourcePriority: 40))
            .ToArray();

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
        }
    }
}

/// <summary>
/// Incremental bounded token index plus pluggable providers and stable Tab/Shift-Tab cycle state.
/// No completion operation rescans scrollback.
/// </summary>
public sealed class CompletionService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _recency = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ICompletionProvider> _providers = [];
    private long _sequence;
    private int _tokenLimit;
    private string? _cycleHead;
    private string? _cycleTail;
    private string? _cyclePrefix;
    private IReadOnlyList<string> _cycle = Array.Empty<string>();
    private int _cycleIndex = -1;

    public CompletionService(int tokenLimit = 6_000) => Configure(tokenLimit);

    public void Configure(int tokenLimit)
    {
        lock (_sync)
        {
            _tokenLimit = Math.Clamp(tokenLimit, 100, 50_000);
            Trim();
        }
    }

    public void SetProviders(IEnumerable<ICompletionProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        lock (_sync)
        {
            _providers.Clear();
            _providers.AddRange(providers);
            ResetCycleCore();
        }
    }

    public void IndexText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_sync)
        {
            int start = -1;
            for (int index = 0; index <= text.Length; index++)
            {
                bool token = index < text.Length && IsTokenCharacter(text[index]);
                if (token && start < 0)
                {
                    start = index;
                    continue;
                }
                if (token || start < 0) continue;

                int length = index - start;
                if (length >= 2)
                {
                    string value = text.Substring(start, length);
                    _recency[value] = ++_sequence;
                }
                start = -1;
            }
            Trim();
        }
    }

    public IReadOnlyList<string> Find(CompletionContext context, int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Prefix.Length == 0) return Array.Empty<string>();
        int bounded = Math.Clamp(limit, 1, 500);

        lock (_sync)
        {
            Dictionary<string, CompletionCandidate> candidates = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string token, long recency) in _recency)
            {
                if (!token.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                candidates[token] = new CompletionCandidate(token, recency, 0);
            }

            foreach (ICompletionProvider provider in _providers)
            {
                foreach (CompletionCandidate candidate in provider.GetCandidates(context))
                {
                    string value = candidate.Value?.Trim() ?? string.Empty;
                    if (value.Length == 0 || !value.StartsWith(context.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!candidates.TryGetValue(value, out CompletionCandidate? existing))
                    {
                        candidates[value] = candidate with { Value = value };
                        continue;
                    }

                    candidates[value] = new CompletionCandidate(
                        existing.Value,
                        Math.Max(existing.Recency, candidate.Recency),
                        Math.Max(existing.SourcePriority, candidate.SourcePriority));
                }
            }

            return candidates.Values
                .OrderByDescending(candidate => candidate.Recency)
                .ThenBy(candidate => candidate.Value.Length)
                .ThenByDescending(candidate => candidate.SourcePriority)
                .ThenBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
                .Take(bounded)
                .Select(candidate => candidate.Value)
                .ToArray();
        }
    }

    public InputEditResult? Complete(InputBufferSnapshot buffer, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_sync)
        {
            int caret = Math.Clamp(buffer.CaretIndex, 0, buffer.Text.Length);
            bool continuing = _cycleHead is not null && _cycleTail is not null && _cyclePrefix is not null && _cycle.Count > 0 &&
                              string.Equals(buffer.Text, _cycleHead + _cycle[_cycleIndex] + _cycleTail, StringComparison.Ordinal) &&
                              caret == _cycleHead.Length + _cycle[_cycleIndex].Length;

            if (!continuing)
            {
                int tokenStart = caret;
                while (tokenStart > 0 && IsTokenCharacter(buffer.Text[tokenStart - 1])) tokenStart--;
                string prefix = buffer.Text[tokenStart..caret];
                if (prefix.Length == 0)
                {
                    ResetCycleCore();
                    return null;
                }

                IReadOnlyList<string> candidates = Find(new CompletionContext(prefix));
                if (candidates.Count == 0)
                {
                    ResetCycleCore();
                    return null;
                }

                _cycleHead = buffer.Text[..tokenStart];
                _cycleTail = buffer.Text[caret..];
                _cyclePrefix = prefix;
                _cycle = candidates;
                _cycleIndex = reverse ? _cycle.Count - 1 : 0;
            }
            else
            {
                _cycleIndex = reverse
                    ? (_cycleIndex - 1 + _cycle.Count) % _cycle.Count
                    : (_cycleIndex + 1) % _cycle.Count;
            }

            if (_cycleHead is null || _cycleTail is null)
            {
                ResetCycleCore();
                return null;
            }

            string candidate = _cycle[_cycleIndex];
            string text = _cycleHead + candidate + _cycleTail;
            return new InputEditResult(text, _cycleHead.Length + candidate.Length);
        }
    }

    // Kept for focused tests and non-UI callers which already computed a stable candidate set.
    public string? Cycle(string prefix, IReadOnlyList<string> candidates, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_sync)
        {
            if (!string.Equals(_cyclePrefix, prefix, StringComparison.Ordinal) ||
                !_cycle.SequenceEqual(candidates, StringComparer.OrdinalIgnoreCase) ||
                _cycleHead is not null)
            {
                _cycleHead = null;
                _cycleTail = null;
                _cyclePrefix = prefix;
                _cycle = candidates.ToArray();
                _cycleIndex = reverse ? _cycle.Count - 1 : 0;
            }
            else if (_cycle.Count > 0)
            {
                _cycleIndex = reverse
                    ? (_cycleIndex - 1 + _cycle.Count) % _cycle.Count
                    : (_cycleIndex + 1) % _cycle.Count;
            }

            return _cycle.Count == 0 ? null : _cycle[_cycleIndex];
        }
    }

    public void ResetCycle()
    {
        lock (_sync) ResetCycleCore();
    }

    private void ResetCycleCore()
    {
        _cycleHead = null;
        _cycleTail = null;
        _cyclePrefix = null;
        _cycle = Array.Empty<string>();
        _cycleIndex = -1;
    }

    private static bool IsTokenCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '\'' or '-' or '_';

    private void Trim()
    {
        if (_recency.Count <= _tokenLimit) return;
        foreach (string token in _recency
                     .OrderBy(pair => pair.Value)
                     .Take(_recency.Count - _tokenLimit)
                     .Select(pair => pair.Key)
                     .ToArray())
            _recency.Remove(token);
    }
}

public sealed class InputEditingService
{
    private readonly ICommandHistory _history;
    private readonly CompletionService _completion;

    public InputEditingService(ICommandHistory history, CompletionService completion)
    {
        _history = history;
        _completion = completion;
    }

    public InputEditResult? HistoryPrevious(InputBufferSnapshot buffer)
    {
        string? value = _history.Previous(buffer.Text);
        return value is null ? null : new InputEditResult(value, value.Length);
    }

    public InputEditResult? HistoryNext()
    {
        string? value = _history.Next();
        return value is null ? null : new InputEditResult(value, value.Length);
    }

    public InputEditResult? Complete(InputBufferSnapshot buffer, bool reverse) => _completion.Complete(buffer, reverse);

    public void ResetCompletion() => _completion.ResetCycle();
}

public interface ICommandTokenizer
{
    IReadOnlyList<string> Tokenize(string input, string? separator);
}

public sealed class CommandTokenizer : ICommandTokenizer
{
    public IReadOnlyList<string> Tokenize(string input, string? separator) => CommandBatchSplitter.Split(input, separator);
}

public interface IInputAliasResolver
{
    Task<bool> TryResolveAsync(string command, CancellationToken cancellationToken = default);
}

public sealed class NullInputAliasResolver : IInputAliasResolver
{
    public Task<bool> TryResolveAsync(string command, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

public interface IInputCommandDispatcher
{
    Task<LocalCommandResult> DispatchAsync(
        string command,
        InputSourceKind source,
        SessionInputMode inputMode,
        CancellationToken cancellationToken = default);
}

public interface ICommandHistoryPersistence
{
    Task<IReadOnlyList<string>> LoadAsync(int limit, CancellationToken cancellationToken = default);
    Task SaveAsync(string command, bool sentToMud, CancellationToken cancellationToken = default);
}

public sealed record InputSubmissionResult(
    IReadOnlyList<LocalCommandResult> Results,
    IReadOnlyList<string> Commands);

/// <summary>
/// Application input pipeline. UI surfaces submit typed requests; history, tokenization,
/// alias/local-command resolution, provenance, and command dispatch stay behind this boundary.
/// </summary>
public sealed class InputPipeline
{
    private readonly Func<ClientSettings> _settings;
    private readonly ICommandHistory _history;
    private readonly CompletionService _completion;
    private readonly ICommandTokenizer _tokenizer;
    private readonly IInputAliasResolver _aliases;
    private readonly IInputCommandDispatcher? _dispatcher;
    private readonly ICommandHistoryPersistence? _persistence;

    public InputPipeline(
        Func<ClientSettings> settings,
        ICommandHistory history,
        CompletionService completion,
        ICommandTokenizer? tokenizer = null,
        IInputAliasResolver? aliases = null,
        IInputCommandDispatcher? dispatcher = null,
        ICommandHistoryPersistence? persistence = null)
    {
        _settings = settings;
        _history = history;
        _completion = completion;
        _tokenizer = tokenizer ?? new CommandTokenizer();
        _aliases = aliases ?? new NullInputAliasResolver();
        _dispatcher = dispatcher;
        _persistence = persistence;
    }

    public async Task RestoreHistoryAsync(CancellationToken cancellationToken = default)
    {
        InputPreferences preferences = _settings().Input ?? new InputPreferences();
        if (_persistence is null || !preferences.PersistHistory) return;
        IReadOnlyList<string> persisted = await _persistence
            .LoadAsync(preferences.HistoryMaximumEntries, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> current = _history.Snapshot();
        _history.Restore(persisted.Concat(current));
        foreach (string command in _history.Snapshot()) _completion.IndexText(command);
    }

    public async Task<InputSubmissionResult> SubmitAsync(
        InputRequest request,
        SessionInputMode mode,
        CancellationToken cancellationToken = default)
    {
        if (_dispatcher is null)
            throw new InvalidOperationException("The input pipeline has no command dispatcher.");

        return await SubmitCoreAsync(
            request,
            mode,
            (command, source, inputMode, token) => _dispatcher.DispatchAsync(command, source, inputMode, token),
            cancellationToken).ConfigureAwait(false);
    }

    // Focused-test seam. Production callers use SubmitAsync without supplying a dispatcher.
    public async Task<InputSubmissionResult> SubmitAsync(
        InputRequest request,
        SessionInputMode mode,
        Func<string, CancellationToken, Task<LocalCommandResult>> dispatcher,
        CancellationToken cancellationToken = default) =>
        await SubmitCoreAsync(
            request,
            mode,
            (command, _, _, token) => dispatcher(command, token),
            cancellationToken).ConfigureAwait(false);

    private async Task<InputSubmissionResult> SubmitCoreAsync(
        InputRequest request,
        SessionInputMode mode,
        Func<string, InputSourceKind, SessionInputMode, CancellationToken, Task<LocalCommandResult>> dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dispatcher);

        bool manualHistorySource = request.SourceKind is InputSourceKind.Keyboard or InputSourceKind.Paste or InputSourceKind.History;
        bool recordHistory = manualHistorySource && CommandInputPolicy.ShouldRecordHistory(mode, request.Text);
        if (recordHistory)
        {
            _history.Add(request.Text);
            _completion.IndexText(request.Text);
        }

        InputPreferences inputPreferences = _settings().Input ?? new InputPreferences();
        IReadOnlyList<string> commands = mode == SessionInputMode.Normal && inputPreferences.CommandBatchingEnabled
            ? _tokenizer.Tokenize(request.Text, _settings().CommandSeparator)
            : [request.Text];

        List<LocalCommandResult> results = [];
        foreach (string command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool aliasEligible = mode == SessionInputMode.Normal &&
                                 !command.StartsWith(':') &&
                                 request.SourceKind is InputSourceKind.Keyboard or InputSourceKind.Paste or InputSourceKind.History or InputSourceKind.Keybinding or InputSourceKind.UiAction;
            if (aliasEligible && await _aliases.TryResolveAsync(command, cancellationToken).ConfigureAwait(false))
            {
                results.Add(new LocalCommandResult(false));
                continue;
            }

            results.Add(await dispatcher(command, request.SourceKind, mode, cancellationToken).ConfigureAwait(false));
        }

        if (recordHistory && _persistence is not null && (_settings().Input ?? new InputPreferences()).PersistHistory)
        {
            bool sentToMud = mode == SessionInputMode.Editor || !request.Text.TrimStart().StartsWith(':');
            try
            {
                await _persistence.SaveAsync(request.Text, sentToMud, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // History persistence is convenience state. A database failure must not fail command dispatch.
            }
        }

        return new InputSubmissionResult(results, commands);
    }
}

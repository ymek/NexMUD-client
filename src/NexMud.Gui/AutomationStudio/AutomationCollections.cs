using NexMud.Client.Interaction;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;

namespace NexMud.Gui.AutomationStudio;

internal sealed record AutomationEntryInfo(StudioDocumentKind Kind, int Index, string Name, bool Enabled);

/// <summary>
/// Immutable working copy of every Automation definition list. All add/replace/remove/duplicate
/// operations are pure so they can be tested without a runtime; <see cref="SaveAsync"/> is the
/// only side effect and always goes through the single composition save path.
/// </summary>
internal sealed record AutomationCollections(
    IReadOnlyList<CommandAlias> Aliases,
    IReadOnlyList<TriggerRule> Triggers,
    IReadOnlyList<SemanticTriggerRule> SemanticTriggers,
    IReadOnlyList<GameRule> Rules,
    IReadOnlyList<AutomationWorkflow> Workflows,
    IReadOnlyList<CommandTimer> Timers,
    IReadOnlyList<CommandKeyBinding> Keys,
    IReadOnlyList<TranscriptHighlightRule> Highlights)
{
    public static AutomationCollections From(ClientSettings settings) => new(
        settings.Aliases ?? [], settings.Triggers ?? [], settings.SemanticTriggers ?? [], settings.GameRules ?? [],
        settings.Workflows ?? [], settings.Timers ?? [], settings.KeyBindings ?? [], settings.HighlightRules ?? []);

    public async Task SaveAsync(NexMudRuntime runtime, CancellationToken cancellationToken)
    {
        await runtime.SaveAutomationCompositionAsync(Aliases, Triggers, SemanticTriggers, Rules, Workflows, Timers, Keys, cancellationToken).ConfigureAwait(false);
        await runtime.SaveHighlightRulesAsync(Highlights, cancellationToken).ConfigureAwait(false);
    }

    public int Count(StudioDocumentKind kind) => kind switch
    {
        StudioDocumentKind.Alias => Aliases.Count,
        StudioDocumentKind.Trigger => Triggers.Count,
        StudioDocumentKind.SemanticTrigger => SemanticTriggers.Count,
        StudioDocumentKind.Keybinding => Keys.Count,
        StudioDocumentKind.Timer => Timers.Count,
        StudioDocumentKind.StateRule => Rules.Count,
        StudioDocumentKind.Workflow => Workflows.Count,
        StudioDocumentKind.Highlight => Highlights.Count,
        _ => 0
    };

    public object? Get(StudioDocumentKind kind, int index)
    {
        if (index < 0 || index >= Count(kind)) return null;
        return kind switch
        {
            StudioDocumentKind.Alias => Aliases[index],
            StudioDocumentKind.Trigger => Triggers[index],
            StudioDocumentKind.SemanticTrigger => SemanticTriggers[index],
            StudioDocumentKind.Keybinding => Keys[index],
            StudioDocumentKind.Timer => Timers[index],
            StudioDocumentKind.StateRule => Rules[index],
            StudioDocumentKind.Workflow => Workflows[index],
            StudioDocumentKind.Highlight => Highlights[index],
            _ => null
        };
    }

    public IReadOnlyList<AutomationEntryInfo> Entries(StudioDocumentKind kind) => kind switch
    {
        StudioDocumentKind.Alias => Aliases.Select((v, i) => new AutomationEntryInfo(kind, i, v.Name, v.Enabled)).ToArray(),
        StudioDocumentKind.Trigger => Triggers.Select((v, i) => new AutomationEntryInfo(kind, i, v.Pattern, v.Enabled)).ToArray(),
        StudioDocumentKind.SemanticTrigger => SemanticTriggers.Select((v, i) => new AutomationEntryInfo(kind, i, v.Name, v.Enabled)).ToArray(),
        StudioDocumentKind.Keybinding => Keys.Select((v, i) => new AutomationEntryInfo(kind, i, DisplayName(v), v.Enabled)).ToArray(),
        StudioDocumentKind.Timer => Timers.Select((v, i) => new AutomationEntryInfo(kind, i, v.Name, v.Enabled)).ToArray(),
        StudioDocumentKind.StateRule => Rules.Select((v, i) => new AutomationEntryInfo(kind, i, v.Name, v.Enabled)).ToArray(),
        StudioDocumentKind.Workflow => Workflows.Select((v, i) => new AutomationEntryInfo(kind, i, v.Name, v.Enabled)).ToArray(),
        StudioDocumentKind.Highlight => Highlights.Select((v, i) => new AutomationEntryInfo(kind, i, v.Pattern, v.Enabled)).ToArray(),
        _ => []
    };

    public static string DisplayName(CommandKeyBinding key) => string.IsNullOrWhiteSpace(key.Name) ? key.Gesture : key.Name!;

    public string? NameOf(StudioDocumentKind kind, int index) =>
        Entries(kind).FirstOrDefault(entry => entry.Index == index)?.Name;

    /// <summary>Replaces the definition at <paramref name="index"/>. The value type must match the kind.</summary>
    public AutomationCollections Replace(StudioDocumentKind kind, int index, object value) => value switch
    {
        CommandAlias v when kind == StudioDocumentKind.Alias => this with { Aliases = ReplaceAt(Aliases, index, v) },
        TriggerRule v when kind == StudioDocumentKind.Trigger => this with { Triggers = ReplaceAt(Triggers, index, v) },
        SemanticTriggerRule v when kind == StudioDocumentKind.SemanticTrigger => this with { SemanticTriggers = ReplaceAt(SemanticTriggers, index, v) },
        CommandKeyBinding v when kind == StudioDocumentKind.Keybinding => this with { Keys = ReplaceAt(Keys, index, v) },
        CommandTimer v when kind == StudioDocumentKind.Timer => this with { Timers = ReplaceAt(Timers, index, v) },
        GameRule v when kind == StudioDocumentKind.StateRule => this with { Rules = ReplaceAt(Rules, index, v) },
        AutomationWorkflow v when kind == StudioDocumentKind.Workflow => this with { Workflows = ReplaceAt(Workflows, index, v) },
        TranscriptHighlightRule v when kind == StudioDocumentKind.Highlight => this with { Highlights = ReplaceAt(Highlights, index, v) },
        _ => throw new ArgumentException($"Value {value.GetType().Name} does not belong to {kind}.", nameof(value))
    };

    public AutomationCollections Remove(StudioDocumentKind kind, int index) => kind switch
    {
        StudioDocumentKind.Alias => this with { Aliases = RemoveAt(Aliases, index) },
        StudioDocumentKind.Trigger => this with { Triggers = RemoveAt(Triggers, index) },
        StudioDocumentKind.SemanticTrigger => this with { SemanticTriggers = RemoveAt(SemanticTriggers, index) },
        StudioDocumentKind.Keybinding => this with { Keys = RemoveAt(Keys, index) },
        StudioDocumentKind.Timer => this with { Timers = RemoveAt(Timers, index) },
        StudioDocumentKind.StateRule => this with { Rules = RemoveAt(Rules, index) },
        StudioDocumentKind.Workflow => this with { Workflows = RemoveAt(Workflows, index) },
        StudioDocumentKind.Highlight => this with { Highlights = RemoveAt(Highlights, index) },
        _ => this
    };

    /// <summary>Appends a new definition with a unique default name. Returns the new index.</summary>
    public (AutomationCollections Collections, int Index) AddNew(StudioDocumentKind kind)
    {
        HashSet<string> names = Entries(kind).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Unique(string seed)
        {
            if (!names.Contains(seed)) return seed;
            for (int n = 2; ; n++) if (!names.Contains($"{seed}-{n}")) return $"{seed}-{n}";
        }
        return kind switch
        {
            StudioDocumentKind.Alias => (this with { Aliases = [.. Aliases, new CommandAlias(Unique("new-alias"), "look")] }, Aliases.Count),
            StudioDocumentKind.Trigger => (this with { Triggers = [.. Triggers, new TriggerRule(Unique("new trigger"), "look")] }, Triggers.Count),
            StudioDocumentKind.SemanticTrigger => (this with { SemanticTriggers = [.. SemanticTriggers, new SemanticTriggerRule(Unique("new-semantic-trigger"), "RoomChanged")] }, SemanticTriggers.Count),
            StudioDocumentKind.Keybinding => (this with { Keys = [.. Keys, new CommandKeyBinding("Cmd+1", "look", Name: Unique("New keybinding"), Context: KeybindingContext.Input)] }, Keys.Count),
            StudioDocumentKind.Timer => (this with { Timers = [.. Timers, new CommandTimer(Unique("new-timer"), 60, "score")] }, Timers.Count),
            StudioDocumentKind.StateRule => (this with { Rules = [.. Rules, new GameRule(Unique("new-state-rule"), "hp.percent < 30", "look")] }, Rules.Count),
            StudioDocumentKind.Workflow => (this with { Workflows = [.. Workflows, new AutomationWorkflow(Unique("new-workflow"), "send look")] }, Workflows.Count),
            StudioDocumentKind.Highlight => (this with { Highlights = [.. Highlights, new TranscriptHighlightRule(Unique("new highlight"), "#F59E0B")] }, Highlights.Count),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Automation kinds can be created here.")
        };
    }

    /// <summary>
    /// Appends a new definition populated from wizard values (ordered as <see cref="NewItemTemplate.Fields"/>).
    /// Names are made unique. Returns the new index.
    /// </summary>
    public (AutomationCollections Collections, int Index) AddFrom(StudioDocumentKind kind, IReadOnlyList<string> values)
    {
        (AutomationCollections added, int index) = AddNew(kind);
        string Name(int at) => values[at];
        object filled = added.Get(kind, index) switch
        {
            CommandAlias v => v with { Name = UniqueName(kind, Name(0)), Expansion = Name(1) },
            TriggerRule v => v with { Pattern = Name(0), Command = Name(1) },
            TranscriptHighlightRule v => v with { Pattern = Name(0), Foreground = Name(1) },
            CommandTimer v => v with { Name = UniqueName(kind, Name(0)), IntervalSeconds = int.Parse(Name(1)), Command = Name(2) },
            CommandKeyBinding v => v with { Gesture = Name(0), Name = UniqueName(kind, Name(0)), Command = Name(1) },
            GameRule v => v with { Name = UniqueName(kind, Name(0)), Condition = Name(1), Command = Name(2) },
            SemanticTriggerRule v => v with { Name = UniqueName(kind, Name(0)), EventName = Name(1) },
            AutomationWorkflow v => v with { Name = UniqueName(kind, Name(0)), Steps = Name(1) },
            var other => other!
        };
        return (added.Replace(kind, index, filled), index);
    }

    private string UniqueName(StudioDocumentKind kind, string wanted)
    {
        HashSet<string> names = Entries(kind).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(wanted)) return wanted;
        for (int n = 2; ; n++) if (!names.Contains($"{wanted}-{n}")) return $"{wanted}-{n}";
    }

    /// <summary>Appends a copy of the definition (new identity, disabled-state preserved). Returns the new index.</summary>
    public (AutomationCollections Collections, int Index) Duplicate(StudioDocumentKind kind, int index)
    {
        if (Get(kind, index) is not { } source) return (this, -1);
        HashSet<string> names = Entries(kind).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Copy(string name)
        {
            string seed = $"{name}-copy";
            if (!names.Contains(seed)) return seed;
            for (int n = 2; ; n++) if (!names.Contains($"{seed}-{n}")) return $"{seed}-{n}";
        }
        return source switch
        {
            CommandAlias v => (this with { Aliases = [.. Aliases, v with { Name = Copy(v.Name), Id = null }] }, Aliases.Count),
            TriggerRule v => (this with { Triggers = [.. Triggers, v with { Pattern = Copy(v.Pattern), Id = null }] }, Triggers.Count),
            SemanticTriggerRule v => (this with { SemanticTriggers = [.. SemanticTriggers, v with { Name = Copy(v.Name), Id = null }] }, SemanticTriggers.Count),
            CommandKeyBinding v => (this with { Keys = [.. Keys, v with { Name = Copy(DisplayName(v)), Id = null }] }, Keys.Count),
            CommandTimer v => (this with { Timers = [.. Timers, v with { Name = Copy(v.Name), Id = null }] }, Timers.Count),
            GameRule v => (this with { Rules = [.. Rules, v with { Name = Copy(v.Name), Id = null }] }, Rules.Count),
            AutomationWorkflow v => (this with { Workflows = [.. Workflows, v with { Name = Copy(v.Name), Id = null }] }, Workflows.Count),
            TranscriptHighlightRule v => (this with { Highlights = [.. Highlights, v with { Pattern = Copy(v.Pattern) }] }, Highlights.Count),
            _ => (this, -1)
        };
    }

    private static IReadOnlyList<T> ReplaceAt<T>(IReadOnlyList<T> source, int index, T value)
    {
        if (index < 0 || index >= source.Count) throw new ArgumentOutOfRangeException(nameof(index));
        List<T> list = [.. source];
        list[index] = value;
        return list;
    }

    private static IReadOnlyList<T> RemoveAt<T>(IReadOnlyList<T> source, int index)
    {
        if (index < 0 || index >= source.Count) return source;
        List<T> list = [.. source];
        list.RemoveAt(index);
        return list;
    }
}

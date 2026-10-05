using System.Text.Json;
using NexMud.Client.Automation;
using NexMud.Client.Paths;
using NexMud.Scripting.Runtime;

namespace NexMud.Gui.AutomationStudio;

/// <summary>Kinds of first-class Studio documents. Every kind shares one document host.</summary>
internal enum StudioDocumentKind
{
    Alias,
    Trigger,
    SemanticTrigger,
    Keybinding,
    Timer,
    StateRule,
    Workflow,
    Highlight,
    Script
}

internal static class StudioDocumentKinds
{
    public static string Label(this StudioDocumentKind kind) => kind switch
    {
        StudioDocumentKind.Alias => "Alias",
        StudioDocumentKind.Trigger => "Trigger",
        StudioDocumentKind.SemanticTrigger => "Semantic Trigger",
        StudioDocumentKind.Keybinding => "Keybinding",
        StudioDocumentKind.Timer => "Timer",
        StudioDocumentKind.StateRule => "State Rule",
        StudioDocumentKind.Workflow => "Workflow",
        StudioDocumentKind.Highlight => "Highlight",
        _ => "Script"
    };

    public static string CategoryLabel(this StudioDocumentKind kind) => kind switch
    {
        StudioDocumentKind.Alias => "Aliases",
        StudioDocumentKind.Trigger => "Triggers",
        StudioDocumentKind.SemanticTrigger => "Semantic Triggers",
        StudioDocumentKind.Keybinding => "Keybindings",
        StudioDocumentKind.Timer => "Timers",
        StudioDocumentKind.StateRule => "State Rules",
        StudioDocumentKind.Workflow => "Workflows",
        StudioDocumentKind.Highlight => "Highlights",
        _ => "Scripts"
    };

    public static readonly StudioDocumentKind[] AutomationKinds =
    [
        StudioDocumentKind.Alias, StudioDocumentKind.Trigger, StudioDocumentKind.SemanticTrigger,
        StudioDocumentKind.Keybinding, StudioDocumentKind.Timer, StudioDocumentKind.StateRule,
        StudioDocumentKind.Workflow, StudioDocumentKind.Highlight
    ];

    public static StudioDocumentKind FromSource(AutomationInvocationSourceKind kind) => kind switch
    {
        AutomationInvocationSourceKind.Alias => StudioDocumentKind.Alias,
        AutomationInvocationSourceKind.Keybinding => StudioDocumentKind.Keybinding,
        AutomationInvocationSourceKind.TextTrigger => StudioDocumentKind.Trigger,
        AutomationInvocationSourceKind.SemanticTrigger => StudioDocumentKind.SemanticTrigger,
        AutomationInvocationSourceKind.Timer => StudioDocumentKind.Timer,
        AutomationInvocationSourceKind.StateRule => StudioDocumentKind.StateRule,
        _ => StudioDocumentKind.Workflow
    };

    /// <summary>Extracts the definition index from an AutomationFunctionReference.DefinitionId ("Alias:3", "keybinding:0").</summary>
    public static int? DefinitionIndex(string definitionId)
    {
        int colon = definitionId.LastIndexOf(':');
        return colon >= 0 && int.TryParse(definitionId[(colon + 1)..], out int index) ? index : null;
    }
}

/// <summary>Stable document identity. Automation: kind + index; Script: Monaco document URI.</summary>
internal sealed class StudioDocument
{
    public StudioDocument(string key, StudioDocumentKind kind, string title)
    {
        Key = key;
        Kind = kind;
        Title = title;
    }

    public string Key { get; }
    public StudioDocumentKind Kind { get; }
    public string Title { get; set; }
    public bool IsDirty { get; set; }
    public IReadOnlyList<string> Breadcrumb { get; set; } = [];
    public string? AutomationId { get; init; }
    public string? ProfileId { get; init; }
    public string? PackageId { get; init; }
    public string? Path { get; init; }
    public bool IsScript => Kind == StudioDocumentKind.Script;

    public static string AutomationKey(string profileId, StudioDocumentKind kind, string automationId) =>
        $"automation:{profileId}:{kind}:{automationId}";
}

/// <summary>
/// Ordered set of open documents with a single active document. Pure state: no UI types,
/// so open/focus/close/dirty behavior is deterministic and unit-testable.
/// </summary>
internal sealed class StudioDocumentSet
{
    private readonly List<StudioDocument> _documents = [];

    public IReadOnlyList<StudioDocument> Documents => _documents;
    public StudioDocument? Active { get; private set; }
    public event Action? Changed;

    public StudioDocument? Find(string key) => _documents.FirstOrDefault(d => d.Key == key);

    /// <summary>Opens the document, or focuses the already-open one with the same key.</summary>
    public StudioDocument OpenOrFocus(StudioDocument document)
    {
        StudioDocument target = Find(document.Key) ?? Add(document);
        Active = target;
        Changed?.Invoke();
        return target;
    }

    private StudioDocument Add(StudioDocument document)
    {
        int at = Active is null ? _documents.Count : _documents.IndexOf(Active) + 1;
        _documents.Insert(at, document);
        return document;
    }

    public bool Activate(string key)
    {
        StudioDocument? document = Find(key);
        if (document is null) return false;
        Active = document;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Activates the neighboring tab, wrapping around. Returns the new active document.</summary>
    public StudioDocument? Cycle(int direction)
    {
        if (_documents.Count == 0) return null;
        int current = Active is null ? 0 : _documents.IndexOf(Active);
        Active = _documents[((current + direction) % _documents.Count + _documents.Count) % _documents.Count];
        Changed?.Invoke();
        return Active;
    }

    /// <summary>Closes the document; focus moves to the right neighbor, else the left one, else nothing.</summary>
    public StudioDocument? Close(string key)
    {
        StudioDocument? document = Find(key);
        if (document is null) return Active;
        int index = _documents.IndexOf(document);
        _documents.RemoveAt(index);
        if (ReferenceEquals(Active, document))
            Active = _documents.Count == 0 ? null : _documents[Math.Min(index, _documents.Count - 1)];
        Changed?.Invoke();
        return Active;
    }

    public void CloseAll()
    {
        _documents.Clear();
        Active = null;
        Changed?.Invoke();
    }

    public void SetDirty(string key, bool dirty)
    {
        StudioDocument? document = Find(key);
        if (document is null || document.IsDirty == dirty) return;
        document.IsDirty = dirty;
        Changed?.Invoke();
    }

    public void Retitle(string key, string title)
    {
        StudioDocument? document = Find(key);
        if (document is null || document.Title == title) return;
        document.Title = title;
        Changed?.Invoke();
    }

    public IEnumerable<StudioDocument> Dirty => _documents.Where(d => d.IsDirty);
}

internal sealed record StudioSavedSearch(
    string Name,
    string Query,
    bool Automations = true,
    bool Workflows = true,
    bool Scripts = true,
    bool Descriptions = true,
    bool ScriptContent = true,
    bool MatchCase = false,
    bool WholeWords = false,
    string IncludeTerms = "",
    string ExcludeTerms = "");

/// <summary>Studio layout preferences. Widths/heights are clamped to the ranges defined by the workbench spec.</summary>
internal sealed class StudioUiPreferences
{
    public const double DefaultExplorerWidth = 300, MinExplorerWidth = 240, MaxExplorerWidth = 420;
    public const double DefaultBottomHeight = 240, MinBottomHeight = 120, MaxBottomHeight = 600;
    public const double CollapsedBottomHeight = 40;

    private double _explorerWidth = DefaultExplorerWidth;
    private double _bottomHeight = DefaultBottomHeight;

    public double ExplorerWidth { get => _explorerWidth; set => _explorerWidth = Math.Clamp(value, MinExplorerWidth, MaxExplorerWidth); }
    public double BottomHeight { get => _bottomHeight; set => _bottomHeight = Math.Clamp(value, MinBottomHeight, MaxBottomHeight); }
    public bool BottomCollapsed { get; set; } = true;
    public string ActiveActivity { get; set; } = nameof(StudioActivity.Automations);
    public List<string> RecentSearches { get; set; } = [];
    public List<StudioSavedSearch> SavedSearches { get; set; } = [];
    public bool SearchAutomations { get; set; } = true;
    public bool SearchWorkflows { get; set; } = true;
    public bool SearchScripts { get; set; } = true;
    public bool SearchDescriptions { get; set; } = true;
    public bool SearchScriptContent { get; set; } = true;
    public bool SearchMatchCase { get; set; }
    public bool SearchWholeWords { get; set; }
    public string SearchIncludeTerms { get; set; } = string.Empty;
    public string SearchExcludeTerms { get; set; } = string.Empty;

    public StudioActivity ResolveActivity() =>
        Enum.TryParse(ActiveActivity, ignoreCase: true, out StudioActivity activity)
            ? activity
            : StudioActivity.Automations;

    public void SetActivity(StudioActivity activity) => ActiveActivity = activity.ToString();

    public static string DefaultPath => NexMudDataPaths.GetFilePath("automation-studio-ui.json");

    public static StudioUiPreferences Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new StudioUiPreferences();
            return JsonSerializer.Deserialize<StudioUiPreferences>(File.ReadAllText(path)) ?? new StudioUiPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StudioUiPreferences();
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Layout preferences are best-effort; failing to persist must never break authoring.
        }
    }
}

/// <summary>Case-insensitive substring filter shared by Explorer and pickers.</summary>
internal static class StudioFilter
{
    public static bool Matches(string? text, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (text?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}

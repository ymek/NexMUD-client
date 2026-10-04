using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NexMud.Client.Automation;
using NexMud.Client.Runtime;
using NexMud.Client.Scripting;
using NexMud.Client.Settings;
using NexMud.Contracts.Events;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Runtime;

namespace NexMud.Gui.AutomationStudio;

/// <summary>
/// Automation Studio workbench: Explorer and tabbed documents above a collapsible diagnostics panel.
/// Every automation definition and script source opens as a document tab.
/// </summary>
internal sealed partial class AutomationStudioWindow : Window
{
    private enum NodeKind { Category, Folder, Entry, Scripts, Package, SourceFolder, SourceFile }
    private sealed record StudioNode(
        NodeKind Kind,
        string Label,
        StudioDocumentKind? DocKind = null,
        int Index = -1,
        string? AutomationId = null,
        string? FolderId = null,
        string? PackageId = null,
        string? Path = null);

    private const int MaximumEventRows = 500;
    private readonly NexMudRuntime _runtime;
    private readonly ScriptWorkspaceService _workspace;
    private readonly StudioSessionController _session;
    private readonly StudioProfileTransitionGate _profileTransitions = new();
    private readonly StudioDocumentSessionStore _profileDocumentSessions = new();
    private readonly StudioTypeScriptLanguageHost _typescript;
    private readonly AutomationOrganizationStore _organizationStore = new();
    private AutomationOrganizationCatalog _organization = AutomationOrganizationCatalog.Empty;
    private readonly AutomationReferenceIndex _referencesIndex = new();
    private readonly StudioDocumentSet _documents = new();
    private readonly StudioTextModelRegistry _textModels = new();
    private readonly HashSet<string> _ignoredMonacoChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MonacoSelectionChanged> _scriptSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StudioLanguageDiagnostics> _pendingLanguageDiagnostics = new(StringComparer.Ordinal);
    private readonly StudioDiagnosticsHub _diagnostics = new();
    private Button? _languageServerRestartButton;
    private readonly StudioUiPreferences _preferences = StudioUiPreferences.Load();
    private readonly Dictionary<string, AutomationDocumentEditor> _editors = new(StringComparer.Ordinal);
    private readonly Dictionary<TreeViewItem, StudioNode> _nodes = [];
    private readonly ComboBox _profile = new() { MinWidth = 160, MaxWidth = 160 };
    private readonly TextBox _filter = new()
    {
        Background = StudioShellChrome.Input,
        Foreground = StudioShellChrome.Foreground,
        BorderBrush = StudioShellChrome.Border,
        CornerRadius = new CornerRadius(5),
        MinHeight = 34,
        Padding = new Thickness(8, 4),
        FontSize = 13
    };
    private readonly TreeView _navigator = new();
    private readonly Border _activityRail = new()
    {
        Width = 108,
        Background = StudioShellChrome.Rail,
        BorderBrush = StudioShellChrome.Border,
        BorderThickness = new Thickness(0, 0, 1, 0)
    };
    private readonly TextBlock _explorerTitle = new()
    {
        Foreground = StudioShellChrome.Foreground,
        FontWeight = FontWeight.SemiBold,
        FontSize = 18,
        VerticalAlignment = VerticalAlignment.Center
    };
    private sealed record FolderChoice(string? FolderId, string Label);
    private sealed record ScriptFolderChoice(string Path, string Label);

    private Button _newButton = new();
    private Button _organizeButton = new();
    private StudioActivity _activity;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _center = new();
    private readonly StackPanel _problems = new() { Spacing = 3 };
    private readonly StackPanel _console = new() { Spacing = 2 };
    private readonly StackPanel _runtimePanel = new() { Spacing = 3 };
    private readonly StackPanel _eventsPanel = new() { Spacing = 2 };
    private readonly StackPanel _referencesPanel = new() { Spacing = 3 };
    private readonly TextBlock _buildState = new() { Text = "Build —", Foreground = UiTheme.Muted };
    private readonly TextBlock _runtimeState = new() { Text = "Runtime —", Foreground = UiTheme.Muted };
    private readonly Border _connectionIndicator = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5) };
    private readonly TextBlock _connectionStatus = new() { Foreground = StudioShellChrome.Foreground };
    private readonly Queue<string> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private Grid _root = new();
    private Grid _body = new();
    private TabControl _bottom = new();
    private Button _bottomToggle = new();
    private readonly MonacoEditorHost _monaco = new();
    private ScriptWorkspaceWatcher? _workspaceWatcher;
    private readonly TextBlock _breadcrumb = new() { Foreground = UiTheme.Muted, FontSize = 12, Margin = new Thickness(14, 5), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _externalConflictBar = new() { IsVisible = false };
    private readonly TextBlock _externalConflictText = new() { Foreground = UiTheme.Text, TextWrapping = TextWrapping.Wrap };
    private Button _externalConflictReload = new();
    private readonly TextBlock _statusLeft = new() { Foreground = UiTheme.Muted, FontSize = 12 };
    private readonly TextBlock _statusCursor = new() { Foreground = UiTheme.Muted, FontSize = 12 };
    private Border _breadcrumbBar = new();
    private Border _explorerFrame = new();
    private GridSplitter _leftSplit = new();
    private IReadOnlyList<ScriptPackageSnapshot> _packages = [];
    private ScriptPackageSnapshot? _activePackage;
    private bool _bottomCollapsed;
    private bool _packageOperationRunning;
    private bool _refreshingProfiles;
    private bool _refreshingNavigator;
    private long _navigatorRefreshGeneration;
    private bool _closeApproved;

    internal event Func<string, Task>? GlobalSearchRequested;
    internal event Func<Task>? StudioSettingsRequested;

    public AutomationStudioWindow(NexMudRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _workspace = runtime.ScriptWorkspace;
        _session = new StudioSessionController(runtime.ActiveConnectionProfile.Id);
        _typescript = new StudioTypeScriptLanguageHost(_workspace.GetLanguageWorkspaceRoot);
        _bottomCollapsed = _preferences.BottomCollapsed;
        _activity = _preferences.ResolveActivity();
        UpdateConnectionStatus(_runtime.Transport.IsConnected
            ? ConnectionStatus.Connected
            : ConnectionStatus.Disconnected);

        Title = "NexMUD Automation Studio";
        Width = 1672;
        Height = 913;
        MinWidth = 1180;
        MinHeight = 700;
        CanResize = true;
        Background = StudioShellChrome.Canvas;
        FontFamily = StudioShellChrome.Font;
        WireMonaco();
        _filter.PlaceholderText = _activity == StudioActivity.Automations ? "Search automations…" : "Filter";
        _filter.TextChanged += async (_, _) => await RefreshNavigatorAsync().ConfigureAwait(true);
        Content = BuildLayout();

        _profile.Background = StudioShellChrome.Input;
        _profile.Foreground = StudioShellChrome.Foreground;
        _profile.BorderBrush = StudioShellChrome.Border;
        _profile.MinHeight = 34;
        _profile.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ConnectionProfile.Name));
        _profile.SelectionChanged += ProfileSelectionChanged;
        _navigator.SelectionChanged += NavigatorSelectionChanged;
        _workspace.WorkspaceChanged += WorkspaceChanged;
        _documents.Changed += DocumentsChanged;
        KeyDown += WindowKeyDown;
        Opened += WindowOpened;
        Closing += WindowClosing;
        Closed += WindowClosed;
    }

    private string SourceDocumentUri(string packageId, string relativePath) =>
        _workspace.GetSourceDocumentUri(SelectedProfileId, packageId, relativePath);

    private string SelectedProfileId =>
        _session.Current.ProfileId;

    // ───────────────────────────── Layout ─────────────────────────────

    private Control BuildLayout()
    {
        double bottomHeight = _bottomCollapsed
            ? StudioUiPreferences.CollapsedBottomHeight
            : _preferences.BottomHeight;
        _root = new Grid { RowDefinitions = new RowDefinitions($"Auto,*,5,{bottomHeight}") };
        _root.Children.Add(BuildHeader());

        _body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"108,{_preferences.ExplorerWidth},5,*")
        };
        Grid.SetRow(_body, 1);
        _root.Children.Add(_body);

        _body.Children.Add(BuildActivityRail());

        Grid explorer = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = StudioShellChrome.Explorer };
        explorer.Children.Add(BuildExplorerHeader());
        _filter.Margin = new Thickness(8, 0, 8, 6);
        Grid.SetRow(_filter, 1);
        explorer.Children.Add(_filter);
        ScrollViewer tree = new() { Content = _navigator };
        Grid.SetRow(tree, 2);
        explorer.Children.Add(tree);
        _explorerFrame = Frame("EXPLORER", explorer, withHeader: false);
        _explorerFrame.Background = StudioShellChrome.Explorer;
        _explorerFrame.BorderBrush = StudioShellChrome.Border;
        Grid.SetColumn(_explorerFrame, 1);
        _body.Children.Add(_explorerFrame);

        _leftSplit = Splitter(GridResizeDirection.Columns); Grid.SetColumn(_leftSplit, 2); _body.Children.Add(_leftSplit);
        Grid documentArea = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Background = StudioShellChrome.Canvas };
        documentArea.Children.Add(new Border
        {
            MinHeight = 40, Background = StudioShellChrome.Card, BorderBrush = StudioShellChrome.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        });
        _breadcrumbBar = new Border { Child = _breadcrumb, Background = StudioShellChrome.Canvas, BorderBrush = StudioShellChrome.Border, BorderThickness = new Thickness(0, 0, 0, 1) };
        Grid.SetRow(_breadcrumbBar, 1);
        documentArea.Children.Add(_breadcrumbBar);
        Control conflictBar = BuildExternalConflictBar();
        Grid.SetRow(conflictBar, 2);
        documentArea.Children.Add(conflictBar);
        // Keep the native WebView attached for the full window lifetime; detaching it destroys editor state.
        _monaco.MaxHeight = 0;
        Grid surface = new() { Background = StudioShellChrome.Canvas, Children = { _center, _monaco } };
        Grid.SetRow(surface, 3);
        documentArea.Children.Add(surface);
        Border centerFrame = new() { Background = StudioShellChrome.Canvas, BorderBrush = StudioShellChrome.Border, BorderThickness = new Thickness(1), Child = documentArea };
        Grid.SetColumn(centerFrame, 3); _body.Children.Add(centerFrame);

        GridSplitter horizontal = Splitter(GridResizeDirection.Rows); Grid.SetRow(horizontal, 2); _root.Children.Add(horizontal);
        _bottom = new TabControl
        {
            Background = StudioShellChrome.Canvas,
            Foreground = StudioShellChrome.Secondary,
            FontSize = 12,
            ItemsSource = new object[]
            {
                BottomTab("Problems", _problems), BottomTab("Output", _console), BottomTab("Runtime", _runtimePanel),
                BottomTab("Events", _eventsPanel), BottomTab("References", _referencesPanel), BuildTestsTab()
            }
        };
        _bottomToggle = new Button
        {
            Content = _bottomCollapsed ? "⌃" : "⌄",
            Width = 34,
            Background = StudioShellChrome.Canvas,
            Foreground = StudioShellChrome.Foreground,
            BorderThickness = new Thickness(0)
        };
        _bottomToggle.Click += (_, _) => ToggleBottom();
        Grid bottom = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bottom.Children.Add(_bottom);
        Grid.SetColumn(_bottomToggle, 1);
        bottom.Children.Add(_bottomToggle);
        Grid.SetRow(bottom, 3);
        _root.Children.Add(bottom);
        return _root;
    }

    private Control BuildActivityRail()
    {
        RenderActivityRail();
        return _activityRail;
    }

    private void RenderActivityRail() =>
        _activityRail.Child = StudioShellChrome.BuildActivityButtons(_activity, SetActivityAsync);

    private async Task SetActivityAsync(StudioActivity activity)
    {
        if (_activity == activity) return;
        _activity = activity;
        _preferences.SetActivity(activity);
        RenderActivityRail();
        UpdateActivityChrome();
        await RefreshNavigatorAsync().ConfigureAwait(true);
    }

    private void UpdateActivityChrome()
    {
        _explorerTitle.Text = StudioActivityModel.ExplorerTitle(_activity);
        _filter.IsVisible = _activity is StudioActivity.Automations or StudioActivity.Scripts or StudioActivity.Workflows;
        _filter.PlaceholderText = _activity == StudioActivity.Automations ? "Search automations…" : "Filter";
        _newButton.IsVisible = _activity is StudioActivity.Automations or StudioActivity.Scripts or StudioActivity.Workflows;
        UpdateOrganizeButton();
    }

    private Control BuildExplorerHeader()
    {
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 5, Margin = new Thickness(10, 12) };
        header.Children.Add(_explorerTitle);
        _organizeButton = UiTheme.QuietButton("Move");
        _organizeButton.Foreground = StudioShellChrome.Foreground;
        _organizeButton.BorderBrush = StudioShellChrome.Border;
        _organizeButton.IsVisible = false;
        _organizeButton.Click += async (_, _) => await OrganizeSelectedAsync().ConfigureAwait(true);
        Grid.SetColumn(_organizeButton, 1);
        header.Children.Add(_organizeButton);

        _newButton = UiTheme.QuietButton("+ New");
        _newButton.Foreground = StudioShellChrome.Foreground;
        _newButton.BorderBrush = StudioShellChrome.Border;
        _newButton.Click += async (_, _) =>
        {
            if (_activity == StudioActivity.Scripts)
            {
                ShowNewScriptMenu();
                return;
            }

            ContextMenu menu = new();
            StudioDocumentKind[] kinds = StudioDocumentKinds.AutomationKinds
                .Where(kind => StudioActivityModel.IncludesAutomationKind(_activity, kind))
                .ToArray();
            foreach (StudioDocumentKind kind in kinds)
            {
                MenuItem item = new() { Header = kind.Label() };
                StudioDocumentKind captured = kind;
                item.Click += async (_, _) => await CreateAutomationAsync(captured).ConfigureAwait(true);
                menu.Items.Add(item);
            }
            if (kinds.Length > 0) menu.Items.Add(new Separator());
            foreach (StudioDocumentKind kind in kinds)
            {
                MenuItem folder = new() { Header = $"New {kind.CategoryLabel()} Folder…" };
                StudioDocumentKind captured = kind;
                folder.Click += async (_, _) => await CreateFolderAsync(captured).ConfigureAwait(true);
                menu.Items.Add(folder);
            }
            menu.Open(_newButton);
        };
        Grid.SetColumn(_newButton, 2);
        header.Children.Add(_newButton);
        UpdateActivityChrome();
        return header;
    }

    private void ShowNewScriptMenu()
    {
        ContextMenu menu = new();
        MenuItem package = new() { Header = "New Package…" };
        package.Click += async (_, _) => await CreatePackageAsync().ConfigureAwait(true);
        menu.Items.Add(package);

        (string PackageId, string ParentPath)? location = SelectedScriptLocation();
        if (location is not null)
        {
            menu.Items.Add(new Separator());
            MenuItem folder = new() { Header = "New Folder…" };
            folder.Click += async (_, _) => await CreateScriptFolderAsync().ConfigureAwait(true);
            menu.Items.Add(folder);
            menu.Items.Add(new Separator());
            AddScriptFileMenuItem(menu, "New TypeScript File…", ".ts");
            AddScriptFileMenuItem(menu, "New JavaScript File…", ".js");
            AddScriptFileMenuItem(menu, "New JSON File…", ".json");
        }
        menu.Open(_newButton);
    }

    private void AddScriptFileMenuItem(ContextMenu menu, string label, string extension)
    {
        MenuItem item = new() { Header = label };
        item.Click += async (_, _) => await CreateScriptFileAsync(extension).ConfigureAwait(true);
        menu.Items.Add(item);
    }

    private (string PackageId, string ParentPath)? SelectedScriptLocation()
    {
        if (_navigator.SelectedItem is TreeViewItem selected && _nodes.TryGetValue(selected, out StudioNode? node) &&
            node.PackageId is { } packageId)
        {
            return node.Kind switch
            {
                NodeKind.Package => (packageId, string.Empty),
                NodeKind.SourceFolder when node.Path is { } path => (packageId, path),
                NodeKind.SourceFile when node.Path is { } path => (packageId, ScriptExplorerTree.ParentPath(path)),
                _ => null
            };
        }
        return _activePackage is null ? null : (_activePackage.Definition.PackageId, string.Empty);
    }

    private Control BuildHeader()
    {
        StackPanel profile = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Profile:", Foreground = StudioShellChrome.Foreground, VerticalAlignment = VerticalAlignment.Center },
                _profile
            }
        };
        StackPanel connection = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _connectionIndicator,
                _connectionStatus
            }
        };
        _connectionIndicator.VerticalAlignment = VerticalAlignment.Center;
        _connectionStatus.VerticalAlignment = VerticalAlignment.Center;
        return StudioShellChrome.BuildHeader(
            profile,
            connection,
            RouteGlobalSearchAsync,
            () => StudioSettingsRequested?.Invoke() ?? Task.CompletedTask);
    }

    private async Task RouteGlobalSearchAsync(string query)
    {
        await SetActivityAsync(StudioActivity.Search).ConfigureAwait(true);
        if (GlobalSearchRequested is { } searchRequested)
            await searchRequested(query).ConfigureAwait(true);
    }

    private void UpdateConnectionStatus(ConnectionStatus status)
    {
        _connectionStatus.Text = status.ToString();
        _connectionIndicator.Background = status switch
        {
            ConnectionStatus.Connected => StudioShellChrome.Success,
            ConnectionStatus.Connecting => StudioShellChrome.Connecting,
            _ => StudioShellChrome.Secondary
        };
    }

    private void ShowPackageOperationsMenu(Button anchor)
    {
        ContextMenu menu = new();
        MenuItem restore = new() { Header = "Restore from Lockfile" };
        restore.Click += async (_, _) => await RestorePackagesAsync().ConfigureAwait(true);
        MenuItem install = new() { Header = "Install / Resolve" };
        install.Click += async (_, _) => await InstallPackagesAsync().ConfigureAwait(true);
        MenuItem update = new() { Header = "Update All" };
        update.Click += async (_, _) => await UpdatePackagesAsync().ConfigureAwait(true);
        MenuItem clean = new() { Header = "Clean Generated Artifacts" };
        clean.Click += async (_, _) => await CleanPackagesAsync().ConfigureAwait(true);
        menu.Items.Add(restore);
        menu.Items.Add(install);
        menu.Items.Add(update);
        menu.Items.Add(new Separator());
        menu.Items.Add(clean);
        menu.Open(anchor);
    }

    private Control BuildExternalConflictBar()
    {
        Button compare = Button("Compare", CompareExternalConflictAsync);
        _externalConflictReload = Button("Reload from Disk", ReloadExternalConflictAsync);
        Button keep = Button("Keep Editor Version", KeepExternalEditorVersionAsync);
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { compare, _externalConflictReload, keep }
        };
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        content.Children.Add(_externalConflictText);
        Grid.SetColumn(actions, 1);
        content.Children.Add(actions);
        _externalConflictBar.Background = StudioShellChrome.Card;
        _externalConflictBar.BorderBrush = StudioShellChrome.Border;
        _externalConflictBar.BorderThickness = new Thickness(0, 0, 0, 1);
        _externalConflictBar.Padding = new Thickness(12, 7);
        _externalConflictBar.Child = content;
        return _externalConflictBar;
    }

    private void TogglePane(Border frame, GridSplitter splitter, int column, double width)
    {
        bool show = !frame.IsVisible;
        frame.IsVisible = show;
        splitter.IsVisible = show;
        _body.ColumnDefinitions[column].Width = show ? new GridLength(width) : new GridLength(0);
        _body.ColumnDefinitions[column].MinWidth = show ? 0 : 0;
    }

    private async Task QuickOpenAsync()
    {
        AutomationCollections collections = AutomationCollections.From(_runtime.Settings);
        List<(string Label, Func<Task> Open)> items = [];
        foreach (StudioDocumentKind kind in StudioDocumentKinds.AutomationKinds)
            foreach (AutomationEntryInfo entry in collections.Entries(kind))
            {
                StudioDocumentKind k = kind;
                string automationId = _organization.IdFor(kind, entry.Index);
                items.Add(($"{kind.Label()}  ·  {entry.Name}", () => { OpenAutomation(k, automationId); return Task.CompletedTask; }));
            }
        foreach (ScriptPackageSnapshot package in _packages)
            foreach (ScriptWorkspaceSourceFile file in await _workspace.ListSourceFilesAsync(SelectedProfileId, package.Definition.PackageId, _cts.Token).ConfigureAwait(true))
            {
                string id = package.Definition.PackageId, path = file.RelativePath;
                items.Add(($"Script  ·  {package.Definition.Name}/{path}", () => OpenSourceAsync(id, path)));
            }
        Window dialog = Dialog("Quick Open", 620, 420);
        TextBox input = UiTheme.FieldBox();
        input.PlaceholderText = "Type to search aliases, triggers, scripts…";
        ListBox list = new() { Background = Brushes.Transparent };
        void Refresh() => list.ItemsSource = items.Where(item => StudioFilter.Matches(item.Label, input.Text)).Take(60).Select(item => item.Label).ToArray();
        input.TextChanged += (_, _) => { Refresh(); list.SelectedIndex = 0; };
        void Accept() { if (list.SelectedItem is string label) dialog.Close(label); }
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Down) { list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { dialog.Close(null); e.Handled = true; }
        };
        list.DoubleTapped += (_, _) => Accept();
        Refresh(); list.SelectedIndex = 0;
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12), RowSpacing = 8 };
        layout.Children.Add(input);
        Grid.SetRow(list, 1); layout.Children.Add(list);
        dialog.Content = layout;
        dialog.Opened += (_, _) => input.Focus();
        string? chosen = await dialog.ShowDialog<string?>(this).ConfigureAwait(true);
        if (chosen is null) return;
        try { await items.First(item => item.Label == chosen).Open().ConfigureAwait(true); }
        catch (Exception exception) { AddProblem("Quick Open", exception.Message); }
    }

    private void ToggleBottom()
    {
        _bottomCollapsed = !_bottomCollapsed;
        _root.RowDefinitions[3].Height = new GridLength(_bottomCollapsed ? StudioUiPreferences.CollapsedBottomHeight : _preferences.BottomHeight);
        _bottomToggle.Content = _bottomCollapsed ? "⌃" : "⌄";
    }

    private static Button Button(string label, Func<Task> action)
    {
        Button button = UiTheme.QuietButton(label);
        button.Click += async (_, _) => await action().ConfigureAwait(true);
        return button;
    }

    private static GridSplitter Splitter(GridResizeDirection direction) => new()
    {
        ResizeDirection = direction,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        Background = StudioShellChrome.Border
    };

    private static Border Frame(string title, Control content, bool withHeader = true)
    {
        Control child = content;
        if (withHeader)
        {
            Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*") };
            grid.Children.Add(new TextBlock
            {
                Text = title, Foreground = UiTheme.Accent, FontWeight = FontWeight.SemiBold,
                FontSize = NexTypography.Metadata, Margin = new Thickness(8, 6)
            });
            Grid.SetRow(content, 1); grid.Children.Add(content);
            child = grid;
        }
        return new Border { BorderBrush = StudioShellChrome.Border, BorderThickness = new Thickness(1), Background = StudioShellChrome.Explorer, Child = child };
    }

    private static TabItem BottomTab(string header, Control content) => new()
    {
        Header = new TextBlock
        {
            Text = header,
            FontSize = 12,
            Foreground = StudioShellChrome.Secondary,
            VerticalAlignment = VerticalAlignment.Center
        },
        Content = new ScrollViewer { Content = content, Padding = new Thickness(8), Background = StudioShellChrome.Canvas }
    };

    // ───────────────────────────── Lifecycle ─────────────────────────────

    private async void WindowOpened(object? sender, EventArgs e)
    {
        await RefreshProfilesAsync().ConfigureAwait(true);
        RestartWorkspaceWatcher();
        await RefreshNavigatorAsync().ConfigureAwait(true);
        RenderActive();
        _ = Task.Run(() => ConsumeEventsAsync(_cts.Token), CancellationToken.None);
    }

    private async Task RefreshProfilesAsync()
    {
        _refreshingProfiles = true;
        try
        {
            IReadOnlyList<ConnectionProfile> profiles = _runtime.ConnectionProfiles;
            string selected = _session.Current.ProfileId;
            _profile.ItemsSource = profiles;
            _profile.SelectedItem = profiles.FirstOrDefault(item => item.Id.Equals(selected, StringComparison.Ordinal))
                ?? profiles.FirstOrDefault();
        }
        finally { _refreshingProfiles = false; }
        await Task.CompletedTask;
    }

    private async void ProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingProfiles) return;
        if (_profile.SelectedItem is not ConnectionProfile selectedProfile) return;
        long generation = _profileTransitions.Request();
        string targetProfileId = selectedProfile.Id;

        try
        {
            if (!await _profileTransitions.EnterLatestAsync(generation, _cts.Token).ConfigureAwait(true)) return;
            try
            {
                if (!_profileTransitions.IsCurrent(generation) ||
                    targetProfileId.Equals(_session.Current.ProfileId, StringComparison.Ordinal)) return;
                if (_documents.Dirty.Any())
                {
                    AddProblem("Warning", "Save or close dirty documents before changing profile scope.");
                    await RefreshProfilesAsync().ConfigureAwait(true);
                    return;
                }

                StudioSessionSnapshot previous = _session.Current;
                _profileDocumentSessions.Capture(previous.ProfileId, _documents.Documents, _documents.Active?.Key);
                await CloseSessionDocumentsAsync(previous.CancellationToken).ConfigureAwait(true);
                if (!_profileTransitions.IsCurrent(generation))
                {
                    if (_session.IsCurrent(previous) && _profile.SelectedItem is ConnectionProfile latestProfile &&
                        latestProfile.Id.Equals(previous.ProfileId, StringComparison.Ordinal) &&
                        _profileDocumentSessions.TryGet(previous.ProfileId, out StudioDocumentSessionSnapshot snapshot))
                    {
                        await RestoreSessionDocumentsAsync(previous, snapshot.Documents, snapshot.ActiveDocumentKey).ConfigureAwait(true);
                        _profileDocumentSessions.Remove(previous.ProfileId);
                    }
                    return;
                }

                _pendingLanguageDiagnostics.Clear();
                RefreshProblems();
                CancelTests("Profile changed.");
                ResetTestsPanel("Select a package to discover tests.");
                _session.SwitchProfile(targetProfileId);
                _diagnostics.ResolveSourcesByPrefix(targetProfileId, "tests:");
                Interlocked.Increment(ref _navigatorRefreshGeneration);
                _refreshingNavigator = false;
                _packages = [];
                _nodes.Clear();
                _navigator.ItemsSource = Array.Empty<object>();
                _activePackage = null;
                RefreshProblems();
                await RefreshRuntimePanelAsync().ConfigureAwait(true);
                _diagnostics.ResolveSourcesByPrefix(previous.ProfileId, "tests:");
                try
                {
                    await _typescript.SwitchProfileAsync(targetProfileId, _cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    string message = $"Could not switch TypeScript language service to profile '{targetProfileId}': {exception.Message}";
                    _diagnostics.ReplaceSource(targetProfileId, "typescript-service",
                        [new StudioDiagnostic(targetProfileId, "typescript-service", "switch", "Error", message)]);
                    RefreshProblems();
                    _pendingLanguageDiagnostics.Clear();
                    _ = _monaco.SetLanguageServerAvailableAsync(false);
                    ShowLanguageServerFailure(message);
                }
                RestartWorkspaceWatcher();
                await RefreshNavigatorAsync().ConfigureAwait(true);
                if (_profileTransitions.IsCurrent(generation) &&
                    _profileDocumentSessions.TryGet(targetProfileId, out StudioDocumentSessionSnapshot targetSnapshot))
                {
                    await RestoreSessionDocumentsAsync(_session.Current, targetSnapshot.Documents, targetSnapshot.ActiveDocumentKey).ConfigureAwait(true);
                    if (_profileTransitions.IsCurrent(generation)) _profileDocumentSessions.Remove(targetProfileId);
                }
            }
            finally
            {
                _profileTransitions.Exit();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    private async Task RestoreSessionDocumentsAsync(
        StudioSessionSnapshot session,
        IReadOnlyList<StudioDocument> documents,
        string? activeDocumentKey)
    {
        foreach (StudioDocument document in documents)
        {
            if (!_session.IsCurrent(session)) return;
            if (document.IsScript && document.PackageId is not null && document.Path is not null)
            {
                ScriptPackageSnapshot? package = await _workspace.GetPackageAsync(
                    session.ProfileId, document.PackageId, session.CancellationToken).ConfigureAwait(true);
                if (!_session.IsCurrent(session)) return;
                if (package is null)
                {
                    AddProblem("Session", $"Skipped restoring '{document.Title}' because package '{document.PackageId}' no longer exists.");
                    continue;
                }
                try
                {
                    await OpenSourceAsync(session, document.PackageId, document.Path).ConfigureAwait(true);
                }
                catch (FileNotFoundException)
                {
                    if (!_session.IsCurrent(session)) return;
                    AddProblem("Session", $"Skipped restoring '{document.Title}' because its source file no longer exists.");
                }
                catch (DirectoryNotFoundException)
                {
                    if (!_session.IsCurrent(session)) return;
                    AddProblem("Session", $"Skipped restoring '{document.Title}' because its source directory no longer exists.");
                }
            }
            else
                _documents.OpenOrFocus(document);
        }

        if (activeDocumentKey is not null && _documents.Activate(activeDocumentKey))
        {
            StudioDocument? activeDocument = _documents.Find(activeDocumentKey);
            if (activeDocument?.IsScript == true)
            {
                if (activeDocument.PackageId is not null)
                    await SelectPackageAsync(session, activeDocument.PackageId).ConfigureAwait(true);
                await _monaco.SetActiveDocumentAsync(activeDocumentKey, session.CancellationToken).ConfigureAwait(true);
            }
            else
            {
                RenderActive();
            }
        }
    }

    private async Task CloseSessionDocumentsAsync(CancellationToken cancellationToken)
    {
        foreach (StudioDocument document in _documents.Documents.Where(document => document.IsScript).ToArray())
        {
            try
            {
                await _monaco.CloseDocumentAsync(document.Key, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        CloseAllEditors();
        _documents.CloseAll();
        _textModels.Clear();
        _ignoredMonacoChanges.Clear();
        _scriptSelections.Clear();
    }

    private void RestartWorkspaceWatcher()
    {
        if (_workspaceWatcher is not null)
        {
            _workspaceWatcher.Changed -= WorkspaceExternalChanged;
            _workspaceWatcher.Dispose();
            _workspaceWatcher = null;
        }
        try
        {
            _workspaceWatcher = _workspace.WatchProfile(SelectedProfileId);
            _workspaceWatcher.Changed += WorkspaceExternalChanged;
        }
        catch (Exception exception)
        {
            AddProblem("Scripts", $"Unable to watch the script workspace: {exception.Message}");
        }
    }

    private void WorkspaceChanged(object? sender, EventArgs e)
    {
        if (_cts.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(async () => await RefreshNavigatorAsync().ConfigureAwait(true));
    }

    private void WorkspaceExternalChanged(object? sender, ScriptWorkspaceExternalChangesEventArgs e)
    {
        if (_cts.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (_cts.IsCancellationRequested ||
                !e.ProfileId.Equals(SelectedProfileId, StringComparison.Ordinal)) return;
            StudioSessionSnapshot snapshot = _session.Current;
            if (!_session.IsCurrent(snapshot)) return;
            try
            {
                await HandleExternalWorkspaceChangesAsync(e.Changes).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                AddProblem("Scripts", $"External file change handling failed: {exception.Message}");
            }
        });
    }

    private async Task HandleExternalWorkspaceChangesAsync(IReadOnlyList<ScriptWorkspaceExternalChange> changes)
    {
        foreach (ScriptWorkspaceExternalChange change in changes)
        {
            if (!change.ProfileId.Equals(SelectedProfileId, StringComparison.Ordinal)) continue;
            switch (change.Kind)
            {
                case ScriptWorkspaceExternalChangeKind.Created:
                case ScriptWorkspaceExternalChangeKind.Changed:
                    await HandleExternalFileChangedAsync(change).ConfigureAwait(true);
                    break;
                case ScriptWorkspaceExternalChangeKind.Deleted:
                    await HandleExternalPathDeletedAsync(change).ConfigureAwait(true);
                    break;
                case ScriptWorkspaceExternalChangeKind.Renamed:
                    await HandleExternalPathRenamedAsync(change).ConfigureAwait(true);
                    break;
            }
        }
        await RefreshNavigatorAsync().ConfigureAwait(true);
        RenderExternalConflict();
    }

    private async Task HandleExternalFileChangedAsync(ScriptWorkspaceExternalChange change)
    {
        if (!ScriptWorkspaceService.IsSupportedSourcePath(change.RelativePath)) return;
        string uri = SourceDocumentUri(change.PackageId, change.RelativePath);
        if (_documents.Find(uri) is not { IsScript: true } document) return;
        (bool exists, string? diskContent) = await TryReadSourceAsync(change.PackageId, change.RelativePath).ConfigureAwait(true);
        if (!exists || diskContent is null)
        {
            await HandleExternalPathDeletedAsync(change with { Kind = ScriptWorkspaceExternalChangeKind.Deleted }).ConfigureAwait(true);
            return;
        }
        if (_textModels.MatchesBaseline(uri, diskContent)) return;

        if (document.IsDirty)
        {
            if (_textModels.RecordConflict(uri, StudioExternalFileChangeKind.Modified, diskContent))
                AddProblem("Scripts", $"{change.PackageId}/{change.RelativePath} changed on disk; unsaved editor changes were preserved.");
            return;
        }

        _scriptSelections.TryGetValue(uri, out MonacoSelectionChanged? selection);
        _ignoredMonacoChanges.Add(uri);
        try
        {
            await _monaco.SetDocumentContentAsync(uri, diskContent, _cts.Token).ConfigureAwait(true);
            if (selection is not null)
                await _monaco.RevealLocationAsync(uri, selection.EndLine, selection.EndColumn, _cts.Token).ConfigureAwait(true);
        }
        catch
        {
            _ignoredMonacoChanges.Remove(uri);
            throw;
        }
        _textModels.MarkSaved(uri, diskContent);
        _documents.SetDirty(uri, false);
        AddConsole($"Reloaded external change: {change.PackageId}/{change.RelativePath}");
    }

    private async Task HandleExternalPathDeletedAsync(ScriptWorkspaceExternalChange change)
    {
        if (string.IsNullOrWhiteSpace(change.RelativePath))
        {
            _diagnostics.ResolveSource(change.ProfileId, "tests:" + change.PackageId);
            RefreshProblems();
        }
        StudioDocument[] affected = OpenScriptDocuments(change.PackageId, change.RelativePath).ToArray();
        foreach (StudioDocument document in affected)
        {
            if (document.IsDirty)
            {
                if (_textModels.RecordConflict(document.Key, StudioExternalFileChangeKind.Deleted, null))
                    AddProblem("Scripts", $"{document.PackageId}/{document.Path} was deleted on disk; the unsaved editor buffer was preserved.");
                continue;
            }

            await _monaco.CloseDocumentAsync(document.Key, _cts.Token).ConfigureAwait(true);
            _textModels.Remove(document.Key);
            _ignoredMonacoChanges.Remove(document.Key);
            _documents.Close(document.Key);
            AddConsole($"Closed externally deleted file: {document.PackageId}/{document.Path}");
        }
    }

    private async Task HandleExternalPathRenamedAsync(ScriptWorkspaceExternalChange change)
    {
        if (string.IsNullOrWhiteSpace(change.PreviousRelativePath)) return;
        string sourcePath = change.PreviousRelativePath;
        string destinationPath = change.RelativePath;
        StudioDocument[] affected = OpenScriptDocuments(change.PackageId, sourcePath).ToArray();
        bool hasDirty = affected.Any(document => document.IsDirty);
        string? previousActiveKey = _documents.Active?.Key;
        Dictionary<string, string> movedUris = new(StringComparer.Ordinal);

        foreach (StudioDocument document in affected)
        {
            string newPath = ScriptExplorerTree.Rebase(document.Path!, sourcePath, destinationPath);
            (bool exists, string? diskContent) = await TryReadSourceAsync(change.PackageId, newPath).ConfigureAwait(true);
            if (document.IsDirty)
            {
                if (_textModels.RecordConflict(
                        document.Key,
                        StudioExternalFileChangeKind.Renamed,
                        exists ? diskContent : null,
                        newPath))
                    AddProblem("Scripts", $"{document.PackageId}/{document.Path} moved on disk to {newPath}; the unsaved editor buffer was preserved.");
                continue;
            }

            if (!exists) continue;
            string newUri = SourceDocumentUri(change.PackageId, newPath);
            movedUris[document.Key] = newUri;
            _scriptSelections.TryGetValue(document.Key, out MonacoSelectionChanged? selection);
            await _monaco.CloseDocumentAsync(document.Key, _cts.Token).ConfigureAwait(true);
            _textModels.Remove(document.Key);
            _ignoredMonacoChanges.Remove(document.Key);
            _documents.Close(document.Key);
            await OpenSourceAsync(change.PackageId, newPath).ConfigureAwait(true);
            if (selection is not null)
            {
                _scriptSelections[newUri] = selection with { Uri = newUri };
                await _monaco.RevealLocationAsync(newUri, selection.EndLine, selection.EndColumn, _cts.Token).ConfigureAwait(true);
            }
            AddConsole($"Followed external move: {change.PackageId}/{document.Path} → {newPath}");
        }

        if (!hasDirty)
            await RewriteExternalRenameReferencesAsync(change.PackageId, sourcePath, destinationPath).ConfigureAwait(true);

        if (previousActiveKey is not null)
        {
            if (movedUris.TryGetValue(previousActiveKey, out string? movedActive)) _documents.Activate(movedActive);
            else if (_documents.Find(previousActiveKey) is not null) _documents.Activate(previousActiveKey);
        }
    }

    private IEnumerable<StudioDocument> OpenScriptDocuments(string packageId, string path)
    {
        foreach (StudioDocument document in _documents.Documents)
        {
            if (!document.IsScript || document.PackageId is null || document.Path is null) continue;
            if (!document.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(path) || ScriptExplorerTree.IsSameOrDescendant(document.Path, path))
                yield return document;
        }
    }

    private async Task RewriteExternalRenameReferencesAsync(string packageId, string sourcePath, string destinationPath)
    {
        string[] referencedPaths = _referencesIndex.Build(_runtime.Settings)
            .Where(reference => reference.FunctionRef.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
                                ScriptExplorerTree.IsSameOrDescendant(reference.FunctionRef.ModulePath, sourcePath))
            .Select(reference => reference.FunctionRef.ModulePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (referencedPaths.Length == 0) return;

        ClientSettings rewritten = _runtime.Settings;
        foreach (string oldPath in referencedPaths)
        {
            string newPath = ScriptExplorerTree.Rebase(oldPath, sourcePath, destinationPath);
            rewritten = _referencesIndex.RewriteModulePath(rewritten, packageId, oldPath, newPath);
        }
        await AutomationCollections.From(rewritten).SaveAsync(_runtime, _cts.Token).ConfigureAwait(true);
    }

    private async Task<(bool Exists, string? Content)> TryReadSourceAsync(string packageId, string relativePath)
    {
        try
        {
            return (true, await _workspace.ReadSourceAsync(SelectedProfileId, packageId, relativePath, _cts.Token).ConfigureAwait(true));
        }
        catch (FileNotFoundException)
        {
            return (false, null);
        }
        catch (DirectoryNotFoundException)
        {
            return (false, null);
        }
    }

    // ───────────────────────────── Explorer ─────────────────────────────

    private async Task RefreshNavigatorAsync()
    {
        string profileId = SelectedProfileId;
        long generation = Interlocked.Increment(ref _navigatorRefreshGeneration);
        _refreshingNavigator = true;
        try
        {
            IReadOnlyList<ScriptPackageSnapshot> packages = await _workspace.ListPackagesAsync(profileId, _cts.Token).ConfigureAwait(true);
            if (!IsCurrentNavigatorRefresh(profileId, generation)) return;
            AutomationCollections collections = AutomationCollections.From(_runtime.Settings);
            AutomationOrganizationCatalog organization = await _organizationStore.LoadAndReconcileAsync(
                profileId,
                collections,
                _cts.Token).ConfigureAwait(true);
            if (!IsCurrentNavigatorRefresh(profileId, generation)) return;
            HashSet<string> packageIds = packages
                .Select(package => package.Definition.PackageId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string source in _diagnostics.Snapshot(profileId)
                         .Select(diagnostic => diagnostic.Source)
                         .Where(source => source.StartsWith("tests:", StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal))
            {
                if (!packageIds.Contains(source["tests:".Length..]))
                    _diagnostics.ResolveSource(profileId, source);
            }
            _packages = packages;
            _organization = organization;
            RefreshProblems();
            string? filter = _filter.Text;
            _nodes.Clear();
            List<object> roots = [];

            if (_activity is StudioActivity.Automations or StudioActivity.Workflows)
            {
                foreach (StudioDocumentKind kind in StudioDocumentKinds.AutomationKinds
                             .Where(kind => StudioActivityModel.IncludesAutomationKind(_activity, kind)))
                {
                    IReadOnlyList<AutomationEntryInfo> entries = collections.Entries(kind);
                    TreeViewItem category = Node(new StudioNode(NodeKind.Category, $"{kind.CategoryLabel()} ({entries.Count})", kind));
                    category.IsExpanded = !string.IsNullOrWhiteSpace(filter) || entries.Count > 0 && entries.Count <= 12;
                    category.ItemsSource = BuildAutomationNodes(kind, entries, filter);
                    roots.Add(category);
                }
            }
            else if (StudioActivityModel.ShowsScriptPackages(_activity))
            {
                foreach (ScriptPackageSnapshot package in packages)
                {
                    TreeViewItem item = Node(new StudioNode(NodeKind.Package,
                        $"{(package.Definition.Enabled ? "●" : "○")} {package.Definition.Name}", PackageId: package.Definition.PackageId));
                    ContextMenu packageMenu = new();
                    MenuItem packageToggle = new() { Header = package.Definition.Enabled ? "Disable Package" : "Enable Package" };
                    packageToggle.Click += async (_, _) => await TogglePackageAsync(package.Definition.PackageId).ConfigureAwait(true);
                    packageMenu.Items.Add(packageToggle);
                    item.ContextMenu = packageMenu;
                    IReadOnlyList<ScriptWorkspaceEntry> entries = await _workspace.ListEntriesAsync(
                        profileId, package.Definition.PackageId, _cts.Token).ConfigureAwait(true);
                    if (!IsCurrentNavigatorRefresh(profileId, generation)) return;
                    item.IsExpanded = true;
                    item.ItemsSource = BuildScriptNodes(package.Definition.PackageId, entries, filter);
                    roots.Add(item);
                }
            }
            else if (_activity == StudioActivity.Runtime)
            {
                foreach (ScriptPackageSnapshot package in packages)
                    roots.Add(Node(new StudioNode(
                        NodeKind.Package,
                        $"{package.Definition.Name} · {package.RuntimeStatus} · {package.BuildStatus}",
                        PackageId: package.Definition.PackageId)));
            }

            if (!IsCurrentNavigatorRefresh(profileId, generation)) return;
            _navigator.ItemsSource = roots;
            await RefreshRuntimePanelAsync().ConfigureAwait(true);
            if (IsCurrentNavigatorRefresh(profileId, generation)) RefreshReferencePanel();
        }
        finally
        {
            if (generation == Volatile.Read(ref _navigatorRefreshGeneration))
                _refreshingNavigator = false;
        }
    }

    private bool IsCurrentNavigatorRefresh(string profileId, long generation) =>
        generation == Volatile.Read(ref _navigatorRefreshGeneration) &&
        string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal);


    private IReadOnlyList<TreeViewItem> BuildScriptNodes(
        string packageId,
        IReadOnlyList<ScriptWorkspaceEntry> entries,
        string? filter) =>
        ScriptExplorerTree.Build(entries, filter)
            .Select(entry => ScriptExplorerNode(packageId, entry))
            .ToArray();

    private TreeViewItem ScriptExplorerNode(string packageId, ScriptExplorerEntry entry)
    {
        TreeViewItem node = Node(new StudioNode(
            entry.IsFolder ? NodeKind.SourceFolder : NodeKind.SourceFile,
            entry.IsFolder ? $"▸ {entry.Name}" : entry.Name,
            PackageId: packageId,
            Path: entry.RelativePath));
        if (entry.IsFolder)
        {
            node.ItemsSource = entry.Children.Select(child => ScriptExplorerNode(packageId, child)).ToArray();
            node.IsExpanded = !string.IsNullOrWhiteSpace(_filter.Text);
        }
        return node;
    }

    private IReadOnlyList<TreeViewItem> BuildAutomationNodes(
        StudioDocumentKind kind,
        IReadOnlyList<AutomationEntryInfo> entries,
        string? filter)
    {
        Dictionary<int, AutomationEntryInfo> byIndex = entries.ToDictionary(entry => entry.Index);
        List<TreeViewItem> nodes = [];
        foreach (AutomationOrganizationFolder folder in _organization.FoldersFor(kind))
        {
            TreeViewItem? folderNode = BuildFolderNode(folder, byIndex, filter);
            if (folderNode is not null) nodes.Add(folderNode);
        }
        foreach (AutomationOrganizationItem item in _organization.ItemsFor(kind))
        {
            if (!byIndex.TryGetValue(item.SourceIndex, out AutomationEntryInfo? entry) ||
                !StudioFilter.Matches(entry.Name, filter)) continue;
            nodes.Add(AutomationEntryNode(kind, entry, item));
        }
        return nodes;
    }

    private TreeViewItem? BuildFolderNode(
        AutomationOrganizationFolder folder,
        IReadOnlyDictionary<int, AutomationEntryInfo> entries,
        string? filter)
    {
        bool folderMatches = StudioFilter.Matches(folder.Name, filter);
        string? childFilter = folderMatches ? null : filter;
        List<TreeViewItem> children = [];
        foreach (AutomationOrganizationFolder child in _organization.FoldersFor(folder.Kind, folder.Id))
        {
            TreeViewItem? childNode = BuildFolderNode(child, entries, childFilter);
            if (childNode is not null) children.Add(childNode);
        }
        foreach (AutomationOrganizationItem item in _organization.ItemsFor(folder.Kind, folder.Id))
        {
            if (!entries.TryGetValue(item.SourceIndex, out AutomationEntryInfo? entry) ||
                !StudioFilter.Matches(entry.Name, childFilter)) continue;
            children.Add(AutomationEntryNode(folder.Kind, entry, item));
        }
        if (!folderMatches && !string.IsNullOrWhiteSpace(filter) && children.Count == 0) return null;

        TreeViewItem node = Node(new StudioNode(
            NodeKind.Folder,
            $"▸ {folder.Name}",
            folder.Kind,
            FolderId: folder.Id));
        node.IsExpanded = !string.IsNullOrWhiteSpace(filter);
        node.ItemsSource = children;
        return node;
    }

    private TreeViewItem AutomationEntryNode(
        StudioDocumentKind kind,
        AutomationEntryInfo entry,
        AutomationOrganizationItem item)
    {
        TreeViewItem node = Node(new StudioNode(
            NodeKind.Entry,
            $"{(entry.Enabled ? "●" : "○")} {entry.Name}",
            kind,
            entry.Index,
            item.Id,
            item.FolderId));
        ContextMenu menu = new();
        MenuItem toggle = new() { Header = entry.Enabled ? "Disable" : "Enable" };
        toggle.Click += async (_, _) =>
        {
            OpenAutomation(kind, item.Id);
            if (_documents.Active is { } document)
                await ToggleAutomationAsync(document).ConfigureAwait(true);
        };
        MenuItem duplicate = new() { Header = "Duplicate" };
        duplicate.Click += async (_, _) =>
        {
            OpenAutomation(kind, item.Id);
            if (_documents.Active is { } document)
                await DuplicateAutomationAsync(document).ConfigureAwait(true);
        };
        MenuItem delete = new() { Header = "Delete…" };
        delete.Click += async (_, _) =>
        {
            OpenAutomation(kind, item.Id);
            if (_documents.Active is { } document)
                await DeleteAutomationAsync(document).ConfigureAwait(true);
        };
        menu.Items.Add(toggle);
        menu.Items.Add(duplicate);
        menu.Items.Add(delete);
        node.ContextMenu = menu;
        return node;
    }

    private TreeViewItem Node(StudioNode node)
    {
        TreeViewItem item = new() { Header = node.Label };
        _nodes[item] = node;
        return item;
    }

    private async void NavigatorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingNavigator) return;
        if (_navigator.SelectedItem is not TreeViewItem item || !_nodes.TryGetValue(item, out StudioNode? node))
        {
            UpdateOrganizeButton();
            return;
        }
        UpdateOrganizeButton(node);
        try
        {
            switch (node.Kind)
            {
                case NodeKind.Entry when node.DocKind is { } kind && node.AutomationId is { } automationId:
                    OpenAutomation(kind, automationId);
                    break;
                case NodeKind.Folder:
                    item.IsExpanded = !item.IsExpanded;
                    break;
                case NodeKind.Package when node.PackageId is { } packageId:
                    await SelectPackageAsync(packageId).ConfigureAwait(true);
                    RenderActive();
                    break;
                case NodeKind.SourceFolder:
                    item.IsExpanded = !item.IsExpanded;
                    break;
                case NodeKind.SourceFile when node.PackageId is { } package && node.Path is { } path:
                    await OpenSourceAsync(package, path).ConfigureAwait(true);
                    break;
                case NodeKind.Category or NodeKind.Scripts:
                    item.IsExpanded = !item.IsExpanded;
                    break;
            }
        }
        catch (Exception exception) { AddProblem("Error", exception.Message); }
    }

    private void UpdateOrganizeButton(StudioNode? node = null)
    {
        node ??= _navigator.SelectedItem is TreeViewItem selected && _nodes.TryGetValue(selected, out StudioNode? found)
            ? found
            : null;
        bool automationNode = node?.Kind is NodeKind.Entry or NodeKind.Folder;
        bool scriptNode = _activity == StudioActivity.Scripts &&
                          node?.Kind is NodeKind.Package or NodeKind.SourceFolder or NodeKind.SourceFile;
        _organizeButton.IsVisible = automationNode || scriptNode;
        _organizeButton.Content = node?.Kind switch
        {
            NodeKind.Folder or NodeKind.SourceFolder => "Folder…",
            NodeKind.Package => "Package…",
            NodeKind.SourceFile => "File…",
            _ => "Move"
        };
    }

    private async Task OrganizeSelectedAsync()
    {
        if (_navigator.SelectedItem is not TreeViewItem item || !_nodes.TryGetValue(item, out StudioNode? node)) return;
        if (node.Kind == NodeKind.Entry)
        {
            await MoveAutomationAsync(node).ConfigureAwait(true);
            return;
        }
        if (node.Kind == NodeKind.Package)
        {
            ShowScriptPackageMenu(node);
            return;
        }
        if (node.Kind == NodeKind.SourceFolder)
        {
            ShowScriptFolderMenu(node);
            return;
        }
        if (node.Kind == NodeKind.SourceFile)
        {
            ShowScriptFileMenu(node);
            return;
        }
        if (node.Kind != NodeKind.Folder || node.DocKind is not { } kind || node.FolderId is not { } folderId) return;

        ContextMenu menu = new();
        MenuItem child = new() { Header = "New Subfolder…" };
        child.Click += async (_, _) => await CreateFolderAsync(kind, folderId).ConfigureAwait(true);
        MenuItem rename = new() { Header = "Rename…" };
        rename.Click += async (_, _) => await RenameFolderAsync(folderId).ConfigureAwait(true);
        MenuItem delete = new() { Header = "Delete Empty Folder…" };
        delete.Click += async (_, _) => await DeleteFolderAsync(folderId).ConfigureAwait(true);
        menu.Items.Add(child);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.Open(_organizeButton);
    }

    private void ShowScriptPackageMenu(StudioNode node)
    {
        ContextMenu menu = new();
        MenuItem folder = new() { Header = "New Folder…" };
        folder.Click += async (_, _) => await CreateScriptFolderAsync().ConfigureAwait(true);
        MenuItem reveal = new() { Header = "Reveal in File Manager" };
        reveal.Click += async (_, _) => await RevealScriptPathAsync(node).ConfigureAwait(true);
        menu.Items.Add(folder);
        menu.Items.Add(reveal);
        if (node.PackageId is { } packageId)
        {
            menu.Items.Add(new Separator());
            MenuItem update = new() { Header = "Update Dependencies" };
            update.Click += async (_, _) => await UpdatePackagesAsync(packageId).ConfigureAwait(true);
            MenuItem clean = new() { Header = "Clean Generated Artifacts" };
            clean.Click += async (_, _) => await CleanPackagesAsync(packageId).ConfigureAwait(true);
            menu.Items.Add(update);
            menu.Items.Add(clean);
        }
        menu.Open(_organizeButton);
    }

    private void ShowScriptFolderMenu(StudioNode node)
    {
        ContextMenu menu = new();
        MenuItem child = new() { Header = "New Subfolder…" };
        child.Click += async (_, _) => await CreateScriptFolderAsync().ConfigureAwait(true);
        MenuItem move = new() { Header = "Move Folder…" };
        move.Click += async (_, _) => await MoveScriptFolderAsync(node).ConfigureAwait(true);
        MenuItem rename = new() { Header = "Rename…" };
        rename.Click += async (_, _) => await RenameScriptFolderAsync(node).ConfigureAwait(true);
        MenuItem reveal = new() { Header = "Reveal in File Manager" };
        reveal.Click += async (_, _) => await RevealScriptPathAsync(node).ConfigureAwait(true);
        MenuItem delete = new() { Header = "Delete Folder…" };
        delete.Click += async (_, _) => await DeleteScriptFolderAsync(node).ConfigureAwait(true);
        menu.Items.Add(child);
        menu.Items.Add(move);
        menu.Items.Add(rename);
        menu.Items.Add(reveal);
        menu.Items.Add(delete);
        menu.Open(_organizeButton);
    }

    private void ShowScriptFileMenu(StudioNode node)
    {
        ContextMenu menu = new();
        MenuItem move = new() { Header = "Move…" };
        move.Click += async (_, _) => await MoveScriptFileAsync(node).ConfigureAwait(true);
        MenuItem rename = new() { Header = "Rename…" };
        rename.Click += async (_, _) => await RenameScriptFileAsync(node).ConfigureAwait(true);
        MenuItem duplicate = new() { Header = "Duplicate…" };
        duplicate.Click += async (_, _) => await DuplicateScriptFileAsync(node).ConfigureAwait(true);
        MenuItem reveal = new() { Header = "Reveal in File Manager" };
        reveal.Click += async (_, _) => await RevealScriptPathAsync(node).ConfigureAwait(true);
        MenuItem delete = new() { Header = "Delete…" };
        delete.Click += async (_, _) => await DeleteScriptFileAsync(node).ConfigureAwait(true);
        menu.Items.Add(move);
        menu.Items.Add(rename);
        menu.Items.Add(duplicate);
        menu.Items.Add(reveal);
        menu.Items.Add(delete);
        menu.Open(_organizeButton);
    }

    private async Task RenameScriptFileAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId || node.Path is not { } sourcePath) return;
        string? newName = await PromptAsync("Rename Script File", "File name", System.IO.Path.GetFileName(sourcePath)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            string destinationPath = ScriptExplorerTree.Combine(ScriptExplorerTree.ParentPath(sourcePath), newName.Trim());
            if (!ScriptWorkspaceService.IsSupportedSourcePath(destinationPath))
                throw new InvalidOperationException("Script files must use .ts, .js, .d.ts, or .json.");
            await RelocateScriptFileAsync(packageId, sourcePath, destinationPath).ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task MoveScriptFileAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId || node.Path is not { } sourcePath) return;
        ScriptFolderChoice? folder = await ChooseScriptFolderAsync(
            packageId,
            "Move Script File",
            ScriptExplorerTree.ParentPath(sourcePath)).ConfigureAwait(true);
        if (folder is null) return;
        try
        {
            string destinationPath = ScriptExplorerTree.Combine(folder.Path, System.IO.Path.GetFileName(sourcePath));
            await RelocateScriptFileAsync(packageId, sourcePath, destinationPath).ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task RelocateScriptFileAsync(string packageId, string sourcePath, string destinationPath)
    {
        if (destinationPath.Equals(sourcePath, StringComparison.Ordinal)) return;
        await SelectPackageAsync(packageId).ConfigureAwait(true);
        if (_activePackage is not null &&
            sourcePath.Equals(_activePackage.Definition.Entrypoint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The package entrypoint cannot be moved until package metadata editing is available.");

        string sourceUri = SourceDocumentUri(packageId, sourcePath);
        StudioDocument? open = _documents.Find(sourceUri);
        if (open?.IsDirty == true)
            throw new InvalidOperationException("Save or close the dirty file before moving it.");

        bool reopen = open is not null;
        string? previousActiveKey = _documents.Active?.Key;
        if (open is not null)
        {
            await _monaco.CloseDocumentAsync(sourceUri, _cts.Token).ConfigureAwait(true);
            _textModels.Remove(sourceUri);
            _ignoredMonacoChanges.Remove(sourceUri);
            _documents.Close(sourceUri);
        }
        await _workspace.RenamePathAsync(SelectedProfileId, packageId, sourcePath, destinationPath, _cts.Token).ConfigureAwait(true);

        ClientSettings rewritten = _referencesIndex.RewriteModulePath(
            _runtime.Settings,
            packageId,
            sourcePath,
            destinationPath);
        await AutomationCollections.From(rewritten).SaveAsync(_runtime, _cts.Token).ConfigureAwait(true);
        await RefreshNavigatorAsync().ConfigureAwait(true);
        if (reopen)
        {
            await OpenSourceAsync(packageId, destinationPath).ConfigureAwait(true);
            if (previousActiveKey is not null && previousActiveKey != sourceUri)
                _documents.Activate(previousActiveKey);
        }
    }

    private async Task DuplicateScriptFileAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId || node.Path is not { } sourcePath) return;
        string fileName = System.IO.Path.GetFileName(sourcePath);
        string extension = System.IO.Path.GetExtension(fileName);
        string stem = fileName[..^extension.Length];
        string suggested = $"{stem}-copy{extension}";
        string? newName = await PromptAsync("Duplicate Script File", "New file name", suggested).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            string destinationPath = ScriptExplorerTree.Combine(ScriptExplorerTree.ParentPath(sourcePath), newName.Trim());
            if (!ScriptWorkspaceService.IsSupportedSourcePath(destinationPath))
                throw new InvalidOperationException("Script files must use .ts, .js, .d.ts, or .json.");
            await _workspace.DuplicateFileAsync(SelectedProfileId, packageId, sourcePath, destinationPath, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
            await OpenSourceAsync(packageId, destinationPath).ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task DeleteScriptFileAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId || node.Path is not { } path) return;
        await SelectPackageAsync(packageId).ConfigureAwait(true);
        if (_activePackage is not null && path.Equals(_activePackage.Definition.Entrypoint, StringComparison.OrdinalIgnoreCase))
        {
            AddProblem("Scripts", "The package entrypoint cannot be deleted.");
            return;
        }

        string uri = SourceDocumentUri(packageId, path);
        StudioDocument? open = _documents.Find(uri);
        if (open?.IsDirty == true)
        {
            AddProblem("Scripts", "Save or close the dirty file before deleting it.");
            return;
        }
        int referenceCount = _referencesIndex.Build(_runtime.Settings).Count(reference =>
            reference.FunctionRef.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
            reference.FunctionRef.ModulePath.Equals(path, StringComparison.Ordinal));
        string referenceWarning = referenceCount == 0
            ? string.Empty
            : $" {referenceCount} Automation reference(s) will become unresolved.";
        if (await ConfirmAsync("Delete Script File", $"Delete \"{path}\"?{referenceWarning}", "Delete").ConfigureAwait(true) != true) return;

        try
        {
            if (open is not null)
            {
                await _monaco.CloseDocumentAsync(uri, _cts.Token).ConfigureAwait(true);
                _textModels.Remove(uri);
                _ignoredMonacoChanges.Remove(uri);
                _documents.Close(uri);
            }
            await _workspace.DeletePathAsync(SelectedProfileId, packageId, path, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task CreateScriptFolderAsync()
    {
        (string PackageId, string ParentPath)? location = SelectedScriptLocation();
        if (location is null) { AddProblem("Scripts", "Select a script package or folder first."); return; }
        string? name = await PromptAsync("New Script Folder", "Folder name", "folder").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            string path = ScriptExplorerTree.Combine(location.Value.ParentPath, name.Trim());
            await _workspace.CreateFolderAsync(SelectedProfileId, location.Value.PackageId, path, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task RenameScriptFolderAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId || node.Path is not { } sourcePath) return;
        string? name = await PromptAsync("Rename Script Folder", "Folder name", System.IO.Path.GetFileName(sourcePath)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            string destination = ScriptExplorerTree.Combine(ScriptExplorerTree.ParentPath(sourcePath), name.Trim());
            await RelocateScriptFolderAsync(packageId, sourcePath, destination).ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task MoveScriptFolderAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId || node.Path is not { } sourcePath) return;
        ScriptFolderChoice? folder = await ChooseScriptFolderAsync(
            packageId,
            "Move Script Folder",
            ScriptExplorerTree.ParentPath(sourcePath),
            sourcePath).ConfigureAwait(true);
        if (folder is null) return;
        try
        {
            string destination = ScriptExplorerTree.Combine(folder.Path, System.IO.Path.GetFileName(sourcePath));
            await RelocateScriptFolderAsync(packageId, sourcePath, destination).ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task RelocateScriptFolderAsync(string packageId, string sourcePath, string destinationPath)
    {
        if (destinationPath.Equals(sourcePath, StringComparison.Ordinal)) return;
        if (ScriptExplorerTree.IsSameOrDescendant(destinationPath, sourcePath))
            throw new InvalidOperationException("A script folder cannot be moved into itself.");

        await SelectPackageAsync(packageId).ConfigureAwait(true);
        if (_activePackage is not null &&
            ScriptExplorerTree.IsSameOrDescendant(_activePackage.Definition.Entrypoint, sourcePath))
            throw new InvalidOperationException("A folder containing the package entrypoint cannot be moved until package metadata editing is available.");

        IReadOnlyList<ScriptWorkspaceSourceFile> files = await _workspace.ListSourceFilesAsync(
            SelectedProfileId, packageId, _cts.Token).ConfigureAwait(true);
        string[] affected = files
            .Select(file => file.RelativePath)
            .Where(path => ScriptExplorerTree.IsSameOrDescendant(path, sourcePath))
            .ToArray();
        StudioDocument[] open = affected
            .Select(path => _documents.Find(SourceDocumentUri(packageId, path)))
            .Where(document => document is not null)
            .Cast<StudioDocument>()
            .ToArray();
        if (open.Any(document => document.IsDirty))
            throw new InvalidOperationException("Save or close dirty files in the folder before moving it.");

        string? previousActiveKey = _documents.Active?.Key;
        Dictionary<string, string> movedUris = new(StringComparer.Ordinal);
        foreach (StudioDocument document in open)
        {
            string oldPath = document.Path!;
            string newPath = ScriptExplorerTree.Rebase(oldPath, sourcePath, destinationPath);
            movedUris[document.Key] = SourceDocumentUri(packageId, newPath);
            await _monaco.CloseDocumentAsync(document.Key, _cts.Token).ConfigureAwait(true);
            _textModels.Remove(document.Key);
            _ignoredMonacoChanges.Remove(document.Key);
            _documents.Close(document.Key);
        }

        await _workspace.RenamePathAsync(SelectedProfileId, packageId, sourcePath, destinationPath, _cts.Token).ConfigureAwait(true);
        ClientSettings rewritten = _runtime.Settings;
        foreach (string oldPath in affected)
        {
            string newPath = ScriptExplorerTree.Rebase(oldPath, sourcePath, destinationPath);
            rewritten = _referencesIndex.RewriteModulePath(rewritten, packageId, oldPath, newPath);
        }
        await AutomationCollections.From(rewritten).SaveAsync(_runtime, _cts.Token).ConfigureAwait(true);
        await RefreshNavigatorAsync().ConfigureAwait(true);
        foreach (StudioDocument document in open)
        {
            string newPath = ScriptExplorerTree.Rebase(document.Path!, sourcePath, destinationPath);
            await OpenSourceAsync(packageId, newPath).ConfigureAwait(true);
        }
        if (previousActiveKey is not null)
        {
            if (movedUris.TryGetValue(previousActiveKey, out string? movedActive)) _documents.Activate(movedActive);
            else _documents.Activate(previousActiveKey);
        }
    }

    private async Task DeleteScriptFolderAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId || node.Path is not { } path) return;
        await SelectPackageAsync(packageId).ConfigureAwait(true);
        if (_activePackage is not null && ScriptExplorerTree.IsSameOrDescendant(_activePackage.Definition.Entrypoint, path))
        {
            AddProblem("Scripts", "A folder containing the package entrypoint cannot be deleted.");
            return;
        }

        IReadOnlyList<ScriptWorkspaceSourceFile> files = await _workspace.ListSourceFilesAsync(
            SelectedProfileId, packageId, _cts.Token).ConfigureAwait(true);
        string[] affected = files.Select(file => file.RelativePath)
            .Where(file => ScriptExplorerTree.IsSameOrDescendant(file, path))
            .ToArray();
        StudioDocument[] open = affected
            .Select(file => _documents.Find(SourceDocumentUri(packageId, file)))
            .Where(document => document is not null)
            .Cast<StudioDocument>()
            .ToArray();
        if (open.Any(document => document.IsDirty))
        {
            AddProblem("Scripts", "Save or close dirty files in the folder before deleting it.");
            return;
        }
        int referenceCount = _referencesIndex.Build(_runtime.Settings).Count(reference =>
            reference.FunctionRef.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
            affected.Contains(reference.FunctionRef.ModulePath, StringComparer.Ordinal));
        string warning = referenceCount == 0 ? string.Empty : $" {referenceCount} Automation reference(s) will become unresolved.";
        if (await ConfirmAsync("Delete Script Folder", $"Delete \"{path}\" and all of its contents?{warning}", "Delete").ConfigureAwait(true) != true) return;

        try
        {
            foreach (StudioDocument document in open)
            {
                await _monaco.CloseDocumentAsync(document.Key, _cts.Token).ConfigureAwait(true);
                _textModels.Remove(document.Key);
                _ignoredMonacoChanges.Remove(document.Key);
                _documents.Close(document.Key);
            }
            await _workspace.DeletePathAsync(SelectedProfileId, packageId, path, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task<ScriptFolderChoice?> ChooseScriptFolderAsync(
        string packageId,
        string title,
        string currentPath,
        string? excludedRoot = null)
    {
        IReadOnlyList<ScriptWorkspaceEntry> entries = await _workspace.ListEntriesAsync(
            SelectedProfileId, packageId, _cts.Token).ConfigureAwait(true);
        List<ScriptFolderChoice> choices = [new ScriptFolderChoice(string.Empty, "/")];
        choices.AddRange(entries
            .Where(entry => entry.IsDirectory)
            .Where(entry => excludedRoot is null || !ScriptExplorerTree.IsSameOrDescendant(entry.RelativePath, excludedRoot))
            .Select(entry => new ScriptFolderChoice(entry.RelativePath, entry.RelativePath))
            .OrderBy(choice => choice.Path, StringComparer.OrdinalIgnoreCase));
        ScriptFolderChoice? initial = choices.FirstOrDefault(choice => choice.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase));
        return await ChooseAsync(title, choices, choice => choice.Label, initial).ConfigureAwait(true);
    }

    private async Task RevealScriptPathAsync(StudioNode node)
    {
        if (node.PackageId is not { } packageId) return;
        try
        {
            await _workspace.RevealInFileManagerAsync(
                SelectedProfileId,
                packageId,
                node.Path,
                _cts.Token).ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Scripts", exception.Message); }
    }

    private async Task CreateFolderAsync(StudioDocumentKind kind, string? parentFolderId = null)
    {
        string? name = await PromptAsync("New Folder", "Folder name", "New folder").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _organization = _organization.AddFolder(kind, name, parentFolderId);
            await _organizationStore.SaveAsync(SelectedProfileId, _organization, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Automation", exception.Message); }
    }

    private async Task RenameFolderAsync(string folderId)
    {
        AutomationOrganizationFolder? folder = _organization.Folder(folderId);
        if (folder is null) return;
        string? name = await PromptAsync("Rename Folder", "Folder name", folder.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _organization = _organization.RenameFolder(folderId, name);
            await _organizationStore.SaveAsync(SelectedProfileId, _organization, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Automation", exception.Message); }
    }

    private async Task DeleteFolderAsync(string folderId)
    {
        AutomationOrganizationFolder? folder = _organization.Folder(folderId);
        if (folder is null) return;
        if (await ConfirmAsync("Delete Folder", $"Delete empty folder \"{folder.Name}\"?", "Delete").ConfigureAwait(true) != true) return;
        try
        {
            _organization = _organization.DeleteEmptyFolder(folderId);
            await _organizationStore.SaveAsync(SelectedProfileId, _organization, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Automation", exception.Message); }
    }

    private async Task MoveAutomationAsync(StudioNode node)
    {
        if (node.DocKind is not { } kind || node.AutomationId is not { } automationId) return;
        List<FolderChoice> choices = [new FolderChoice(null, $"{kind.CategoryLabel()} root")];
        choices.AddRange(_organization.Folders
            .Where(folder => folder.Kind == kind)
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(folder => new FolderChoice(folder.Id, FolderPath(folder))));
        FolderChoice? current = choices.FirstOrDefault(choice => choice.FolderId == node.FolderId) ?? choices[0];
        FolderChoice? selected = await ChooseAsync("Move Automation", choices, choice => choice.Label, current).ConfigureAwait(true);
        if (selected is null || selected.FolderId == node.FolderId) return;
        try
        {
            _organization = _organization.MoveItem(kind, automationId, selected.FolderId);
            await _organizationStore.SaveAsync(SelectedProfileId, _organization, _cts.Token).ConfigureAwait(true);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Automation", exception.Message); }
    }

    private string FolderPath(AutomationOrganizationFolder folder)
    {
        List<string> names = [folder.Name];
        string? parentId = folder.ParentFolderId;
        HashSet<string> visited = [folder.Id];
        while (parentId is not null && visited.Add(parentId) && _organization.Folder(parentId) is { } parent)
        {
            names.Add(parent.Name);
            parentId = parent.ParentFolderId;
        }
        names.Reverse();
        return string.Join(" / ", names);
    }

    // ───────────────────────────── Automation documents ─────────────────────────────

    private AutomationEditorServices EditorServices() => new(
        _runtime,
        OpenDefinitionAsync,
        ChooseFunctionAsync,
        reference => _packages.SelectMany(package => package.Exports).FirstOrDefault(export => export.FunctionRef == reference),
        (kind, automationId) => _organization.SourceIndexFor(kind, automationId),
        (kind, automationId) => RunAutomationDocumentActionAsync(kind, automationId, DuplicateAutomationAsync),
        (kind, automationId) => RunAutomationDocumentActionAsync(kind, automationId, DeleteAutomationAsync));

    private Task RunAutomationDocumentActionAsync(
        StudioDocumentKind kind,
        string automationId,
        Func<StudioDocument, Task> action)
    {
        OpenAutomation(kind, automationId);
        return _documents.Active is { } document ? action(document) : Task.CompletedTask;
    }

    private void OpenAutomation(StudioDocumentKind kind, string automationId)
    {
        if (_organization.SourceIndexFor(kind, automationId) is null)
        {
            AddProblem("Automation", $"{kind.Label()} '{automationId}' no longer exists.");
            return;
        }

        string key = StudioDocument.AutomationKey(SelectedProfileId, kind, automationId);
        if (!_editors.ContainsKey(key))
        {
            AutomationDocumentEditor? editor = AutomationDocumentEditor.Create(kind, automationId, EditorServices());
            if (editor is null) { AddProblem("Automation", $"{kind.Label()} '{automationId}' no longer exists."); return; }
            editor.Changed += () => _documents.SetDirty(key, true);
            editor.Saved += title =>
            {
                _documents.SetDirty(key, false);
                _documents.Retitle(key, title);
                Dispatcher.UIThread.Post(async () => await RefreshNavigatorAsync().ConfigureAwait(true));
            };
            _editors[key] = editor;
        }
        _documents.OpenOrFocus(new StudioDocument(key, kind, _editors[key].Title)
        {
            AutomationId = automationId,
            ProfileId = SelectedProfileId,
            Breadcrumb = ["Automation", kind.CategoryLabel(), _editors[key].Title]
        });
    }

    private async Task CreateAutomationAsync(StudioDocumentKind kind)
    {
        try
        {
            (AutomationCollections collections, int index) = AutomationCollections.From(_runtime.Settings).AddNew(kind);
            (string firstLabel, string secondLabel) = kind switch
            {
                StudioDocumentKind.Alias => ("Alias name (what you type)", "Expands to"),
                StudioDocumentKind.Trigger => ("Text pattern to match", "Command to send"),
                StudioDocumentKind.SemanticTrigger => ("Name", "Event (e.g. RoomChanged)"),
                StudioDocumentKind.Keybinding => ("Gesture (e.g. Cmd+1)", "Command to send"),
                StudioDocumentKind.Timer => ("Name", "Command to send"),
                StudioDocumentKind.StateRule => ("Name", "State expression (e.g. hp.percent < 30)"),
                _ => ("Name", "First step (e.g. send look)")
            };
            string? first = await PromptAsync($"New {kind.Label()}", firstLabel, (string?)collections.NameOf(kind, index) ?? "").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(first)) return;
            string? second = await PromptAsync($"New {kind.Label()}", secondLabel, "").ConfigureAwait(true);
            if (second is null) return;
            first = first.Trim();
            second = second.Trim();
            object? seeded = collections.Get(kind, index) switch
            {
                CommandAlias v => v with { Name = first, Expansion = second.Length > 0 ? second : v.Expansion },
                TriggerRule v => v with { Pattern = first, Command = second.Length > 0 ? second : v.Command },
                SemanticTriggerRule v => v with { Name = first, EventName = second.Length > 0 ? second : v.EventName },
                CommandKeyBinding v => v with { Gesture = first, Name = first, Command = second.Length > 0 ? second : v.Command },
                CommandTimer v => v with { Name = first, Command = second.Length > 0 ? second : v.Command },
                GameRule v => v with { Name = first, Condition = second.Length > 0 ? second : v.Condition },
                AutomationWorkflow v => v with { Name = first, Steps = second.Length > 0 ? second : v.Steps },
                _ => null
            };
            if (seeded is not null) collections = collections.Replace(kind, index, seeded);
            await collections.SaveAsync(_runtime, _cts.Token).ConfigureAwait(true);
            _organization = _organization.RegisterAdded(kind, index);
            await _organizationStore.SaveAsync(SelectedProfileId, _organization, _cts.Token).ConfigureAwait(true);
            string automationId = _organization.IdFor(kind, index);
            await RefreshNavigatorAsync().ConfigureAwait(true);
            OpenAutomation(kind, automationId);
        }
        catch (Exception exception) { AddProblem("Automation", exception.Message); }
    }

    private async Task DuplicateAutomationAsync(StudioDocument document)
    {
        if (!await ResolveDirtyAutomationAsync(document, "duplicating").ConfigureAwait(true)) return;
        try
        {
            if (document.AutomationId is null) return;
            int sourceIndex = _organization.SourceIndexFor(document.Kind, document.AutomationId) ?? -1;
            if (sourceIndex < 0) return;
            string? folderId = _organization.FolderIdFor(document.Kind, document.AutomationId);
            (AutomationCollections collections, int index) = AutomationCollections.From(_runtime.Settings).Duplicate(document.Kind, sourceIndex);
            if (index < 0) return;
            await collections.SaveAsync(_runtime, _cts.Token).ConfigureAwait(true);
            _organization = _organization.RegisterAdded(document.Kind, index, folderId);
            await _organizationStore.SaveAsync(SelectedProfileId, _organization, _cts.Token).ConfigureAwait(true);
            string automationId = _organization.IdFor(document.Kind, index);
            await RefreshNavigatorAsync().ConfigureAwait(true);
            OpenAutomation(document.Kind, automationId);
        }
        catch (Exception exception) { AddProblem("Automation", exception.Message); }
    }

    private async Task DeleteAutomationAsync(StudioDocument document)
    {
        if (!await ResolveDirtyAutomationAsync(document, "deleting").ConfigureAwait(true)) return;
        if (await ConfirmAsync($"Delete {document.Kind.Label()}", $"Delete '{document.Title}'? This cannot be undone.", "Delete").ConfigureAwait(true) != true) return;
        try
        {
            if (document.AutomationId is null) return;
            int sourceIndex = _organization.SourceIndexFor(document.Kind, document.AutomationId) ?? -1;
            if (sourceIndex < 0) return;
            AutomationCollections collections = AutomationCollections.From(_runtime.Settings).Remove(document.Kind, sourceIndex);
            await collections.SaveAsync(_runtime, _cts.Token).ConfigureAwait(true);
            _organization = _organization.RegisterRemoved(document.Kind, sourceIndex);
            await _organizationStore.SaveAsync(SelectedProfileId, _organization, _cts.Token).ConfigureAwait(true);
            _editors.Remove(document.Key);
            _documents.Close(document.Key);
            await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Automation", exception.Message); }
    }

    private async Task ToggleAutomationAsync(StudioDocument document)
    {
        if (_editors.TryGetValue(document.Key, out AutomationDocumentEditor? editor))
        {
            try { await editor.ToggleEnabledAsync(_cts.Token).ConfigureAwait(true); }
            catch (Exception exception) { AddProblem("Automation", exception.Message); }
        }
    }

    // ───────────────────────────── Script documents ─────────────────────────────

    private Task SelectPackageAsync(string packageId) => SelectPackageAsync(_session.Current, packageId);

    private async Task SelectPackageAsync(StudioSessionSnapshot session, string packageId)
    {
        if (!_session.IsCurrent(session)) return;
        string? previousPackageId = _activePackage?.Definition.PackageId;
        if (!string.Equals(previousPackageId, packageId, StringComparison.Ordinal))
        {
            CancelTests("Package changed.");
            ResetTestsPanel("Package changed. Discover tests.");
            if (previousPackageId is not null)
            {
                _diagnostics.ResolveSource(session.ProfileId, "tests:" + previousPackageId);
                RefreshProblems();
            }
        }
        ScriptPackageSnapshot? package;
        try
        {
            package = await _workspace.GetPackageAsync(session.ProfileId, packageId, session.CancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!_session.IsCurrent(session)) { return; }
        if (!_session.IsCurrent(session)) return;
        _activePackage = package ?? throw new InvalidOperationException($"Script package '{packageId}' no longer exists.");
    }

    private Task OpenSourceAsync(string packageId, string relativePath) =>
        OpenSourceAsync(_session.Current, packageId, relativePath);

    private async Task OpenSourceAsync(StudioSessionSnapshot session, string packageId, string relativePath)
    {
        try
        {
            await SelectPackageAsync(session, packageId).ConfigureAwait(true);
            if (!_session.IsCurrent(session)) return;
            string profileId = session.ProfileId;
            string uri = SourceDocumentUri(packageId, relativePath);
            MonacoEditorHost monaco = _monaco;
            ShowMonaco(true);
            // A NativeWebView only creates its native handle (and can only navigate) once visible in the tree.
            monaco.IsVisible = true;
            _center.Content = null;
            if (_documents.Find(uri) is null)
            {
                string content = await _workspace.ReadSourceAsync(profileId, packageId, relativePath, session.CancellationToken).ConfigureAwait(true);
                if (!_session.IsCurrent(session)) return;
                try
                {
                    await _typescript.OpenDocumentAsync(profileId, uri, relativePath, 1, content, session.CancellationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!_session.IsCurrent(session)) { return; }
                catch (Exception exception)
                {
                    if (!_session.IsCurrent(session)) return;
                    _ = _monaco.SetLanguageServerAvailableAsync(false);
                    ShowLanguageServerFailure(exception.Message);
                }
                if (!_session.IsCurrent(session)) return;
                await monaco.OpenDocumentAsync(profileId, packageId, relativePath, content, cancellationToken: session.CancellationToken, documentUri: uri).ConfigureAwait(true);
                if (!_session.IsCurrent(session)) return;
                _textModels.Track(uri, profileId, packageId, relativePath, content);
            }
            else if (_textModels.Find(uri) is null)
            {
                string content = await _workspace.ReadSourceAsync(profileId, packageId, relativePath, session.CancellationToken).ConfigureAwait(true);
                if (!_session.IsCurrent(session)) return;
                _textModels.Track(uri, profileId, packageId, relativePath, content);
            }
            _documents.OpenOrFocus(new StudioDocument(uri, StudioDocumentKind.Script, System.IO.Path.GetFileName(relativePath))
            {
                ProfileId = profileId,
                PackageId = packageId,
                Path = relativePath,
                Breadcrumb = ["Scripts", _activePackage?.Definition.Name ?? packageId, relativePath]
            });
            if (!_session.IsCurrent(session)) return;
            await monaco.SetActiveDocumentAsync(uri, session.CancellationToken).ConfigureAwait(true);
            if (!_session.IsCurrent(session)) return;
            if (_pendingLanguageDiagnostics.Remove(uri, out StudioLanguageDiagnostics? pending))
                await ApplyLanguageDiagnosticsAsync(pending).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!_session.IsCurrent(session)) { }
    }

    private void WireMonaco()
    {
        _monaco.DocumentClosed += async uri =>
        {
            string profileId = _documents.Find(uri)?.ProfileId ?? SelectedProfileId;
            _diagnostics.ResolveSource(profileId, "typescript:" + uri);
            _pendingLanguageDiagnostics.Remove(uri);
            RefreshLanguageProblems();
            await CloseLanguageDocumentAsync(profileId, uri).ConfigureAwait(false);
        };
        _monaco.DocumentChanged += change => Dispatcher.UIThread.Post(() =>
        {
            if (_ignoredMonacoChanges.Remove(change.Uri)) return;
            _documents.SetDirty(change.Uri, true);
            _ = ForwardLanguageChangeAsync(change);
        });
        _monaco.SaveRequested += request => Dispatcher.UIThread.Post(async () =>
        {
            if (request.All) await SaveAllAsync().ConfigureAwait(true);
            else if (request.Uri is { Length: > 0 }) await SaveScriptAsync(request.Uri).ConfigureAwait(true);
        });
        _monaco.SelectionChanged += selection => Dispatcher.UIThread.Post(() =>
        {
            _scriptSelections[selection.Uri] = selection;
            _statusCursor.Text = $"Ln {selection.EndLine}, Col {selection.EndColumn}";
        });
        _monaco.ActiveDocumentChanged += uri => Dispatcher.UIThread.Post(() =>
        {
            if (_documents.Find(uri) is not null && _documents.Active?.Key != uri) _documents.Activate(uri);
        });
        _monaco.EditorReady += () => Dispatcher.UIThread.Post(RenderActive);
        _monaco.EditorFailed += failure => AddProblem("Editor", failure.Message);
        _monaco.LanguageServerRequest += (method, parameters, cancellationToken) =>
        {
            string profileId = parameters.ValueKind == JsonValueKind.Object &&
                               parameters.TryGetProperty("textDocument", out JsonElement textDocument) &&
                               textDocument.ValueKind == JsonValueKind.Object &&
                               textDocument.TryGetProperty("uri", out JsonElement uriElement) &&
                               uriElement.ValueKind == JsonValueKind.String &&
                               _documents.Find(uriElement.GetString() ?? string.Empty) is { ProfileId: { } documentProfile }
                ? documentProfile
                : SelectedProfileId;
            return _typescript.RequestAsync(profileId, method, parameters, cancellationToken);
        };
        _monaco.LanguageServerRequestFailed += (method, error) => Dispatcher.UIThread.Post(() =>
        {
            AddConsole($"TypeScript language service request '{method}' failed ({error.Code}): {error.Message}");
            if (!error.Unavailable) return;
            _ = _monaco.SetLanguageServerAvailableAsync(false);
            ShowLanguageServerFailure(error.Message);
        });
        _typescript.DiagnosticsPublished += diagnostics => Dispatcher.UIThread.Post(async () =>
        {
            if (!_typescript.IsCurrentProfile(diagnostics.ProfileId, diagnostics.Generation) ||
                !_typescript.IsOpenDocument(diagnostics.ProfileId, diagnostics.Uri)) return;
            if (_documents.Find(diagnostics.Uri) is not { IsScript: true } openDocument)
            {
                _pendingLanguageDiagnostics[diagnostics.Uri] = diagnostics;
                return;
            }
            if (!string.Equals(openDocument.ProfileId, diagnostics.ProfileId, StringComparison.Ordinal)) return;
            await ApplyLanguageDiagnosticsAsync(diagnostics).ConfigureAwait(true);
        });
        _typescript.OutputReceived += line => Dispatcher.UIThread.Post(() => AddConsole(line));
        _typescript.SessionStarted += session => Dispatcher.UIThread.Post(() =>
        {
            if (!_typescript.IsCurrentProfile(session.ProfileId, session.Generation) ||
                !string.Equals(session.ProfileId, SelectedProfileId, StringComparison.Ordinal)) return;
            _diagnostics.ResolveSource(session.ProfileId, "typescript-service");
            _diagnostics.ResolveSource(session.ProfileId, "studio:TypeScript");
            RefreshProblems();
            RemoveLanguageServerRestartButton();
            _ = _monaco.SetLanguageServerAvailableAsync(true);
        });
        _typescript.FailureReceived += failure => Dispatcher.UIThread.Post(() =>
        {
            if (!_typescript.IsCurrentProfile(failure.ProfileId, failure.Generation) ||
                !string.Equals(failure.ProfileId, SelectedProfileId, StringComparison.Ordinal)) return;
            _diagnostics.ReplaceSource(failure.ProfileId, "typescript-service",
                [new StudioDiagnostic(failure.ProfileId, "typescript-service", "failure", "Error", failure.Message)]);
            _pendingLanguageDiagnostics.Clear();
            RefreshProblems();
            _ = _monaco.SetLanguageServerAvailableAsync(false);
            ShowLanguageServerFailure(failure.Message);
        });
    }

    private void ShowLanguageServerFailure(string message)
    {
        AddProblem("TypeScript", message);
        if (_languageServerRestartButton is not null) return;
        Button restart = new() { Content = "Restart TypeScript" };
        restart.Click += async (_, _) => await RestartTypeScriptLanguageServiceAsync().ConfigureAwait(true);
        _languageServerRestartButton = restart;
        _problems.Children.Add(restart);
    }

    private void RemoveLanguageServerRestartButton()
    {
        if (_languageServerRestartButton is null) return;
        _problems.Children.Remove(_languageServerRestartButton);
        _languageServerRestartButton = null;
    }

    private async Task RestartTypeScriptLanguageServiceAsync()
    {
        Button? restart = _languageServerRestartButton;
        if (restart is null) return;
        restart.IsEnabled = false;
        try
        {
            await _typescript.RestartAsync(SelectedProfileId, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AddProblem("TypeScript", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_languageServerRestartButton, restart)) restart.IsEnabled = true;
        }
    }

    private async Task ApplyLanguageDiagnosticsAsync(StudioLanguageDiagnostics diagnostics)
    {
        string profileId = diagnostics.ProfileId;
        long generation = diagnostics.Generation;
        StudioDocument? document = _documents.Find(diagnostics.Uri);
        if (!_typescript.IsCurrentProfile(profileId, generation) ||
            !string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal) ||
            document is not null && !string.Equals(document.ProfileId, profileId, StringComparison.Ordinal)) return;
        try
        {
            JsonElement values = diagnostics.Parameters.TryGetProperty("diagnostics", out JsonElement items)
                ? items.Clone()
                : JsonSerializer.SerializeToElement(Array.Empty<object>());
            await _monaco.SetDiagnosticsAsync(diagnostics.Uri, values, _cts.Token).ConfigureAwait(true);
            if (!_typescript.IsCurrentProfile(profileId, generation) ||
                !string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal) ||
                document is not null && !ReferenceEquals(document, _documents.Find(diagnostics.Uri))) return;
            string source = "typescript:" + diagnostics.Uri;
            StudioDiagnostic[] issues = values.EnumerateArray().Select((item, index) =>
            {
                string message = item.TryGetProperty("message", out JsonElement text) ? text.GetString() ?? "TypeScript diagnostic" : "TypeScript diagnostic";
                string severity = item.TryGetProperty("severity", out JsonElement severityValue) && severityValue.TryGetInt32(out int code)
                    ? code switch { 1 => "Error", 2 => "Warning", 3 => "Info", 4 => "Hint", _ => "Error" }
                    : "Error";
                string diagnosticCode = item.TryGetProperty("code", out JsonElement codeValue) ? codeValue.ToString() : "";
                int? line = item.TryGetProperty("range", out JsonElement range) && range.TryGetProperty("start", out JsonElement start) && start.TryGetProperty("line", out JsonElement lineValue) && lineValue.TryGetInt32(out int lineNumber) ? lineNumber + 1 : null;
                int? column = item.TryGetProperty("range", out range) && range.TryGetProperty("start", out start) && start.TryGetProperty("character", out JsonElement columnValue) && columnValue.TryGetInt32(out int columnNumber) ? columnNumber + 1 : null;
                return new StudioDiagnostic(profileId, source, $"{index}:{line}:{column}:{diagnosticCode}:{message}", severity, message, document?.PackageId, document?.Path, line, column);
            }).ToArray();
            _diagnostics.ReplaceSource(profileId, source, issues);
            RefreshProblems();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception exception) when (_typescript.IsCurrentProfile(profileId, generation) &&
                                           string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal))
        {
            AddProblem("TypeScript", exception.Message);
        }
        catch (Exception) { }
    }

    private async Task CloseLanguageDocumentAsync(string profileId, string uri)
    {
        try { await _typescript.CloseDocumentAsync(profileId, uri, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception exception) { Dispatcher.UIThread.Post(() => AddProblem("TypeScript", exception.Message)); }
    }

    private async Task ForwardLanguageChangeAsync(MonacoDocumentChanged change)
    {
        try
        {
            StudioDocument? document = _documents.Find(change.Uri);
            if (document is not { IsScript: true } ||
                !string.Equals(document.ProfileId, SelectedProfileId, StringComparison.Ordinal)) return;
            string text = change.Text ?? await _monaco.RequestDocumentContentAsync(change.Uri, _cts.Token).ConfigureAwait(false);
            await _typescript.ChangeDocumentAsync(document.ProfileId!, change.Uri, change.VersionId, text, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception exception) { Dispatcher.UIThread.Post(() => AddProblem("TypeScript", exception.Message)); }
    }

    private void RefreshLanguageProblems()
    {
        RefreshProblems();
    }

    private async Task SaveScriptAsync(string uri, bool build = true)
    {
        if (_documents.Find(uri) is not { IsScript: true } document) return;
        StudioTextModelState? state = _textModels.Find(uri);
        if (state?.Conflict is not null && !state.OverwriteApproved)
        {
            RenderExternalConflict();
            AddProblem("Save", "Resolve the external file conflict before saving.");
            return;
        }
        if (state is not null && !state.OverwriteApproved)
        {
            (bool exists, string? diskContent) = await TryReadSourceAsync(document.PackageId!, document.Path!).ConfigureAwait(true);
            if (!_textModels.MatchesBaseline(uri, diskContent, exists))
            {
                _textModels.RecordConflict(
                    uri,
                    exists ? StudioExternalFileChangeKind.Modified : StudioExternalFileChangeKind.Deleted,
                    diskContent);
                RenderExternalConflict();
                AddProblem("Save", "The file changed on disk after it was opened. Review the conflict before saving.");
                return;
            }
        }

        string content = await _monaco.RequestDocumentContentAsync(uri, _cts.Token).ConfigureAwait(true);
        await _workspace.SaveSourceAsync(document.ProfileId!, document.PackageId!, document.Path!, content, build, _cts.Token).ConfigureAwait(true);
        try
        {
            await _typescript.SaveDocumentAsync(document.ProfileId!, uri, content, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _ = _monaco.SetLanguageServerAvailableAsync(false);
            ShowLanguageServerFailure(exception.Message);
        }
        _textModels.MarkSaved(uri, content);
        _documents.SetDirty(uri, false);
        RenderExternalConflict();
        ScriptPackageSnapshot? package = await _workspace.GetPackageAsync(document.ProfileId!, document.PackageId!, _cts.Token).ConfigureAwait(true);
        if (package is not null)
        {
            _activePackage = package;
            RenderBuildProblems(package);
            RenderActive();
        }
    }

    private async Task SaveActiveAsync()
    {
        if (_documents.Active is not { } active) return;
        try
        {
            if (active.IsScript) await SaveScriptAsync(active.Key).ConfigureAwait(true);
            else if (_editors.TryGetValue(active.Key, out AutomationDocumentEditor? editor)) await editor.SaveAsync(_cts.Token).ConfigureAwait(true);
        }
        catch (Exception exception) { AddProblem("Save", exception.Message); }
    }

    private async Task SaveAllAsync()
    {
        foreach (StudioDocument document in _documents.Dirty.ToArray())
        {
            try
            {
                if (document.IsScript) await SaveScriptAsync(document.Key).ConfigureAwait(true);
                else if (_editors.TryGetValue(document.Key, out AutomationDocumentEditor? editor)) await editor.SaveAsync(_cts.Token).ConfigureAwait(true);
            }
            catch (Exception exception) { AddProblem("Save", exception.Message); }
        }
    }

    private async Task OpenDefinitionAsync(ScriptFunctionRef function)
    {
        ExportedScriptFunction? export = _packages.SelectMany(package => package.Exports).FirstOrDefault(item => item.FunctionRef == function);
        if (export is null) { AddProblem("Automation", "Function not found in the latest successful build. Build the package first."); return; }
        await OpenSourceAsync(function.PackageId, export.ModulePath).ConfigureAwait(true);
        await _monaco.RevealLocationAsync(
                SourceDocumentUri(function.PackageId, export.ModulePath),
                export.SourceLocation.Line, export.SourceLocation.Column, _cts.Token).ConfigureAwait(true);
    }

    private async Task<ScriptFunctionRef?> ChooseFunctionAsync(ScriptFunctionRef? current)
    {
        ExportedScriptFunction[] exports = _packages.SelectMany(package => package.Exports).ToArray();
        if (exports.Length == 0) { AddProblem("Automation", "No built script functions are available in this profile."); return null; }
        ExportedScriptFunction? picked = await ChooseAsync("Script Function", exports,
            item => $"{item.FunctionRef.PackageId}/{item.ModulePath}#{item.ExportName}",
            current is null ? null : exports.FirstOrDefault(item => item.FunctionRef == current)).ConfigureAwait(true);
        return picked?.FunctionRef;
    }

    private Task RestorePackagesAsync() =>
        RunPackageOperationAsync("Restore", profileId => _runtime.ScriptPackages.RestoreAsync(profileId, _cts.Token));

    private Task InstallPackagesAsync() =>
        RunPackageOperationAsync("Install", profileId => _runtime.ScriptPackages.InstallAsync(profileId, _cts.Token));

    private Task UpdatePackagesAsync(string? packageId = null) =>
        RunPackageOperationAsync(
            packageId is null ? "Update all" : $"Update {packageId}",
            profileId => _runtime.ScriptPackages.UpdateAsync(profileId, packageId, _cts.Token));

    private Task CleanPackagesAsync(string? packageId = null) =>
        RunPackageOperationAsync(
            packageId is null ? "Clean workspace" : $"Clean {packageId}",
            profileId => _runtime.ScriptPackages.CleanAsync(profileId, packageId, _cts.Token));

    private async Task RunPackageOperationAsync(
        string label,
        Func<string, Task<ScriptPackageOperationResult>> operation)
    {
        if (_packageOperationRunning)
        {
            AddProblem("Packages", "A package-manager operation is already running.");
            return;
        }

        string profileId = SelectedProfileId;
        _packageOperationRunning = true;
        if (_bottomCollapsed) ToggleBottom();
        if (_bottom.Items.Count > 1) _bottom.SelectedIndex = 1;
        AddConsole($"{label} started…");
        try
        {
            ScriptPackageOperationResult result = await operation(profileId).ConfigureAwait(true);
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddConsole(line);
            foreach (string line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddConsole($"stderr: {line}");

            string source = "package-operation:" + label;
            if (!result.Success)
            {
                AddConsole($"{label} failed · {result.Code}: {result.Message}");
                _diagnostics.ReplaceSource(profileId, source,
                    [new StudioDiagnostic(profileId, source, "failure", "Error", $"{result.Code}: {result.Message}")]);
                RefreshProblems();
                return;
            }

            _diagnostics.ResolveSource(profileId, source);
            RefreshProblems();
            AddConsole($"{label} completed · {result.Message}");
            if (string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal))
                await RefreshNavigatorAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AddConsole($"{label} failed · {exception.Message}");
            string source = "package-operation:" + label;
            _diagnostics.ReplaceSource(profileId, source,
                [new StudioDiagnostic(profileId, source, "failure", "Error", exception.Message)]);
            RefreshProblems();
        }
        finally
        {
            _packageOperationRunning = false;
        }
    }

    private async Task BuildActivePackageAsync()
    {
        if (_activePackage is null) { AddProblem("Build", "Select a script package first."); return; }
        StudioSessionSnapshot session = _session.Current;
        string packageId = _activePackage.Definition.PackageId;
        ScriptPackageBuildResult result;
        try
        {
            result = await _workspace.BuildPackageAsync(session.ProfileId, packageId, session.CancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (session.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        bool updateActiveStatus = _session.IsCurrent(session) && _activePackage?.Definition.PackageId == packageId;
        RenderBuildProblems(result, session.ProfileId, updateActiveStatus);
        if (!updateActiveStatus) return;
        _activePackage = await _workspace.GetPackageAsync(session.ProfileId, packageId, session.CancellationToken).ConfigureAwait(true);
        if (_session.IsCurrent(session) && _activePackage?.Definition.PackageId == packageId)
            RenderActive();
    }

    private void RenderBuildProblems(ScriptPackageSnapshot? package)
    {
        _buildState.Text = package?.BuildStatus switch
        {
            ScriptPackageBuildStatus.Succeeded => "Build ✓",
            ScriptPackageBuildStatus.Failed => "Build ✕",
            _ => "Build —"
        };
        _runtimeState.Text = package is null ? "Runtime —" : $"Runtime {package.RuntimeStatus}";
    }

    private void RenderBuildProblems(ScriptPackageBuildResult result, string profileId, bool updateActiveStatus = true)
    {
        string source = "build:" + result.PackageId;
        _diagnostics.ReplaceSource(profileId, source, result.Diagnostics.Select((diagnostic, index) =>
            new StudioDiagnostic(profileId, source, $"{index}:{diagnostic.SourceFile}:{diagnostic.Line}:{diagnostic.Column}:{diagnostic.Code}",
                diagnostic.Severity.ToString(), diagnostic.Message, result.PackageId, diagnostic.SourceFile, diagnostic.Line, diagnostic.Column)));
        foreach (ScriptCompilerDiagnostic diagnostic in result.Diagnostics)
            AddConsole($"Build {result.PackageId}: {diagnostic.SourceFile}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.Code} {diagnostic.Message}");
        RefreshProblems();
        if (updateActiveStatus && profileId == SelectedProfileId)
            _buildState.Text = result.Success ? "Build ✓" : "Build ✕";
    }

    private async Task TogglePackageAsync(string packageId)
    {
        string profileId = SelectedProfileId;
        ScriptPackageSnapshot? package = await _workspace.GetPackageAsync(profileId, packageId, _cts.Token).ConfigureAwait(true);
        if (package is null) return;
        await _workspace.SetEnabledAsync(profileId, packageId, !package.Definition.Enabled, _cts.Token).ConfigureAwait(true);
        if (!string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal)) return;
        if (_activePackage?.Definition.PackageId == packageId)
            _activePackage = await _workspace.GetPackageAsync(profileId, packageId, _cts.Token).ConfigureAwait(true);
        await RefreshNavigatorAsync().ConfigureAwait(true);
        RenderActive();
    }

    private async Task RunFunctionAsync()
    {
        if (_activePackage is null || _activePackage.Exports.Count == 0)
        {
            AddProblem("Run", "Build the package and export a function before running it.");
            return;
        }
        ExportedScriptFunction? function = _activePackage.Exports.Count == 1
            ? _activePackage.Exports[0]
            : await ChooseAsync("Run Function", _activePackage.Exports, item => $"{item.ModulePath}#{item.ExportName}").ConfigureAwait(true);
        if (function is null) return;
        string? json = await PromptAsync("Run Function", "JSON arguments", "{}").ConfigureAwait(true);
        if (json is null) return;
        JsonElement arguments;
        try { using JsonDocument document = JsonDocument.Parse(json); arguments = document.RootElement.Clone(); }
        catch (JsonException exception) { AddProblem("Run", exception.Message); return; }
        ScriptFunctionInvocationResult result = await _workspace.RunFunctionAsync(SelectedProfileId, function.FunctionRef, arguments, _cts.Token).ConfigureAwait(true);
        if (!result.Success) AddProblem("Run", $"{result.ErrorCode}: {result.ErrorMessage}");
        else AddConsole($"Manual run completed: {function.FunctionRef.PackageId}/{function.ExportName}");
    }

    private async Task CreatePackageAsync()
    {
        StudioSessionSnapshot session = _session.Current;
        string? name = await PromptAsync("New Script Package", "Package name", "New package").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || !_session.IsCurrent(session)) return;
        try
        {
            ScriptPackageDefinition created = await _workspace.CreatePackageAsync(
                session.ProfileId, name, cancellationToken: session.CancellationToken).ConfigureAwait(true);
            if (!_session.IsCurrent(session)) return;
            await RefreshNavigatorAsync().ConfigureAwait(true);
            if (_session.IsCurrent(session))
                await OpenSourceAsync(session, created.PackageId, created.Entrypoint).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!_session.IsCurrent(session)) { }
        catch (Exception exception)
        {
            if (_session.IsCurrent(session)) AddProblem("Scripts", exception.Message);
        }
    }

    private async Task CreateScriptFileAsync(string extension)
    {
        StudioSessionSnapshot session = _session.Current;
        (string PackageId, string ParentPath)? location = SelectedScriptLocation();
        if (location is null) { AddProblem("Scripts", "Select a script package or folder first."); return; }
        string defaultName = extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? "data.json" : $"module{extension}";
        string? name = await PromptAsync("New Script File", "File name", defaultName).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || !_session.IsCurrent(session)) return;
        name = name.Trim();
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
        try
        {
            string path = ScriptExplorerTree.Combine(location.Value.ParentPath, name);
            await _workspace.CreateFileAsync(
                session.ProfileId, location.Value.PackageId, path, cancellationToken: session.CancellationToken).ConfigureAwait(true);
            if (!_session.IsCurrent(session)) return;
            await RefreshNavigatorAsync().ConfigureAwait(true);
            if (_session.IsCurrent(session))
                await OpenSourceAsync(session, location.Value.PackageId, path).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!_session.IsCurrent(session)) { }
        catch (Exception exception)
        {
            if (_session.IsCurrent(session)) AddProblem("Scripts", exception.Message);
        }
    }

    private async Task SearchAsync()
    {
        string? query = await PromptAsync("Workspace Search", "Search source", string.Empty).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(query)) return;
        IReadOnlyList<ScriptSourceSearchResult> results = await _workspace.SearchAsync(SelectedProfileId, query, _cts.Token).ConfigureAwait(true);
        _referencesPanel.Children.Clear();
        foreach (ScriptSourceSearchResult result in results.Take(150))
        {
            Button row = UiTheme.QuietButton($"{result.PackageId}/{result.RelativePath}:{result.Line}  {result.Preview}");
            row.HorizontalContentAlignment = HorizontalAlignment.Left;
            row.Click += async (_, _) =>
            {
                await OpenSourceAsync(result.PackageId, result.RelativePath).ConfigureAwait(true);
                await _monaco.RevealLocationAsync(
                        SourceDocumentUri(result.PackageId, result.RelativePath),
                        result.Line, result.Column, _cts.Token).ConfigureAwait(true);
            };
            _referencesPanel.Children.Add(row);
        }
        if (_bottomCollapsed) ToggleBottom();
    }

    // ───────────────────────────── Document area rendering ─────────────────────────────

    private void RenderExternalConflict()
    {
        StudioDocument? active = _documents.Active;
        StudioExternalFileConflict? conflict = active is { IsScript: true }
            ? _textModels.Find(active.Key)?.Conflict
            : null;
        _externalConflictBar.IsVisible = conflict is not null;
        if (conflict is null) return;

        _externalConflictText.Text = conflict.Kind switch
        {
            StudioExternalFileChangeKind.Modified => "File changed on disk. Unsaved editor changes were preserved.",
            StudioExternalFileChangeKind.Deleted => "File was deleted on disk. Unsaved editor changes were preserved. Keep Editor Version will recreate this path on the next save.",
            StudioExternalFileChangeKind.Renamed => $"File moved on disk to {conflict.NewPath}. Reload follows the disk move; Keep Editor Version preserves the old path for the next save.",
            _ => "File changed on disk."
        };
        _externalConflictReload.IsVisible = conflict.Kind != StudioExternalFileChangeKind.Deleted;
    }

    private async Task CompareExternalConflictAsync()
    {
        if (_documents.Active is not { IsScript: true } active) return;
        StudioExternalFileConflict? conflict = _textModels.Find(active.Key)?.Conflict;
        if (conflict is null) return;

        string editorContent = await _monaco.RequestDocumentContentAsync(active.Key, _cts.Token).ConfigureAwait(true);
        TextBox editor = ComparisonTextBox(editorContent);
        TextBox disk = ComparisonTextBox(conflict.DiskContent ?? "(file deleted on disk)");
        Grid columns = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        columns.Children.Add(ComparisonColumn("Editor version", editor));
        Control diskColumn = ComparisonColumn(
            conflict.Kind == StudioExternalFileChangeKind.Renamed && conflict.NewPath is not null
                ? $"Disk version · {conflict.NewPath}"
                : "Disk version",
            disk);
        Grid.SetColumn(diskColumn, 1);
        columns.Children.Add(diskColumn);

        Window dialog = Dialog("External File Change", 1080, 680);
        Button close = new() { Content = "Close", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        Grid layout = new() { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(12), RowSpacing = 8 };
        layout.Children.Add(columns);
        Grid.SetRow(close, 1);
        layout.Children.Add(close);
        dialog.Content = layout;
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private static Control ComparisonColumn(string label, Control content)
    {
        StackPanel column = new() { Spacing = 4 };
        column.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.Muted, FontSize = 12 });
        column.Children.Add(content);
        return column;
    }

    private static TextBox ComparisonTextBox(string content)
    {
        TextBox box = new()
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = UiTheme.Mono
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return box;
    }

    private async Task ReloadExternalConflictAsync()
    {
        if (_documents.Active is not { IsScript: true } active) return;
        StudioExternalFileConflict? conflict = _textModels.Find(active.Key)?.Conflict;
        if (conflict is null || conflict.Kind == StudioExternalFileChangeKind.Deleted) return;

        if (conflict.Kind == StudioExternalFileChangeKind.Renamed && conflict.NewPath is not null)
        {
            string oldPath = active.Path!;
            string newPath = conflict.NewPath;
            string newUri = SourceDocumentUri(active.PackageId!, newPath);
            _scriptSelections.TryGetValue(active.Key, out MonacoSelectionChanged? movedSelection);
            await _monaco.CloseDocumentAsync(active.Key, _cts.Token).ConfigureAwait(true);
            _textModels.Remove(active.Key);
            _ignoredMonacoChanges.Remove(active.Key);
            _documents.Close(active.Key);
            await RewriteExternalRenameReferencesAsync(active.PackageId!, oldPath, newPath).ConfigureAwait(true);
            await OpenSourceAsync(active.PackageId!, newPath).ConfigureAwait(true);
            if (movedSelection is not null)
            {
                _scriptSelections[newUri] = movedSelection with { Uri = newUri };
                await _monaco.RevealLocationAsync(newUri, movedSelection.EndLine, movedSelection.EndColumn, _cts.Token).ConfigureAwait(true);
            }
            AddConsole($"Reloaded external move: {active.PackageId}/{oldPath} → {newPath}");
            return;
        }

        string content = conflict.DiskContent ?? string.Empty;
        _scriptSelections.TryGetValue(active.Key, out MonacoSelectionChanged? selection);
        _ignoredMonacoChanges.Add(active.Key);
        try
        {
            await _monaco.SetDocumentContentAsync(active.Key, content, _cts.Token).ConfigureAwait(true);
            if (selection is not null)
                await _monaco.RevealLocationAsync(active.Key, selection.EndLine, selection.EndColumn, _cts.Token).ConfigureAwait(true);
        }
        catch
        {
            _ignoredMonacoChanges.Remove(active.Key);
            throw;
        }
        _textModels.MarkSaved(active.Key, content);
        _documents.SetDirty(active.Key, false);
        RenderExternalConflict();
        AddConsole($"Reloaded disk version: {active.PackageId}/{active.Path}");
    }

    private Task KeepExternalEditorVersionAsync()
    {
        if (_documents.Active is { IsScript: true } active)
        {
            _textModels.KeepEditorVersion(active.Key);
            RenderExternalConflict();
            AddConsole($"Keeping editor version for next save: {active.PackageId}/{active.Path}");
        }
        return Task.CompletedTask;
    }

    private void DocumentsChanged()
    {
        RenderTabs();
        RenderActive();
    }

    private void RenderTabs()
    {
        _tabs.Children.Clear();
        foreach (StudioDocument document in _documents.Documents)
        {
            bool active = ReferenceEquals(document, _documents.Active);
            StudioDocument captured = document;
            TextBlock label = new()
            {
                Text = $"{(document.IsDirty ? "● " : "")}{document.Title}",
                Foreground = active ? StudioShellChrome.Foreground : StudioShellChrome.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Button close = new()
            {
                Content = "×", Padding = new Thickness(4, 0), MinWidth = 22, Background = Brushes.Transparent,
                Foreground = StudioShellChrome.Secondary, BorderThickness = new Thickness(0)
            };
            Avalonia.Automation.AutomationProperties.SetName(close, $"Close {document.Title}");
            close.Click += async (_, _) => await CloseDocumentAsync(captured).ConfigureAwait(true);
            StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, close } };
            Border tab = new()
            {
                Child = content,
                Padding = new Thickness(12, 7, 6, 7),
                Background = active ? StudioShellChrome.Input : StudioShellChrome.Card,
                BorderBrush = active ? StudioShellChrome.Blue : StudioShellChrome.Border,
                BorderThickness = new Thickness(0, 0, 1, active ? 2 : 1),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(tab, string.Join(" › ", document.Breadcrumb));
            tab.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed) _ = CloseDocumentAsync(captured);
                else _documents.Activate(captured.Key);
            };
            _tabs.Children.Add(tab);
        }
    }

    private async Task CloseDocumentAsync(StudioDocument document, bool discardDirtyChanges = false)
    {
        if (document.IsDirty && !discardDirtyChanges)
        {
            string choice = await ConfirmDirtyAsync(document.Title).ConfigureAwait(true);
            if (choice == "cancel") return;
            if (choice == "save")
            {
                _documents.Activate(document.Key);
                await SaveActiveAsync().ConfigureAwait(true);
                if (document.IsDirty) return;
            }
        }
        if (document.IsScript)
        {
            await _monaco.CloseDocumentAsync(document.Key, _cts.Token).ConfigureAwait(true);
            _textModels.Remove(document.Key);
            _ignoredMonacoChanges.Remove(document.Key);
        }
        else _editors.Remove(document.Key);
        _documents.Close(document.Key);
    }

    private void CloseAllEditors() => _editors.Clear();

    /// <summary>
    /// Shows or hides the native WebView by collapsing its height. Toggling IsVisible on a native
    /// control left its bounds stale (it painted over the tab strip) and destroying it loses all models.
    /// </summary>
    private bool _monacoStarted;

    private void ShowMonaco(bool show)
    {
        // WebKit will not boot a zero-size page: stay expanded until the editor reports ready.
        if (show) _monacoStarted = true;
        else if (_monacoStarted && !_monaco.IsReady) return;
        _monaco.MaxHeight = show ? double.PositiveInfinity : 0;
        _center.IsVisible = !show;
    }

    private void RenderActive()
    {
        StudioDocument? active = _documents.Active;
        RenderExternalConflict();
        _breadcrumb.Text = active is null ? "" : string.Join("  ›  ", active.Breadcrumb);
        _breadcrumbBar.IsVisible = active is not null;
        _statusLeft.Text = active is null ? "Ready" : $"{active.Kind.Label()} · {active.Title}{(active.IsDirty ? " ●" : "")}";
        RenderBuildProblems(_activePackage);
        if (active is null)
        {
            ShowMonaco(false);
            _center.Content = EmptyState();
            _statusCursor.Text = "";
            return;
        }
        if (active.IsScript)
        {
            _center.Content = null;
            ShowMonaco(true);
            _ = _monaco.SetActiveDocumentAsync(active.Key, _cts.Token);
            return;
        }
        ShowMonaco(false);
        _statusCursor.Text = "";
        if (_editors.TryGetValue(active.Key, out AutomationDocumentEditor? editor))
            _center.Content = editor.View;
    }

    private Control EmptyState()
    {
        StackPanel panel = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "No document open", Foreground = UiTheme.Text, FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Select an item in the Explorer, or create a new one.", Foreground = UiTheme.Muted, HorizontalAlignment = HorizontalAlignment.Center });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(Button("New Alias", () => CreateAutomationAsync(StudioDocumentKind.Alias)));
        buttons.Children.Add(Button("New Trigger", () => CreateAutomationAsync(StudioDocumentKind.Trigger)));
        buttons.Children.Add(Button("New Script Package", CreatePackageAsync));
        panel.Children.Add(buttons);
        return panel;
    }

    // ───────────────────────────── Bottom panels ─────────────────────────────

    // ───────────────────────────── Bottom panels ─────────────────────────────

    private void RefreshReferencePanel()
    {
        _referencesPanel.Children.Clear();
        HashSet<ScriptFunctionRef> exports = _packages.SelectMany(package => package.Exports).Select(export => export.FunctionRef).ToHashSet();
        foreach (AutomationFunctionReference reference in _referencesIndex.Build(_runtime.Settings))
        {
            bool valid = exports.Contains(reference.FunctionRef);
            _referencesPanel.Children.Add(Text($"{(valid ? "✓" : "✕")} {reference.DefinitionName} → {reference.FunctionRef.PackageId}/{reference.FunctionRef.ModulePath}#{reference.FunctionRef.ExportName}"));
        }
    }

    private async Task RefreshRuntimePanelAsync()
    {
        _runtimePanel.Children.Clear();
        foreach (ScriptPackageSnapshot package in _packages)
        {
            string source = "runtime-fault:" + package.Definition.PackageId;
            IEnumerable<StudioDiagnostic> fault = package.LastRuntimeFault is null ? [] :
                [new StudioDiagnostic(SelectedProfileId, source, "last-runtime-fault", "Error", package.LastRuntimeFault, package.Definition.PackageId)];
            _diagnostics.ReplaceSource(SelectedProfileId, source, fault);
            _runtimePanel.Children.Add(Text($"{package.Definition.Name} · {package.RuntimeStatus} · {package.BuildStatus} · enabled={package.Definition.Enabled}"));
        }
        RefreshProblems();
        foreach (StudioDiagnostic diagnostic in _diagnostics.Snapshot(SelectedProfileId).Where(item => item.Source.StartsWith("runtime:", StringComparison.Ordinal)))
            _runtimePanel.Children.Add(Text($"{diagnostic.Severity}: {diagnostic.PackageId} {diagnostic.FilePath}{(diagnostic.Line is null ? "" : $":{diagnostic.Line}:{diagnostic.Column}")} {diagnostic.Message}"));
        await Task.CompletedTask;
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        var reader = _runtime.Events.Subscribe(512);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => UpdateConnectionStatus(
                _runtime.Transport.IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected));
            await foreach (var envelope in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                string row = $"{envelope.Timestamp:HH:mm:ss.fff} {envelope.Payload.GetType().Name}";
                lock (_events)
                {
                    _events.Enqueue(row);
                    while (_events.Count > MaximumEventRows) _events.Dequeue();
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _eventsPanel.Children.Clear();
                    string[] snapshot; lock (_events) snapshot = _events.ToArray();
                    foreach (string item in snapshot.TakeLast(150)) _eventsPanel.Children.Add(Text(item));
                    if (envelope.Payload is ScriptLogEmitted log) AddConsole($"{envelope.Timestamp:HH:mm:ss.fff} [{log.ModuleId}] {log.Level}: {log.Message}");
                    if (envelope.Payload is ScriptRuntimeDiagnosticEmitted diagnostic) ApplyRuntimeDiagnostic(diagnostic);
                    if (envelope.Payload is ConnectionStateChanged connection) UpdateConnectionStatus(connection.Status);
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void AddConsole(string message)
    {
        _console.Children.Add(Text(message));
        while (_console.Children.Count > 500) _console.Children.RemoveAt(0);
    }

    private void ApplyRuntimeDiagnostic(ScriptRuntimeDiagnosticEmitted diagnostic)
    {
        string? profileId = diagnostic.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId) ||
            !string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal)) return;
        string source = $"runtime:{diagnostic.ScriptId}:{diagnostic.ScriptInstanceId}";
        string key = diagnostic.InvocationId?.ToString() ?? diagnostic.OperationId?.ToString() ?? diagnostic.EventId?.ToString() ?? diagnostic.Kind;
        if (diagnostic.Kind is "InvocationStarted" or "InvocationCompleted" or "ReloadCompleted" or "ScriptUnloaded")
            _diagnostics.Resolve(profileId, source, key);
        else if (diagnostic.Kind is "InvocationFaulted" or "PermissionDenied" or "Timeout" or "ResourceLimitExceeded" or "UncaughtException" or "QueueOverflow")
        {
            _diagnostics.Upsert(new StudioDiagnostic(profileId, source, key, "Error",
                diagnostic.Message ?? diagnostic.Kind, diagnostic.ScriptId, diagnostic.SourceFile, diagnostic.Line, diagnostic.Column));
            AddConsole($"Runtime [{diagnostic.ScriptId}] {diagnostic.Kind}: {diagnostic.Message}");
        }
        RefreshProblems();
        _ = RefreshRuntimePanelAsync();
    }

    private void AddProblem(string severity, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => AddProblem(severity, message)); return; }
        string source = "studio:" + severity;
        string level = severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "Warning"
            : severity.Equals("Info", StringComparison.OrdinalIgnoreCase) ? "Info"
            : severity.Equals("Hint", StringComparison.OrdinalIgnoreCase) ? "Hint" : "Error";
        _diagnostics.Upsert(new StudioDiagnostic(SelectedProfileId, source, message, level, message));
        RefreshProblems();
        if (_bottomCollapsed) ToggleBottom();
        if (_bottom.Items.Count > 0) _bottom.SelectedIndex = 0;
    }

    private void RefreshProblems()
    {
        _problems.Children.Clear();
        StudioDiagnostic[] items = _diagnostics.Snapshot(SelectedProfileId).ToArray();
        foreach (IGrouping<string, StudioDiagnostic> group in items.GroupBy(item => item.PackageId ?? item.FilePath ?? item.Source))
        {
            int errors = group.Count(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
            int warnings = group.Count(item => item.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
            int info = group.Count() - errors - warnings;
            _problems.Children.Add(Text($"{group.Key} · {errors} errors · {warnings} warnings · {info} info"));
            foreach (StudioDiagnostic item in group)
            {
                string location = item.FilePath is null ? item.Source :
                    $"{item.FilePath}{(item.Line is null ? "" : $":{item.Line}:{item.Column}")}";
                Button row = new()
                {
                    Content = Text($"{item.Severity}: {location} {item.Message}"),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(4, 2),
                    IsEnabled = item.PackageId is not null && item.FilePath is not null && item.Line is not null
                };
                row.Click += async (_, _) => await OpenDiagnosticAsync(item).ConfigureAwait(true);
                _problems.Children.Add(row);
            }
        }
        if (_languageServerRestartButton is not null) _problems.Children.Add(_languageServerRestartButton);
    }

    private async Task OpenDiagnosticAsync(StudioDiagnostic diagnostic)
    {
        StudioSessionSnapshot session = _session.Current;
        if (diagnostic.ProfileId != session.ProfileId || diagnostic.PackageId is not { } packageId ||
            diagnostic.FilePath is not { } filePath || diagnostic.Line is not { } line ||
            !_packages.Any(package => package.Definition.PackageId == packageId)) return;
        try
        {
            await OpenSourceAsync(session, packageId, filePath).ConfigureAwait(true);
            if (!_session.IsCurrent(session)) return;
            await _monaco.RevealLocationAsync(
                SourceDocumentUri(packageId, filePath), line, diagnostic.Column, session.CancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!_session.IsCurrent(session)) { }
        catch (Exception exception)
        {
            if (_session.IsCurrent(session)) AddProblem("Open diagnostic", exception.Message);
        }
    }

    // ───────────────────────────── Window events ─────────────────────────────

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!command) return;
        if (e.Key == Key.S)
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) await SaveAllAsync().ConfigureAwait(true);
            else await SaveActiveAsync().ConfigureAwait(true);
        }
        else if (e.Key == Key.P) { e.Handled = true; await QuickOpenAsync().ConfigureAwait(true); }
        else if (e.Key == Key.B) { e.Handled = true; TogglePane(_explorerFrame, _leftSplit, 1, _preferences.ExplorerWidth); }
        else if (e.Key == Key.J) { e.Handled = true; ToggleBottom(); }
        else if (e.Key == Key.W && _documents.Active is { } active)
        {
            e.Handled = true;
            await CloseDocumentAsync(active).ConfigureAwait(true);
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            _documents.Cycle(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || !_documents.Dirty.Any()) return;
        e.Cancel = true;
        string choice = await ConfirmDirtyAsync("all open documents").ConfigureAwait(true);
        if (choice == "cancel") return;
        if (choice == "save")
        {
            await SaveAllAsync().ConfigureAwait(true);
            if (_documents.Dirty.Any()) return;
        }
        _closeApproved = true;
        Close();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _workspace.WorkspaceChanged -= WorkspaceChanged;
        if (_workspaceWatcher is not null)
        {
            _workspaceWatcher.Changed -= WorkspaceExternalChanged;
            _workspaceWatcher.Dispose();
            _workspaceWatcher = null;
        }
        CancelTests("Studio closed.");
        _cts.Cancel();
        if (_body.ColumnDefinitions.Count >= 4)
            _preferences.ExplorerWidth = _body.ColumnDefinitions[1].ActualWidth;
        if (!_bottomCollapsed) _preferences.BottomHeight = _root.RowDefinitions[3].ActualHeight;
        _preferences.BottomCollapsed = _bottomCollapsed;
        _preferences.SetActivity(_activity);
        _preferences.Save();
        _ = _monaco.DisposeAsync();
        _ = _typescript.DisposeAsync();
        _session.Dispose();
        _cts.Dispose();
    }

    // ───────────────────────────── Dialogs ─────────────────────────────

    private async Task<string> ConfirmDirtyAsync(string what, string action = "closing")
    {
        Window dialog = Dialog("Unsaved changes", 430, 180);
        Button save = new() { Content = "Save", MinWidth = 90 };
        Button discard = new() { Content = "Discard", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90 };
        save.Click += (_, _) => dialog.Close("save"); discard.Click += (_, _) => dialog.Close("discard"); cancel.Click += (_, _) => dialog.Close("cancel");
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save); buttons.Children.Add(discard); buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 16, Children = { Text($"Save changes to {what} before {action}?"), buttons } };
        return await dialog.ShowDialog<string>(this).ConfigureAwait(true) ?? "cancel";
    }

    private async Task<bool> ResolveDirtyAutomationAsync(StudioDocument document, string action)
    {
        if (!document.IsDirty) return true;
        string choice = await ConfirmDirtyAsync(document.Title, action).ConfigureAwait(true);
        if (choice == "cancel") return false;

        _documents.Activate(document.Key);
        if (choice == "save")
        {
            await SaveActiveAsync().ConfigureAwait(true);
            return !document.IsDirty;
        }

        await CloseDocumentAsync(document, discardDirtyChanges: true).ConfigureAwait(true);
        return true;
    }

    private async Task<bool?> ConfirmAsync(string title, string message, string confirm)
    {
        Window dialog = Dialog(title, 430, 170);
        Button ok = new() { Content = confirm, MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 16, Children = { Text(message), buttons } };
        return await dialog.ShowDialog<bool?>(this).ConfigureAwait(true);
    }

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        Window dialog = Dialog(title, 480, 210);
        TextBox input = new() { Text = initial };
        Button ok = new() { Content = "OK", MinWidth = 90 }; Button cancel = new() { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => dialog.Close(input.Text); cancel.Click += (_, _) => dialog.Close(null);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 10, Children = { Text(label), input, buttons } };
        return await dialog.ShowDialog<string?>(this).ConfigureAwait(true);
    }

    private async Task<T?> ChooseAsync<T>(string title, IReadOnlyList<T> values, Func<T, string>? label = null, T? initial = null) where T : class
    {
        Window dialog = Dialog(title, 560, 240);
        ComboBox picker = new() { ItemsSource = values, SelectedItem = initial ?? values.FirstOrDefault(), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (label is not null)
            picker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<T>((item, _) => new TextBlock { Text = label(item) }, true);
        Button ok = new() { Content = "Select", MinWidth = 90 }; Button cancel = new() { Content = "Cancel", MinWidth = 90 };
        ok.Click += (_, _) => dialog.Close(picker.SelectedItem as T); cancel.Click += (_, _) => dialog.Close(null);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { picker, buttons } };
        return await dialog.ShowDialog<T?>(this).ConfigureAwait(true);
    }

    private static Window Dialog(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    };

    private static TextBlock Text(string value) => new() { Text = value, Foreground = UiTheme.Text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Heading(string value) => new() { Text = value, Foreground = UiTheme.Accent, FontWeight = FontWeight.SemiBold };
}

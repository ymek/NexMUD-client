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
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Runtime;

namespace NexMud.Gui.AutomationStudio;

/// <summary>
/// Automation Studio workbench: Explorer | tabbed document area | contextual Inspector, with a
/// collapsible bottom panel. Every Automation definition and script source opens as a document tab.
/// </summary>
internal sealed class AutomationStudioWindow : Window
{
    private enum NodeKind { Category, Folder, Entry, Scripts, Package, SourceFile }
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
    private readonly AutomationOrganizationStore _organizationStore = new();
    private AutomationOrganizationCatalog _organization = AutomationOrganizationCatalog.Empty;
    private readonly AutomationReferenceIndex _referencesIndex = new();
    private readonly StudioDocumentSet _documents = new();
    private readonly StudioUiPreferences _preferences = StudioUiPreferences.Load();
    private readonly Dictionary<string, AutomationDocumentEditor> _editors = new(StringComparer.Ordinal);
    private readonly Dictionary<TreeViewItem, StudioNode> _nodes = [];
    private readonly ComboBox _profile = new() { MinWidth = 210 };
    private readonly TextBox _filter = UiTheme.FieldBox();
    private readonly TreeView _navigator = new();
    private readonly StackPanel _activityRail = new() { Spacing = 6, Margin = new Thickness(5, 7) };
    private readonly TextBlock _explorerTitle = new()
    {
        Foreground = UiTheme.Accent,
        FontWeight = FontWeight.SemiBold,
        FontSize = NexTypography.Metadata,
        VerticalAlignment = VerticalAlignment.Center
    };
    private sealed record FolderChoice(string? FolderId, string Label);

    private Button _newButton = new();
    private Button _organizeButton = new();
    private StudioActivity _activity;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _center = new();
    private readonly StackPanel _inspector = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly StackPanel _problems = new() { Spacing = 3 };
    private readonly StackPanel _console = new() { Spacing = 2 };
    private readonly StackPanel _runtimePanel = new() { Spacing = 3 };
    private readonly StackPanel _eventsPanel = new() { Spacing = 2 };
    private readonly StackPanel _referencesPanel = new() { Spacing = 3 };
    private readonly TextBlock _buildState = new() { Text = "Build —", Foreground = UiTheme.Muted };
    private readonly TextBlock _runtimeState = new() { Text = "Runtime —", Foreground = UiTheme.Muted };
    private readonly Queue<string> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private Grid _root = new();
    private Grid _body = new();
    private TabControl _bottom = new();
    private Button _bottomToggle = new();
    private readonly MonacoEditorHost _monaco = new();
    private readonly TextBlock _breadcrumb = new() { Foreground = UiTheme.Muted, FontSize = 12, Margin = new Thickness(14, 5), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _statusLeft = new() { Foreground = UiTheme.Muted, FontSize = 12 };
    private readonly TextBlock _statusCursor = new() { Foreground = UiTheme.Muted, FontSize = 12 };
    private Border _breadcrumbBar = new();
    private Border _explorerFrame = new();
    private Border _inspectorFrame = new();
    private GridSplitter _leftSplit = new();
    private GridSplitter _rightSplit = new();
    private IReadOnlyList<ScriptPackageSnapshot> _packages = [];
    private ScriptPackageSnapshot? _activePackage;
    private bool _bottomCollapsed;
    private bool _refreshingProfiles;
    private bool _refreshingNavigator;
    private bool _closeApproved;

    public AutomationStudioWindow(NexMudRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _workspace = runtime.ScriptWorkspace;
        _session = new StudioSessionController(runtime.ActiveConnectionProfile.Id);
        _bottomCollapsed = _preferences.BottomCollapsed;
        _activity = _preferences.ResolveActivity();

        Title = "NexMUD Automation Studio";
        Width = 1440;
        Height = 900;
        MinWidth = 1060;
        MinHeight = 700;
        Background = UiTheme.Window;
        FontFamily = UiTheme.Sans;
        WireMonaco();
        _filter.PlaceholderText = "Filter";
        _filter.TextChanged += async (_, _) => await RefreshNavigatorAsync().ConfigureAwait(true);
        Content = BuildLayout();

        _profile.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ConnectionProfile>(
            (item, _) => new TextBlock { Text = item?.Name ?? "" }, true);
        _profile.SelectionChanged += ProfileSelectionChanged;
        _navigator.SelectionChanged += NavigatorSelectionChanged;
        _workspace.WorkspaceChanged += WorkspaceChanged;
        _documents.Changed += DocumentsChanged;
        KeyDown += WindowKeyDown;
        Opened += WindowOpened;
        Closing += WindowClosing;
        Closed += WindowClosed;
    }

    private string SelectedProfileId =>
        _session.Current.ProfileId;

    // ───────────────────────────── Layout ─────────────────────────────

    private Control BuildLayout()
    {
        double bottomHeight = _bottomCollapsed
            ? StudioUiPreferences.CollapsedBottomHeight
            : _preferences.BottomHeight;
        _root = new Grid { RowDefinitions = new RowDefinitions($"Auto,*,5,{bottomHeight},Auto") };
        _root.Children.Add(BuildToolbar());

        _body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"44,{_preferences.ExplorerWidth},5,*")
        };
        Grid.SetRow(_body, 1);
        _root.Children.Add(_body);

        _body.Children.Add(BuildActivityRail());

        Grid explorer = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        explorer.Children.Add(BuildExplorerHeader());
        Grid.SetRow(_filter, 1);
        _filter.Margin = new Thickness(8, 0, 8, 6);
        explorer.Children.Add(_filter);
        ScrollViewer tree = new() { Content = _navigator };
        Grid.SetRow(tree, 2);
        explorer.Children.Add(tree);
        _explorerFrame = Frame("EXPLORER", explorer, withHeader: false);
        Grid.SetColumn(_explorerFrame, 1);
        _body.Children.Add(_explorerFrame);

        _leftSplit = Splitter(GridResizeDirection.Columns); Grid.SetColumn(_leftSplit, 2); _body.Children.Add(_leftSplit);
        Grid documentArea = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        documentArea.Children.Add(new Border
        {
            MinHeight = 36, Background = UiTheme.Surface, BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        });
        _breadcrumbBar = new Border { Child = _breadcrumb, Background = UiTheme.Console, BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(0, 0, 0, 1) };
        Grid.SetRow(_breadcrumbBar, 1);
        documentArea.Children.Add(_breadcrumbBar);
        // The Monaco WebView must stay attached to the visual tree for the window's lifetime;
        // detaching a native WebView destroys its page and every open model.
        _monaco.MaxHeight = 0;
        Grid surface = new() { Children = { _center, _monaco } };
        Grid.SetRow(surface, 2);
        documentArea.Children.Add(surface);
        Border centerFrame = new() { Background = UiTheme.Console, BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(1), Child = documentArea };
        Grid.SetColumn(centerFrame, 3); _body.Children.Add(centerFrame);

        GridSplitter horizontal = Splitter(GridResizeDirection.Rows); Grid.SetRow(horizontal, 2); _root.Children.Add(horizontal);
        _bottom = new TabControl
        {
            ItemsSource = new object[]
            {
                BottomTab("Problems", _problems), BottomTab("Console", _console), BottomTab("Runtime", _runtimePanel),
                BottomTab("Events", _eventsPanel), BottomTab("References", _referencesPanel)
            }
        };
        _bottom.SelectionChanged += (_, _) =>
        {
            if (_bottomCollapsed) ToggleBottom();
        };
        Grid.SetRow(_bottom, 3);
        _root.Children.Add(_bottom);
        Border status = new()
        {
            Background = UiTheme.Raised, Padding = new Thickness(12, 3),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 18,
                Children = { _statusLeft }
            }
        };
        Grid statusGrid = (Grid)status.Child;
        Grid.SetColumn(_statusCursor, 1); statusGrid.Children.Add(_statusCursor);
        Grid.SetColumn(_buildState, 2); statusGrid.Children.Add(_buildState);
        Grid.SetColumn(_runtimeState, 3); statusGrid.Children.Add(_runtimeState);
        Grid.SetRow(status, 4);
        _root.Children.Add(status);
        return _root;
    }

    private Control BuildActivityRail()
    {
        RenderActivityRail();
        return new Border
        {
            Background = UiTheme.Console,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _activityRail
        };
    }

    private void RenderActivityRail()
    {
        _activityRail.Children.Clear();
        _activityRail.Children.Add(ActivityButton("A", "Automations", StudioActivity.Automations));
        _activityRail.Children.Add(ActivityButton("</>", "Scripts", StudioActivity.Scripts));
        _activityRail.Children.Add(ActivityButton("W", "Workflows", StudioActivity.Workflows));
        _activityRail.Children.Add(ActivityButton("⌕", "Search", StudioActivity.Search));
        _activityRail.Children.Add(ActivityButton("▶", "Runtime", StudioActivity.Runtime));
    }

    private Button ActivityButton(string glyph, string label, StudioActivity activity)
    {
        Button button = UiTheme.QuietButton(glyph);
        button.Width = 34;
        button.MinWidth = 34;
        button.Padding = new Thickness(2);
        button.Background = _activity == activity ? UiTheme.Raised : Brushes.Transparent;
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        button.Click += async (_, _) => await SetActivityAsync(activity).ConfigureAwait(true);
        return button;
    }

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
        _newButton.IsVisible = _activity is StudioActivity.Automations or StudioActivity.Scripts or StudioActivity.Workflows;
        UpdateOrganizeButton();
    }

    private Control BuildExplorerHeader()
    {
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 5, Margin = new Thickness(8, 6) };
        header.Children.Add(_explorerTitle);
        _organizeButton = UiTheme.QuietButton("Move");
        _organizeButton.IsVisible = false;
        _organizeButton.Click += async (_, _) => await OrganizeSelectedAsync().ConfigureAwait(true);
        Grid.SetColumn(_organizeButton, 1);
        header.Children.Add(_organizeButton);

        _newButton = UiTheme.QuietButton("+ New");
        _newButton.Click += async (_, _) =>
        {
            if (_activity == StudioActivity.Scripts)
            {
                await CreatePackageAsync().ConfigureAwait(true);
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

    private Control BuildToolbar()
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(10, 7) };
        grid.Children.Add(new TextBlock
        {
            Text = "Automation Studio", Foreground = UiTheme.Text, FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center
        });

        StackPanel profile = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        profile.Children.Add(new TextBlock { Text = "Profile", Foreground = UiTheme.Muted, VerticalAlignment = VerticalAlignment.Center });
        profile.Children.Add(_profile);
        Grid.SetColumn(profile, 1); grid.Children.Add(profile);

        StackPanel commands = new() { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
        commands.Children.Add(Button("Save", SaveActiveAsync));
        commands.Children.Add(Button("Save All", SaveAllAsync));
        commands.Children.Add(Button("New Package", CreatePackageAsync));
        commands.Children.Add(Button("New TS", CreateTypeScriptFileAsync));
        commands.Children.Add(Button("Build", BuildActivePackageAsync));
        commands.Children.Add(Button("Run", RunFunctionAsync));
        commands.Children.Add(Button("Search", SearchAsync));
        _bottomToggle = Button(_bottomCollapsed ? "Panel ▴" : "Panel ▾", () => { ToggleBottom(); return Task.CompletedTask; });
        commands.Children.Add(_bottomToggle);
        Grid.SetColumn(commands, 2); grid.Children.Add(commands);

        Button palette = Button("Quick Open  ⌘P", QuickOpenAsync);
        Grid.SetColumn(palette, 3); grid.Children.Add(palette);
        return grid;
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
        _bottomToggle.Content = _bottomCollapsed ? "Panel ▴" : "Panel ▾";
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
        Background = UiTheme.Divider
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
        return new Border { BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(1), Background = UiTheme.Surface, Child = child };
    }

    private static TabItem BottomTab(string header, Control content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, Padding = new Thickness(8) }
    };

    // ───────────────────────────── Lifecycle ─────────────────────────────

    private async void WindowOpened(object? sender, EventArgs e)
    {
        await RefreshProfilesAsync().ConfigureAwait(true);
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
        if (selectedProfile.Id.Equals(_session.Current.ProfileId, StringComparison.Ordinal)) return;
        if (_documents.Dirty.Any())
        {
            AddProblem("Warning", "Save or close dirty documents before changing profile scope.");
            await RefreshProfilesAsync().ConfigureAwait(true);
            return;
        }

        StudioSessionSnapshot previous = _session.Current;
        await CloseSessionDocumentsAsync(previous.CancellationToken).ConfigureAwait(true);
        _session.SwitchProfile(selectedProfile.Id);
        _activePackage = null;
        await RefreshNavigatorAsync().ConfigureAwait(true);
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
    }

    private void WorkspaceChanged(object? sender, EventArgs e)
    {
        if (_cts.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(async () => await RefreshNavigatorAsync().ConfigureAwait(true));
    }

    // ───────────────────────────── Explorer ─────────────────────────────

    private async Task RefreshNavigatorAsync()
    {
        _packages = await _workspace.ListPackagesAsync(SelectedProfileId, _cts.Token).ConfigureAwait(true);
        AutomationCollections collections = AutomationCollections.From(_runtime.Settings);
        _organization = await _organizationStore.LoadAndReconcileAsync(
            SelectedProfileId,
            collections,
            _cts.Token).ConfigureAwait(true);
        string? filter = _filter.Text;
        _refreshingNavigator = true;
        try
        {
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
                foreach (ScriptPackageSnapshot package in _packages)
                {
                    TreeViewItem item = Node(new StudioNode(NodeKind.Package,
                        $"{(package.Definition.Enabled ? "●" : "○")} {package.Definition.Name}", PackageId: package.Definition.PackageId));
                    IReadOnlyList<ScriptWorkspaceSourceFile> files = await _workspace.ListSourceFilesAsync(
                        SelectedProfileId, package.Definition.PackageId, _cts.Token).ConfigureAwait(true);
                    item.IsExpanded = true;
                    item.ItemsSource = files
                        .Where(file => StudioFilter.Matches(file.RelativePath, filter))
                        .Select(file => Node(new StudioNode(NodeKind.SourceFile, file.RelativePath, PackageId: package.Definition.PackageId, Path: file.RelativePath)))
                        .ToArray();
                    roots.Add(item);
                }
            }
            else if (_activity == StudioActivity.Runtime)
            {
                foreach (ScriptPackageSnapshot package in _packages)
                    roots.Add(Node(new StudioNode(
                        NodeKind.Package,
                        $"{package.Definition.Name} · {package.RuntimeStatus} · {package.BuildStatus}",
                        PackageId: package.Definition.PackageId)));
            }

            _navigator.ItemsSource = roots;
        }
        finally { _refreshingNavigator = false; }
        await RefreshRuntimePanelAsync().ConfigureAwait(true);
        RefreshReferencePanel();
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
        AutomationOrganizationItem item) =>
        Node(new StudioNode(
            NodeKind.Entry,
            $"{(entry.Enabled ? "●" : "○")} {entry.Name}",
            kind,
            entry.Index,
            item.Id,
            item.FolderId));

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
        _organizeButton.IsVisible = node?.Kind is NodeKind.Entry or NodeKind.Folder;
        _organizeButton.Content = node?.Kind == NodeKind.Folder ? "Folder…" : "Move";
    }

    private async Task OrganizeSelectedAsync()
    {
        if (_navigator.SelectedItem is not TreeViewItem item || !_nodes.TryGetValue(item, out StudioNode? node)) return;
        if (node.Kind == NodeKind.Entry)
        {
            await MoveAutomationAsync(node).ConfigureAwait(true);
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
        (kind, automationId) => _organization.SourceIndexFor(kind, automationId));

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

    private async Task SelectPackageAsync(string packageId)
    {
        _activePackage = await _workspace.GetPackageAsync(SelectedProfileId, packageId, _cts.Token).ConfigureAwait(true)
            ?? throw new InvalidOperationException($"Script package '{packageId}' no longer exists.");
    }

    private async Task OpenSourceAsync(string packageId, string relativePath)
    {
        await SelectPackageAsync(packageId).ConfigureAwait(true);
        string uri = MonacoEditorHost.CreateDocumentUri(SelectedProfileId, packageId, relativePath);
        MonacoEditorHost monaco = _monaco;
        ShowMonaco(true);
        // A NativeWebView only creates its native handle (and can only navigate) once visible in the tree.
        monaco.IsVisible = true;
        _center.Content = null;
        if (_documents.Find(uri) is null)
        {
            string content = await _workspace.ReadSourceAsync(SelectedProfileId, packageId, relativePath, _cts.Token).ConfigureAwait(true);
            await monaco.OpenDocumentAsync(SelectedProfileId, packageId, relativePath, content, cancellationToken: _cts.Token).ConfigureAwait(true);
        }
        _documents.OpenOrFocus(new StudioDocument(uri, StudioDocumentKind.Script, System.IO.Path.GetFileName(relativePath))
        {
            ProfileId = SelectedProfileId,
            PackageId = packageId,
            Path = relativePath,
            Breadcrumb = ["Scripts", _activePackage?.Definition.Name ?? packageId, relativePath]
        });
        await monaco.SetActiveDocumentAsync(uri, _cts.Token).ConfigureAwait(true);
    }

    private void WireMonaco()
    {
        _monaco.DocumentChanged += change => Dispatcher.UIThread.Post(() => _documents.SetDirty(change.Uri, true));
        _monaco.SaveRequested += request => Dispatcher.UIThread.Post(async () =>
        {
            if (request.All) await SaveAllAsync().ConfigureAwait(true);
            else if (request.Uri is { Length: > 0 }) await SaveScriptAsync(request.Uri).ConfigureAwait(true);
        });
        _monaco.SelectionChanged += selection => Dispatcher.UIThread.Post(() =>
            _statusCursor.Text = $"Ln {selection.EndLine}, Col {selection.EndColumn}");
        _monaco.ActiveDocumentChanged += uri => Dispatcher.UIThread.Post(() =>
        {
            if (_documents.Find(uri) is not null && _documents.Active?.Key != uri) _documents.Activate(uri);
        });
        _monaco.EditorReady += () => Dispatcher.UIThread.Post(RenderActive);
        _monaco.EditorFailed += failure => AddProblem("Editor", failure.Message);
    }

    private async Task SaveScriptAsync(string uri)
    {
        if (_documents.Find(uri) is not { IsScript: true } document) return;
        string content = await _monaco.RequestDocumentContentAsync(uri, _cts.Token).ConfigureAwait(true);
        await _workspace.SaveSourceAsync(document.ProfileId!, document.PackageId!, document.Path!, content, build: true, _cts.Token).ConfigureAwait(true);
        _documents.SetDirty(uri, false);
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
                MonacoEditorHost.CreateDocumentUri(SelectedProfileId, function.PackageId, export.ModulePath),
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

    private async Task BuildActivePackageAsync()
    {
        if (_activePackage is null) { AddProblem("Build", "Select a script package first."); return; }
        ScriptPackageBuildResult result = await _workspace.BuildPackageAsync(SelectedProfileId, _activePackage.Definition.PackageId, _cts.Token).ConfigureAwait(true);
        RenderBuildProblems(result);
        _activePackage = await _workspace.GetPackageAsync(SelectedProfileId, _activePackage.Definition.PackageId, _cts.Token).ConfigureAwait(true);
        RenderActive();
    }

    private void RenderBuildProblems(ScriptPackageSnapshot package) =>
        _buildState.Text = package.BuildStatus switch
        {
            ScriptPackageBuildStatus.Succeeded => "Build ✓",
            ScriptPackageBuildStatus.Failed => "Build ✕",
            _ => "Build —"
        };

    private void RenderBuildProblems(ScriptPackageBuildResult result)
    {
        _problems.Children.Clear();
        foreach (ScriptCompilerDiagnostic diagnostic in result.Diagnostics)
            AddProblem(diagnostic.Severity.ToString(), $"{diagnostic.SourceFile}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.Code} {diagnostic.Message}");
        _buildState.Text = result.Success ? "Build ✓" : "Build ✕";
    }

    private async Task TogglePackageAsync()
    {
        if (_activePackage is null) return;
        await _workspace.SetEnabledAsync(SelectedProfileId, _activePackage.Definition.PackageId, !_activePackage.Definition.Enabled, _cts.Token).ConfigureAwait(true);
        _activePackage = await _workspace.GetPackageAsync(SelectedProfileId, _activePackage.Definition.PackageId, _cts.Token).ConfigureAwait(true);
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
        string? name = await PromptAsync("New Script Package", "Package name", "New package").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        ScriptPackageDefinition created = await _workspace.CreatePackageAsync(SelectedProfileId, name, cancellationToken: _cts.Token).ConfigureAwait(true);
        await OpenSourceAsync(created.PackageId, "main.ts").ConfigureAwait(true);
    }

    private async Task CreateTypeScriptFileAsync()
    {
        if (_activePackage is null) { AddProblem("New TS", "Select a script package first."); return; }
        string? path = await PromptAsync("New TypeScript File", "Package-relative path", "module.ts").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) path += ".ts";
        await _workspace.CreateFileAsync(SelectedProfileId, _activePackage.Definition.PackageId, path, cancellationToken: _cts.Token).ConfigureAwait(true);
        await OpenSourceAsync(_activePackage.Definition.PackageId, path).ConfigureAwait(true);
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
                        MonacoEditorHost.CreateDocumentUri(SelectedProfileId, result.PackageId, result.RelativePath),
                        result.Line, result.Column, _cts.Token).ConfigureAwait(true);
            };
            _referencesPanel.Children.Add(row);
        }
        if (_bottomCollapsed) ToggleBottom();
    }

    // ───────────────────────────── Document area rendering ─────────────────────────────

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
                Foreground = active ? UiTheme.Text : UiTheme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Button close = new()
            {
                Content = "×", Padding = new Thickness(4, 0), MinWidth = 22, Background = Brushes.Transparent,
                Foreground = UiTheme.Muted, BorderThickness = new Thickness(0)
            };
            Avalonia.Automation.AutomationProperties.SetName(close, $"Close {document.Title}");
            close.Click += async (_, _) => await CloseDocumentAsync(captured).ConfigureAwait(true);
            StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, close } };
            Border tab = new()
            {
                Child = content,
                Padding = new Thickness(12, 7, 6, 7),
                Background = active ? UiTheme.Console : UiTheme.Surface,
                BorderBrush = active ? UiTheme.Accent : UiTheme.Divider,
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

    private async Task CloseDocumentAsync(StudioDocument document)
    {
        if (document.IsDirty)
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
        if (document.IsScript) await _monaco.CloseDocumentAsync(document.Key, _cts.Token).ConfigureAwait(true);
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
        _breadcrumb.Text = active is null ? "" : string.Join("  ›  ", active.Breadcrumb);
        _breadcrumbBar.IsVisible = active is not null;
        _statusLeft.Text = active is null ? "Ready" : $"{active.Kind.Label()} · {active.Title}{(active.IsDirty ? " ●" : "")}";
        if (active is null)
        {
            ShowMonaco(false);
            _center.Content = EmptyState();
            _statusCursor.Text = "";
            BuildEmptyInspector();
            return;
        }
        if (active.IsScript)
        {
            _center.Content = null;
            ShowMonaco(true);
            _ = _monaco.SetActiveDocumentAsync(active.Key, _cts.Token);
            if (_activePackage is not null) BuildPackageInspector(_activePackage);
            else BuildEmptyInspector();
            return;
        }
        ShowMonaco(false);
        _statusCursor.Text = "";
        if (_editors.TryGetValue(active.Key, out AutomationDocumentEditor? editor))
        {
            _center.Content = editor.View;
            BuildAutomationInspector(active, editor);
        }
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

    // ───────────────────────────── Inspector (context only) ─────────────────────────────

    private void BuildEmptyInspector()
    {
        _inspector.Children.Clear();
        _inspector.Children.Add(Heading("Automation Studio"));
        _inspector.Children.Add(Text($"Profile scope: {SelectedProfileId}"));
        _inspector.Children.Add(Text(SelectedProfileId == _runtime.ActiveConnectionProfile.Id
            ? "This is the active runtime profile."
            : "Authoring only: another Connection Profile is currently active."));
    }

    private void BuildAutomationInspector(StudioDocument document, AutomationDocumentEditor editor)
    {
        _inspector.Children.Clear();
        _inspector.Children.Add(Heading(document.Title));
        _inspector.Children.Add(Text($"{document.Kind.Label()} · {(editor.IsEnabled ? "Enabled" : "Disabled")}{(document.IsDirty ? " · Unsaved changes" : "")}"));
        _inspector.Children.Add(Heading("Actions"));
        _inspector.Children.Add(Button(editor.IsEnabled ? "Disable" : "Enable", () => ToggleAutomationAsync(document)));
        _inspector.Children.Add(Button("Duplicate", () => DuplicateAutomationAsync(document)));
        _inspector.Children.Add(Button("Delete…", () => DeleteAutomationAsync(document)));

        int? sourceIndex = document.AutomationId is null
            ? null
            : _organization.SourceIndexFor(document.Kind, document.AutomationId);
        AutomationFunctionReference[] uses = sourceIndex is null
            ? []
            : _referencesIndex.Build(_runtime.Settings)
                .Where(reference => StudioDocumentKinds.FromSource(reference.SourceKind) == document.Kind
                    && StudioDocumentKinds.DefinitionIndex(reference.DefinitionId) == sourceIndex)
                .ToArray();
        _inspector.Children.Add(Heading("Script references"));
        if (uses.Length == 0) _inspector.Children.Add(Text("None"));
        foreach (AutomationFunctionReference use in uses)
        {
            ExportedScriptFunction? export = _packages.SelectMany(package => package.Exports).FirstOrDefault(item => item.FunctionRef == use.FunctionRef);
            _inspector.Children.Add(Text($"{(export is null ? "✕" : "✓")} {use.FunctionRef.PackageId}/{use.FunctionRef.ModulePath}#{use.FunctionRef.ExportName}"));
        }
    }

    private void BuildPackageInspector(ScriptPackageSnapshot package)
    {
        _inspector.Children.Clear();
        _inspector.Children.Add(Heading(package.Definition.Name));
        _inspector.Children.Add(Text($"PackageId: {package.Definition.PackageId}"));
        _inspector.Children.Add(Text($"Enabled: {package.Definition.Enabled}"));
        _inspector.Children.Add(Text($"Build: {package.BuildStatus}"));
        _inspector.Children.Add(Text($"Runtime: {package.RuntimeStatus}"));
        _inspector.Children.Add(Text($"Capabilities: {package.Definition.Capabilities}"));
        _inspector.Children.Add(Button(package.Definition.Enabled ? "Disable Package" : "Enable Package", TogglePackageAsync));
        _buildState.Text = package.BuildStatus == ScriptPackageBuildStatus.Succeeded ? "Build ✓" : package.BuildStatus == ScriptPackageBuildStatus.Failed ? "Build ✕" : "Build —";
        _runtimeState.Text = $"Runtime {package.RuntimeStatus}";
        if (!string.IsNullOrWhiteSpace(package.LastRuntimeFault)) _inspector.Children.Add(Text($"Fault: {package.LastRuntimeFault}"));
        _inspector.Children.Add(Heading("Exports"));
        if (package.Exports.Count == 0) _inspector.Children.Add(Text("No callable exports discovered."));
        foreach (ExportedScriptFunction export in package.Exports)
        {
            Button open = UiTheme.QuietButton($"{export.ModulePath}#{export.ExportName}({string.Join(", ", export.Parameters.Select(parameter => parameter.Name))})");
            open.HorizontalContentAlignment = HorizontalAlignment.Left;
            ScriptFunctionRef reference = export.FunctionRef;
            open.Click += async (_, _) => await OpenDefinitionAsync(reference).ConfigureAwait(true);
            _inspector.Children.Add(open);
            IReadOnlyList<AutomationFunctionReference> uses = _referencesIndex.FindUses(_runtime.Settings, export.FunctionRef);
            if (uses.Count > 0) _inspector.Children.Add(Text($"Used by: {string.Join(", ", uses.Select(use => use.DefinitionName))}"));
        }
    }

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
            _runtimePanel.Children.Add(Text($"{package.Definition.Name} · {package.RuntimeStatus} · {package.BuildStatus} · enabled={package.Definition.Enabled}"));
        await Task.CompletedTask;
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        var reader = _runtime.Events.Subscribe(512);
        try
        {
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

    private void AddProblem(string severity, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => AddProblem(severity, message)); return; }
        _problems.Children.Add(Text($"{severity}: {message}"));
        while (_problems.Children.Count > 500) _problems.Children.RemoveAt(0);
        if (_bottomCollapsed) ToggleBottom();
        if (_bottom.Items.Count > 0) _bottom.SelectedIndex = 0;
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
        _cts.Cancel();
        if (_body.ColumnDefinitions.Count >= 4)
            _preferences.ExplorerWidth = _body.ColumnDefinitions[1].ActualWidth;
        if (!_bottomCollapsed) _preferences.BottomHeight = _root.RowDefinitions[3].ActualHeight;
        _preferences.BottomCollapsed = _bottomCollapsed;
        _preferences.SetActivity(_activity);
        _preferences.Save();
        _ = _monaco.DisposeAsync();
        _session.Dispose();
        _cts.Dispose();
    }

    // ───────────────────────────── Dialogs ─────────────────────────────

    private async Task<string> ConfirmDirtyAsync(string what)
    {
        Window dialog = Dialog("Unsaved changes", 430, 180);
        Button save = new() { Content = "Save", MinWidth = 90 };
        Button discard = new() { Content = "Discard", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90 };
        save.Click += (_, _) => dialog.Close("save"); discard.Click += (_, _) => dialog.Close("discard"); cancel.Click += (_, _) => dialog.Close("cancel");
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save); buttons.Children.Add(discard); buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 16, Children = { Text($"Save changes to {what} before closing?"), buttons } };
        return await dialog.ShowDialog<string>(this).ConfigureAwait(true) ?? "cancel";
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

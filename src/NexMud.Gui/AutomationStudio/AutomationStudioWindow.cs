using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NexMud.Client.Automation;
using NexMud.Client.Interaction;
using NexMud.Client.Runtime;
using NexMud.Client.Scripting;
using NexMud.Client.Settings;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Runtime;

namespace NexMud.Gui.AutomationStudio;

internal sealed class AutomationStudioWindow : Window
{
    private enum NodeKind { Automation, Scripts, Package, SourceFile }
    private sealed record StudioNode(NodeKind Kind, string Label, string? PackageId = null, string? Path = null);
    private sealed record OpenDocument(string Uri, string ProfileId, string PackageId, string Path);
    private sealed record AutomationTarget(AutomationInvocationSourceKind Kind, int Index, string Name)
    {
        public override string ToString() => $"{Kind}: {Name}";
    }

    private const int MaximumEventRows = 500;
    private readonly NexMudRuntime _runtime;
    private readonly ScriptWorkspaceService _workspace;
    private readonly AutomationReferenceIndex _referencesIndex = new();
    private readonly AutomationWorkspace _automationEditor;
    private readonly ComboBox _profile = new() { MinWidth = 210 };
    private readonly TreeView _navigator = new();
    private readonly ContentControl _center = new();
    private readonly StackPanel _inspector = new() { Spacing = 8 };
    private readonly StackPanel _problems = new() { Spacing = 3 };
    private readonly StackPanel _console = new() { Spacing = 2 };
    private readonly StackPanel _runtimePanel = new() { Spacing = 3 };
    private readonly StackPanel _eventsPanel = new() { Spacing = 2 };
    private readonly StackPanel _referencesPanel = new() { Spacing = 3 };
    private readonly TextBlock _buildState = new() { Text = "Build —" };
    private readonly TextBlock _runtimeState = new() { Text = "Runtime —" };
    private readonly Dictionary<TreeViewItem, StudioNode> _nodes = [];
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly Queue<string> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private MonacoEditorHost? _monaco;
    private OpenDocument? _activeDocument;
    private ScriptPackageSnapshot? _activePackage;
    private bool _refreshingProfiles;
    private bool _closeApproved;

    public AutomationStudioWindow(NexMudRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _workspace = runtime.ScriptWorkspace;
        _automationEditor = new AutomationWorkspace(runtime, SelectScriptsAsync);

        Title = "NexMUD Automation Studio";
        Width = 1440;
        Height = 900;
        MinWidth = 1060;
        MinHeight = 700;
        Background = UiTheme.Window;
        FontFamily = UiTheme.Sans;
        Content = BuildLayout();

        _profile.SelectionChanged += ProfileSelectionChanged;
        _navigator.SelectionChanged += NavigatorSelectionChanged;
        _workspace.WorkspaceChanged += WorkspaceChanged;
        KeyDown += WindowKeyDown;
        Opened += WindowOpened;
        Closing += WindowClosing;
        Closed += WindowClosed;
    }

    private string SelectedProfileId =>
        (_profile.SelectedItem as ConnectionProfile)?.Id ?? _runtime.ActiveConnectionProfile.Id;

    private Control BuildLayout()
    {
        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,*,5,0.24*") };
        root.Children.Add(BuildToolbar());

        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("0.22*,5,0.56*,5,0.22*") };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        body.Children.Add(Panel("NAVIGATOR", new ScrollViewer { Content = _navigator }));
        GridSplitter left = Splitter(GridResizeDirection.Columns); Grid.SetColumn(left, 1); body.Children.Add(left);
        Border centerFrame = new() { Background = UiTheme.Console, BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(1), Child = _center };
        Grid.SetColumn(centerFrame, 2); body.Children.Add(centerFrame);
        GridSplitter right = Splitter(GridResizeDirection.Columns); Grid.SetColumn(right, 3); body.Children.Add(right);
        Border inspector = Panel("INSPECTOR", new ScrollViewer { Content = _inspector }); Grid.SetColumn(inspector, 4); body.Children.Add(inspector);

        GridSplitter horizontal = Splitter(GridResizeDirection.Rows); Grid.SetRow(horizontal, 2); root.Children.Add(horizontal);
        TabControl bottom = new()
        {
            ItemsSource = new object[]
            {
                BottomTab("Problems", _problems),
                BottomTab("Console", _console),
                BottomTab("Runtime", _runtimePanel),
                BottomTab("Events", _eventsPanel),
                BottomTab("References", _referencesPanel)
            }
        };
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        return root;
    }

    private Control BuildToolbar()
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(10, 7)
        };
        grid.Children.Add(new TextBlock
        {
            Text = "Automation Studio",
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        StackPanel profile = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        profile.Children.Add(new TextBlock { Text = "Profile", Foreground = UiTheme.Muted, VerticalAlignment = VerticalAlignment.Center });
        profile.Children.Add(_profile);
        Grid.SetColumn(profile, 1); grid.Children.Add(profile);

        StackPanel commands = new() { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
        commands.Children.Add(Button("New Package", CreatePackageAsync));
        commands.Children.Add(Button("New TS", CreateTypeScriptFileAsync));
        commands.Children.Add(Button("Save", SaveActiveAsync));
        commands.Children.Add(Button("Save All", SaveAllAsync));
        commands.Children.Add(Button("Build", BuildActivePackageAsync));
        commands.Children.Add(Button("Run", RunFunctionAsync));
        commands.Children.Add(Button("Enable/Disable", TogglePackageAsync));
        commands.Children.Add(Button("Search", SearchAsync));
        Grid.SetColumn(commands, 2); grid.Children.Add(commands);

        StackPanel status = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(_buildState); status.Children.Add(_runtimeState);
        Grid.SetColumn(status, 3); grid.Children.Add(status);
        return grid;
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

    private static Border Panel(string title, Control content)
    {
        Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = UiTheme.Accent,
            FontWeight = FontWeight.SemiBold,
            FontSize = NexTypography.Metadata,
            Margin = new Thickness(8, 5)
        });
        Grid.SetRow(content, 1); grid.Children.Add(content);
        return new Border { BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(1), Background = UiTheme.Surface, Child = grid };
    }

    private static TabItem BottomTab(string header, Control content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, Padding = new Thickness(8) }
    };

    private async void WindowOpened(object? sender, EventArgs e)
    {
        await RefreshProfilesAsync().ConfigureAwait(true);
        await RefreshNavigatorAsync().ConfigureAwait(true);
        ShowAutomation();
        _ = Task.Run(() => ConsumeEventsAsync(_cts.Token), CancellationToken.None);
    }

    private async Task RefreshProfilesAsync()
    {
        _refreshingProfiles = true;
        try
        {
            IReadOnlyList<ConnectionProfile> profiles = _runtime.ConnectionProfiles;
            string selected = (_profile.SelectedItem as ConnectionProfile)?.Id ?? _runtime.ActiveConnectionProfile.Id;
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
        if (_dirty.Count > 0)
        {
            AddProblem("Warning", "Save or discard dirty documents before changing profile scope.");
            await RefreshProfilesAsync().ConfigureAwait(true);
            return;
        }
        _activeDocument = null;
        _activePackage = null;
        ShowAutomation();
        await RefreshNavigatorAsync().ConfigureAwait(true);
    }

    private void WorkspaceChanged(object? sender, EventArgs e)
    {
        if (_cts.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(async () => await RefreshNavigatorAsync().ConfigureAwait(true));
    }

    private async Task RefreshNavigatorAsync()
    {
        IReadOnlyList<ScriptPackageSnapshot> packages = await _workspace.ListPackagesAsync(SelectedProfileId, _cts.Token).ConfigureAwait(true);
        _nodes.Clear();
        TreeViewItem automation = Node(new StudioNode(NodeKind.Automation, "AUTOMATION"));
        automation.IsExpanded = true;
        automation.ItemsSource = new object[]
        {
            new TreeViewItem { Header = "Aliases" }, new TreeViewItem { Header = "Keybindings" },
            new TreeViewItem { Header = "Text Triggers" }, new TreeViewItem { Header = "Semantic Triggers" },
            new TreeViewItem { Header = "Timers" }, new TreeViewItem { Header = "State Rules" },
            new TreeViewItem { Header = "Workflows" }
        };

        TreeViewItem scripts = Node(new StudioNode(NodeKind.Scripts, "SCRIPTS"));
        scripts.IsExpanded = true;
        List<TreeViewItem> packageItems = [];
        foreach (ScriptPackageSnapshot package in packages)
        {
            TreeViewItem item = Node(new StudioNode(
                NodeKind.Package,
                $"{(package.Definition.Enabled ? "●" : "○")} {package.Definition.Name}",
                package.Definition.PackageId));
            IReadOnlyList<ScriptWorkspaceSourceFile> files = await _workspace.ListSourceFilesAsync(
                SelectedProfileId,
                package.Definition.PackageId,
                _cts.Token).ConfigureAwait(true);
            item.ItemsSource = files.Select(file => Node(new StudioNode(
                NodeKind.SourceFile,
                file.RelativePath,
                package.Definition.PackageId,
                file.RelativePath))).ToArray();
            packageItems.Add(item);
        }
        scripts.ItemsSource = packageItems;
        _navigator.ItemsSource = new object[] { automation, scripts };
        await RefreshRuntimePanelAsync().ConfigureAwait(true);
        RefreshReferencePanel(packages);
    }

    private TreeViewItem Node(StudioNode node)
    {
        TreeViewItem item = new() { Header = node.Label };
        _nodes[item] = node;
        return item;
    }

    private async void NavigatorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_navigator.SelectedItem is not TreeViewItem item || !_nodes.TryGetValue(item, out StudioNode? node)) return;
        try
        {
            switch (node.Kind)
            {
                case NodeKind.Automation:
                    ShowAutomation();
                    break;
                case NodeKind.Scripts:
                    ShowScriptsInspector();
                    break;
                case NodeKind.Package when node.PackageId is { } packageId:
                    await SelectPackageAsync(packageId).ConfigureAwait(true);
                    break;
                case NodeKind.SourceFile when node.PackageId is { } package && node.Path is { } path:
                    await OpenSourceAsync(package, path).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception exception) { AddProblem("Error", exception.Message); }
    }

    private void ShowAutomation()
    {
        _activeDocument = null;
        _activePackage = null;
        _center.Content = _automationEditor;
        _automationEditor.Activate();
        BuildAutomationInspector();
    }

    private void ShowScriptsInspector()
    {
        _activeDocument = null;
        _activePackage = null;
        _inspector.Children.Clear();
        _inspector.Children.Add(Text("Scripts are persisted by C#, built by the authoritative TypeScript compiler, and executed only by Jint."));
        _inspector.Children.Add(Text($"Profile scope: {SelectedProfileId}"));
        _inspector.Children.Add(Text(SelectedProfileId == _runtime.ActiveConnectionProfile.Id
            ? "This is the active runtime profile."
            : "Authoring only: another Connection Profile is currently active."));
    }

    private async Task SelectPackageAsync(string packageId)
    {
        _activePackage = await _workspace.GetPackageAsync(SelectedProfileId, packageId, _cts.Token).ConfigureAwait(true)
            ?? throw new InvalidOperationException($"Script package '{packageId}' no longer exists.");
        _activeDocument = null;
        BuildPackageInspector(_activePackage);
    }

    private async Task OpenSourceAsync(string packageId, string relativePath)
    {
        await SelectPackageAsync(packageId).ConfigureAwait(true);
        string content = await _workspace.ReadSourceAsync(SelectedProfileId, packageId, relativePath, _cts.Token).ConfigureAwait(true);
        MonacoEditorHost monaco = EnsureMonaco();
        _center.Content = monaco;
        string uri = MonacoEditorHost.CreateDocumentUri(SelectedProfileId, packageId, relativePath);
        _documents[uri] = new OpenDocument(uri, SelectedProfileId, packageId, relativePath);
        _activeDocument = _documents[uri];
        await monaco.OpenDocumentAsync(SelectedProfileId, packageId, relativePath, content, cancellationToken: _cts.Token).ConfigureAwait(true);
        BuildPackageInspector(_activePackage!);
    }

    private MonacoEditorHost EnsureMonaco()
    {
        if (_monaco is not null) return _monaco;
        _monaco = new MonacoEditorHost();
        _monaco.DocumentChanged += change => Dispatcher.UIThread.Post(() => _dirty.Add(change.Uri));
        _monaco.SaveRequested += request => Dispatcher.UIThread.Post(async () =>
        {
            if (request.All) await SaveAllAsync().ConfigureAwait(true);
            else if (request.Uri is { Length: > 0 }) await SaveDocumentAsync(request.Uri).ConfigureAwait(true);
        });
        _monaco.EditorFailed += failure => AddProblem("Editor", failure.Message);
        return _monaco;
    }

    private async Task SaveActiveAsync()
    {
        if (_activeDocument is null) return;
        await SaveDocumentAsync(_activeDocument.Uri).ConfigureAwait(true);
    }

    private async Task SaveAllAsync()
    {
        foreach (string uri in _dirty.ToArray())
            await SaveDocumentAsync(uri).ConfigureAwait(true);
    }

    private async Task SaveDocumentAsync(string uri)
    {
        if (_monaco is null || !_documents.TryGetValue(uri, out OpenDocument? document)) return;
        string content = await _monaco.RequestDocumentContentAsync(uri, _cts.Token).ConfigureAwait(true);
        await _workspace.SaveSourceAsync(document.ProfileId, document.PackageId, document.Path, content, build: true, _cts.Token)
            .ConfigureAwait(true);
        _dirty.Remove(uri);
        ScriptPackageSnapshot? package = await _workspace.GetPackageAsync(document.ProfileId, document.PackageId, _cts.Token).ConfigureAwait(true);
        if (package is not null)
        {
            _activePackage = package;
            BuildPackageInspector(package);
            RenderBuildProblems(package);
        }
    }

    private async Task BuildActivePackageAsync()
    {
        if (_activePackage is null) return;
        ScriptPackageBuildResult result = await _workspace.BuildPackageAsync(
            SelectedProfileId,
            _activePackage.Definition.PackageId,
            _cts.Token).ConfigureAwait(true);
        RenderBuildProblems(result);
        _activePackage = await _workspace.GetPackageAsync(SelectedProfileId, _activePackage.Definition.PackageId, _cts.Token).ConfigureAwait(true);
        if (_activePackage is not null) BuildPackageInspector(_activePackage);
    }

    private void RenderBuildProblems(ScriptPackageSnapshot package)
    {
        _buildState.Text = package.BuildStatus switch
        {
            ScriptPackageBuildStatus.Succeeded => "Build ✓",
            ScriptPackageBuildStatus.Failed => "Build ✕",
            _ => "Build —"
        };
    }

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
        bool enabled = !_activePackage.Definition.Enabled;
        await _workspace.SetEnabledAsync(SelectedProfileId, _activePackage.Definition.PackageId, enabled, _cts.Token).ConfigureAwait(true);
        _activePackage = await _workspace.GetPackageAsync(SelectedProfileId, _activePackage.Definition.PackageId, _cts.Token).ConfigureAwait(true);
        if (_activePackage is not null) BuildPackageInspector(_activePackage);
    }

    private async Task RunFunctionAsync()
    {
        if (_activePackage is null || _activePackage.Exports.Count == 0)
        {
            AddProblem("Run", "Build the package and export a function before running it.");
            return;
        }
        ExportedScriptFunction? function = await ChooseExportAsync(_activePackage.Exports).ConfigureAwait(true);
        if (function is null) return;
        string? json = await PromptAsync("Run Function", "JSON arguments", "{}").ConfigureAwait(true);
        if (json is null) return;
        JsonElement arguments;
        try { using JsonDocument document = JsonDocument.Parse(json); arguments = document.RootElement.Clone(); }
        catch (JsonException exception) { AddProblem("Run", exception.Message); return; }
        ScriptFunctionInvocationResult result = await _workspace.RunFunctionAsync(
            SelectedProfileId,
            function.FunctionRef,
            arguments,
            _cts.Token).ConfigureAwait(true);
        if (!result.Success) AddProblem("Run", $"{result.ErrorCode}: {result.ErrorMessage}");
        else AddConsole($"Manual run completed: {function.FunctionRef.PackageId}/{function.ExportName}");
    }

    private async Task<ExportedScriptFunction?> ChooseExportAsync(IReadOnlyList<ExportedScriptFunction> exports)
    {
        if (exports.Count == 1) return exports[0];
        Window dialog = Dialog("Run Function", 520, 220);
        ComboBox picker = new() { ItemsSource = exports, SelectedIndex = 0 };
        picker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ExportedScriptFunction>((item, _) =>
            new TextBlock { Text = $"{item.ModulePath}#{item.ExportName}" }, true);
        Button run = new() { Content = "Run", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90 };
        run.Click += (_, _) => dialog.Close(picker.SelectedItem as ExportedScriptFunction);
        cancel.Click += (_, _) => dialog.Close(null);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(run); buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { picker, buttons } };
        return await dialog.ShowDialog<ExportedScriptFunction?>(this).ConfigureAwait(true);
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
                if (_monaco is not null)
                    await _monaco.RevealLocationAsync(
                        MonacoEditorHost.CreateDocumentUri(SelectedProfileId, result.PackageId, result.RelativePath),
                        result.Line,
                        result.Column,
                        _cts.Token).ConfigureAwait(true);
            };
            _referencesPanel.Children.Add(row);
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
        _buildState.Text = package.BuildStatus == ScriptPackageBuildStatus.Succeeded ? "Build ✓" : package.BuildStatus == ScriptPackageBuildStatus.Failed ? "Build ✕" : "Build —";
        _runtimeState.Text = $"Runtime {package.RuntimeStatus}";
        if (!string.IsNullOrWhiteSpace(package.LastRuntimeFault)) _inspector.Children.Add(Text($"Fault: {package.LastRuntimeFault}"));
        _inspector.Children.Add(Heading("Exports"));
        if (package.Exports.Count == 0) _inspector.Children.Add(Text("No callable exports discovered."));
        foreach (ExportedScriptFunction export in package.Exports)
        {
            Button open = UiTheme.QuietButton($"{export.ModulePath}#{export.ExportName}({string.Join(", ", export.Parameters.Select(parameter => parameter.Name))})");
            open.HorizontalContentAlignment = HorizontalAlignment.Left;
            open.Click += async (_, _) =>
            {
                await OpenSourceAsync(package.Definition.PackageId, export.ModulePath).ConfigureAwait(true);
                if (_monaco is not null)
                    await _monaco.RevealLocationAsync(
                        MonacoEditorHost.CreateDocumentUri(SelectedProfileId, package.Definition.PackageId, export.ModulePath),
                        export.SourceLocation.Line,
                        export.SourceLocation.Column,
                        _cts.Token).ConfigureAwait(true);
            };
            _inspector.Children.Add(open);
            IReadOnlyList<AutomationFunctionReference> uses = _referencesIndex.FindUses(_runtime.Settings, export.FunctionRef);
            if (uses.Count > 0) _inspector.Children.Add(Text($"Used by: {string.Join(", ", uses.Select(use => use.DefinitionName))}"));
        }
    }

    private void BuildAutomationInspector()
    {
        _inspector.Children.Clear();
        _inspector.Children.Add(Heading("Automation"));
        _inspector.Children.Add(Text("Visual Automation and reusable TypeScript exports share the same Jint execution platform."));
        _inspector.Children.Add(Button("Add Run Script Function…", AddScriptActionAsync));
        _inspector.Children.Add(Button("Add Script Predicate…", AddScriptPredicateAsync));
    }

    private async Task AddScriptActionAsync()
    {
        AutomationTarget? target = await ChooseAutomationTargetAsync(predicate: false).ConfigureAwait(true);
        if (target is null) return;
        ExportedScriptFunction? function = await ChooseAnyExportAsync().ConfigureAwait(true);
        if (function is null) return;
        string? json = await PromptAsync("Run Script Function", "JSON arguments", "{}").ConfigureAwait(true);
        if (json is null || !TryJson(json, out JsonElement arguments)) return;
        RunScriptFunctionAutomationAction action = new(function.FunctionRef, arguments);
        await SaveScriptActionAsync(target, action).ConfigureAwait(true);
    }

    private async Task AddScriptPredicateAsync()
    {
        AutomationTarget? target = await ChooseAutomationTargetAsync(predicate: true).ConfigureAwait(true);
        if (target is null) return;
        ExportedScriptFunction? function = await ChooseAnyExportAsync().ConfigureAwait(true);
        if (function is null) return;
        string? json = await PromptAsync("Script Predicate", "JSON arguments", "{}").ConfigureAwait(true);
        if (json is null || !TryJson(json, out JsonElement arguments)) return;
        await SaveScriptPredicateAsync(target, new ScriptPredicateAutomationCondition(function.FunctionRef, arguments)).ConfigureAwait(true);
    }

    private async Task<AutomationTarget?> ChooseAutomationTargetAsync(bool predicate)
    {
        List<AutomationTarget> targets = [];
        targets.AddRange((_runtime.Settings.Aliases ?? []).Select((item, index) => new AutomationTarget(AutomationInvocationSourceKind.Alias, index, item.Name)));
        targets.AddRange((_runtime.Settings.KeyBindings ?? []).Select((item, index) => new AutomationTarget(AutomationInvocationSourceKind.Keybinding, index, item.Name ?? item.Gesture)));
        targets.AddRange((_runtime.Settings.Triggers ?? []).Select((item, index) => new AutomationTarget(AutomationInvocationSourceKind.TextTrigger, index, item.Pattern)));
        targets.AddRange((_runtime.Settings.SemanticTriggers ?? []).Select((item, index) => new AutomationTarget(AutomationInvocationSourceKind.SemanticTrigger, index, item.Name)));
        targets.AddRange((_runtime.Settings.Timers ?? []).Select((item, index) => new AutomationTarget(AutomationInvocationSourceKind.Timer, index, item.Name)));
        targets.AddRange((_runtime.Settings.GameRules ?? []).Select((item, index) => new AutomationTarget(AutomationInvocationSourceKind.StateRule, index, item.Name)));
        targets.AddRange((_runtime.Settings.Workflows ?? []).Select((item, index) => new AutomationTarget(AutomationInvocationSourceKind.Workflow, index, item.Name)));
        if (predicate) targets = targets.Where(target => target.Kind is AutomationInvocationSourceKind.TextTrigger or AutomationInvocationSourceKind.SemanticTrigger or AutomationInvocationSourceKind.StateRule).ToList();
        if (targets.Count == 0) { AddProblem("Automation", "Create an Automation definition first."); return null; }
        return await ChooseAsync("Automation Definition", targets).ConfigureAwait(true);
    }

    private async Task<ExportedScriptFunction?> ChooseAnyExportAsync()
    {
        IReadOnlyList<ScriptPackageSnapshot> packages = await _workspace.ListPackagesAsync(SelectedProfileId, _cts.Token).ConfigureAwait(true);
        ExportedScriptFunction[] exports = packages.SelectMany(package => package.Exports).ToArray();
        if (exports.Length == 0) { AddProblem("Automation", "No built script functions are available in this profile."); return null; }
        return await ChooseAsync("Script Function", exports, item => $"{item.FunctionRef.PackageId}/{item.ModulePath}#{item.ExportName}").ConfigureAwait(true);
    }

    private async Task SaveScriptActionAsync(AutomationTarget target, RunScriptFunctionAutomationAction action)
    {
        ClientSettings settings = _runtime.Settings;
        CommandAlias[] aliases = [.. settings.Aliases ?? []];
        TriggerRule[] triggers = [.. settings.Triggers ?? []];
        SemanticTriggerRule[] semantic = [.. settings.SemanticTriggers ?? []];
        GameRule[] rules = [.. settings.GameRules ?? []];
        AutomationWorkflow[] workflows = [.. settings.Workflows ?? []];
        CommandTimer[] timers = [.. settings.Timers ?? []];
        CommandKeyBinding[] keys = [.. settings.KeyBindings ?? []];
        switch (target.Kind)
        {
            case AutomationInvocationSourceKind.Alias: aliases[target.Index] = aliases[target.Index] with { Actions = Append(aliases[target.Index].Actions, action) }; break;
            case AutomationInvocationSourceKind.Keybinding: keys[target.Index] = keys[target.Index] with { Action = KeybindingActionKind.RunScriptFunction, ScriptAction = action }; break;
            case AutomationInvocationSourceKind.TextTrigger: triggers[target.Index] = triggers[target.Index] with { Actions = Append(triggers[target.Index].Actions, action) }; break;
            case AutomationInvocationSourceKind.SemanticTrigger: semantic[target.Index] = semantic[target.Index] with { Actions = Append(semantic[target.Index].Actions, action) }; break;
            case AutomationInvocationSourceKind.Timer: timers[target.Index] = timers[target.Index] with { Actions = Append(timers[target.Index].Actions, action) }; break;
            case AutomationInvocationSourceKind.StateRule: rules[target.Index] = rules[target.Index] with { Actions = Append(rules[target.Index].Actions, action) }; break;
            case AutomationInvocationSourceKind.Workflow: workflows[target.Index] = workflows[target.Index] with { Actions = Append(workflows[target.Index].Actions, action) }; break;
        }
        await _runtime.SaveAutomationCompositionAsync(aliases, triggers, semantic, rules, workflows, timers, keys, _cts.Token).ConfigureAwait(true);
        _automationEditor.RefreshSnapshot();
        RefreshReferencePanel(await _workspace.ListPackagesAsync(SelectedProfileId, _cts.Token).ConfigureAwait(true));
    }

    private async Task SaveScriptPredicateAsync(AutomationTarget target, ScriptPredicateAutomationCondition predicate)
    {
        ClientSettings settings = _runtime.Settings;
        TriggerRule[] triggers = [.. settings.Triggers ?? []];
        SemanticTriggerRule[] semantic = [.. settings.SemanticTriggers ?? []];
        GameRule[] rules = [.. settings.GameRules ?? []];
        switch (target.Kind)
        {
            case AutomationInvocationSourceKind.TextTrigger: triggers[target.Index] = triggers[target.Index] with { Conditions = Append(triggers[target.Index].Conditions, predicate) }; break;
            case AutomationInvocationSourceKind.SemanticTrigger: semantic[target.Index] = semantic[target.Index] with { Conditions = Append(semantic[target.Index].Conditions, predicate) }; break;
            case AutomationInvocationSourceKind.StateRule: rules[target.Index] = rules[target.Index] with { Conditions = Append(rules[target.Index].Conditions, predicate) }; break;
            default: return;
        }
        await _runtime.SaveAutomationCompositionAsync(
            settings.Aliases ?? [], triggers, semantic, rules, settings.Workflows ?? [], settings.Timers ?? [], settings.KeyBindings ?? [], _cts.Token).ConfigureAwait(true);
        _automationEditor.RefreshSnapshot();
    }

    private static IReadOnlyList<T> Append<T>(IReadOnlyList<T>? values, T value) => [.. values ?? [], value];

    private void RefreshReferencePanel(IReadOnlyList<ScriptPackageSnapshot> packages)
    {
        _referencesPanel.Children.Clear();
        Dictionary<ScriptFunctionRef, ExportedScriptFunction> exports = packages.SelectMany(package => package.Exports).ToDictionary(export => export.FunctionRef);
        foreach (AutomationFunctionReference reference in _referencesIndex.Build(_runtime.Settings))
        {
            bool valid = exports.ContainsKey(reference.FunctionRef);
            _referencesPanel.Children.Add(Text($"{(valid ? "✓" : "✕")} {reference.DefinitionName} → {reference.FunctionRef.PackageId}/{reference.FunctionRef.ModulePath}#{reference.FunctionRef.ExportName}"));
        }
    }

    private async Task RefreshRuntimePanelAsync()
    {
        _runtimePanel.Children.Clear();
        foreach (ScriptPackageSnapshot package in await _workspace.ListPackagesAsync(SelectedProfileId, _cts.Token).ConfigureAwait(true))
            _runtimePanel.Children.Add(Text($"{package.Definition.Name} · {package.RuntimeStatus} · {package.BuildStatus} · enabled={package.Definition.Enabled}"));
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
    }

    private async Task SelectScriptsAsync()
    {
        TreeViewItem? scripts = _nodes.FirstOrDefault(pair => pair.Value.Kind == NodeKind.Scripts).Key;
        if (scripts is not null) _navigator.SelectedItem = scripts;
        await Task.CompletedTask;
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.S)
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) await SaveAllAsync().ConfigureAwait(true);
            else await SaveActiveAsync().ConfigureAwait(true);
        }
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || _dirty.Count == 0) return;
        e.Cancel = true;
        string choice = await ConfirmDirtyCloseAsync().ConfigureAwait(true);
        if (choice == "cancel") return;
        if (choice == "save") await SaveAllAsync().ConfigureAwait(true);
        _closeApproved = true;
        Close();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _workspace.WorkspaceChanged -= WorkspaceChanged;
        _cts.Cancel();
        _automationEditor.Deactivate();
        if (_monaco is not null) _ = _monaco.DisposeAsync();
        _cts.Dispose();
    }

    private async Task<string> ConfirmDirtyCloseAsync()
    {
        Window dialog = Dialog("Unsaved script changes", 430, 180);
        Button save = new() { Content = "Save", MinWidth = 90 };
        Button discard = new() { Content = "Discard", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90 };
        save.Click += (_, _) => dialog.Close("save"); discard.Click += (_, _) => dialog.Close("discard"); cancel.Click += (_, _) => dialog.Close("cancel");
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save); buttons.Children.Add(discard); buttons.Children.Add(cancel);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 16, Children = { Text("Save dirty script documents before closing?"), buttons } };
        return await dialog.ShowDialog<string>(this).ConfigureAwait(true) ?? "cancel";
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

    private async Task<T?> ChooseAsync<T>(string title, IReadOnlyList<T> values, Func<T, string>? label = null) where T : class
    {
        Window dialog = Dialog(title, 560, 240);
        ComboBox picker = new() { ItemsSource = values, SelectedIndex = 0 };
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

    private bool TryJson(string value, out JsonElement element)
    {
        try { using JsonDocument document = JsonDocument.Parse(value); element = document.RootElement.Clone(); return true; }
        catch (JsonException exception) { element = default; AddProblem("JSON", exception.Message); return false; }
    }

    private static TextBlock Text(string value) => new() { Text = value, Foreground = UiTheme.Text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Heading(string value) => new() { Text = value, Foreground = UiTheme.Accent, FontWeight = FontWeight.SemiBold };
}

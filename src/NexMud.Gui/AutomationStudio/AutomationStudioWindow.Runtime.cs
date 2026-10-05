using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NexMud.Client.Scripting;
using NexMud.Client.Settings;
using NexMud.Contracts.Events;
using NexMud.Scripting.Runtime;

namespace NexMud.Gui.AutomationStudio;

internal sealed partial class AutomationStudioWindow
{
    private enum RuntimeSection { PackageStatus, ActiveRuntime, Diagnostics, RecentFaults, Events }
    private sealed record RuntimeEventRow(string? ProfileId, string? PackageId, DateTimeOffset Timestamp, string Level, string Source, string Message);

    private RuntimeSection _runtimeSection = RuntimeSection.PackageStatus;
    private readonly Queue<RuntimeEventRow> _runtimeEvents = new();
    private string _runtimeSearchText = "";
    private string _runtimeSelectedStatus = "All statuses";
    private readonly RuntimeUiRefreshGate _runtimeUiRefreshGate = new();
    private int _runtimeEventsDirty;
    private int _eventsPanelDirty;
    private int _runtimeDiagnosticsDirty;
    private int _runtimeDashboardDirty;
    private ContentControl? _runtimeRecentEventsHost;
    private ContentControl? _runtimeSectionHost;
    private static readonly string[] RuntimeStatusOptions = ["All statuses", "Running", "Disabled", "Faulted", "Build failed"];

    private void InvalidateRuntimeRefreshForProfileChange()
    {
        Interlocked.Increment(ref _runtimeRefreshGeneration);
        _runtimeUiRefreshGate.Invalidate();
        Interlocked.Exchange(ref _runtimeEventsDirty, 0);
        Interlocked.Exchange(ref _eventsPanelDirty, 0);
        Interlocked.Exchange(ref _runtimeDiagnosticsDirty, 0);
        Interlocked.Exchange(ref _runtimeDashboardDirty, 0);
    }

    private async Task RefreshRuntimeActivityAsync()
    {
        string profileId = SelectedProfileId;
        long generation = Interlocked.Increment(ref _runtimeRefreshGeneration);
        try
        {
            IReadOnlyList<ScriptPackageSnapshot> packages = await _workspace.ListPackagesAsync(
                profileId, _cts.Token).ConfigureAwait(true);
            if (_cts.IsCancellationRequested || generation != Volatile.Read(ref _runtimeRefreshGeneration) ||
                !string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal)) return;

            _packages = packages;
            await RefreshRuntimePanelAsync().ConfigureAwait(true);
            if (_activity == StudioActivity.Runtime) RenderRuntimeDashboard();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            AddProblem("Runtime", $"Unable to refresh runtime activity: {exception.Message}");
        }
    }

    private bool IsCurrentRuntimeRefresh(string profileId, long generation) =>
        !_cts.IsCancellationRequested && generation == Volatile.Read(ref _runtimeRefreshGeneration) &&
        string.Equals(profileId, SelectedProfileId, StringComparison.Ordinal);

    private void RequestRuntimeUiRefresh(bool events = false, bool eventsPanel = false, bool diagnostics = false, bool dashboard = false)
    {
        if (_cts.IsCancellationRequested) return;
        if (events) Interlocked.Exchange(ref _runtimeEventsDirty, 1);
        if (eventsPanel) Interlocked.Exchange(ref _eventsPanelDirty, 1);
        if (diagnostics) Interlocked.Exchange(ref _runtimeDiagnosticsDirty, 1);
        if (dashboard) Interlocked.Exchange(ref _runtimeDashboardDirty, 1);
        if (!_runtimeUiRefreshGate.TryQueue(out long generation)) return;
        _ = DebounceRuntimeUiRefreshAsync(generation);
    }

    private async Task DebounceRuntimeUiRefreshAsync(long generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), _cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => _ = ApplyRuntimeUiRefreshAsync(generation), DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _runtimeUiRefreshGate.Invalidate();
        }
    }

    private async Task ApplyRuntimeUiRefreshAsync(long generation)
    {
        string profileId = SelectedProfileId;
        long profileGeneration = Volatile.Read(ref _runtimeRefreshGeneration);
        try
        {
            bool updateEvents = Interlocked.Exchange(ref _runtimeEventsDirty, 0) != 0;
            bool updateEventsPanel = Interlocked.Exchange(ref _eventsPanelDirty, 0) != 0;
            bool updateDiagnostics = Interlocked.Exchange(ref _runtimeDiagnosticsDirty, 0) != 0;
            bool updateDashboard = Interlocked.Exchange(ref _runtimeDashboardDirty, 0) != 0;
            if (!IsCurrentRuntimeRefresh(profileId, profileGeneration)) return;

            if (updateEventsPanel) RefreshEventsPanel();
            if (updateDiagnostics) await RefreshRuntimePanelAsync().ConfigureAwait(true);
            if (!IsCurrentRuntimeRefresh(profileId, profileGeneration)) return;
            if (_activity == StudioActivity.Runtime)
            {
                if (updateDashboard) RenderRuntimeDashboard();
                else
                {
                    if (updateEvents) RefreshRuntimeEventViews();
                    if (updateDiagnostics && _runtimeSection is RuntimeSection.Diagnostics or RuntimeSection.RecentFaults &&
                        _runtimeSectionHost is not null)
                        _runtimeSectionHost.Content = RenderRuntimeSection();
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AddProblem("Runtime", $"Unable to update runtime activity: {exception.Message}");
        }
        finally
        {
            if (_runtimeUiRefreshGate.Complete(generation))
                _ = DebounceRuntimeUiRefreshAsync(generation);
        }
    }

    private bool IsEventsTabSelected() =>
        _bottom.SelectedItem is TabItem selected && string.Equals(selected.Header as string, "Events", StringComparison.Ordinal);

    private void RefreshEventsPanel()
    {
        if (_bottomCollapsed || !IsEventsTabSelected()) return;
        _eventsPanel.Children.Clear();
        string[] snapshot;
        lock (_events) snapshot = _events.ToArray();
        foreach (string item in snapshot.TakeLast(150)) _eventsPanel.Children.Add(Text(item));
    }

    private void RefreshRuntimeEventViews()
    {
        if (_runtimeRecentEventsHost is not null)
            _runtimeRecentEventsHost.Content = RenderRecentEventsPanel();
        if (_runtimeSection is RuntimeSection.Events && _runtimeSectionHost is not null)
            _runtimeSectionHost.Content = RenderRuntimeSection();
    }

    private void RenderRuntimeDashboard()
    {
        if (_activity != StudioActivity.Runtime) return;
        _runtimeRecentEventsHost = null;
        _runtimeSectionHost = null;
        _breadcrumbBar.IsVisible = false;
        _statusLeft.Text = $"Runtime · {SelectedProfileId}";
        _statusCursor.Text = "";
        ShowMonaco(false);

        StackPanel page = new() { Spacing = 12, Margin = new Thickness(18), MinWidth = 700 };
        ConnectionProfile selectedProfile = _runtime.ConnectionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, SelectedProfileId, StringComparison.Ordinal)) ?? _runtime.ActiveConnectionProfile;
        bool isActiveProfile = string.Equals(SelectedProfileId, _runtime.ActiveConnectionProfile.Id, StringComparison.Ordinal);
        ScriptModuleSnapshot[] profileModules = ProfileRuntimeModules();
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 10 };
        StackPanel heading = new() { Spacing = 3 };
        heading.Children.Add(new TextBlock { Text = "Runtime", Foreground = StudioShellChrome.Foreground, FontSize = 24, FontWeight = FontWeight.Bold });
        heading.Children.Add(new TextBlock { Text = "Monitor package status, runtime activity, and live diagnostics.", Foreground = StudioShellChrome.Secondary, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(heading);
        TextBlock status = new()
        {
            Text = !isActiveProfile ? "Profile Inactive" : profileModules.Any(module => module.Loaded) ? "Runtime Active" : "Runtime Ready",
            Foreground = isActiveProfile ? StudioShellChrome.Success : StudioShellChrome.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        Button restart = RuntimeButton("Reload Runtime Packages", ReloadRuntimeProfileAsync);
        restart.IsEnabled = isActiveProfile;
        Grid.SetColumn(restart, 2);
        header.Children.Add(restart);
        Button refresh = RuntimeButton("Refresh", RefreshRuntimeActivityAsync);
        Grid.SetColumn(refresh, 3);
        header.Children.Add(refresh);
        page.Children.Add(header);

        int loaded = profileModules.Count(module => module.Loaded);
        int enabled = _packages.Count(package => package.Definition.Enabled);
        Grid cards = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 9 };
        cards.Children.Add(RuntimeCard("Selected Profile", selectedProfile.Name,
            isActiveProfile ? "Active runtime profile" : "Inactive — runtime operations unavailable"));
        Control engineCard = RuntimeCard("Runtime Engine", _runtime.Scripting.JavaScriptRuntime.RuntimeName, "JavaScript runtime");
        Grid.SetColumn(engineCard, 1);
        cards.Children.Add(engineCard);
        Control loadedCard = RuntimeCard("Loaded Packages", $"{loaded} / {enabled}", "Loaded module snapshot");
        Grid.SetColumn(loadedCard, 2);
        cards.Children.Add(loadedCard);
        Control stateCard = RuntimeCard("Overall State", loaded > 0 ? "Running" : enabled > 0 ? "Not running" : "Idle", "From current runtime snapshot");
        Grid.SetColumn(stateCard, 3);
        cards.Children.Add(stateCard);
        page.Children.Add(cards);

        if (_runtimeSection is RuntimeSection.PackageStatus)
        {
            ContentControl packageTableHost = new();
            TextBox packageSearch = new()
            {
                PlaceholderText = "Search packages…", Text = _runtimeSearchText,
                Background = StudioShellChrome.Input, Foreground = StudioShellChrome.Foreground,
                BorderBrush = StudioShellChrome.Border, CornerRadius = new CornerRadius(5),
                MinWidth = 210, Padding = new Thickness(8, 4)
            };
            packageSearch.TextChanged += (_, _) =>
            {
                string query = packageSearch.Text ?? "";
                if (string.Equals(query, _runtimeSearchText, StringComparison.Ordinal)) return;
                _runtimeSearchText = query;
                packageTableHost.Content = RenderPackageTable(isActiveProfile);
            };
            ComboBox statusFilter = new()
            {
                ItemsSource = RuntimeStatusOptions, SelectedItem = _runtimeSelectedStatus, MinWidth = 130
            };
            statusFilter.SelectionChanged += (_, _) =>
            {
                string statusValue = statusFilter.SelectedItem as string ?? RuntimeStatusOptions[0];
                if (string.Equals(statusValue, _runtimeSelectedStatus, StringComparison.Ordinal)) return;
                _runtimeSelectedStatus = statusValue;
                packageTableHost.Content = RenderPackageTable(isActiveProfile);
            };
            Grid toolbar = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            toolbar.Children.Add(packageSearch);
            Grid.SetColumn(statusFilter, 1);
            toolbar.Children.Add(statusFilter);
            Button load = RuntimeButton("Load / Reload Enabled", ReloadRuntimeProfileAsync);
            load.IsEnabled = isActiveProfile;
            Grid.SetColumn(load, 2);
            toolbar.Children.Add(load);
            page.Children.Add(toolbar);
            packageTableHost.Content = RenderPackageTable(isActiveProfile);
            page.Children.Add(packageTableHost);
            _runtimeRecentEventsHost = new ContentControl { Content = RenderRecentEventsPanel() };
            page.Children.Add(_runtimeRecentEventsHost);
        }
        else
        {
            page.Children.Add(new TextBlock { Text = _runtimeSection switch
            {
                RuntimeSection.ActiveRuntime => "Active Runtime",
                RuntimeSection.Diagnostics => "Diagnostics",
                RuntimeSection.RecentFaults => "Recent Faults",
                RuntimeSection.Events => "Events",
                _ => "Package Status"
            }, Foreground = StudioShellChrome.Foreground, FontSize = 18, FontWeight = FontWeight.SemiBold });
            _runtimeSectionHost = new ContentControl { Content = RenderRuntimeSection() };
            page.Children.Add(_runtimeSectionHost);
        }
        _center.Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private Control RenderPackageTable(bool isActiveProfile)
    {
        StackPanel table = new() { Spacing = 0 };
        table.Children.Add(RuntimeTableRow(["Package", "Enabled", "Build", "Runtime", "Version", "Dependency Directory", "Last Build", "Actions"], true));
        string query = _runtimeSearchText.Trim();
        IEnumerable<ScriptPackageSnapshot> filtered = _packages;
        if (query.Length > 0)
            filtered = filtered.Where(package => package.Definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Definition.PackageId.Contains(query, StringComparison.OrdinalIgnoreCase));
        filtered = _runtimeSelectedStatus switch
        {
            "Running" => filtered.Where(package => package.RuntimeStatus == ScriptStatus.Running),
            "Disabled" => filtered.Where(package => !package.Definition.Enabled),
            "Faulted" => filtered.Where(package => package.RuntimeStatus == ScriptStatus.Faulted || package.LastRuntimeFault is not null),
            "Build failed" => filtered.Where(package => package.BuildStatus == ScriptPackageBuildStatus.Failed),
            _ => filtered
        };
        ScriptPackageSnapshot[] packages = filtered.ToArray();
        foreach (ScriptPackageSnapshot package in packages)
        {
            StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
            Button toggle = RuntimeButton(package.Definition.Enabled ? "Disable" : "Enable",
                () => TogglePackageAsync(package.Definition.PackageId));
            toggle.IsEnabled = isActiveProfile;
            actions.Children.Add(toggle);
            Button build = RuntimeButton("Build", () => BuildRuntimePackageAsync(package.Definition.PackageId));
            build.IsEnabled = isActiveProfile;
            actions.Children.Add(build);
            string runtimeLabel = package.RuntimeStatus.ToString();
            if (package.RuntimeStatus == ScriptStatus.Running && package.BuildStatus == ScriptPackageBuildStatus.Failed)
                runtimeLabel = "Running previous build · Current source build failed";
            if (package.RuntimeMessage is not null)
                runtimeLabel = package.RuntimeMessage;
            table.Children.Add(RuntimeTableRow([
                $"{package.Definition.Name}\n{package.Definition.PackageId}",
                package.Definition.Enabled ? "Enabled" : "Disabled",
                package.BuildStatus.ToString(),
                runtimeLabel,
                package.Definition.Version,
                package.HasNodeModulesDirectory ? "node_modules directory present" : "node_modules directory absent",
                package.LastBuild?.ToLocalTime().ToString("g") ?? "—",
                actions
            ], false));
        }
        if (packages.Length == 0) table.Children.Add(RuntimeTableRow(["No packages match this filter.", "", "", "", "", "", "", ""], false));
        return new Border
        {
            Background = StudioShellChrome.Card,
            BorderBrush = StudioShellChrome.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = table
        };
    }

    private Control RenderRuntimeSection()
    {
        StackPanel content = new() { Spacing = 6 };
        switch (_runtimeSection)
        {
            case RuntimeSection.ActiveRuntime:
                content.Children.Add(RuntimeTableRow(["Module", "State", "Tasks", "Invocations"], true));
                foreach (ScriptModuleSnapshot module in ProfileRuntimeModules())
                    content.Children.Add(RuntimeTableRow([
                        $"{module.Name} · {module.Id}", module.Status.ToString(), module.Tasks.Count.ToString(),
                        (module.Invocations?.Count ?? 0).ToString()
                    ], false));
                if (ProfileRuntimeModules().Length == 0) content.Children.Add(RuntimeEmpty("No loaded script modules for this profile."));
                break;
            case RuntimeSection.Diagnostics:
            case RuntimeSection.RecentFaults:
                IEnumerable<StudioDiagnostic> diagnostics = _diagnostics.Snapshot(SelectedProfileId)
                    .Where(item => item.Source.StartsWith("runtime:", StringComparison.Ordinal) ||
                                   item.Source.StartsWith("runtime-fault:", StringComparison.Ordinal));
                if (_runtimeSection == RuntimeSection.RecentFaults)
                    diagnostics = diagnostics.Where(item => item.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
                foreach (StudioDiagnostic item in diagnostics)
                    content.Children.Add(RuntimeTableRow([item.Severity, item.PackageId ?? item.FilePath ?? item.Source, item.Message], false));
                foreach (ScriptPackageSnapshot package in _packages.Where(package => package.LastRuntimeFault is not null))
                {
                    bool alreadyListed = diagnostics.Any(item => item.PackageId == package.Definition.PackageId &&
                        string.Equals(item.Message, package.LastRuntimeFault, StringComparison.Ordinal));
                    if (!alreadyListed)
                        content.Children.Add(RuntimeTableRow(["Fault", package.Definition.Name, package.LastRuntimeFault!], false));
                }
                if (!content.Children.Any()) content.Children.Add(RuntimeEmpty(_runtimeSection == RuntimeSection.Diagnostics ? "No diagnostics." : "No runtime faults."));
                break;
            case RuntimeSection.Events:
                content.Children.Add(RuntimeTableRow(["Time", "Level", "Source", "Message"], true));
                RuntimeEventRow[] rows;
                lock (_runtimeEvents) rows = _runtimeEvents.ToArray();
                foreach (RuntimeEventRow row in rows.Reverse())
                    content.Children.Add(RuntimeTableRow([row.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"), row.Level, row.Source, row.Message], false));
                if (rows.Length == 0) content.Children.Add(RuntimeEmpty("No runtime events have been received."));
                break;
        }
        return new Border { Background = StudioShellChrome.Card, BorderBrush = StudioShellChrome.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = content };
    }

    private ScriptModuleSnapshot[] ProfileRuntimeModules()
    {
        if (!string.Equals(SelectedProfileId, _runtime.ActiveConnectionProfile.Id, StringComparison.Ordinal)) return [];
        HashSet<string> packageIds = _packages.Select(package => package.Definition.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _runtime.Scripting.JavaScriptRuntime.Snapshot()
            .Where(module => packageIds.Contains(module.Id.Value) &&
                string.Equals(_runtime.Scripting.ResolveRuntimeProfile(module.Id.Value), SelectedProfileId, StringComparison.Ordinal))
            .ToArray();
    }

    private Control RenderRecentEventsPanel()
    {
        StackPanel panel = new() { Spacing = 0 };
        panel.Children.Add(RuntimeTableRow(["Recent Events", "", "", ""], true));
        panel.Children.Add(RuntimeTableRow(["Time", "Level", "Source", "Message"], true));
        RuntimeEventRow[] rows;
        lock (_runtimeEvents) rows = _runtimeEvents.Where(row => row.ProfileId == SelectedProfileId).ToArray();
        foreach (RuntimeEventRow row in rows.TakeLast(100).Reverse())
            panel.Children.Add(RuntimeTableRow([
                row.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"), row.Level,
                row.PackageId is null ? row.Source : $"{row.Source} · {row.PackageId}", row.Message
            ], false));
        if (rows.Length == 0) panel.Children.Add(RuntimeEmpty("No recent runtime events for this profile."));
        return new Border { Background = StudioShellChrome.Card, BorderBrush = StudioShellChrome.Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel };
    }

    private static Control RuntimeCard(string title, string value, string detail) => new Border
    {
        Background = StudioShellChrome.Card,
        BorderBrush = StudioShellChrome.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, Foreground = StudioShellChrome.Secondary },
                new TextBlock { Text = value, Foreground = StudioShellChrome.Foreground, FontSize = 17, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = detail, Foreground = StudioShellChrome.Secondary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        }
    };

    private static Control RuntimeTableRow(IReadOnlyList<object> cells, bool header)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", cells.Count))), MinHeight = header ? 38 : 44 };
        for (int index = 0; index < cells.Count; index++)
        {
            Control cell = cells[index] as Control ?? new TextBlock
            {
                Text = cells[index].ToString(),
                Foreground = header ? StudioShellChrome.Foreground : StudioShellChrome.Secondary,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 5)
            };
            Grid.SetColumn(cell, index);
            row.Children.Add(cell);
        }
        return new Border
        {
            Background = header ? StudioShellChrome.Input : Brushes.Transparent,
            BorderBrush = StudioShellChrome.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private static Control RuntimeEmpty(string message) => new TextBlock { Text = message, Foreground = StudioShellChrome.Secondary, Margin = new Thickness(8) };

    private static Button RuntimeButton(string label, Func<Task> action)
    {
        Button button = UiTheme.QuietButton(label);
        button.Foreground = StudioShellChrome.Foreground;
        button.BorderBrush = StudioShellChrome.Border;
        button.Click += async (_, _) => await action().ConfigureAwait(true);
        return button;
    }

    private async Task BuildRuntimePackageAsync(string packageId)
    {
        string profileId = SelectedProfileId;
        try
        {
            ScriptPackageBuildResult result = await _workspace.BuildPackageAsync(profileId, packageId, _cts.Token).ConfigureAwait(true);
            AddConsole($"Runtime build {packageId}: {(result.Success ? "succeeded" : "failed")}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddConsole($"Runtime build {packageId} failed: {exception.Message}");
        }
        if (profileId == SelectedProfileId)
            await RefreshRuntimeActivityAsync().ConfigureAwait(true);
    }

    private async Task ReloadRuntimeProfileAsync()
    {
        try
        {
            await _workspace.ReloadProfileAsync(SelectedProfileId, _cts.Token).ConfigureAwait(true);
            AddConsole("Enabled runtime packages reloaded.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddConsole(exception.Message);
        }
        await RefreshRuntimeActivityAsync().ConfigureAwait(true);
    }

    private bool RecordRuntimeEvent(EventEnvelope envelope)
    {
        RuntimeEventRow? row = envelope.Payload switch
        {
            ScriptLogEmitted log when string.Equals(_runtime.Scripting.ResolveRuntimeProfile(log.ModuleId), SelectedProfileId, StringComparison.Ordinal) =>
                new RuntimeEventRow(SelectedProfileId, null, envelope.Timestamp, log.Level, log.ModuleId,
                    log.DataJson is null ? log.Message : $"{log.Message} · {log.DataJson}"),
            ScriptRuntimeDiagnosticEmitted diagnostic when string.Equals(diagnostic.ProfileId, SelectedProfileId, StringComparison.Ordinal) =>
                new RuntimeEventRow(diagnostic.ProfileId, null, envelope.Timestamp,
                    diagnostic.Kind.Contains("Fault", StringComparison.OrdinalIgnoreCase) ? "Error" : "Info",
                    diagnostic.ScriptId, diagnostic.Message ?? diagnostic.Kind),
            ScriptPackageBuildChanged build when string.Equals(build.ProfileId, SelectedProfileId, StringComparison.Ordinal) =>
                new RuntimeEventRow(build.ProfileId, build.PackageId, build.BuiltAt,
                    build.Success ? "Info" : "Error", build.PackageId,
                    build.Success ? "Package build succeeded." : $"Package build failed ({build.ErrorCount} errors)."),
            ScriptPackageRuntimeChanged package when string.Equals(package.ProfileId, SelectedProfileId, StringComparison.Ordinal) =>
                new RuntimeEventRow(package.ProfileId, package.PackageId, envelope.Timestamp, package.Status,
                    package.PackageId, package.Message ?? package.Status),
            _ => null
        };
        if (row is null) return false;
        lock (_runtimeEvents)
        {
            _runtimeEvents.Enqueue(row);
            while (_runtimeEvents.Count > MaximumEventRows) _runtimeEvents.Dequeue();
        }
        RequestRuntimeUiRefresh(events: _activity == StudioActivity.Runtime,
            eventsPanel: true,
            dashboard: _activity == StudioActivity.Runtime &&
                envelope.Payload is ScriptPackageBuildChanged or ScriptPackageRuntimeChanged);
        return true;
    }

    private void ClearRuntimeEvents()
    {
        lock (_runtimeEvents) _runtimeEvents.Clear();
        lock (_events) _events.Clear();
        RefreshEventsPanel();
    }
}

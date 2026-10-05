using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Scripting;
using NexMud.Contracts.Events;
using NexMud.Scripting.Runtime;

namespace NexMud.Gui.AutomationStudio;

internal sealed partial class AutomationStudioWindow
{
    private enum RuntimeSection { PackageStatus, ActiveRuntime, Diagnostics, RecentFaults, Events }
    private sealed record RuntimeEventRow(DateTimeOffset Timestamp, string Level, string Source, string Message);

    private RuntimeSection _runtimeSection = RuntimeSection.PackageStatus;
    private readonly Queue<RuntimeEventRow> _runtimeEvents = new();
    private readonly TextBox _runtimePackageSearch = new()
    {
        PlaceholderText = "Search packages…",
        Background = StudioShellChrome.Input,
        Foreground = StudioShellChrome.Foreground,
        BorderBrush = StudioShellChrome.Border,
        CornerRadius = new CornerRadius(5),
        MinWidth = 210,
        Padding = new Thickness(8, 4)
    };
    private readonly ComboBox _runtimeStatusFilter = new()
    {
        ItemsSource = new[] { "All statuses", "Running", "Disabled", "Faulted", "Build failed" },
        SelectedIndex = 0,
        MinWidth = 130
    };
    private bool _runtimeFiltersWired;

    private void RenderRuntimeDashboard()
    {
        if (_activity != StudioActivity.Runtime) return;
        _breadcrumbBar.IsVisible = false;
        _statusLeft.Text = $"Runtime · {SelectedProfileId}";
        _statusCursor.Text = "";
        ShowMonaco(false);

        StackPanel page = new() { Spacing = 12, Margin = new Thickness(18), MinWidth = 700 };
        ScriptModuleSnapshot[] profileModules = ProfileRuntimeModules();
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 10 };
        StackPanel heading = new() { Spacing = 3 };
        heading.Children.Add(new TextBlock { Text = "Runtime", Foreground = StudioShellChrome.Foreground, FontSize = 24, FontWeight = FontWeight.Bold });
        heading.Children.Add(new TextBlock { Text = "Monitor package status, runtime activity, and live diagnostics.", Foreground = StudioShellChrome.Secondary, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(heading);
        TextBlock status = new()
        {
            Text = profileModules.Any(module => module.Loaded) ? "Runtime Active" : "Runtime Ready",
            Foreground = StudioShellChrome.Success,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };
        Grid.SetColumn(status, 1);
        header.Children.Add(status);
        Button restart = RuntimeButton("Reload Runtime Packages", ReloadRuntimeProfileAsync);
        restart.IsEnabled = string.Equals(SelectedProfileId, _runtime.ActiveConnectionProfile.Id, StringComparison.Ordinal);
        Grid.SetColumn(restart, 2);
        header.Children.Add(restart);
        Button refresh = RuntimeButton("Refresh", async () => await RefreshNavigatorAsync().ConfigureAwait(true));
        Grid.SetColumn(refresh, 3);
        header.Children.Add(refresh);
        page.Children.Add(header);

        int loaded = profileModules.Count(module => module.Loaded);
        int enabled = _packages.Count(package => package.Definition.Enabled);
        Grid cards = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 9 };
        cards.Children.Add(RuntimeCard("Active Profile", _runtime.ActiveConnectionProfile.Name, _runtime.ActiveConnectionProfile.Id));
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
            WireRuntimeFilters();
            Grid toolbar = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            toolbar.Children.Add(_runtimePackageSearch);
            Grid.SetColumn(_runtimeStatusFilter, 1);
            toolbar.Children.Add(_runtimeStatusFilter);
            Button load = RuntimeButton("Load / Reload Enabled", ReloadRuntimeProfileAsync);
            load.IsEnabled = string.Equals(SelectedProfileId, _runtime.ActiveConnectionProfile.Id, StringComparison.Ordinal);
            Grid.SetColumn(load, 2);
            toolbar.Children.Add(load);
            page.Children.Add(toolbar);
            page.Children.Add(RenderPackageTable());
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
            page.Children.Add(RenderRuntimeSection());
        }
        _center.Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private void WireRuntimeFilters()
    {
        if (_runtimeFiltersWired) return;
        _runtimeFiltersWired = true;
        _runtimePackageSearch.TextChanged += (_, _) => RenderRuntimeDashboard();
        _runtimeStatusFilter.SelectionChanged += (_, _) => RenderRuntimeDashboard();
    }

    private Control RenderPackageTable()
    {
        StackPanel table = new() { Spacing = 0 };
        table.Children.Add(RuntimeTableRow(["Package", "Enabled", "Build", "Runtime", "Last Build", "Actions"], true));
        IEnumerable<ScriptPackageSnapshot> packages = _packages;
        string query = _runtimePackageSearch.Text?.Trim() ?? "";
        if (query.Length > 0)
            packages = packages.Where(package => package.Definition.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Definition.PackageId.Contains(query, StringComparison.OrdinalIgnoreCase));
        string filter = _runtimeStatusFilter.SelectedItem as string ?? "All statuses";
        packages = filter switch
        {
            "Running" => packages.Where(package => package.RuntimeStatus == ScriptStatus.Running),
            "Disabled" => packages.Where(package => package.RuntimeStatus == ScriptStatus.Disabled),
            "Faulted" => packages.Where(package => package.RuntimeStatus == ScriptStatus.Faulted || package.LastRuntimeFault is not null),
            "Build failed" => packages.Where(package => package.BuildStatus == ScriptPackageBuildStatus.Failed),
            _ => packages
        };
        foreach (ScriptPackageSnapshot package in packages)
        {
            StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
            Button toggle = RuntimeButton(package.Definition.Enabled ? "Disable" : "Enable",
                () => TogglePackageAsync(package.Definition.PackageId));
            actions.Children.Add(toggle);
            Button build = RuntimeButton("Build", () => BuildRuntimePackageAsync(package.Definition.PackageId));
            actions.Children.Add(build);
            string fault = package.LastRuntimeFault ?? "";
            table.Children.Add(RuntimeTableRow([
                $"{package.Definition.Name}\n{package.Definition.PackageId}",
                package.Definition.Enabled ? "Enabled" : "Disabled",
                package.BuildStatus.ToString(),
                package.RuntimeStatus + (fault.Length > 0 ? $" · {fault}" : ""),
                package.LastBuild?.ToLocalTime().ToString("g") ?? "—",
                actions
            ], false));
        }
        if (!packages.Any()) table.Children.Add(RuntimeTableRow(["No packages match this filter.", "", "", "", "", ""], false));
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
                if (_runtime.Scripting.JavaScriptRuntime.Snapshot().Count == 0) content.Children.Add(RuntimeEmpty("No loaded script modules."));
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
        HashSet<string> packageIds = _packages.Select(package => package.Definition.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _runtime.Scripting.JavaScriptRuntime.Snapshot()
            .Where(module => packageIds.Contains(module.Id.Value))
            .ToArray();
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
        if (profileId == SelectedProfileId) await RefreshNavigatorAsync().ConfigureAwait(true);
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
        await RefreshNavigatorAsync().ConfigureAwait(true);
    }

    private void RecordRuntimeEvent(EventEnvelope envelope)
    {
        if (envelope.Payload is not (ScriptLogEmitted or ScriptRuntimeDiagnosticEmitted or ScriptPackageRuntimeChanged)) return;
        RuntimeEventRow row = envelope.Payload switch
        {
            ScriptLogEmitted log => new RuntimeEventRow(envelope.Timestamp, log.Level, log.ModuleId,
                log.DataJson is null ? log.Message : $"{log.Message} · {log.DataJson}"),
            ScriptRuntimeDiagnosticEmitted diagnostic => new RuntimeEventRow(envelope.Timestamp,
                diagnostic.Kind.Contains("Fault", StringComparison.OrdinalIgnoreCase) ? "Error" : "Info",
                diagnostic.ScriptId, diagnostic.Message ?? diagnostic.Kind),
            ScriptPackageRuntimeChanged package => new RuntimeEventRow(envelope.Timestamp, package.Status,
                package.PackageId, package.Message ?? package.Status),
            _ => throw new InvalidOperationException("Unsupported runtime event payload.")
        };
        if (!_packages.Any(package => string.Equals(package.Definition.PackageId, row.Source, StringComparison.OrdinalIgnoreCase))) return;
        lock (_runtimeEvents)
        {
            _runtimeEvents.Enqueue(row);
            while (_runtimeEvents.Count > MaximumEventRows) _runtimeEvents.Dequeue();
        }
        if (_activity == StudioActivity.Runtime && _runtimeSection == RuntimeSection.Events) RenderRuntimeDashboard();
    }
}

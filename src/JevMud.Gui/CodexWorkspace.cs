using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JevMud.Client.Knowledge;
using JevMud.Contracts.State;

namespace JevMud.Gui;

/// <summary>
/// Persistent Codex explorer. This control owns its result list, selection, scrolling,
/// cancellation, and detail lifecycle. It is intentionally independent from MainWindow's
/// render loop: live game-state ticks never rebuild the Codex visual tree.
/// </summary>
internal sealed class CodexWorkspace : UserControl, IDisposable
{
    private readonly WorldKnowledgeStore _knowledge;
    private readonly Func<CodexEntryDetail, CodexLocation, Task> _routeToLocation;
    private readonly Func<string, Task> _submitCommand;
    private readonly CancellationToken _applicationToken;
    private readonly ObservableCollection<CodexEntrySummary> _entries = [];
    private readonly Dictionary<string, Button> _categoryButtons = new(StringComparer.Ordinal);
    private readonly TextBox _search = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _context = new();
    private readonly TextBlock _status = new();
    private readonly ListBox _results = new();
    private readonly ContentControl _detail = new();
    private readonly Button _refresh = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailCts;
    private CodexEntryKind? _category;
    private CodexEntrySummary? _selected;
    private string? _roomName;
    private bool _activated;
    private bool _suppressSelection;

    public CodexWorkspace(
        WorldKnowledgeStore knowledge,
        Func<CodexEntryDetail, CodexLocation, Task> routeToLocation,
        Func<string, Task> submitCommand,
        CancellationToken applicationToken)
    {
        _knowledge = knowledge;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;
        _routeToLocation = routeToLocation;
        _submitCommand = submitCommand;
        _applicationToken = applicationToken;
        Content = Build();
        UpdateSummary();
        UpdateDetail(null);
    }

    public void Activate()
    {
        _activated = true;
        UpdateSummary();
        UpdateContext();
    }

    public void Deactivate()
    {
        _activated = false;
        _searchCts?.Cancel();
        _detailCts?.Cancel();
    }

    public void UpdateState(StateSnapshot state)
    {
        string? roomName = state.Room.Name;
        if (string.Equals(_roomName, roomName, StringComparison.Ordinal)) return;
        _roomName = roomName;
        if (_activated) UpdateContext();
    }

    public void NotifyKnowledgeChanged()
    {
        UpdateSummary();
        _refresh.Content = "Refresh •";
    }

    private Control Build()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = UiTheme.Window
        };

        StackPanel header = new() { Spacing = 7, Margin = new Thickness(10, 8, 10, 8) };
        Grid searchRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        _search.PlaceholderText = "Search rooms, MOBs, items, abilities, combat…";
        _search.MinHeight = 30;
        _search.TextChanged += (_, _) => QueueSearch(immediate: false);
        searchRow.Children.Add(_search);

        _summary.Foreground = UiTheme.Faint;
        _summary.FontSize = NexTypography.Metadata;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_summary, 1);
        searchRow.Children.Add(_summary);

        _refresh.Content = "Refresh";
        _refresh.MinHeight = 28;
        _refresh.Padding = new Thickness(8, 2);
        _refresh.Click += (_, _) => QueueSearch(immediate: true, force: true);
        Grid.SetColumn(_refresh, 2);
        searchRow.Children.Add(_refresh);
        header.Children.Add(searchRow);

        StackPanel categories = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
        AddCategory(categories, "Overview", null);
        AddCategory(categories, "Rooms", CodexEntryKind.Room);
        AddCategory(categories, "MOBs", CodexEntryKind.Entity);
        AddCategory(categories, "Items", CodexEntryKind.Item);
        AddCategory(categories, "Abilities", CodexEntryKind.Ability);
        AddCategory(categories, "Combat", CodexEntryKind.Combat);
        header.Children.Add(categories);

        _context.Foreground = UiTheme.Muted;
        _context.FontSize = NexTypography.Metadata;
        _context.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_context);

        _status.Foreground = UiTheme.Faint;
        _status.FontSize = NexTypography.Metadata;
        _status.IsVisible = false;
        header.Children.Add(_status);
        root.Children.Add(header);

        Grid browser = new()
        {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(10, 0, 10, 10)
        };

        _results.ItemsSource = _entries;
        _results.Background = Brushes.Transparent;
        _results.BorderThickness = new Thickness(0);
        _results.ItemTemplate = new FuncDataTemplate<CodexEntrySummary>((entry, _) => BuildResultRow(entry), true);
        _results.SelectionChanged += (_, _) =>
        {
            if (_suppressSelection || _results.SelectedItem is not CodexEntrySummary entry) return;
            HandleEntrySelection(entry);
        };
        browser.Children.Add(new Border
        {
            Background = UiTheme.Console,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(1),
            Child = _results
        });

        ScrollViewer detailScroll = new()
        {
            Content = _detail,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Border detailFrame = new()
        {
            Background = UiTheme.Surface,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(1),
            Child = detailScroll
        };
        Grid.SetColumn(detailFrame, 1);
        browser.Children.Add(detailFrame);
        Grid.SetRow(browser, 1);
        root.Children.Add(browser);
        return root;
    }

    private Control BuildResultRow(CodexEntrySummary? entry)
    {
        if (entry is null) return new TextBlock();
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(7, 6)
        };
        StackPanel identity = new() { Spacing = 1 };
        identity.Children.Add(new TextBlock
        {
            Text = entry.Title,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Body,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        identity.Children.Add(new TextBlock
        {
            Text = entry.Subtitle ?? entry.Kind.ToString(),
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        row.Children.Add(identity);
        TextBlock observations = new()
        {
            Text = $"×{entry.ObservationCount:N0}",
            Foreground = UiTheme.Faint,
            FontSize = NexTypography.Metadata,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(observations, 1);
        row.Children.Add(observations);
        return new Border
        {
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private void AddCategory(StackPanel host, string label, CodexEntryKind? kind)
    {
        Button button = new()
        {
            Content = label,
            Padding = new Thickness(7, 3),
            MinHeight = 26,
            Background = Brushes.Transparent,
            Foreground = kind is null ? UiTheme.Accent : UiTheme.Muted,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, kind is null ? 2 : 0)
        };
        _categoryButtons[CategoryKey(kind)] = button;
        button.Click += (_, _) =>
        {
            if (_category == kind) return;
            _category = kind;
            UpdateCategoryStyles();
            _selected = null;
            _suppressSelection = true;
            _results.SelectedItem = null;
            _suppressSelection = false;
            UpdateDetail(null);
            if (kind is null && string.IsNullOrWhiteSpace(_search.Text))
            {
                ReplaceEntries([]);
                SetStatus("Choose a category to browse, or type at least two characters to search across the Codex.");
                return;
            }
            QueueSearch(immediate: true, force: true);
        };
        host.Children.Add(button);
    }

    private void UpdateCategoryStyles()
    {
        foreach ((string key, Button button) in _categoryButtons)
        {
            bool selected = string.Equals(key, CategoryKey(_category), StringComparison.Ordinal);
            button.Foreground = selected ? UiTheme.Accent : UiTheme.Muted;
            button.Background = selected ? UiTheme.Raised : Brushes.Transparent;
            button.BorderBrush = selected ? UiTheme.Accent : Brushes.Transparent;
            button.BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 0);
        }
    }


    private static string CategoryKey(CodexEntryKind? kind) => kind?.ToString() ?? "Overview";

    private void QueueSearch(bool immediate, bool force = false)
    {
        if (!_activated && !force) return;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
        CancellationToken token = _searchCts.Token;
        string query = _search.Text?.Trim() ?? string.Empty;
        CodexEntryKind? category = _category;

        if (category is null && query.Length < 2)
        {
            ReplaceEntries([]);
            SetStatus(query.Length == 0
                ? "Choose a category to browse, or type at least two characters to search across the Codex."
                : "Type at least two characters to search across all Codex categories.");
            return;
        }

        SetStatus("Searching Codex…");
        _ = SearchAsync(category, query, immediate, token);
    }

    private async Task SearchAsync(CodexEntryKind? category, string query, bool immediate, CancellationToken token)
    {
        try
        {
            if (!immediate) await Task.Delay(180, token).ConfigureAwait(false);
            IReadOnlyList<CodexEntrySummary> results = await Task.Run(
                async () => await _knowledge.SearchCodexAsync(category, query, 72, token).ConfigureAwait(false),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                _refresh.Content = "Refresh";
                ReplaceEntries(results);
                SetStatus(results.Count == 0 ? "No matching Codex entries." : $"{results.Count:N0} entries");
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetStatus($"Codex unavailable: {exception.Message}", error: true));
        }
    }

    private void ReplaceEntries(IReadOnlyList<CodexEntrySummary> results)
    {
        string? selectedKey = _selected?.Key;
        CodexEntryKind? selectedKind = _selected?.Kind;
        _suppressSelection = true;
        _entries.Clear();
        foreach (CodexEntrySummary entry in results) _entries.Add(entry);
        if (selectedKey is not null && selectedKind is not null)
        {
            _results.SelectedItem = _entries.FirstOrDefault(entry =>
                entry.Kind == selectedKind && string.Equals(entry.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
        }
        _suppressSelection = false;
    }

    private void HandleEntrySelection(CodexEntrySummary entry)
    {
        _ = HandleEntrySelectionAsync(entry);
    }

    private async Task HandleEntrySelectionAsync(CodexEntrySummary entry)
    {
        try
        {
            await LoadDetailAsync(entry).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _detail.Content = BuildMessage($"Unable to load entry: {exception.Message}");
        }
    }

    private async Task LoadDetailAsync(CodexEntrySummary entry)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
        CancellationToken token = _detailCts.Token;
        _selected = entry;
        _detail.Content = BuildLoadingDetail(entry.Title);
        try
        {
            CodexEntryDetail? detail = await Task.Run(
                async () => await _knowledge.GetCodexEntryAsync(entry.Kind, entry.Key, token).ConfigureAwait(false),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) UpdateDetail(detail);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _detail.Content = BuildMessage($"Unable to load entry: {exception.Message}"));
        }
    }

    private void UpdateDetail(CodexEntryDetail? detail)
    {
        _detail.Content = detail is null ? BuildOverview() : BuildDetail(detail);
    }

    private Control BuildOverview()
    {
        KnowledgeSummary summary = _knowledge.Summary;
        StackPanel panel = new() { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Codex",
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Persistent knowledge collected from observed play. Browsing is explicit so live game updates never disturb your place.",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(BuildMetricGrid(summary));
        return panel;
    }

    private static Control BuildMetricGrid(KnowledgeSummary summary)
    {
        UniformGrid metrics = new() { Columns = 2, Rows = 3 };
        metrics.Children.Add(Metric("Rooms", summary.Rooms));
        metrics.Children.Add(Metric("Entities", summary.Entities));
        metrics.Children.Add(Metric("Items", summary.Items));
        metrics.Children.Add(Metric("Abilities", summary.AbilityHelpDocuments));
        metrics.Children.Add(Metric("Combat events", summary.CombatEvents));
        metrics.Children.Add(Metric("Sessions", summary.Sessions));
        return metrics;
    }

    private static Control Metric(string label, long value)
    {
        StackPanel panel = new() { Margin = new Thickness(0, 5), Spacing = 1 };
        panel.Children.Add(new TextBlock { Text = value.ToString("N0"), Foreground = UiTheme.Text, FontSize = NexTypography.SectionTitle, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.Faint, FontSize = NexTypography.Metadata });
        return panel;
    }

    private Control BuildDetail(CodexEntryDetail detail)
    {
        StackPanel content = new() { Margin = new Thickness(18), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = detail.Title,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[]
            {
                detail.Subtitle,
                detail.ObservationCount > 0 ? $"observed {detail.ObservationCount:N0}×" : null,
                detail.LastSeenAt is DateTimeOffset lastSeen ? $"last {lastSeen.ToLocalTime():g}" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value))),
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(detail.Description))
        {
            content.Children.Add(new TextBlock
            {
                Text = detail.Description,
                Foreground = UiTheme.Text,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (detail.Fields.Count > 0)
        {
            StackPanel fields = new() { Spacing = 5 };
            foreach ((string key, string value) in detail.Fields.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("140,*"), ColumnSpacing = 10 };
                row.Children.Add(new TextBlock { Text = key, Foreground = UiTheme.Faint, FontSize = NexTypography.Metadata });
                TextBlock valueText = new() { Text = value, Foreground = UiTheme.Text, FontSize = NexTypography.Body, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(valueText, 1);
                row.Children.Add(valueText);
                fields.Children.Add(row);
            }
            content.Children.Add(fields);
        }

        if (detail.RelatedItems.Count > 0)
        {
            content.Children.Add(new TextBlock { Text = "Observed loot", Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata, FontWeight = FontWeight.SemiBold });
            foreach (CodexRelatedItem item in detail.RelatedItems.OrderByDescending(item => item.LastSeenAt).ThenBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase))
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, MinHeight = 28 };
                row.Children.Add(new TextBlock
                {
                    Text = item.ItemName,
                    Foreground = UiTheme.Text,
                    FontSize = NexTypography.Body,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                StackPanel actions = new()
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                actions.Children.Add(new TextBlock
                {
                    Text = $"×{item.ObservationCount:N0}",
                    Foreground = UiTheme.Faint,
                    FontSize = NexTypography.Metadata,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Button open = new() { Content = "Open", Padding = new Thickness(7, 2), MinHeight = 24 };
                open.Click += async (_, _) => await LoadDetailAsync(new CodexEntrySummary(
                    CodexEntryKind.Item, item.ItemName, item.ItemName, "Item", item.ObservationCount, item.LastSeenAt)).ConfigureAwait(true);
                actions.Children.Add(open);
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
                content.Children.Add(row);
            }
        }

        if (detail.Locations.Count > 0)
        {
            bool mobSources = detail.Locations.Any(location => !string.IsNullOrWhiteSpace(location.EntityName));
            content.Children.Add(new TextBlock
            {
                Text = mobSources ? "Known MOB sources" : "Known locations",
                Foreground = UiTheme.Muted,
                FontSize = NexTypography.Metadata,
                FontWeight = FontWeight.SemiBold
            });
            foreach (CodexLocation location in detail.Locations.OrderByDescending(item => item.LastSeenAt).Take(24))
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, MinHeight = 34 };
                StackPanel identity = new() { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                identity.Children.Add(new TextBlock
                {
                    Text = location.EntityName ?? location.RoomName ?? "Observed location",
                    Foreground = UiTheme.Text,
                    FontSize = NexTypography.Body,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                identity.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", new[]
                    {
                        location.Area,
                        location.EntityName is null ? null : location.RoomName,
                        $"×{location.ObservationCount:N0}"
                    }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    Foreground = UiTheme.Faint,
                    FontSize = NexTypography.Metadata
                });
                row.Children.Add(identity);
                Button route = new() { Content = "Route", Padding = new Thickness(7, 2), MinHeight = 26 };
                route.Click += async (_, _) => await _routeToLocation(detail, location).ConfigureAwait(true);
                Grid.SetColumn(route, 1);
                row.Children.Add(route);
                content.Children.Add(row);
            }
        }

        if (detail.Kind == CodexEntryKind.Ability)
        {
            Button help = new() { Content = $"Request help {detail.Title}", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3) };
            help.Click += async (_, _) => await _submitCommand($"help {detail.Title}").ConfigureAwait(true);
            content.Children.Add(help);
        }

        if (!string.IsNullOrWhiteSpace(detail.RawText))
        {
            content.Children.Add(new TextBlock { Text = "Raw reference", Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata, FontWeight = FontWeight.SemiBold });
            content.Children.Add(new TextBox
            {
                Text = detail.RawText,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120,
                MaxHeight = 300,
                FontFamily = UiTheme.Mono,
                FontSize = NexTypography.Monospace,
                Background = UiTheme.Console,
                Foreground = UiTheme.Muted
            });
        }
        return content;
    }

    private static Control BuildLoadingDetail(string title) => BuildMessage($"Loading {title}…");

    private static Control BuildMessage(string message) => new TextBlock
    {
        Text = message,
        Foreground = UiTheme.Muted,
        Margin = new Thickness(18),
        TextWrapping = TextWrapping.Wrap
    };

    private void UpdateSummary()
    {
        KnowledgeSummary summary = _knowledge.Summary;
        _summary.Text = $"{summary.Rooms + summary.Entities + summary.Items + summary.AbilityHelpDocuments:N0} references";
    }

    private void UpdateContext()
    {
        _context.Text = string.IsNullOrWhiteSpace(_roomName)
            ? "Persistent knowledge collected from observed play."
            : $"Browsing persistent knowledge · currently in {_roomName}";
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = error ? UiTheme.Danger : UiTheme.Faint;
        _status.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _detailCts?.Cancel();
        _detailCts?.Dispose();
    }
}

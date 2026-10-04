using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Scripting;

namespace NexMud.Gui.AutomationStudio;

internal sealed partial class AutomationStudioWindow
{
    private const int MaxRenderedSearchResults = 500;

    private sealed record StudioSearchHit(
        string Source,
        string Title,
        string Location,
        string Preview,
        int Rank,
        StudioDocumentKind? DocumentKind = null,
        string? AutomationId = null,
        string? PackageId = null,
        string? RelativePath = null,
        int? Line = null,
        int? Column = null,
        object? Definition = null);

    private readonly ScrollViewer _explorerTreeScroll = new();
    private readonly Button _searchTab = new();
    private readonly Button _searchTabClose = new();
    private readonly TextBox _searchInput = new()
    {
        Background = StudioShellChrome.Input,
        Foreground = StudioShellChrome.Foreground,
        BorderBrush = StudioShellChrome.Border,
        CornerRadius = new CornerRadius(6),
        MinHeight = 44,
        Padding = new Thickness(12, 8),
        FontSize = 16,
        PlaceholderText = "Search automations, workflows, and scripts"
    };
    private readonly Button _searchButton = new();
    private readonly TextBox _searchName = new() { PlaceholderText = "Saved search name" };
    private readonly TextBox _includeTerms = new() { PlaceholderText = "Include terms (comma separated)" };
    private readonly TextBox _excludeTerms = new() { PlaceholderText = "Exclude terms (comma separated)" };
    private readonly CheckBox _scopeAutomations = new() { Content = "Automations" };
    private readonly CheckBox _scopeWorkflows = new() { Content = "Workflows" };
    private readonly CheckBox _scopeScripts = new() { Content = "Scripts" };
    private readonly CheckBox _searchDescriptions = new() { Content = "Search in descriptions" };
    private readonly CheckBox _searchScriptContent = new() { Content = "Search in code / script content" };
    private readonly CheckBox _searchMatchCase = new() { Content = "Match case" };
    private readonly CheckBox _searchWholeWords = new() { Content = "Whole words only" };
    private readonly TextBlock _searchCount = new() { Foreground = StudioShellChrome.Secondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _searchMessage = new() { Foreground = StudioShellChrome.Secondary, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _searchResultList = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly StackPanel _searchScopeChips = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 0, 14, 8) };
    private readonly StackPanel _searchPreview = new() { Spacing = 10, Margin = new Thickness(14) };
    private readonly ContentControl _searchResultsContent = new();
    private readonly ContentControl _searchPreviewContent = new();
    private readonly ScrollViewer _searchPreviewScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private Control? _searchWorkspace;
    private IReadOnlyList<StudioSearchHit> _searchHits = [];
    private IReadOnlyDictionary<string, int> _searchGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    private StudioSearchHit? _selectedSearchHit;
    private CancellationTokenSource? _searchCancellation;
    private long _searchGeneration;
    private ComboBox? _searchSort;
    private bool _searchGridView;
    private bool _suppressSearchOptionEvents;
    private readonly Button _listViewButton = new();
    private readonly Button _gridViewButton = new();

    private void InitializeSearchUi()
    {
        _searchInput.Text = string.Empty;
        _searchInput.TextChanged += (_, _) => UpdateSearchTab();
        _searchInput.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            await RunSearchAsync().ConfigureAwait(true);
        };
        _searchButton.Content = "Search";
        _searchButton.MinWidth = 90;
        _searchButton.MinHeight = 44;
        _searchButton.Background = StudioShellChrome.Selected;
        _searchButton.Foreground = StudioShellChrome.Foreground;
        _searchButton.BorderBrush = StudioShellChrome.Blue;
        _searchButton.Click += async (_, _) => await RunSearchAsync().ConfigureAwait(true);
        _searchName.Text = string.Empty;
        _includeTerms.Text = _preferences.SearchIncludeTerms;
        _excludeTerms.Text = _preferences.SearchExcludeTerms;
        _scopeAutomations.IsChecked = _preferences.SearchAutomations;
        _scopeWorkflows.IsChecked = _preferences.SearchWorkflows;
        _scopeScripts.IsChecked = _preferences.SearchScripts;
        _searchDescriptions.IsChecked = _preferences.SearchDescriptions;
        _searchScriptContent.IsChecked = _preferences.SearchScriptContent;
        _searchMatchCase.IsChecked = _preferences.SearchMatchCase;
        _searchWholeWords.IsChecked = _preferences.SearchWholeWords;

        _searchTab.Content = "Search";
        _searchTab.Margin = new Thickness(4, 4, 0, 0);
        _searchTab.Padding = new Thickness(14, 6);
        _searchTab.Background = Brushes.Transparent;
        _searchTab.Foreground = StudioShellChrome.Secondary;
        _searchTab.BorderThickness = new Thickness(0, 0, 0, 2);
        _searchTab.BorderBrush = Brushes.Transparent;
        _searchTab.Click += async (_, _) => await SetActivityAsync(StudioActivity.Search).ConfigureAwait(true);
        _searchTabClose.Content = "×";
        _searchTabClose.Width = 30;
        _searchTabClose.Background = Brushes.Transparent;
        _searchTabClose.Foreground = StudioShellChrome.Secondary;
        _searchTabClose.BorderThickness = new Thickness(0);
        _searchTabClose.Click += async (_, _) => await CloseSearchTabAsync().ConfigureAwait(true);
        _searchTabClose.IsVisible = _activity == StudioActivity.Search;
        _searchTab.IsVisible = _activity == StudioActivity.Search;

        _suppressSearchOptionEvents = true;
        foreach (CheckBox option in SearchOptions())
        {
            option.Foreground = StudioShellChrome.Secondary;
            option.Margin = new Thickness(0, 2);
            option.IsCheckedChanged += async (_, _) =>
            {
                if (_suppressSearchOptionEvents) return;
                if (string.IsNullOrWhiteSpace(_searchInput.Text)) SaveSearchOptions();
                else await RunSearchAsync().ConfigureAwait(true);
            };
        }
        _suppressSearchOptionEvents = false;
        _includeTerms.TextChanged += async (_, _) =>
        {
            if (!_suppressSearchOptionEvents && !string.IsNullOrWhiteSpace(_searchInput.Text))
                await RunSearchAsync().ConfigureAwait(true);
        };
        _excludeTerms.TextChanged += async (_, _) =>
        {
            if (!_suppressSearchOptionEvents && !string.IsNullOrWhiteSpace(_searchInput.Text))
                await RunSearchAsync().ConfigureAwait(true);
        };
        _includeTerms.LostFocus += (_, _) => { if (!_suppressSearchOptionEvents) SaveSearchOptions(); };
        _excludeTerms.LostFocus += (_, _) => { if (!_suppressSearchOptionEvents) SaveSearchOptions(); };
    }

    private IEnumerable<CheckBox> SearchOptions() =>
    [
        _scopeAutomations, _scopeWorkflows, _scopeScripts, _searchDescriptions,
        _searchScriptContent, _searchMatchCase, _searchWholeWords
    ];

    private void SaveSearchOptions(bool persist = true)
    {
        _preferences.SearchAutomations = _scopeAutomations.IsChecked == true;
        _preferences.SearchWorkflows = _scopeWorkflows.IsChecked == true;
        _preferences.SearchScripts = _scopeScripts.IsChecked == true;
        _preferences.SearchDescriptions = _searchDescriptions.IsChecked == true;
        _preferences.SearchScriptContent = _searchScriptContent.IsChecked == true;
        _preferences.SearchMatchCase = _searchMatchCase.IsChecked == true;
        _preferences.SearchWholeWords = _searchWholeWords.IsChecked == true;
        _preferences.SearchIncludeTerms = _includeTerms.Text ?? string.Empty;
        _preferences.SearchExcludeTerms = _excludeTerms.Text ?? string.Empty;
        if (persist) _preferences.Save();
    }

    private Control BuildSearchSidebar()
    {
        StackPanel sidebar = new() { Spacing = 8, Margin = new Thickness(10, 8, 10, 16) };
        Button newSearch = QuietSearchButton("＋  New Search");
        newSearch.HorizontalContentAlignment = HorizontalAlignment.Center;
        newSearch.MinHeight = 38;
        newSearch.Click += (_, _) => BeginNewSearch();
        sidebar.Children.Add(newSearch);

        AddSearchSection(sidebar, "Recent Searches");
        List<string> recent = _preferences.RecentSearches ?? [];
        if (recent.Count == 0)
            sidebar.Children.Add(SearchSideHint("No recent searches"));
        foreach (string query in recent.Take(10))
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Button load = QuietSearchButton("◷  " + query);
            load.HorizontalContentAlignment = HorizontalAlignment.Left;
            load.Click += async (_, _) => await LoadSearchAsync(query).ConfigureAwait(true);
            row.Children.Add(load);
            Button save = QuietSearchButton("☆");
            save.Width = 34;
            save.Click += (_, _) => SaveSearch(query, query);
            Grid.SetColumn(save, 1);
            row.Children.Add(save);
            sidebar.Children.Add(row);
        }

        AddSearchSection(sidebar, "Saved Searches");
        List<StudioSavedSearch> saved = _preferences.SavedSearches ?? [];
        if (saved.Count == 0)
            sidebar.Children.Add(SearchSideHint("Save a query to keep it here"));
        foreach (StudioSavedSearch item in saved)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Button load = QuietSearchButton("▱  " + item.Name);
            load.HorizontalContentAlignment = HorizontalAlignment.Left;
            load.Click += async (_, _) => await LoadSearchAsync(item).ConfigureAwait(true);
            row.Children.Add(load);
            Button remove = QuietSearchButton("×");
            remove.Width = 34;
            remove.Click += (_, _) => RemoveSavedSearch(item);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            sidebar.Children.Add(row);
        }
        _searchName.Background = StudioShellChrome.Input;
        _searchName.Foreground = StudioShellChrome.Foreground;
        _searchName.BorderBrush = StudioShellChrome.Border;
        _searchName.CornerRadius = new CornerRadius(4);
        _searchName.MinHeight = 30;
        _searchName.Padding = new Thickness(7, 4);
        sidebar.Children.Add(_searchName);
        Button saveCurrent = QuietSearchButton("☆  Save Search");
        saveCurrent.Click += (_, _) =>
        {
            string query = _searchInput.Text?.Trim() ?? string.Empty;
            SaveSearch(string.IsNullOrWhiteSpace(_searchName.Text) ? query : _searchName.Text.Trim(), query);
        };
        sidebar.Children.Add(saveCurrent);

        AddSearchSection(sidebar, "Search Scope");
        sidebar.Children.Add(_scopeAutomations);
        sidebar.Children.Add(_scopeWorkflows);
        sidebar.Children.Add(_scopeScripts);
        AddSearchSection(sidebar, "Search Options");
        sidebar.Children.Add(_searchMatchCase);
        sidebar.Children.Add(_searchWholeWords);
        sidebar.Children.Add(_searchDescriptions);
        sidebar.Children.Add(_searchScriptContent);

        AddSearchSection(sidebar, "Include / Exclude");
        StyleSearchInput(_includeTerms);
        StyleSearchInput(_excludeTerms);
        sidebar.Children.Add(_includeTerms);
        sidebar.Children.Add(_excludeTerms);
        TextBlock filterHelp = SearchSideHint("All include terms must match; excluded terms remove a result.");
        sidebar.Children.Add(filterHelp);
        return sidebar;
    }

    private Control BuildSearchWorkspace()
    {
        Grid workspace = new()
        {
            ColumnDefinitions = new ColumnDefinitions("1.45*,1fr"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Background = StudioShellChrome.Canvas
        };
        Grid searchRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(14, 12, 14, 8) };
        Grid queryFrame = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Background = StudioShellChrome.Input };
        TextBlock icon = new() { Text = "⌕", Foreground = StudioShellChrome.Secondary, FontSize = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
        queryFrame.Children.Add(icon);
        Grid.SetColumn(_searchInput, 1);
        queryFrame.Children.Add(_searchInput);
        searchRow.Children.Add(new Border { Child = queryFrame, BorderBrush = StudioShellChrome.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) });
        Grid.SetColumn(_searchButton, 1);
        searchRow.Children.Add(_searchButton);
        workspace.Children.Add(searchRow);

        Grid.SetRow(_searchScopeChips, 1);
        workspace.Children.Add(_searchScopeChips);
        Grid options = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(14, 0, 14, 8), ColumnSpacing = 8 };
        options.Children.Add(_searchCount);
        _searchSort ??= new ComboBox
        {
            ItemsSource = new[] { "Relevance", "Name" },
            SelectedIndex = 0,
            MinWidth = 120,
            Background = StudioShellChrome.Input,
            Foreground = StudioShellChrome.Foreground
        };
        _searchSort.SelectionChanged += (_, _) => RenderSearchResults();
        Grid.SetColumn(_searchSort, 1);
        options.Children.Add(_searchSort);
        _listViewButton.Content = "☷";
        _listViewButton.Width = 36;
        _listViewButton.Background = _searchGridView ? Brushes.Transparent : StudioShellChrome.Input;
        _listViewButton.Foreground = StudioShellChrome.Foreground;
        _listViewButton.Click += (_, _) => { _searchGridView = false; RenderSearchResults(); };
        ToolTip.SetTip(_listViewButton, "List view");
        Grid.SetColumn(_listViewButton, 2);
        options.Children.Add(_listViewButton);
        _gridViewButton.Content = "▦";
        _gridViewButton.Width = 36;
        _gridViewButton.Background = _searchGridView ? StudioShellChrome.Input : Brushes.Transparent;
        _gridViewButton.Foreground = StudioShellChrome.Foreground;
        _gridViewButton.Click += (_, _) => { _searchGridView = true; RenderSearchResults(); };
        ToolTip.SetTip(_gridViewButton, "Grid view");
        Grid.SetColumn(_gridViewButton, 3);
        options.Children.Add(_gridViewButton);
        Grid.SetRow(options, 2);
        workspace.Children.Add(options);

        Border resultsFrame = SearchPane(_searchResultsContent);
        ScrollViewer resultsScroll = new() { Content = _searchResultList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _searchResultsContent.Content = resultsScroll;
        _searchPreviewScroll.Content = _searchPreview;
        _searchPreviewContent.Content = _searchPreviewScroll;
        Grid.SetRow(resultsFrame, 3);
        workspace.Children.Add(resultsFrame);
        Border previewFrame = SearchPane(_searchPreviewContent);
        Grid.SetColumn(previewFrame, 1);
        Grid.SetRow(previewFrame, 3);
        workspace.Children.Add(previewFrame);
        RenderSearchResults();
        RenderSearchPreview();
        return workspace;
    }

    private async Task CloseSearchTabAsync()
    {
        _searchCancellation?.Cancel();
        Interlocked.Increment(ref _searchGeneration);
        _searchInput.Text = string.Empty;
        _searchHits = [];
        _searchGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        _selectedSearchHit = null;
        _searchCount.Text = "0 results";
        _searchMessage.Text = string.Empty;
        StudioActivity returnActivity = _documents.Active?.Kind == StudioDocumentKind.Workflow
            ? StudioActivity.Workflows
            : _documents.Active?.IsScript == true
                ? StudioActivity.Scripts
                : StudioActivity.Automations;
        await SetActivityAsync(returnActivity).ConfigureAwait(true);
    }

    private void BeginNewSearch()
    {
        _searchInput.Text = string.Empty;
        _searchName.Text = string.Empty;
        _searchHits = [];
        _searchGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        _selectedSearchHit = null;
        _searchCount.Text = "0 results";
        _searchMessage.Text = string.Empty;
        _searchCancellation?.Cancel();
        Interlocked.Increment(ref _searchGeneration);
        UpdateSearchTab();
        RenderSearchActivity();
    }

    private async Task LoadSearchAsync(string query)
    {
        _searchInput.Text = query;
        if (_activity != StudioActivity.Search)
            await SetActivityAsync(StudioActivity.Search).ConfigureAwait(true);
        await RunSearchAsync().ConfigureAwait(true);
    }

    private async Task LoadSearchAsync(StudioSavedSearch saved)
    {
        _suppressSearchOptionEvents = true;
        _scopeAutomations.IsChecked = saved.Automations;
        _scopeWorkflows.IsChecked = saved.Workflows;
        _scopeScripts.IsChecked = saved.Scripts;
        _searchDescriptions.IsChecked = saved.Descriptions;
        _searchScriptContent.IsChecked = saved.ScriptContent;
        _searchMatchCase.IsChecked = saved.MatchCase;
        _searchWholeWords.IsChecked = saved.WholeWords;
        _includeTerms.Text = saved.IncludeTerms;
        _excludeTerms.Text = saved.ExcludeTerms;
        _searchName.Text = saved.Name;
        _suppressSearchOptionEvents = false;
        SaveSearchOptions();
        await LoadSearchAsync(saved.Query).ConfigureAwait(true);
    }

    private void SaveSearch(string name, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchMessage.Text = "Enter a query before saving a search.";
            return;
        }
        name = string.IsNullOrWhiteSpace(name) ? query : name.Trim();
        List<StudioSavedSearch> searches = (_preferences.SavedSearches ?? []).ToList();
        searches.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        searches.Insert(0, new StudioSavedSearch(
            name,
            query.Trim(),
            _scopeAutomations.IsChecked == true,
            _scopeWorkflows.IsChecked == true,
            _scopeScripts.IsChecked == true,
            _searchDescriptions.IsChecked == true,
            _searchScriptContent.IsChecked == true,
            _searchMatchCase.IsChecked == true,
            _searchWholeWords.IsChecked == true,
            _includeTerms.Text ?? string.Empty,
            _excludeTerms.Text ?? string.Empty));
        _preferences.SavedSearches = searches.Take(20).ToList();
        _preferences.Save();
        if (_activity == StudioActivity.Search) RefreshSearchSidebar();
    }

    private void RemoveSavedSearch(StudioSavedSearch item)
    {
        _preferences.SavedSearches = (_preferences.SavedSearches ?? [])
            .Where(saved => saved != item)
            .ToList();
        _preferences.Save();
        RefreshSearchSidebar();
    }

    private void RefreshSearchSidebar()
    {
        _explorerTreeScroll.Content = null;
        _explorerTreeScroll.Content = BuildSearchSidebar();
    }

    private async Task RunSearchAsync()
    {
        string query = _searchInput.Text?.Trim() ?? string.Empty;
        _searchCancellation?.Cancel();
        CancellationTokenSource searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _searchCancellation = searchCancellation;
        CancellationToken token = searchCancellation.Token;
        string profileId = SelectedProfileId;
        long generation = Interlocked.Increment(ref _searchGeneration);
        _searchHits = [];
        _searchGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        _selectedSearchHit = null;
        if (query.Length == 0)
        {
            _searchCount.Text = "0 results";
            _searchMessage.Text = "Enter a word, phrase, or name to search.";
            RenderSearchResults();
            RenderSearchPreview();
            UpdateSearchTab();
            searchCancellation.Dispose();
            if (ReferenceEquals(_searchCancellation, searchCancellation)) _searchCancellation = null;
            return;
        }

        SaveSearchOptions(persist: false);
        List<string> recent = (_preferences.RecentSearches ?? []).ToList();
        bool recentChanged = recent.Count == 0 || !recent[0].Equals(query, StringComparison.OrdinalIgnoreCase);
        recent.RemoveAll(value => value.Equals(query, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, query);
        _preferences.RecentSearches = recent.Take(10).ToList();
        _preferences.Save();
        if (recentChanged) RefreshSearchSidebar();
        _searchMessage.Text = "Searching workspace…";
        RenderSearchResults();
        RenderSearchPreview();
        UpdateSearchTab();
        List<StudioSearchHit> hits = [];
        bool matchCase = _searchMatchCase.IsChecked == true;
        bool wholeWord = _searchWholeWords.IsChecked == true;
        string[] included = StudioSearchMatcher.ParseTerms(_includeTerms.Text);
        string[] excluded = StudioSearchMatcher.ParseTerms(_excludeTerms.Text);
        try
        {
            AutomationCollections collections = AutomationCollections.From(_runtime.Settings);
            foreach (StudioDocumentKind kind in StudioDocumentKinds.AutomationKinds)
            {
                if (kind == StudioDocumentKind.Workflow ? _scopeWorkflows.IsChecked != true : _scopeAutomations.IsChecked != true)
                    continue;
                IReadOnlyList<AutomationEntryInfo> entries = collections.Entries(kind);
                foreach (AutomationEntryInfo entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    object? definition = collections.Get(kind, entry.Index);
                    string details = _searchDescriptions.IsChecked == true && definition is not null
                        ? JsonSerializer.Serialize(definition)
                        : string.Empty;
                    string searchable = $"{entry.Name}\n{details}";
                    if (!StudioSearchMatcher.Contains(searchable, query, matchCase, wholeWord) ||
                        !StudioSearchMatcher.MatchesFilters(searchable, included, excluded, matchCase, wholeWord))
                        continue;
                    string id = _organization.IdFor(kind, entry.Index);
                    hits.Add(new StudioSearchHit(
                        kind == StudioDocumentKind.Workflow ? "Workflow" : "Automation",
                        entry.Name,
                        $"{kind.CategoryLabel()} / {entry.Name}",
                        PreviewText(details, entry.Name),
                        StudioSearchMatcher.Rank(entry.Name, query, matchCase),
                        kind,
                        id,
                        Definition: definition));
                }
            }

            if (_scopeScripts.IsChecked == true)
            {
                foreach (ScriptPackageSnapshot package in _packages)
                {
                    token.ThrowIfCancellationRequested();
                    string packageId = package.Definition.PackageId;
                    string metadata = package.Definition.Name + " " + packageId;
                    if (_searchDescriptions.IsChecked == true)
                        metadata += " " + JsonSerializer.Serialize(package.Definition);
                    if (StudioSearchMatcher.Contains(metadata, query, matchCase, wholeWord) &&
                        StudioSearchMatcher.MatchesFilters(metadata, included, excluded, matchCase, wholeWord))
                    {
                        hits.Add(new StudioSearchHit(
                            "Script",
                            package.Definition.Name,
                            $"Script Packages / {packageId}",
                            package.Definition.Entrypoint,
                            StudioSearchMatcher.Rank(package.Definition.Name, query, matchCase),
                            PackageId: packageId,
                            Definition: package.Definition));
                    }

                    IReadOnlyList<ScriptWorkspaceEntry> entries = await _workspace.ListEntriesAsync(
                        profileId, packageId, token).ConfigureAwait(true);
                    foreach (ScriptWorkspaceEntry entry in entries.Where(entry => !entry.IsDirectory))
                    {
                        token.ThrowIfCancellationRequested();
                        string searchable = entry.RelativePath;
                        if (!StudioSearchMatcher.Contains(searchable, query, matchCase, wholeWord) ||
                            !StudioSearchMatcher.MatchesFilters(searchable, included, excluded, matchCase, wholeWord))
                            continue;
                        hits.Add(new StudioSearchHit(
                            "Script",
                            System.IO.Path.GetFileName(entry.RelativePath),
                            $"{package.Definition.Name} / {entry.RelativePath}",
                            entry.RelativePath,
                            StudioSearchMatcher.Rank(entry.RelativePath, query, matchCase),
                            PackageId: packageId,
                            RelativePath: entry.RelativePath));
                    }
                }

                if (_searchScriptContent.IsChecked == true)
                {
                    IReadOnlyList<ScriptSourceSearchResult> sourceResults = await _workspace.SearchAsync(
                        profileId,
                        query,
                        new ScriptSourceSearchOptions(matchCase, wholeWord, included, excluded),
                        token).ConfigureAwait(true);
                    foreach (ScriptSourceSearchResult source in sourceResults)
                    {
                        token.ThrowIfCancellationRequested();
                        string title = System.IO.Path.GetFileName(source.RelativePath);
                        ScriptPackageSnapshot? package = _packages.FirstOrDefault(item =>
                            item.Definition.PackageId.Equals(source.PackageId, StringComparison.Ordinal));
                        hits.Add(new StudioSearchHit(
                            "Script",
                            title,
                            $"{package?.Definition.Name ?? source.PackageId} / {source.RelativePath}:{source.Line}",
                            source.Preview,
                            StudioSearchMatcher.Rank(title, query, matchCase),
                            PackageId: source.PackageId,
                            RelativePath: source.RelativePath,
                            Line: source.Line,
                            Column: source.Column));
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _searchGeneration) ||
                !profileId.Equals(SelectedProfileId, StringComparison.Ordinal)) return;
            IReadOnlyList<StudioSearchHit> orderedHits = SortSearchHits(hits, matchCase);
            _searchGroupCounts = hits.GroupBy(hit => hit.Source)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            _searchHits = orderedHits.Take(MaxRenderedSearchResults).ToArray();
            _selectedSearchHit = _searchHits.FirstOrDefault();
            _searchMessage.Text = orderedHits.Count == 0
                ? $"No results found for ‘{query}’."
                : orderedHits.Count > MaxRenderedSearchResults
                    ? $"Showing the first {MaxRenderedSearchResults} of {orderedHits.Count} results."
                    : string.Empty;
            _searchCount.Text = $"{_searchHits.Count}{(orderedHits.Count > MaxRenderedSearchResults ? "+" : string.Empty)} results";
            RenderSearchResults();
            RenderSearchPreview();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _searchGeneration) ||
                !profileId.Equals(SelectedProfileId, StringComparison.Ordinal)) return;
            _searchGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            _searchMessage.Text = $"Search failed: {exception.Message}";
            _searchCount.Text = "Search error";
            RenderSearchResults();
            RenderSearchPreview();
        }
        finally
        {
            searchCancellation.Dispose();
            if (ReferenceEquals(_searchCancellation, searchCancellation)) _searchCancellation = null;
        }
    }

    private void InvalidateSearchForProfileChange()
    {
        Interlocked.Increment(ref _searchGeneration);
        _searchCancellation?.Cancel();
        _searchHits = [];
        _searchGroupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        _selectedSearchHit = null;
        _searchCount.Text = "0 results";
        _searchMessage.Text = "Profile changed. Run the search again to refresh results.";
        UpdateSearchTab();
        if (_activity == StudioActivity.Search)
        {
            RenderSearchResults();
            RenderSearchPreview();
        }
    }

    private IReadOnlyList<StudioSearchHit> SortSearchHits(IEnumerable<StudioSearchHit> hits, bool matchCase)
    {
        bool byName = _searchSort?.SelectedItem as string == "Name";
        return byName
            ? hits.OrderBy(hit => hit.Title, matchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).ToArray()
            : hits.OrderBy(hit => hit.Rank)
                .ThenBy(hit => hit.Source, StringComparer.Ordinal)
                .ThenBy(hit => hit.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private void RenderSearchActivity()
    {
        _breadcrumbBar.IsVisible = false;
        ShowMonaco(false);
        _statusLeft.Text = string.IsNullOrWhiteSpace(_searchInput.Text) ? "Search workspace" : $"{_searchHits.Count} search results";
        _statusCursor.Text = string.Empty;
        _searchWorkspace ??= BuildSearchWorkspace();
        _center.Content = _searchWorkspace;
        RenderSearchResults();
        RenderSearchPreview();
        UpdateSearchTab();
    }

    private void UpdateSearchTab()
    {
        string query = _searchInput.Text?.Trim() ?? string.Empty;
        _searchTab.Content = query.Length == 0 ? "⌕ Search" : $"⌕  Search: {query}";
        _searchTab.IsVisible = _activity == StudioActivity.Search || query.Length > 0;
        _searchTabClose.IsVisible = _searchTab.IsVisible;
        _searchTab.Background = _activity == StudioActivity.Search ? StudioShellChrome.Canvas : Brushes.Transparent;
        _searchTab.Foreground = _activity == StudioActivity.Search ? StudioShellChrome.Foreground : StudioShellChrome.Secondary;
        _searchTab.BorderBrush = _activity == StudioActivity.Search ? StudioShellChrome.Blue : Brushes.Transparent;
    }

    private void RenderSearchScopeChips()
    {
        _searchScopeChips.Children.Clear();
        int automationCount = _searchGroupCounts.TryGetValue("Automation", out int automations) ? automations : 0;
        int workflowCount = _searchGroupCounts.TryGetValue("Workflow", out int workflows) ? workflows : 0;
        int scriptCount = _searchGroupCounts.TryGetValue("Script", out int scripts) ? scripts : 0;
        bool allSelected = _scopeAutomations.IsChecked == true && _scopeWorkflows.IsChecked == true && _scopeScripts.IsChecked == true;
        AddSearchScopeChip("All Results", automationCount + workflowCount + scriptCount, allSelected, true, true, true);
        AddSearchScopeChip("Automations", automationCount, _scopeAutomations.IsChecked == true, true, false, false);
        AddSearchScopeChip("Workflows", workflowCount, _scopeWorkflows.IsChecked == true, false, true, false);
        AddSearchScopeChip("Scripts", scriptCount, _scopeScripts.IsChecked == true, false, false, true);
    }

    private void AddSearchScopeChip(string label, int count, bool selected, bool automations, bool workflows, bool scripts)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new Border
        {
            Background = selected ? StudioShellChrome.Blue : StudioShellChrome.Input,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 1),
            Child = new TextBlock { Text = count.ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
        });
        Button chip = new()
        {
            Content = content,
            Background = selected ? StudioShellChrome.Selected : StudioShellChrome.Input,
            Foreground = StudioShellChrome.Foreground,
            BorderBrush = selected ? StudioShellChrome.Blue : StudioShellChrome.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 5)
        };
        chip.Click += async (_, _) => await SetSearchScopesAsync(automations, workflows, scripts).ConfigureAwait(true);
        _searchScopeChips.Children.Add(chip);
    }

    private async Task SetSearchScopesAsync(bool automations, bool workflows, bool scripts)
    {
        _suppressSearchOptionEvents = true;
        _scopeAutomations.IsChecked = automations;
        _scopeWorkflows.IsChecked = workflows;
        _scopeScripts.IsChecked = scripts;
        _suppressSearchOptionEvents = false;
        if (string.IsNullOrWhiteSpace(_searchInput.Text)) SaveSearchOptions();
        else await RunSearchAsync().ConfigureAwait(true);
    }

    private void RenderSearchResults()
    {
        RenderSearchScopeChips();
        _listViewButton.Background = _searchGridView ? Brushes.Transparent : StudioShellChrome.Input;
        _gridViewButton.Background = _searchGridView ? StudioShellChrome.Input : Brushes.Transparent;
        _searchResultList.Children.Clear();
        _searchMessage.Margin = new Thickness(12, 8);
        _searchResultList.Children.Add(_searchMessage);
        if (_searchHits.Count == 0) return;

        IReadOnlyList<StudioSearchHit> visibleHits = SortSearchHits(_searchHits, _searchMatchCase.IsChecked == true);
        foreach (IGrouping<string, StudioSearchHit> group in visibleHits.GroupBy(hit => hit.Source)
                     .OrderBy(group => group.Key switch { "Automation" => 0, "Workflow" => 1, _ => 2 }))
        {
            Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 10, 0, 2) };
            TextBlock icon = new() { Text = group.Key switch { "Automation" => "ϟ", "Workflow" => "♧", _ => "</>" }, Foreground = StudioShellChrome.Blue, FontSize = 22, Margin = new Thickness(4, 0, 10, 0) };
            heading.Children.Add(icon);
            StackPanel labels = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = group.Key == "Automation" ? "Automations" : group.Key == "Workflow" ? "Workflows" : "Script Packages", Foreground = StudioShellChrome.Foreground, FontSize = 16, FontWeight = FontWeight.SemiBold });
            int groupCount = _searchGroupCounts.TryGetValue(group.Key, out int totalGroupCount) ? totalGroupCount : group.Count();
            labels.Children.Add(new TextBlock { Text = $"{groupCount} results", Foreground = StudioShellChrome.Secondary, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(labels, 1);
            heading.Children.Add(labels);
            Button seeAll = QuietSearchButton("See all  →");
            seeAll.Click += async (_, _) =>
            {
                _suppressSearchOptionEvents = true;
                _scopeAutomations.IsChecked = group.Key == "Automation";
                _scopeWorkflows.IsChecked = group.Key == "Workflow";
                _scopeScripts.IsChecked = group.Key == "Script";
                _suppressSearchOptionEvents = false;
                await RunSearchAsync().ConfigureAwait(true);
            };
            Grid.SetColumn(seeAll, 2);
            heading.Children.Add(seeAll);
            _searchResultList.Children.Add(heading);
            if (_searchGridView)
            {
                Avalonia.Controls.Primitives.UniformGrid grid = new() { Columns = 2, Rows = (group.Count() + 1) / 2, Margin = new Thickness(0, 0, 0, 8) };
                foreach (StudioSearchHit hit in group) grid.Children.Add(BuildSearchResultCard(hit));
                _searchResultList.Children.Add(grid);
            }
            else
            {
                foreach (StudioSearchHit hit in group)
                    _searchResultList.Children.Add(BuildSearchResultCard(hit));
            }
        }
    }

    private Control BuildSearchResultCard(StudioSearchHit hit)
    {
        bool selected = ReferenceEquals(hit, _selectedSearchHit);
        StackPanel content = new() { Spacing = 4, Margin = new Thickness(12, 9) };
        Grid titleRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(new TextBlock { Text = hit.Title, Foreground = StudioShellChrome.Foreground, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        TextBlock badge = new() { Text = hit.Source, Foreground = StudioShellChrome.Secondary, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(badge, 1);
        titleRow.Children.Add(badge);
        content.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(hit.Preview))
            content.Children.Add(new TextBlock { Text = hit.Preview, Foreground = StudioShellChrome.Secondary, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2 });
        content.Children.Add(new TextBlock { Text = hit.Location, Foreground = StudioShellChrome.Secondary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        Button card = new() { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = selected ? StudioShellChrome.Input : StudioShellChrome.Card, Foreground = StudioShellChrome.Foreground, BorderBrush = selected ? StudioShellChrome.Blue : StudioShellChrome.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(0) };
        card.Click += (_, _) =>
        {
            _selectedSearchHit = hit;
            RenderSearchResults();
            RenderSearchPreview();
        };
        return card;
    }

    private void RenderSearchPreview()
    {
        _searchPreview.Children.Clear();
        if (_selectedSearchHit is not { } hit)
        {
            _searchPreview.Children.Add(new TextBlock { Text = "Select a result to preview", Foreground = StudioShellChrome.Secondary, Margin = new Thickness(4, 8) });
            return;
        }

        Grid title = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        StackPanel heading = new() { Spacing = 3 };
        heading.Children.Add(new TextBlock { Text = hit.Title, Foreground = StudioShellChrome.Foreground, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = hit.Location, Foreground = StudioShellChrome.Secondary, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        title.Children.Add(heading);
        Button open = QuietSearchButton("▶  Open");
        open.Background = StudioShellChrome.Selected;
        open.Click += async (_, _) => await OpenSearchHitAsync(hit).ConfigureAwait(true);
        Grid.SetColumn(open, 1);
        title.Children.Add(open);
        _searchPreview.Children.Add(title);
        _searchPreview.Children.Add(new Border { Height = 1, Background = StudioShellChrome.Border, Margin = new Thickness(0, 4) });
        _searchPreview.Children.Add(new TextBlock { Text = "Overview", Foreground = StudioShellChrome.Blue, FontWeight = FontWeight.SemiBold });
        if (hit.Line is { } line)
        {
            _searchPreview.Children.Add(new TextBlock { Text = $"Line {line}, column {hit.Column ?? 1}", Foreground = StudioShellChrome.Secondary });
            _searchPreview.Children.Add(new Border
            {
                Background = StudioShellChrome.Input,
                BorderBrush = StudioShellChrome.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Child = new TextBlock { Text = hit.Preview, Foreground = StudioShellChrome.Foreground, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap }
            });
        }
        else
        {
            _searchPreview.Children.Add(new TextBlock { Text = hit.Source, Foreground = StudioShellChrome.Secondary });
            _searchPreview.Children.Add(new TextBlock { Text = hit.Preview, Foreground = StudioShellChrome.Foreground, TextWrapping = TextWrapping.Wrap });
            if (hit.Definition is not null)
            {
                _searchPreview.Children.Add(new TextBlock { Text = "Definition", Foreground = StudioShellChrome.Secondary, Margin = new Thickness(0, 8, 0, 0) });
                _searchPreview.Children.Add(new Border
                {
                    Background = StudioShellChrome.Input,
                    BorderBrush = StudioShellChrome.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10),
                    Child = new TextBlock { Text = JsonSerializer.Serialize(hit.Definition, new JsonSerializerOptions { WriteIndented = true }), Foreground = StudioShellChrome.Secondary, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap }
                });
            }
        }
        _searchPreviewScroll.Content = _searchPreview;
    }

    private async Task OpenSearchHitAsync(StudioSearchHit hit)
    {
        if (hit.DocumentKind is { } kind && hit.AutomationId is { } automationId)
        {
            await SetActivityAsync(kind == StudioDocumentKind.Workflow ? StudioActivity.Workflows : StudioActivity.Automations).ConfigureAwait(true);
            OpenAutomation(kind, automationId);
            return;
        }
        if (hit.PackageId is { } packageId)
        {
            await SetActivityAsync(StudioActivity.Scripts).ConfigureAwait(true);
            await SelectPackageAsync(packageId).ConfigureAwait(true);
            if (hit.RelativePath is { } relativePath)
            {
                await OpenSourceAsync(packageId, relativePath).ConfigureAwait(true);
                if (hit.Line is { } line)
                    await _monaco.RevealLocationAsync(SourceDocumentUri(packageId, relativePath), line, hit.Column, _cts.Token).ConfigureAwait(true);
            }
        }
    }

    private static string PreviewText(string value, string fallback)
    {
        string text = string.IsNullOrWhiteSpace(value) ? fallback : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 180 ? text : text[..177] + "…";
    }

    private static void AddSearchSection(Panel parent, string title)
    {
        parent.Children.Add(new Border { Height = 1, Background = StudioShellChrome.Border, Margin = new Thickness(0, 8, 0, 2) });
        parent.Children.Add(new TextBlock { Text = title, Foreground = StudioShellChrome.Foreground, FontWeight = FontWeight.SemiBold, FontSize = 13, Margin = new Thickness(2, 4) });
    }

    private static TextBlock SearchSideHint(string text) => new()
    {
        Text = text,
        Foreground = StudioShellChrome.Secondary,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 2)
    };

    private static Button QuietSearchButton(string text) => new()
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Background = Brushes.Transparent,
        Foreground = StudioShellChrome.Secondary,
        BorderBrush = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(5, 4),
        FontSize = 12
    };

    private static void StyleSearchInput(TextBox input)
    {
        input.Background = StudioShellChrome.Input;
        input.Foreground = StudioShellChrome.Foreground;
        input.BorderBrush = StudioShellChrome.Border;
        input.CornerRadius = new CornerRadius(4);
        input.MinHeight = 30;
        input.Padding = new Thickness(7, 4);
        input.FontSize = 11;
    }

    private static Border SearchPane(Control content) => new()
    {
        Child = content,
        Background = StudioShellChrome.Canvas,
        BorderBrush = StudioShellChrome.Border,
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 1, 0)
    };
}

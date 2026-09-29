using System.Diagnostics;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Paths;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;

namespace NexMud.Gui;

/// <summary>
/// Low-frequency client preferences only. Executable behavior belongs to Automation; map policy,
/// Jev policy, and scripting are owned by their respective workspaces.
/// </summary>
public sealed class SettingsWorkspace : UserControl
{
    private enum SettingsPage
    {
        General,
        Appearance,
        Transcript,
        Input,
        Connections,
        DataLogging,
        Advanced
    }

    private readonly NexMudRuntime _runtime;
    private readonly Action _cancel;
    private readonly Action _saved;
    private readonly ContentControl _pageContent = new();
    private readonly TextBlock _validation = new();
    private readonly Dictionary<SettingsPage, Button> _navButtons = [];
    private readonly Dictionary<SettingsPage, Control> _pages = [];

    private readonly CheckBox _restoreWorkspace = new();
    private readonly CheckBox _reconnectLastProfile = new();
    private readonly CheckBox _showGameplayRail = new();

    private readonly ComboBox _theme = new();
    private readonly ComboBox _accent = new();
    private readonly ComboBox _uiScale = new();
    private readonly ComboBox _density = new();
    private readonly TextBox _interfaceFont = Field();
    private readonly TextBox _transcriptFont = Field();
    private readonly ComboBox _transcriptSize = new();
    private readonly ComboBox _transcriptLineSpacing = new();

    private readonly CheckBox _slurpTelemetryPrompt = new();
    private readonly ComboBox _timestampMode = new();
    private readonly CheckBox _wrapLongLines = new();
    private readonly CheckBox _localEcho = new();
    private readonly CheckBox _showCommandProvenance = new();
    private readonly TextBox _scrollbackMaximum = Field();
    private readonly CheckBox _splitOutputEnabled = new();
    private readonly CheckBox _notifyWhenUnfocused = new();
    private readonly OutputRulesEditor _outputRules;

    private readonly TextBox _commandSeparator = Field();
    private readonly CheckBox _commandBatching = new();
    private readonly TextBox _historyMaximum = Field();
    private readonly CheckBox _persistHistory = new();
    private readonly CheckBox _deduplicateHistory = new();
    private readonly CheckBox _completionEnabled = new();
    private readonly TextBox _completionTokenLimit = Field();

    private readonly ConnectionProfilesEditor _connectionProfiles;

    private readonly CheckBox _autoLogSessions = new();
    private readonly ComboBox _logFormat = new();
    private readonly TextBlock _storageStatus = new();

    public SettingsWorkspace(NexMudRuntime runtime, Action cancel, Action saved)
    {
        _runtime = runtime;
        _cancel = cancel;
        _saved = saved;
        _connectionProfiles = new ConnectionProfilesEditor(runtime);
        _outputRules = new OutputRulesEditor(runtime.Settings.HighlightRules, runtime.Settings.OutputRules);

        MinWidth = 820;
        MinHeight = 600;
        Focusable = true;
        Background = UiTheme.Window;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;

        _pages[SettingsPage.General] = BuildGeneralPage();
        _pages[SettingsPage.Appearance] = BuildAppearancePage();
        _pages[SettingsPage.Transcript] = BuildTranscriptPage();
        _pages[SettingsPage.Input] = BuildInputPage();
        _pages[SettingsPage.Connections] = BuildConnectionsPage();
        _pages[SettingsPage.DataLogging] = BuildDataLoggingPage();
        _pages[SettingsPage.Advanced] = BuildAdvancedPage();

        Content = BuildLayout();
        LoadSettings();
        SelectPage(SettingsPage.General);
    }

    public bool Saved { get; private set; }

    private Control BuildLayout()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("210,*"),
            Background = UiTheme.Window
        };

        Border sidebar = new()
        {
            Background = UiTheme.Surface,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 14)
        };
        StackPanel nav = new() { Spacing = 2 };
        nav.Children.Add(new TextBlock
        {
            Text = "Settings",
            Foreground = UiTheme.Text,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 0, 8, 10)
        });
        nav.Children.Add(NavigationButton(SettingsPage.General, "General", "Startup and gameplay defaults"));
        nav.Children.Add(NavigationButton(SettingsPage.Appearance, "Appearance", "Theme, accent, typography and density"));
        nav.Children.Add(NavigationButton(SettingsPage.Transcript, "Transcript", "Presentation, scrollback and output rules"));
        nav.Children.Add(NavigationButton(SettingsPage.Input, "Input", "Command entry, history and completion"));
        nav.Children.Add(NavigationButton(SettingsPage.Connections, "Connections", "Profile library and per-profile protocols"));
        nav.Children.Add(NavigationButton(SettingsPage.DataLogging, "Data & Logging", "Session logs and persistent knowledge"));
        nav.Children.Add(NavigationButton(SettingsPage.Advanced, "Advanced", "Diagnostics and rare client controls"));
        sidebar.Child = nav;
        root.Children.Add(sidebar);

        _pageContent.Margin = new Thickness(22, 16, 22, 12);
        Grid.SetColumn(_pageContent, 1);
        root.Children.Add(_pageContent);

        Border footer = new()
        {
            Background = UiTheme.Surface,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 12)
        };
        Grid footerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _validation.Foreground = UiTheme.Danger;
        _validation.VerticalAlignment = VerticalAlignment.Center;
        _validation.TextWrapping = TextWrapping.Wrap;
        footerGrid.Children.Add(_validation);

        Button cancel = SecondaryButton("Cancel");
        cancel.MinWidth = 92;
        cancel.Click += (_, _) => _cancel();
        Grid.SetColumn(cancel, 1);
        footerGrid.Children.Add(cancel);

        Button save = PrimaryButton("Save Changes");
        save.MinWidth = 130;
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        Grid.SetColumn(save, 2);
        footerGrid.Children.Add(save);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private Control BuildGeneralPage()
    {
        StackPanel stack = PageStack(
            "General",
            "Application-wide lifecycle and default gameplay behavior only.");

        Border startup = SectionCard("Startup", "Workspace restoration and connection behavior when NexMUD opens.");
        StackPanel startupBody = (StackPanel)startup.Child!;
        ConfigureCheckBox(_restoreWorkspace, "Restore previous workspace");
        ConfigureCheckBox(_reconnectLastProfile, "Reconnect last connection profile");
        startupBody.Children.Add(_restoreWorkspace);
        startupBody.Children.Add(_reconnectLastProfile);
        stack.Children.Add(startup);

        Border gameplay = SectionCard("Gameplay", "Default shell behavior. Tool-specific policy belongs in the owning workspace.");
        StackPanel gameplayBody = (StackPanel)gameplay.Child!;
        ConfigureCheckBox(_showGameplayRail, "Show gameplay rail by default");
        gameplayBody.Children.Add(_showGameplayRail);
        stack.Children.Add(gameplay);
        return PageScroll(stack);
    }

    private Control BuildAppearancePage()
    {
        StackPanel stack = PageStack(
            "Appearance",
            "Visual presentation and typography. Transcript geometry remains independent from semantic parsing.");

        Border visual = SectionCard("Interface", "Theme, accent, scale, and density are client presentation preferences.");
        StackPanel visualBody = (StackPanel)visual.Child!;
        _theme.ItemsSource = new[] { "Dark" };
        _theme.MinWidth = 160;
        _accent.ItemsSource = Enum.GetNames<NexAccentTheme>();
        _accent.MinWidth = 180;
        _uiScale.ItemsSource = new double[] { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5 };
        _uiScale.MinWidth = 120;
        _density.ItemsSource = Enum.GetValues<ClientDensity>();
        _density.MinWidth = 150;
        visualBody.Children.Add(FormRow("Theme", _theme));
        visualBody.Children.Add(FormRow("Accent palette", _accent));
        visualBody.Children.Add(FormRow("UI scale", _uiScale));
        visualBody.Children.Add(FormRow("Density", _density));
        stack.Children.Add(visual);

        Border typography = SectionCard("Typography", "Interface and transcript typography are separate roles.");
        StackPanel typographyBody = (StackPanel)typography.Child!;
        _interfaceFont.PlaceholderText = "Inter, SF Pro Text, Helvetica Neue, sans-serif";
        _transcriptFont.PlaceholderText = "Menlo, SFMono-Regular, Consolas, monospace";
        _transcriptSize.ItemsSource = new double[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24 };
        _transcriptSize.MinWidth = 120;
        _transcriptLineSpacing.ItemsSource = new double[] { 1.0, 1.08, 8d / 7d, 1.2, 1.3, 1.4 };
        _transcriptLineSpacing.MinWidth = 120;
        typographyBody.Children.Add(FormRow("Interface font", _interfaceFont));
        typographyBody.Children.Add(FormRow("Transcript font", _transcriptFont));
        typographyBody.Children.Add(FormRow("Transcript size", _transcriptSize));
        typographyBody.Children.Add(FormRow("Transcript line spacing", _transcriptLineSpacing));
        stack.Children.Add(typography);
        return PageScroll(stack);
    }

    private Control BuildTranscriptPage()
    {
        StackPanel stack = PageStack(
            "Transcript",
            "Visible World transcript behavior. Output rules are presentation-only and never mutate semantic source, replay, or raw logs.");

        Border presentation = SectionCard("Presentation", "How decoded World output is presented to the player.");
        StackPanel presentationBody = (StackPanel)presentation.Child!;
        ConfigureCheckBox(_slurpTelemetryPrompt, "Prompt slurp - hide Avendar telemetry prompts from the visible transcript");
        ConfigureCheckBox(_wrapLongLines, "Wrap long lines");
        ConfigureCheckBox(_localEcho, "Show command echo");
        ConfigureCheckBox(_showCommandProvenance, "Show command provenance");
        _timestampMode.ItemsSource = Enum.GetValues<TimestampRenderMode>();
        _timestampMode.MinWidth = 190;
        presentationBody.Children.Add(_slurpTelemetryPrompt);
        presentationBody.Children.Add(FormRow("Timestamps", _timestampMode));
        presentationBody.Children.Add(_wrapLongLines);
        presentationBody.Children.Add(_localEcho);
        presentationBody.Children.Add(_showCommandProvenance);
        stack.Children.Add(presentation);

        Border scrollback = SectionCard("Scrollback", "Logical transcript history and split history/live behavior.");
        StackPanel scrollbackBody = (StackPanel)scrollback.Child!;
        _scrollbackMaximum.Width = 110;
        scrollbackBody.Children.Add(FormRow("Scrollback entries", _scrollbackMaximum));
        ConfigureCheckBox(_splitOutputEnabled, "Enable split history/live view when scrolling away from the bottom");
        scrollbackBody.Children.Add(_splitOutputEnabled);
        stack.Children.Add(scrollback);

        Border rules = SectionCard("Output Rules", "Focused transcript tools: highlights and display-only transformations.");
        StackPanel rulesBody = (StackPanel)rules.Child!;
        rulesBody.Children.Add(_outputRules);
        stack.Children.Add(rules);

        Border attention = SectionCard("Attention", "Presentation rules may request attention without becoming gameplay semantics.");
        StackPanel attentionBody = (StackPanel)attention.Child!;
        ConfigureCheckBox(_notifyWhenUnfocused, "Allow output rules to request attention");
        attentionBody.Children.Add(_notifyWhenUnfocused);
        stack.Children.Add(attention);
        return PageScroll(stack);
    }

    private Control BuildInputPage()
    {
        StackPanel stack = PageStack(
            "Input",
            "Command-entry mechanics only. Keybindings are executable behavior and live in Automation.");

        Border commands = SectionCard("Commands", "Ordered command batching is explicit and uses the configured separator.");
        StackPanel commandsBody = (StackPanel)commands.Child!;
        _commandSeparator.Width = 72;
        _commandSeparator.MaxLength = 1;
        _commandSeparator.PlaceholderText = "Off";
        ConfigureCheckBox(_commandBatching, "Enable command batching");
        commandsBody.Children.Add(_commandBatching);
        commandsBody.Children.Add(FormRow("Command separator", _commandSeparator));
        commandsBody.Children.Add(new TextBlock
        {
            Text = "Default: ;  Example: n;n;n;w;open chest. Prefix the separator with \\ to send it literally.",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(commands);

        Border history = SectionCard("History", "Manual command history is separate from server-side command buffering.");
        StackPanel historyBody = (StackPanel)history.Child!;
        _historyMaximum.Width = 110;
        historyBody.Children.Add(FormRow("History entries", _historyMaximum));
        ConfigureCheckBox(_persistHistory, "Persist manual history");
        ConfigureCheckBox(_deduplicateHistory, "Collapse consecutive duplicate entries");
        historyBody.Children.Add(_persistHistory);
        historyBody.Children.Add(_deduplicateHistory);
        stack.Children.Add(history);

        Border completion = SectionCard("Completion", "Tab and Shift-Tab completion over the transcript/input token index.");
        StackPanel completionBody = (StackPanel)completion.Child!;
        _completionTokenLimit.Width = 110;
        completionBody.Children.Add(FormRow("Completion token index", _completionTokenLimit));
        ConfigureCheckBox(_completionEnabled, "Enable Tab / Shift-Tab completion");
        completionBody.Children.Add(_completionEnabled);
        stack.Children.Add(completion);
        return PageScroll(stack);
    }

    private Control BuildConnectionsPage()
    {
        StackPanel stack = PageStack(
            "Connections",
            "Connection configuration is profile-scoped. Each profile owns server, terminal identity, and protocol overrides.");
        stack.Children.Add(_connectionProfiles);
        return PageScroll(stack);
    }

    private Control BuildDataLoggingPage()
    {
        StackPanel stack = PageStack(
            "Data & Logging",
            "Session logs and persistent local knowledge. Logging never injects client annotations into raw source data.");

        Border logging = SectionCard("Session Logging", "Start policy, format, and log location.");
        StackPanel loggingBody = (StackPanel)logging.Child!;
        ConfigureCheckBox(_autoLogSessions, "Start automatically");
        _logFormat.ItemsSource = Enum.GetValues<TranscriptLogFormat>();
        _logFormat.MinWidth = 170;
        loggingBody.Children.Add(_autoLogSessions);
        loggingBody.Children.Add(FormRow("Format", _logFormat));
        loggingBody.Children.Add(FormRow("Location", ReadOnlyPath(GetLogDirectory())));
        stack.Children.Add(logging);

        Border knowledge = SectionCard("Persistent Knowledge", "Codex and map knowledge share the local SQLite store.");
        StackPanel knowledgeBody = (StackPanel)knowledge.Child!;
        var summary = _runtime.Knowledge.Summary;
        knowledgeBody.Children.Add(FormRow(
            "Codex",
            ReadOnlyPath($"{summary.Entities:N0} entities · {summary.Items:N0} items · {WorldKnowledgePath()}")));
        knowledgeBody.Children.Add(FormRow(
            "Map",
            ReadOnlyPath($"{summary.Rooms:N0} rooms · {WorldKnowledgePath()}")));
        stack.Children.Add(knowledge);

        Border storage = SectionCard("Storage", "Open NexMUD's data folder or create a portable snapshot in Downloads.");
        StackPanel storageBody = (StackPanel)storage.Child!;
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 7 };
        Button open = SecondaryButton("Open Data Folder");
        open.Click += (_, _) => OpenDataFolder();
        Button export = SecondaryButton("Export");
        export.Click += async (_, _) => await ExportDataAsync().ConfigureAwait(true);
        actions.Children.Add(open);
        actions.Children.Add(export);
        storageBody.Children.Add(actions);
        _storageStatus.Foreground = UiTheme.Muted;
        _storageStatus.FontSize = NexTypography.Metadata;
        _storageStatus.TextWrapping = TextWrapping.Wrap;
        storageBody.Children.Add(_storageStatus);
        stack.Children.Add(storage);
        return PageScroll(stack);
    }

    private Control BuildAdvancedPage()
    {
        StackPanel stack = PageStack(
            "Advanced",
            "Rare global client controls and diagnostics. Domain-specific settings do not belong here.");

        Border diagnostics = SectionCard("Diagnostics", "Useful paths and runtime identity for troubleshooting.");
        StackPanel body = (StackPanel)diagnostics.Child!;
        body.Children.Add(FormRow("Settings", ReadOnlyPath(_runtime.SettingsStore.Path)));
        body.Children.Add(FormRow("Knowledge DB", ReadOnlyPath(WorldKnowledgePath())));
        body.Children.Add(FormRow("Client version", ReadOnlyPath("0.30.0")));
        stack.Children.Add(diagnostics);

        Border experimental = SectionCard("Experimental features", "No experimental global features are enabled in this release.");
        ((StackPanel)experimental.Child!).Children.Add(new TextBlock
        {
            Text = "Jev provider policy, Mapper preferences, Automation behavior, and Scripting diagnostics remain in their owning workspaces.",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(experimental);
        return PageScroll(stack);
    }

    private void LoadSettings()
    {
        ClientSettings settings = _runtime.Settings;
        GeneralPreferences general = settings.General ?? new GeneralPreferences(ShowGameplayRailByDefault: settings.ShowContextDock);
        _restoreWorkspace.IsChecked = general.RestorePreviousWorkspace;
        _reconnectLastProfile.IsChecked = general.ReconnectLastConnectionProfile;
        _showGameplayRail.IsChecked = general.ShowGameplayRailByDefault;

        AppearancePreferences appearance = settings.Appearance ?? new AppearancePreferences(TranscriptSize: settings.TranscriptFontSize);
        _theme.SelectedItem = appearance.Theme;
        _accent.SelectedItem = Enum.GetNames<NexAccentTheme>().Contains(appearance.AccentPalette, StringComparer.Ordinal)
            ? appearance.AccentPalette
            : nameof(NexAccentTheme.EmberBrass);
        _uiScale.SelectedItem = Nearest(new double[] { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5 }, appearance.UiScale);
        _density.SelectedItem = appearance.Density;
        _interfaceFont.Text = appearance.InterfaceFont;
        _transcriptFont.Text = appearance.TranscriptFont;
        _transcriptSize.SelectedItem = Nearest(new double[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24 }, appearance.TranscriptSize);
        _transcriptLineSpacing.SelectedItem = Nearest(new double[] { 1.0, 1.08, 8d / 7d, 1.2, 1.3, 1.4 }, appearance.TranscriptLineSpacing);

        _slurpTelemetryPrompt.IsChecked = settings.SlurpTelemetryPrompt;
        InputPreferences input = settings.Input ?? new InputPreferences();
        OutputPreferences output = settings.Output ?? new OutputPreferences();
        _timestampMode.SelectedItem = output.TimestampMode;
        _wrapLongLines.IsChecked = output.WrapLongLines;
        _localEcho.IsChecked = output.ShowCommandEcho && input.LocalEcho;
        _showCommandProvenance.IsChecked = output.ShowCommandProvenance;
        _scrollbackMaximum.Text = output.ScrollbackMaximumEntries.ToString();
        _splitOutputEnabled.IsChecked = output.SplitOutputEnabled;
        _notifyWhenUnfocused.IsChecked = output.NotifyWhenUnfocused;

        _commandSeparator.Text = settings.CommandSeparator;
        _commandBatching.IsChecked = input.CommandBatchingEnabled;
        _historyMaximum.Text = input.HistoryMaximumEntries.ToString();
        _persistHistory.IsChecked = input.PersistHistory;
        _deduplicateHistory.IsChecked = input.DeduplicateConsecutiveHistory;
        _completionEnabled.IsChecked = input.CompletionEnabled;
        _completionTokenLimit.Text = input.CompletionTokenLimit.ToString();

        _autoLogSessions.IsChecked = settings.AutoLogSessions;
        _logFormat.SelectedItem = settings.LogFormat;
    }

    private async Task SaveAsync()
    {
        _validation.Text = string.Empty;

        string separator = _commandSeparator.Text ?? string.Empty;
        if (separator.Length > 1 || separator.Any(char.IsWhiteSpace))
        {
            Fail(SettingsPage.Input, "Command separator must be blank or one non-whitespace character.");
            return;
        }
        if (!int.TryParse(_historyMaximum.Text, out int historyMaximum) || historyMaximum is < 1 or > 10_000)
        {
            Fail(SettingsPage.Input, "History entries must be between 1 and 10,000.");
            return;
        }
        if (!int.TryParse(_completionTokenLimit.Text, out int completionLimit) || completionLimit is < 100 or > 50_000)
        {
            Fail(SettingsPage.Input, "Completion token index must be between 100 and 50,000 entries.");
            return;
        }
        if (!int.TryParse(_scrollbackMaximum.Text, out int scrollbackMaximum) || scrollbackMaximum is < 250 or > 100_000)
        {
            Fail(SettingsPage.Transcript, "Scrollback entries must be between 250 and 100,000.");
            return;
        }
        if (_transcriptSize.SelectedItem is not double transcriptSize ||
            _transcriptLineSpacing.SelectedItem is not double lineSpacing ||
            _uiScale.SelectedItem is not double uiScale ||
            _density.SelectedItem is not ClientDensity density)
        {
            Fail(SettingsPage.Appearance, "Choose valid appearance values.");
            return;
        }
        string interfaceFont = (_interfaceFont.Text ?? string.Empty).Trim();
        string transcriptFont = (_transcriptFont.Text ?? string.Empty).Trim();
        if (interfaceFont.Length == 0 || transcriptFont.Length == 0)
        {
            Fail(SettingsPage.Appearance, "Interface and transcript fonts are required.");
            return;
        }
        if (!_outputRules.TryRead(out IReadOnlyList<TranscriptHighlightRule> highlights,
                out IReadOnlyList<OutputTransformationRule> transformations,
                out string? outputValidation))
        {
            Fail(SettingsPage.Transcript, outputValidation ?? "Output rules are invalid.");
            return;
        }
        if (!_connectionProfiles.TryReadProfiles(out IReadOnlyList<ConnectionProfile> profiles, out string? connectionValidation))
        {
            Fail(SettingsPage.Connections, connectionValidation ?? "Connection profiles are invalid.");
            return;
        }

        GeneralPreferences general = new(
            _restoreWorkspace.IsChecked == true,
            _reconnectLastProfile.IsChecked == true,
            _showGameplayRail.IsChecked == true);
        AppearancePreferences appearance = new(
            _theme.SelectedItem as string ?? "Dark",
            _accent.SelectedItem as string ?? nameof(NexAccentTheme.EmberBrass),
            uiScale,
            density,
            interfaceFont,
            transcriptFont,
            transcriptSize,
            lineSpacing);
        InputPreferences input = new(
            historyMaximum,
            _persistHistory.IsChecked == true,
            _deduplicateHistory.IsChecked == true,
            _completionEnabled.IsChecked == true,
            completionLimit,
            _localEcho.IsChecked == true,
            _commandBatching.IsChecked == true);
        OutputPreferences output = new(
            _timestampMode.SelectedItem is TimestampRenderMode timestampMode ? timestampMode : TimestampRenderMode.Off,
            scrollbackMaximum,
            _splitOutputEnabled.IsChecked == true,
            _notifyWhenUnfocused.IsChecked == true,
            _wrapLongLines.IsChecked == true,
            _localEcho.IsChecked == true,
            _showCommandProvenance.IsChecked == true);
        TranscriptLogFormat logFormat = _logFormat.SelectedItem is TranscriptLogFormat selectedLogFormat
            ? selectedLogFormat
            : TranscriptLogFormat.AnsiText;

        try
        {
            await _runtime.SaveSettingsWorkspaceAsync(
                    profiles,
                    _connectionProfiles.ActiveProfileId,
                    general,
                    appearance,
                    _autoLogSessions.IsChecked == true,
                    _slurpTelemetryPrompt.IsChecked == true,
                    separator,
                    logFormat,
                    input,
                    output,
                    highlights,
                    transformations,
                    _runtime.CancellationToken)
                .ConfigureAwait(true);
            Saved = true;
            _saved();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _validation.Text = $"Could not save settings: {exception.Message}";
        }
    }

    private void Fail(SettingsPage page, string message)
    {
        _validation.Text = message;
        SelectPage(page);
    }

    private void OpenDataFolder()
    {
        try
        {
            string directory = GetDataDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            _storageStatus.Text = directory;
        }
        catch (Exception exception)
        {
            _storageStatus.Text = $"Could not open data folder: {exception.Message}";
        }
    }

    private async Task ExportDataAsync()
    {
        try
        {
            string source = GetDataDirectory();
            Directory.CreateDirectory(source);
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            string destination = Path.Combine(downloads, $"NexMUD-data-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            await Task.Run(() => ZipFile.CreateFromDirectory(source, destination, CompressionLevel.Fastest, false))
                .ConfigureAwait(true);
            _storageStatus.Text = $"Exported to {destination}";
        }
        catch (Exception exception)
        {
            _storageStatus.Text = $"Export failed: {exception.Message}";
        }
    }

    private Button NavigationButton(SettingsPage page, string title, string subtitle)
    {
        TextBlock label = new()
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = NexTypography.Body,
            Foreground = UiTheme.Text
        };
        Button button = new()
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 34,
            Padding = new Thickness(10, 6),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0)
        };
        ToolTip.SetTip(button, subtitle);
        button.Click += (_, _) => SelectPage(page);
        _navButtons[page] = button;
        return button;
    }

    private void SelectPage(SettingsPage page)
    {
        _pageContent.Content = _pages[page];
        foreach ((SettingsPage candidate, Button button) in _navButtons)
        {
            bool selected = candidate == page;
            button.Background = selected ? UiTheme.Raised : Brushes.Transparent;
            button.BorderBrush = selected ? UiTheme.Accent : Brushes.Transparent;
            button.BorderThickness = selected ? new Thickness(2, 0, 0, 0) : new Thickness(0);
            if (button.Content is TextBlock label) label.Foreground = selected ? UiTheme.Accent : UiTheme.Text;
        }
    }

    private static StackPanel PageStack(string title, string subtitle)
    {
        StackPanel stack = new() { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 4)
        });
        return stack;
    }

    private static Border SectionCard(string title, string subtitle)
    {
        StackPanel body = new() { Spacing = 8, Margin = new Thickness(0, 2, 0, 8) };
        body.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        body.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = UiTheme.Muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = NexTypography.Metadata
        });
        body.Children.Add(UiTheme.DividerLine());
        return new Border { Child = body, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    }

    private static Control FormRow(string label, Control control)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("160,*"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static TextBox ReadOnlyPath(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        Background = UiTheme.Console,
        Foreground = UiTheme.Muted,
        BorderBrush = UiTheme.Divider,
        BorderThickness = new Thickness(1),
        FontFamily = UiTheme.Mono,
        FontSize = NexTypography.Metadata,
        Padding = new Thickness(7, 3)
    };

    private static ScrollViewer PageScroll(Control content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private static void ConfigureCheckBox(CheckBox checkBox, string text)
    {
        checkBox.Content = text;
        checkBox.Foreground = UiTheme.Text;
        checkBox.FontSize = NexTypography.Body;
    }

    private static TextBox Field() => UiTheme.FieldBox();
    private static Button PrimaryButton(string text) => UiTheme.PrimaryButton(text);
    private static Button SecondaryButton(string text) => UiTheme.QuietButton(text);

    private static double Nearest(IReadOnlyList<double> values, double current) =>
        values.OrderBy(value => Math.Abs(value - current)).First();

    private string GetDataDirectory() =>
        Path.GetDirectoryName(_runtime.SettingsStore.Path)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), NexMudDataPaths.ProductDirectoryName);

    private string GetLogDirectory() => Path.Combine(GetDataDirectory(), "logs");
    private static string WorldKnowledgePath() => NexMudDataPaths.GetFilePath("knowledge.db", includeSqliteSidecars: true);
}

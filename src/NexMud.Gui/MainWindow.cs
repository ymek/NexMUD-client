using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using NexMud.Adapters.Avendar;
using NexMud.Client.Commands;
using NexMud.Client.Interaction;
using NexMud.Client.Logging;
using NexMud.Client.Knowledge;
using NexMud.Client.Presentation;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;
using NexMud.Client.Scripting;
using NexMud.Contracts.Actions;
using NexMud.Contracts.Events;
using NexMud.Contracts.Gameplay;
using NexMud.Contracts.Jev;
using NexMud.Contracts.State;
using NexMud.Contracts.Transport;
using NexMud.Core.Events;
using NexMud.Core.Jev;
using NexMud.Scripting.Host;
using NexMud.Transport.Text;

namespace NexMud.Gui;

public sealed class MainWindow : Window
{
    private enum ToolView
    {
        Context,
        Character,
        Abilities,
        Skills,
        Spells,
        Automation,
        Jev,
        Knowledge,
        Map,
        Scripting,
        Search
    }

    private enum JevSection
    {
        Status,
        Authority,
        RecentDecisions,
        ProviderSettings
    }

    private sealed record PaletteCommand(
        string Category,
        string Label,
        string Description,
        string? Shortcut,
        Func<Task> Execute);

    private static readonly IBrush WindowBackground = UiTheme.Window;
    private static readonly IBrush PanelBackground = UiTheme.Surface;
    private static readonly IBrush RaisedBackground = UiTheme.Raised;
    private static readonly IBrush ConsoleBackground = UiTheme.Console;
    private static readonly IBrush TextForeground = UiTheme.Text;
    private static readonly IBrush Muted = UiTheme.Muted;
    private static readonly IBrush Accent = UiTheme.Accent;
    private static readonly IBrush Success = UiTheme.Success;
    private static readonly IBrush Warning = UiTheme.Warning;
    private static readonly IBrush Danger = UiTheme.Danger;
    private static readonly IBrush Mana = UiTheme.Mana;
    private static readonly IBrush Movement = UiTheme.Movement;
    private static readonly IBrush PanelBorderBrush = UiTheme.Divider;

    private readonly NexMudRuntime _runtime;
    private readonly MapperWorkspace _mapperWorkspace;
    private readonly CodexWorkspace _codexWorkspace;
    private readonly AutomationWorkspace _automationWorkspace;
    private readonly ScriptingWorkspace _scriptingWorkspace;
    private readonly GameplayHudPanel _gameplayHudPanel = new();
    private readonly CharacterHudPanel _characterHudPanel;
    private readonly CharacterInventoryWorkspace _characterWorkspace;
    private readonly RoomContextPanel _roomContextPanel;
    private readonly Dictionary<ToolView, ContentControl> _dynamicWorkspaceHosts = [];
    private SettingsWorkspace? _settingsWorkspace;
    private MapperRoomMetadata? _currentRoomMetadata;
    private string? _currentRoomMetadataId;
    private CancellationTokenSource? _roomMetadataCts;
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly ChannelReader<StateSnapshot> _snapshots;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _nawsResizeCts;
    private readonly TranscriptLogWriter _logWriter = new();
    private const int RenderedTranscriptSegmentLimit = 2_500;
    private const int RenderedTranscriptHighWatermark = 3_250;

    private readonly SelectableTextBlock _gameText = new() { Inlines = new InlineCollection() };
    private readonly ScrollViewer _gameScroll = new();
    private readonly SelectableTextBlock _liveText = new() { Inlines = new InlineCollection() };
    private readonly ScrollViewer _liveScroll = new();
    private readonly Border _livePane = new();
    private readonly GridSplitter _transcriptSplitter = new();
    private readonly RowDefinition _historyRow = new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 140 };
    private readonly RowDefinition _splitterRow = new() { Height = new GridLength(0) };
    private readonly RowDefinition _liveRow = new() { Height = new GridLength(0), MinHeight = 0 };

    private readonly TextBox _command = new();
    private readonly Button _connect = new();
    private readonly TextBlock _characterLabel = new();
    private readonly TextBlock _locationLabel = new();
    private readonly TextBlock _subLocationLabel = new();
    private readonly TextBlock _notification = new();
    private readonly TextBlock _status = new();
    private readonly Button _jumpLive = new() { Content = "Return to live", IsVisible = false };
    private readonly Button _newOutputIndicator = new() { Content = "New output ↓", IsVisible = false };
    private readonly Button _logButton = new() { MinHeight = 38 };
    private readonly Button _jevToggle = new();

    private readonly Border _contextHud = new();
    private readonly Border _combatHud = new();
    private readonly TextBlock _hpLabel = new();
    private readonly TextBlock _manaLabel = new();
    private readonly TextBlock _moveLabel = new();
    private readonly TextBlock _positionLabel = new();
    private readonly TextBlock _combatLabel = new();
    private readonly TextBlock _targetLabel = new();
    private readonly TextBlock _jevHudLabel = new();
    private readonly TextBlock _xpLabel = new();
    private readonly ProgressBar _hp = VitalBar(Danger);
    private readonly ProgressBar _mana = VitalBar(Mana);
    private readonly ProgressBar _move = VitalBar(Movement);

    private readonly Border _gameplayRail = new();
    private readonly ContentControl _workspaceHost = new();
    private readonly Border _settingsOverlay = new();
    private readonly Border _settingsSheet = new();
    private readonly Dictionary<ToolView, NexNavItem> _navigationButtons = [];
    private readonly Dictionary<string, bool> _sectionExpansion = new(StringComparer.Ordinal);
    private readonly ColumnDefinition _worldColumn = new();
    private readonly ColumnDefinition _workspaceSplitterColumn = new();
    private readonly ColumnDefinition _workspaceColumn = new();
    private readonly GridSplitter _workspaceSplitter = new();
    private readonly TextBox _searchQuery = new();
    private readonly CheckBox _searchCaseSensitive = new() { Content = "Case sensitive" };
    private readonly CheckBox _searchRegex = new() { Content = "Regex" };
    private readonly StackPanel _searchResults = new() { Spacing = 7 };
    private double _preferredRailWidth = 360;
    private double _preferredLiveHeight = 190;
    private bool _gameplayRailVisible = true;

    private StateSnapshot _snapshot = StateSnapshot.Initial;
    private JevDecisionTrace? _lastJevDecision;
    private ItemIdentification? _lastItemIdentification;
    private AbilityHelpDocument? _lastAbilityHelp;
    private readonly ConcurrentDictionary<string, ItemKnowledge> _itemKnowledgeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _itemKnowledgeLookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AbilityHelpKnowledge> _abilityKnowledgeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _abilityKnowledgeLookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<JevDecisionTrace> _jevDecisionHistory = [];
    private Guid? _pendingJevApprovalId;
    private string? _pendingJevCommand;
    private string? _jevExecutionStatus;
    private bool _jevEvaluationInProgress;
    private JevSection _jevSection = JevSection.Status;
    private ToolView _activeTool = ToolView.Context;
    private bool _followTail = true;
    private int _unseenOutputCount;
    private CancellationTokenSource? _searchCts;

    public MainWindow(NexMudRuntime runtime)
    {
        _runtime = runtime;
        _events = runtime.Events.SubscribeLossless();
        _snapshots = runtime.State.Subscribe(capacity: 8);
        _mapperWorkspace = new MapperWorkspace(
            runtime,
            ShowRoomMetadataEditorAsync,
            ShowSpecialExitEditorAsync,
            _cts.Token);
        _codexWorkspace = new CodexWorkspace(
            runtime.Knowledge,
            RouteToCodexLocationAsync,
            SubmitCommandAsync,
            _cts.Token);
        _automationWorkspace = new AutomationWorkspace(runtime, () =>
        {
            ShowTool(ToolView.Scripting);
            return Task.CompletedTask;
        });
        _scriptingWorkspace = new ScriptingWorkspace(runtime);
        _characterHudPanel = new CharacterHudPanel(ResolveItemInspection);
        _characterWorkspace = new CharacterInventoryWorkspace(ResolveItemInspection, SubmitCommandAsync);
        _roomContextPanel = new RoomContextPanel(SubmitCommandAsync, () => ShowTool(ToolView.Map));
        _runtime.Knowledge.MapperKnowledgeChanged += HandleKnowledgeChanged;
        _runtime.Interaction.World.Appended += HandleWorldBufferAppended;
        _runtime.Interaction.NotificationRequested += HandleOutputNotification;
        _runtime.Interaction.RuleDiagnostic += HandleOutputRuleDiagnostic;
        GeneralPreferences general = runtime.Settings.General ?? new GeneralPreferences();
        _gameplayRailVisible = general.ShowGameplayRailByDefault;
        WorkspacePreferences workspace = general.RestorePreviousWorkspace
            ? runtime.Settings.Workspace ?? new WorkspacePreferences()
            : new WorkspacePreferences();
        _preferredRailWidth = workspace.DockWidth;
        _preferredLiveHeight = workspace.LiveSplitHeight;
        _activeTool = ToolView.Context;

        Title = "NexMUD";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NexMUD/Assets/app-icon.ico")));
        Width = workspace.WindowWidth;
        Height = workspace.WindowHeight;
        MinWidth = 1180;
        MinHeight = 720;
        Background = NexMudTheme.ApplicationBackground;
        FontFamily = NexMudTheme.Interface;

        Content = BuildLayout();
        ApplyUiSettings();
        NexMudTheme.AccentChanged += RestyleCommandInput;
        KeyDown += WindowKeyDown;
        SizeChanged += (_, _) =>
        {
            ScheduleTerminalSizeUpdate();
            UpdateNavigationDensity();
        };

        Opened += (_, _) =>
        {
            _ = Task.Run(() => ConsumeEventsAsync(_cts.Token));
            _ = Task.Run(() => ConsumeStateAsync(_cts.Token));
            RenderState(_runtime.State.Current);
            UpdateNavigationDensity();
            _command.Focus();
        };
        Closed += (_, _) =>
        {
            _runtime.Knowledge.MapperKnowledgeChanged -= HandleKnowledgeChanged;
            _runtime.Interaction.World.Appended -= HandleWorldBufferAppended;
            _runtime.Interaction.NotificationRequested -= HandleOutputNotification;
            _runtime.Interaction.RuleDiagnostic -= HandleOutputRuleDiagnostic;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            NexMudTheme.AccentChanged -= RestyleCommandInput;
            DeactivateWorkspace(_activeTool);
            _mapperWorkspace.Dispose();
            _codexWorkspace.Dispose();
            _roomMetadataCts?.Cancel();
            _roomMetadataCts?.Dispose();
            _nawsResizeCts?.Cancel();
            _nawsResizeCts?.Dispose();
            _cts.Cancel();
            _logWriter.Dispose();
        };
    }

    public async Task PrepareForShutdownAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePreferences saved = CaptureWorkspacePreferences();
        try
        {
            await _runtime.SaveWorkspaceAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cts.Cancel();
            _logWriter.Dispose();
        }
    }

    private WorkspacePreferences CaptureWorkspacePreferences()
    {
        double railWidth = _activeTool == ToolView.Context && _gameplayRail.Bounds.Width > 0
            ? _gameplayRail.Bounds.Width
            : _preferredRailWidth;
        return new WorkspacePreferences(
            Width,
            Height,
            true,
            Math.Clamp(railWidth, 370, 445),
            ToolView.Context.ToString(),
            _livePane.IsVisible && _liveRow.Height.Value > 0 ? _liveRow.Height.Value : _preferredLiveHeight);
    }

    private Control BuildLayout()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = NexMudTheme.ApplicationBackground
        };

        root.Children.Add(BuildAppBar());

        Grid body = new() { Margin = new Thickness(2, 2, 2, 2) };
        _worldColumn.Width = new GridLength(1, GridUnitType.Star);
        _worldColumn.MinWidth = 620;
        body.ColumnDefinitions.Add(_worldColumn);
        _workspaceSplitterColumn.Width = new GridLength(2);
        body.ColumnDefinitions.Add(_workspaceSplitterColumn);
        _workspaceColumn.Width = new GridLength(Math.Clamp(_preferredRailWidth, 390, 425));
        _workspaceColumn.MinWidth = 380;
        _workspaceColumn.MaxWidth = 445;
        body.ColumnDefinitions.Add(_workspaceColumn);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        Grid worldBody = new()
        {
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
            Background = NexMudTheme.DeepConsole
        };
        Control transcriptSurface = BuildTranscriptSurface();
        worldBody.Children.Add(transcriptSurface);
        Grid.SetRow(_gameplayHudPanel, 1);
        worldBody.Children.Add(_gameplayHudPanel);
        Control commandPanel = BuildCommandBar();
        Grid.SetRow(commandPanel, 2);
        worldBody.Children.Add(commandPanel);

        Border worldFrame = new()
        {
            Background = NexMudTheme.DeepConsole,
            BorderThickness = new Thickness(0),
            ClipToBounds = true,
            Child = worldBody
        };
        body.Children.Add(worldFrame);

        _workspaceSplitter.Background = NexMudTheme.BronzeShadow;
        _workspaceSplitter.ResizeDirection = GridResizeDirection.Columns;
        _workspaceSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        _workspaceSplitter.ShowsPreview = true;
        Grid.SetColumn(_workspaceSplitter, 1);
        body.Children.Add(_workspaceSplitter);

        BuildGameplayRail();
        _workspaceHost.Content = _gameplayRail;
        _workspaceHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _workspaceHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        _workspaceHost.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(_workspaceHost, 2);
        body.Children.Add(_workspaceHost);

        Control footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        BuildSettingsOverlay();
        Grid.SetRowSpan(_settingsOverlay, 3);
        root.Children.Add(_settingsOverlay);

        return new NexGameFrame(root) { Margin = new Thickness(4) };
    }

    private Control BuildAppBar()
    {
        Border bar = new()
        {
            Background = NexMudTheme.RaisedSurfaceGradient,
            BorderBrush = NexMudTheme.BronzeShadow,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, NexSpacing.Micro),
            MinHeight = 44
        };

        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8
        };

        StackPanel brand = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 5, 0)
        };
        brand.Children.Add(NexApprovedAssets.CreateApplicationIcon(34));
        StackPanel wordmark = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        wordmark.Children.Add(new TextBlock
        {
            Text = "Nex",
            Foreground = NexMudTheme.Parchment,
            FontFamily = NexMudTheme.Display,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.Bold
        });
        wordmark.Children.Add(new TextBlock
        {
            Text = "MUD",
            Foreground = NexMudTheme.AccentBright,
            FontFamily = NexMudTheme.Display,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.Bold
        });
        brand.Children.Add(wordmark);
        grid.Children.Add(brand);

        StackPanel navigation = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        navigation.Children.Add(NavigationButton("World", ToolView.Context, NexIconKind.World));
        navigation.Children.Add(NavigationButton("Character", ToolView.Character, NexIconKind.Character));
        navigation.Children.Add(NavigationButton("Abilities", ToolView.Abilities, NexIconKind.Abilities));
        navigation.Children.Add(NavigationButton("Codex", ToolView.Knowledge, NexIconKind.Codex));
        navigation.Children.Add(NavigationButton("Map", ToolView.Map, NexIconKind.Map));
        navigation.Children.Add(NavigationButton("Automation", ToolView.Automation, NexIconKind.Automation));
        navigation.Children.Add(NavigationButton("Scripting", ToolView.Scripting, NexIconKind.Scripting));
        navigation.Children.Add(NavigationButton("Jev", ToolView.Jev, NexIconKind.Jev));
        Border navigationViewport = new()
        {
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = navigation
        };
        Grid.SetColumn(navigationViewport, 1);
        grid.Children.Add(navigationViewport);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        _connect.MinHeight = 34;
        _connect.Padding = new Thickness(9, 4);
        _connect.CornerRadius = new CornerRadius(2);
        _connect.BorderThickness = new Thickness(1);
        _connect.FontSize = NexTypography.Metadata;
        _connect.FontWeight = FontWeight.SemiBold;
        _connect.Click += async (_, _) => await ToggleConnectionAsync();
        actions.Children.Add(_connect);
        RenderConnectionAction(connected: false, _runtime.ActiveConnectionProfile.Host, _runtime.ActiveConnectionProfile.Port);

        NexIconButton palette = new(NexIconKind.Search, "Command palette (⌘K)");
        palette.MinWidth = 34;
        palette.MinHeight = 34;
        palette.Padding = new Thickness(6);
        palette.Click += (_, _) => ShowCommandPalette();
        actions.Children.Add(palette);

        _logButton.MinWidth = 34;
        _logButton.MinHeight = 34;
        _logButton.Padding = new Thickness(6);
        _logButton.CornerRadius = new CornerRadius(2);
        _logButton.Background = NexMudTheme.RaisedSurfaceGradient;
        _logButton.Foreground = NexMudTheme.Parchment;
        _logButton.BorderBrush = NexMudTheme.Divider;
        _logButton.BorderThickness = new Thickness(1);
        _logButton.Click += (_, _) => ToggleLogging();
        actions.Children.Add(_logButton);
        RenderLoggingState();

        _jevToggle.MinWidth = 64;
        _jevToggle.MinHeight = 34;
        _jevToggle.Padding = new Thickness(8, 4);
        _jevToggle.CornerRadius = new CornerRadius(2);
        _jevToggle.FontSize = NexTypography.Metadata;
        _jevToggle.FontWeight = FontWeight.SemiBold;
        _jevToggle.BorderThickness = new Thickness(1);
        _jevToggle.Click += async (_, _) => await ToggleJevEnabledAsync();
        ToolTip.SetTip(_jevToggle, "Master Jev control. This does not change domain authority settings.");
        actions.Children.Add(_jevToggle);
        UpdateJevToggle();

        NexIconButton settings = new(NexIconKind.Settings, "Settings");
        settings.MinWidth = 34;
        settings.MinHeight = 34;
        settings.Padding = new Thickness(6);
        settings.Click += async (_, _) => await ShowSettingsAsync();
        actions.Children.Add(settings);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        bar.Child = grid;
        UpdateNavigationStyles();
        return bar;
    }

    private NexNavItem NavigationButton(string label, ToolView view, NexIconKind icon)
    {
        NexNavItem button = new(label, icon);
        button.Click += (_, _) => ShowTool(view);
        _navigationButtons[view] = button;
        return button;
    }

    private static Button UtilityButton(string label, string accessibleLabel)
    {
        Button button = new()
        {
            Content = label,
            MinWidth = 34,
            MinHeight = 25,
            Padding = new Thickness(6, 2),
            CornerRadius = new CornerRadius(1),
            FontSize = NexTypography.Metadata,
            Background = Brushes.Transparent,
            Foreground = Muted,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(button, accessibleLabel);
        return button;
    }

    private static void StyleUtilityButton(Button button)
    {
        button.MinWidth = 42;
        button.MinHeight = 25;
        button.Padding = new Thickness(6, 2);
        button.CornerRadius = new CornerRadius(1);
        button.FontSize = NexTypography.Metadata;
        button.FontWeight = FontWeight.SemiBold;
        button.Background = Brushes.Transparent;
        button.Foreground = Muted;
        button.BorderBrush = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
    }

    private static Button WorldUtilityButton(string label, string accessibleLabel)
    {
        Button button = new()
        {
            Content = label,
            MinWidth = 38,
            MinHeight = 34,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(2),
            FontSize = NexTypography.CompactData,
            Background = NexMudTheme.RaisedSurfaceGradient,
            Foreground = NexMudTheme.Muted,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(1)
        };
        ToolTip.SetTip(button, accessibleLabel);
        return button;
    }

    private static void StyleWorldUtilityButton(Button button)
    {
        button.MinWidth = 44;
        button.MinHeight = 38;
        button.Padding = new Thickness(8, 5);
        button.CornerRadius = new CornerRadius(2);
        button.FontSize = NexTypography.CompactData;
        button.FontWeight = FontWeight.SemiBold;
        button.Background = NexMudTheme.RaisedSurfaceGradient;
        button.Foreground = NexMudTheme.Muted;
        button.BorderBrush = NexMudTheme.Divider;
        button.BorderThickness = new Thickness(1);
    }

    private void UpdateNavigationStyles()
    {
        foreach ((ToolView view, NexNavItem button) in _navigationButtons)
        {
            ToolView normalized = NormalizeToolView(view);
            bool active = normalized == NormalizeToolView(_activeTool);
            button.SetActive(active);
        }
    }

    private void UpdateNavigationDensity()
    {
        bool compact = Bounds.Width > 0 && Bounds.Width < 1480;
        foreach (NexNavItem button in _navigationButtons.Values)
        {
            button.SetCompact(compact);
        }
    }

    private Control BuildTranscriptSurface()
    {
        Grid transcript = new() { Background = NexMudTheme.DeepConsole };
        transcript.RowDefinitions.Add(_historyRow);
        transcript.RowDefinitions.Add(_splitterRow);
        transcript.RowDefinitions.Add(_liveRow);

        Border historyPanel = new()
        {
            Background = NexMudTheme.DeepConsole,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        ConfigureTranscriptText(_gameText);
        _gameScroll.Content = TranscriptTextHost(_gameText);
        _gameScroll.Background = NexMudTheme.DeepConsole;
        _gameScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _gameScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _gameScroll.ScrollChanged += (_, _) =>
        {
            double distance = _gameScroll.Extent.Height - _gameScroll.Viewport.Height - _gameScroll.Offset.Y;
            bool followsTail = distance < 24;
            if (_followTail && !followsTail)
            {
                if ((_runtime.Settings.Output ?? new OutputPreferences()).SplitOutputEnabled)
                    ActivateSplitView();
                else
                    _followTail = false;
            }
            else if (!_followTail && followsTail)
            {
                ReturnToLive();
            }
        };
        Grid historyGrid = new();
        historyGrid.Children.Add(_gameScroll);
        _newOutputIndicator.HorizontalAlignment = HorizontalAlignment.Right;
        _newOutputIndicator.VerticalAlignment = VerticalAlignment.Top;
        _newOutputIndicator.Margin = new Thickness(0, 8, 18, 0);
        _newOutputIndicator.Padding = new Thickness(9, 5);
        _newOutputIndicator.Background = NexMudTheme.RaisedSurfaceGradient;
        _newOutputIndicator.Foreground = NexMudTheme.AccentBright;
        _newOutputIndicator.BorderBrush = NexMudTheme.AntiqueBrass;
        _newOutputIndicator.BorderThickness = new Thickness(1);
        _newOutputIndicator.Click += (_, _) => ReturnToLive();
        historyGrid.Children.Add(_newOutputIndicator);
        historyPanel.Child = historyGrid;
        transcript.Children.Add(historyPanel);

        _transcriptSplitter.Background = NexMudTheme.BronzeShadow;
        _transcriptSplitter.ResizeDirection = GridResizeDirection.Rows;
        _transcriptSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        _transcriptSplitter.ShowsPreview = true;
        _transcriptSplitter.IsVisible = false;
        Grid.SetRow(_transcriptSplitter, 1);
        transcript.Children.Add(_transcriptSplitter);

        _livePane.Background = NexMudTheme.DeepConsole;
        _livePane.BorderBrush = NexMudTheme.AntiqueBrass;
        _livePane.BorderThickness = new Thickness(0, 1, 0, 0);
        _livePane.CornerRadius = new CornerRadius(0);
        _livePane.IsVisible = false;

        Grid liveGrid = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        Border liveHeader = new()
        {
            Background = NexMudTheme.RaisedSurfaceGradient,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(11, 6)
        };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = "LIVE OUTPUT",
            Foreground = NexMudTheme.AccentBright,
            FontSize = NexTypography.CompactData,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        _jumpLive.IsVisible = true;
        _jumpLive.Click += (_, _) => ReturnToLive();
        Grid.SetColumn(_jumpLive, 1);
        header.Children.Add(_jumpLive);
        liveHeader.Child = header;
        liveGrid.Children.Add(liveHeader);

        ConfigureTranscriptText(_liveText);
        _liveScroll.Content = TranscriptTextHost(_liveText);
        _liveScroll.Background = NexMudTheme.DeepConsole;
        _liveScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _liveScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetRow(_liveScroll, 1);
        liveGrid.Children.Add(_liveScroll);
        _livePane.Child = liveGrid;
        Grid.SetRow(_livePane, 2);
        transcript.Children.Add(_livePane);

        return transcript;
    }

    private void ConfigureTranscriptText(SelectableTextBlock text)
    {
        AppearancePreferences appearance = _runtime.Settings.Appearance ?? new AppearancePreferences();
        OutputPreferences output = _runtime.Settings.Output ?? new OutputPreferences();
        NexTranscriptTypography.Apply(
            text,
            appearance.TranscriptSize,
            appearance.TranscriptFont,
            appearance.TranscriptLineSpacing);
        text.Foreground = NexMudTheme.Parchment;
        text.Background = NexMudTheme.DeepConsole;
        text.TextWrapping = output.WrapLongLines ? TextWrapping.Wrap : TextWrapping.NoWrap;
        text.Margin = new Thickness(0);
        text.Padding = new Thickness(0);
    }

    private static Border TranscriptTextHost(SelectableTextBlock text) => new()
    {
        Background = NexMudTheme.DeepConsole,
        Padding = new Thickness(
            NexSpacing.Tight,
            0,
            NexSpacing.Tight,
            0),
        Child = text
    };

    private void ActivateSplitView()
    {
        if (!_followTail)
        {
            return;
        }

        _followTail = false;
        _splitterRow.Height = new GridLength(5);
        _liveRow.MinHeight = 110;
        _liveRow.Height = new GridLength(_preferredLiveHeight);
        _transcriptSplitter.IsVisible = true;
        _livePane.IsVisible = true;
        RebuildLiveTranscript();
        Dispatcher.UIThread.Post(_liveScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void ReturnToLive()
    {
        _followTail = true;
        _unseenOutputCount = 0;
        _jumpLive.Content = "Return to live";
        if (_liveRow.Height.Value > 0)
        {
            _preferredLiveHeight = _liveRow.Height.Value;
        }
        _transcriptSplitter.IsVisible = false;
        _livePane.IsVisible = false;
        _splitterRow.Height = new GridLength(0);
        _liveRow.MinHeight = 0;
        _liveRow.Height = new GridLength(0);
        _liveText.Inlines!.Clear();
        _newOutputIndicator.IsVisible = false;
        Dispatcher.UIThread.Post(_gameScroll.ScrollToEnd, DispatcherPriority.Background);
        _command.Focus();
    }

    private void RebuildLiveTranscript()
    {
        _liveText.Inlines!.Clear();
        foreach (WorldBufferEntry entry in _runtime.Interaction.World.Tail(600))
            AppendWorldEntry(_liveText, entry);
    }

    private Control BuildContextHud()
    {
        _contextHud.Background = WindowBackground;
        _contextHud.BorderBrush = PanelBorderBrush;
        _contextHud.BorderThickness = new Thickness(0, 0, 0, 1);
        _contextHud.Padding = new Thickness(14, 8);

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        StackPanel location = new() { Spacing = 1 };
        _locationLabel.Foreground = TextForeground;
        _locationLabel.FontSize = NexTypography.SectionTitle;
        _locationLabel.FontWeight = FontWeight.SemiBold;
        _subLocationLabel.Foreground = Muted;
        _subLocationLabel.FontSize = NexTypography.Metadata;
        location.Children.Add(_locationLabel);
        location.Children.Add(_subLocationLabel);
        grid.Children.Add(location);

        StackPanel progress = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        _xpLabel.Foreground = Warning;
        _xpLabel.FontSize = NexTypography.Hud;
        _xpLabel.VerticalAlignment = VerticalAlignment.Center;
        progress.Children.Add(_xpLabel);
        Grid.SetColumn(progress, 1);
        grid.Children.Add(progress);

        _contextHud.Child = grid;
        return _contextHud;
    }

    private Control BuildCombatHud()
    {
        _combatHud.Background = PanelBackground;
        _combatHud.BorderBrush = PanelBorderBrush;
        _combatHud.BorderThickness = new Thickness(0, 1, 0, 0);
        _combatHud.Margin = new Thickness(0);
        _combatHud.Padding = new Thickness(10, 6);
        _combatHud.CornerRadius = new CornerRadius(0);

        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 12
        };

        Control hpMeter = BuildMeter(_hpLabel, _hp, "HP");
        grid.Children.Add(hpMeter);

        Control manaMeter = BuildMeter(_manaLabel, _mana, "MA");
        Grid.SetColumn(manaMeter, 1);
        grid.Children.Add(manaMeter);

        Control moveMeter = BuildMeter(_moveLabel, _move, "MV");
        Grid.SetColumn(moveMeter, 2);
        grid.Children.Add(moveMeter);

        Control position = BuildHudReadout("Position", _positionLabel, 90);
        Grid.SetColumn(position, 3);
        grid.Children.Add(position);

        Control combat = BuildHudReadout("Combat", _combatLabel, 80);
        Grid.SetColumn(combat, 4);
        grid.Children.Add(combat);

        Control target = BuildHudReadout("Target", _targetLabel, 175);
        Grid.SetColumn(target, 5);
        grid.Children.Add(target);

        Control jev = BuildHudReadout("Jev", _jevHudLabel, 150);
        Grid.SetColumn(jev, 6);
        grid.Children.Add(jev);

        _combatHud.Child = grid;
        return _combatHud;
    }

    private void BuildGameplayRail()
    {
        _gameplayRail.Background = NexMudTheme.ApplicationBackground;
        _gameplayRail.BorderThickness = new Thickness(0);
        _gameplayRail.CornerRadius = new CornerRadius(0);
        _gameplayRail.Margin = new Thickness(0);

        Grid rail = new()
        {
            RowDefinitions = new RowDefinitions("Auto,2,*"),
            Margin = new Thickness(0),
            Background = NexMudTheme.ApplicationBackground
        };
        rail.Children.Add(_characterHudPanel);
        Border divider = new()
        {
            Height = 1,
            Background = NexMudTheme.BronzeShadow,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(divider, 1);
        rail.Children.Add(divider);
        Grid.SetRow(_roomContextPanel, 2);
        _roomContextPanel.VerticalAlignment = VerticalAlignment.Stretch;
        rail.Children.Add(_roomContextPanel);

        _gameplayRail.Child = rail;
    }

    private void BuildSettingsOverlay()
    {
        _settingsOverlay.IsVisible = false;
        _settingsOverlay.Background = new SolidColorBrush(Color.Parse("#D9080B10"));
        _settingsOverlay.Padding = new Thickness(56, 48);

        _settingsSheet.Background = UiTheme.Surface;
        _settingsSheet.BorderBrush = UiTheme.Brass;
        _settingsSheet.BorderThickness = new Thickness(1);
        _settingsSheet.CornerRadius = new CornerRadius(4);
        _settingsSheet.HorizontalAlignment = HorizontalAlignment.Stretch;
        _settingsSheet.VerticalAlignment = VerticalAlignment.Stretch;
        _settingsSheet.MinWidth = 820;
        _settingsSheet.MinHeight = 580;
        _settingsSheet.MaxWidth = 1120;
        _settingsSheet.MaxHeight = 820;

        _settingsOverlay.Child = _settingsSheet;
    }

    private Control BuildCommandBar()
    {
        Grid commandGrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 6
        };

        TextBlock prompt = new()
        {
            Text = ">",
            Foreground = NexMudTheme.AccentBright,
            FontFamily = NexMudTheme.Mono,
            FontSize = NexMudTheme.CommandText,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        commandGrid.Children.Add(prompt);

        _command.Background = NexMudTheme.InsetGradient;
        _command.Foreground = NexMudTheme.Parchment;
        _command.BorderBrush = NexMudTheme.AccentPrimary;
        _command.BorderThickness = new Thickness(1);
        _command.CornerRadius = new CornerRadius(2);
        _command.Padding = new Thickness(9, 4);
        _command.FontFamily = NexMudTheme.Mono;
        _command.FontSize = NexMudTheme.CommandText;
        _command.PlaceholderText = "Enter a command…";
        _command.MinHeight = 32;
        _command.KeyDown += CommandKeyDown;
        Grid.SetColumn(_command, 1);
        commandGrid.Children.Add(_command);

        NexPrimaryButton send = new("Send", NexIconKind.Send);
        send.Click += async (_, _) => await SubmitCurrentInputAsync();
        Grid.SetColumn(send, 2);
        commandGrid.Children.Add(send);

        return new NexCommandBar(commandGrid);
    }

    private void RestyleCommandInput()
    {
        _command.BorderBrush = NexMudTheme.AccentPrimary;
    }

    private Control BuildFooter()
    {
        Grid footer = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(14, 0, 14, 6)
        };
        _notification.Foreground = NexMudTheme.AccentPrimary;
        _notification.FontSize = NexTypography.Metadata;
        _notification.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_notification);
        _status.Foreground = NexMudTheme.Faint;
        _status.FontSize = NexTypography.Metadata;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_status, 1);
        footer.Children.Add(_status);
        return footer;
    }

    private async void CommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab)
        {
            ResetCompletion();
        }

        if (TryDispatchKeyBinding(e, KeybindingContext.Input))
        {
            e.Handled = true;
            return;
        }

        CommandInputPresentation input = CommandInputPolicy.For(_snapshot.Session.InputMode);
        if (e.Key == Key.Tab && input.AllowCompletion && (_runtime.Settings.Input ?? new InputPreferences()).CompletionEnabled)
        {
            e.Handled = true;
            CycleCompletion(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitCurrentInputAsync();
            return;
        }

        if (e.Key == Key.Up && input.AllowHistory)
        {
            e.Handled = true;
            NavigateCommandHistory(-1);
            return;
        }

        if (e.Key == Key.Down && input.AllowHistory)
        {
            e.Handled = true;
            NavigateCommandHistory(1);
        }
    }

    private async Task SubmitCurrentInputAsync()
    {
        string text = _command.Text ?? string.Empty;
        SessionInputMode inputMode = _snapshot.Session.InputMode;
        CommandInputPresentation presentation = CommandInputPolicy.For(inputMode);
        if (presentation.ClearAfterSubmit)
            _command.Text = string.Empty;
        else
            _command.SelectAll();

        await SubmitUserInputAsync(text, InputSourceKind.Keyboard).ConfigureAwait(true);

        _command.Focus();
        if (!presentation.ClearAfterSubmit && _command.Text == text)
            _command.SelectAll();
    }

    private void NavigateCommandHistory(int delta)
    {
        if (!CommandInputPolicy.For(_snapshot.Session.InputMode).AllowHistory) return;
        string text = _command.Text ?? string.Empty;
        InputEditResult? edit = delta < 0
            ? _runtime.Interaction.Editing.HistoryPrevious(new InputBufferSnapshot(
                text,
                Math.Clamp(_command.CaretIndex, 0, text.Length),
                _command.SelectionStart,
                _command.SelectionEnd))
            : _runtime.Interaction.Editing.HistoryNext();
        if (edit is null) return;
        _command.Text = edit.Text;
        _command.CaretIndex = edit.CaretIndex;
        _command.Focus();
    }

    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_settingsOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseSettingsOverlay(saved: false);
            }
            return;
        }

        if (TryDispatchKeyBinding(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _activeTool != ToolView.Context)
        {
            e.Handled = true;
            HideTool();
            return;
        }

        bool primary = HasPrimaryModifier(e.KeyModifiers);
        if (primary && e.Key == Key.F)
        {
            e.Handled = true;
            ShowTool(ToolView.Search);
            Dispatcher.UIThread.Post(() => _searchQuery.Focus());
            return;
        }
        if (primary && e.Key == Key.G)
        {
            e.Handled = true;
            ReturnToLive();
            return;
        }
        if (primary && e.Key == Key.L)
        {
            e.Handled = true;
            ToggleLogging();
            return;
        }
        if ((primary && e.Key == Key.K) ||
            (primary && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.P))
        {
            e.Handled = true;
            ShowCommandPalette();
        }
    }

    private bool TryDispatchKeyBinding(KeyEventArgs e, KeybindingContext? forcedContext = null)
    {
        if (_snapshot.Session.InputMode != SessionInputMode.Normal)
            return false;

        KeybindingContext context = forcedContext ?? (_activeTool == ToolView.Map
            ? KeybindingContext.Mapper
            : _command.IsFocused ? KeybindingContext.Input : KeybindingContext.World);
        KeybindingResolution resolution = _runtime.Interaction.Keybindings.Resolve(
            context,
            binding => CommandGesture.Matches(binding, e));
        if (resolution.HasConflict)
        {
            ShowClientMessage($"Keybinding conflict for {e.Key} in {context} context.", error: true);
            return true;
        }
        if (resolution.Binding is null)
            return false;

        _ = DispatchKeybindingActionAsync(resolution.Binding);
        return true;
    }

    private async Task DispatchKeybindingActionAsync(CommandKeyBinding binding)
    {
        try
        {
            await _runtime.Events.PublishAsync(
                new AutomationKeybindingInvoked(
                    binding.Gesture,
                    binding.Context.ToString(),
                    binding.Action.ToString(),
                    binding.Command),
                "keybinding",
                _cts.Token).ConfigureAwait(true);
            switch (binding.Action)
            {
                case KeybindingActionKind.SubmitInput:
                    await SubmitCurrentInputAsync().ConfigureAwait(true);
                    break;
                case KeybindingActionKind.HistoryPrevious:
                    NavigateCommandHistory(-1);
                    break;
                case KeybindingActionKind.HistoryNext:
                    NavigateCommandHistory(1);
                    break;
                case KeybindingActionKind.CompletionNext:
                    CycleCompletion(reverse: false);
                    break;
                case KeybindingActionKind.CompletionPrevious:
                    CycleCompletion(reverse: true);
                    break;
                case KeybindingActionKind.ClearInput:
                    _command.Text = string.Empty;
                    break;
                case KeybindingActionKind.FocusInput:
                    _command.Focus();
                    break;
                case KeybindingActionKind.ScrollPageUp:
                    _gameScroll.Offset = new Vector(_gameScroll.Offset.X, Math.Max(0, _gameScroll.Offset.Y - _gameScroll.Viewport.Height));
                    break;
                case KeybindingActionKind.ScrollPageDown:
                    _gameScroll.Offset = new Vector(_gameScroll.Offset.X, Math.Min(_gameScroll.Extent.Height, _gameScroll.Offset.Y + _gameScroll.Viewport.Height));
                    break;
                case KeybindingActionKind.ScrollToBottom:
                    ReturnToLive();
                    break;
                case KeybindingActionKind.SearchScrollback:
                    ShowTool(ToolView.Search);
                    Dispatcher.UIThread.Post(() => _searchQuery.Focus());
                    break;
                case KeybindingActionKind.SendCommand:
                    await SubmitKeybindingCommandAsync(binding).ConfigureAwait(true);
                    break;
                case KeybindingActionKind.ToggleJev:
                    await ToggleJevEnabledAsync().ConfigureAwait(true);
                    break;
                case KeybindingActionKind.MapperPause:
                    await _runtime.Navigator.PauseAsync("Paused by keybinding.", _cts.Token).ConfigureAwait(true);
                    break;
                case KeybindingActionKind.MapperResume:
                    await _runtime.Navigator.ResumeAsync(_cts.Token).ConfigureAwait(true);
                    break;
                case KeybindingActionKind.MapperAbort:
                    await _runtime.Navigator.StopAsync("Aborted by keybinding.", _cts.Token).ConfigureAwait(true);
                    break;
                case KeybindingActionKind.TogglePane:
                    if (Enum.TryParse(binding.Command, true, out ToolView view)) ShowTool(view);
                    break;
                case KeybindingActionKind.RunAutomation:
                    if (string.IsNullOrWhiteSpace(binding.Command) ||
                        !await _runtime.Automation.RunWorkflowAsync(binding.Command, _cts.Token).ConfigureAwait(true))
                        throw new InvalidOperationException($"Automation '{binding.Command}' is not available to run.");
                    break;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowClientMessage($"Keybinding failed: {exception.Message}", error: true);
        }
    }

    private async Task SubmitKeybindingCommandAsync(CommandKeyBinding binding)
    {
        InputSubmissionResult submission = await _runtime.Interaction.Input.SubmitAsync(
            new InputRequest(InputSourceKind.Keybinding, binding.Command ?? string.Empty, DateTimeOffset.Now, Guid.NewGuid()),
            _snapshot.Session.InputMode,
            _cts.Token).ConfigureAwait(false);
        LocalCommandResult? failure = submission.Results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Message));
        if (!string.IsNullOrWhiteSpace(failure?.Message))
            ShowClientMessage(failure.Message!);
    }

    private static bool HasPrimaryModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

    private void ResetCompletion() => _runtime.Interaction.Editing.ResetCompletion();

    private void CycleCompletion(bool reverse)
    {
        if (!(_runtime.Settings.Input ?? new InputPreferences()).CompletionEnabled) return;
        string text = _command.Text ?? string.Empty;
        InputEditResult? result = _runtime.Interaction.Editing.Complete(
            new InputBufferSnapshot(
                text,
                Math.Clamp(_command.CaretIndex, 0, text.Length),
                _command.SelectionStart,
                _command.SelectionEnd),
            reverse);
        if (result is null) return;
        _command.Text = result.Text;
        _command.CaretIndex = result.CaretIndex;
    }

    private void TrimRenderedTranscriptIfNeeded()
    {
        if (_gameText.Inlines is null || _gameText.Inlines.Count <= RenderedTranscriptHighWatermark) return;
        RebuildTranscript();
    }

    private Control BuildSearchPanel()
    {
        StackPanel stack = ToolStack();
        _searchQuery.PlaceholderText = "Search rendered scrollback";
        _searchQuery.Background = ConsoleBackground;
        _searchQuery.Foreground = TextForeground;
        _searchQuery.BorderBrush = PanelBorderBrush;
        _searchQuery.TextChanged -= SearchQueryChanged;
        _searchQuery.TextChanged += SearchQueryChanged;
        stack.Children.Add(_searchQuery);

        StackPanel options = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        _searchCaseSensitive.Foreground = TextForeground;
        _searchRegex.Foreground = TextForeground;
        _searchCaseSensitive.Click -= SearchOptionChanged;
        _searchRegex.Click -= SearchOptionChanged;
        _searchCaseSensitive.Click += SearchOptionChanged;
        _searchRegex.Click += SearchOptionChanged;
        options.Children.Add(_searchCaseSensitive);
        options.Children.Add(_searchRegex);
        stack.Children.Add(options);
        stack.Children.Add(new TextBlock
        {
            Text = "Search runs against visible rendered scrollback. Source text and semantic processing are unchanged.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        RebuildSearchResults();
        stack.Children.Add(_searchResults);
        return ToolScroll(stack);
    }

    private void SearchQueryChanged(object? sender, TextChangedEventArgs e) => ScheduleSearchResults();
    private void SearchOptionChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ScheduleSearchResults();

    private void RebuildSearchResults() => ScheduleSearchResults(immediate: true);

    private void ScheduleSearchResults(bool immediate = false)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _searchCts = cts;

        string query = (_searchQuery.Text ?? string.Empty).Trim();
        bool caseSensitive = _searchCaseSensitive.IsChecked == true;
        bool regex = _searchRegex.IsChecked == true;
        _ = SearchScrollbackAsync(query, caseSensitive, regex, immediate, cts);
    }

    private async Task SearchScrollbackAsync(
        string query,
        bool caseSensitive,
        bool regex,
        bool immediate,
        CancellationTokenSource owner)
    {
        try
        {
            if (!immediate) await Task.Delay(90, owner.Token).ConfigureAwait(false);
            if (query.Length == 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_searchCts, owner)) return;
                    _searchResults.Children.Clear();
                    _searchResults.Children.Add(new TextBlock
                    {
                        Text = "Type to search the current rendered scrollback.",
                        Foreground = Muted
                    });
                });
                return;
            }

            IReadOnlyList<WorldBufferSearchResult> matches = await Task.Run(
                () => _runtime.Interaction.World.Search(
                    query,
                    new WorldBufferSearchOptions(caseSensitive, regex, 100)),
                owner.Token).ConfigureAwait(false);

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_searchCts, owner)) return;
                _searchResults.Children.Clear();
                _searchResults.Children.Add(new TextBlock
                {
                    Text = matches.Count == 100 ? "100+ matches" : $"{matches.Count} match{(matches.Count == 1 ? string.Empty : "es")}",
                    Foreground = Accent,
                    FontWeight = FontWeight.SemiBold
                });
                foreach (WorldBufferSearchResult match in matches)
                {
                    StackPanel result = new() { Spacing = 2 };
                    result.Children.Add(new TextBlock
                    {
                        Text = match.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                        Foreground = Muted,
                        FontSize = NexTypography.Metadata
                    });
                    string preview = match.Text.Replace("\r", string.Empty, StringComparison.Ordinal)
                        .Replace("\n", " ", StringComparison.Ordinal).Trim();
                    result.Children.Add(new TextBlock
                    {
                        Text = preview.Length > 240 ? preview[..240] + "…" : preview,
                        Foreground = TextForeground,
                        FontFamily = UiTheme.Mono,
                        FontSize = NexTypography.Metadata,
                        TextWrapping = TextWrapping.Wrap
                    });
                    _searchResults.Children.Add(Card(result, PanelBorderBrush));
                }
            });
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
        }
    }

    private void ShowCommandPalette()
    {
        Window palette = new()
        {
            Title = "NexMUD Command Palette",
            Width = 660,
            Height = 590,
            MinWidth = 540,
            MinHeight = 420,
            Background = WindowBackground,
            Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        TextBox filter = new()
        {
            PlaceholderText = "Search commands, panels, and actions…",
            Background = ConsoleBackground,
            Foreground = TextForeground,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            FontSize = NexTypography.BodyStrong
        };

        StackPanel results = new() { Spacing = 2 };
        ScrollViewer scroll = new()
        {
            Content = results,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        PaletteCommand[] commands =
        [
            new("NAVIGATE", "Focus World", "Return keyboard focus to the persistent World command surface", null,
                () => { ShowTool(ToolView.Context); return Task.CompletedTask; }),
            new("NAVIGATE", "Character / Inventory", "Open equipment, item inspection and carried inventory while keeping World visible", null,
                () => { ShowTool(ToolView.Character); return Task.CompletedTask; }),
            new("NAVIGATE", "Skills", "Open the consolidated ability catalog", null,
                () => { ShowTool(ToolView.Abilities); return Task.CompletedTask; }),
            new("NAVIGATE", "Spells", "Open the consolidated ability catalog", null,
                () => { ShowTool(ToolView.Abilities); return Task.CompletedTask; }),
            new("NAVIGATE", "Automation", "Aliases, keybindings, triggers, state rules, workflows and activity", null,
                () => { ShowTool(ToolView.Automation); return Task.CompletedTask; }),
            new("NAVIGATE", "Scripting", "Open runtime, script-package and Jint module tooling without hiding World", null,
                () => { ShowTool(ToolView.Scripting); return Task.CompletedTask; }),
            new("NAVIGATE", "Jev decision inspector", "Typed Choice / Score / Noul decisions and authority", null,
                () => { ShowTool(ToolView.Jev); return Task.CompletedTask; }),
            new("NAVIGATE", "Codex", "Persistent world, entity and encounter memory", null,
                () => { ShowTool(ToolView.Knowledge); return Task.CompletedTask; }),
            new("NAVIGATE", "Mapper", "Visual world graph, destination search, route planning and auto-move", null,
                () => { ShowTool(ToolView.Map); return Task.CompletedTask; }),
            new("NAVIGATE", "Pause auto-move", "Pause the active mapped route without discarding its destination", null,
                () => _runtime.Navigator.PauseAsync(cancellationToken: _cts.Token)),
            new("NAVIGATE", "Resume auto-move", "Re-plan from the current room and continue toward the selected destination", null,
                () => _runtime.Navigator.ResumeAsync(_cts.Token)),
            new("NAVIGATE", "Stop auto-move", "Stop the active mapped route immediately", null,
                () => _runtime.Navigator.StopAsync(cancellationToken: _cts.Token)),
            new("TRANSCRIPT", "Search transcript", "Search the current session without altering output", "⌘F / Ctrl+F",
                () => { ShowTool(ToolView.Search); Dispatcher.UIThread.Post(() => _searchQuery.Focus()); return Task.CompletedTask; }),
            new("TRANSCRIPT", "Return to live output", "Leave scrollback and follow the live tail", "⌘G / Ctrl+G",
                () => { ReturnToLive(); return Task.CompletedTask; }),
            new("TRANSCRIPT", "Toggle session logging", "Start or stop the configured transcript log", "⌘L / Ctrl+L",
                () => { ToggleLogging(); return Task.CompletedTask; }),
            new("JEV", _runtime.Authority.Enabled ? "Disable Jev" : "Enable Jev", "Master Jev control without changing configured authority", null, ToggleJevEnabledAsync),
            new("JEV", "Evaluate combat now", "Run a fresh typed combat decision against current state", null, EvaluateCombatAsync),
            new("JEV", "Evaluate recovery now", "Run a typed recovery decision against current resources and position", null, EvaluateRecoveryAsync),
            new("JEV", "Evaluate navigation now", "Run a typed route decision against current exits and room memory", null, EvaluateNavigationAsync),
            new("CLIENT", "Settings", "General, appearance, transcript, input, connections, data and advanced", null, ShowSettingsAsync),
            new("CLIENT", "Connect / Disconnect", "Toggle the configured MUD connection", null, ToggleConnectionAsync),
            new("MUD", "Look", "Request the current room description", null, () => SubmitCommandAsync("look")),
            new("MUD", "Score", "Refresh character sheet and combat stats", null, () => SubmitCommandAsync("score")),
            new("MUD", "Inventory", "Refresh carried inventory", null, () => SubmitCommandAsync("inventory")),
            new("MUD", "Equipment", "Refresh worn equipment", null, () => SubmitCommandAsync("equipment")),
            new("MUD", "Skills", "Refresh the complete skill catalog", null, () => SubmitCommandAsync("skills")),
            new("MUD", "Spells", "Refresh the spell catalog", null, () => SubmitCommandAsync("spells")),
            new("MUD", "Where", "Request nearby character locations", null, () => SubmitCommandAsync("where"))
        ];

        List<PaletteCommand> visible = [];
        int selectedIndex = 0;

        async Task ExecuteAsync(PaletteCommand command)
        {
            palette.Close();
            await command.Execute();
        }

        void RenderPalette()
        {
            results.Children.Clear();
            string query = (filter.Text ?? string.Empty).Trim();
            visible = commands
                .Where(command => query.Length == 0 ||
                                  command.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                  command.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                  command.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (visible.Count == 0)
            {
                selectedIndex = 0;
                results.Children.Add(new TextBlock
                {
                    Text = "No matching commands",
                    Foreground = Muted,
                    Margin = new Thickness(10, 14)
                });
                return;
            }

            selectedIndex = Math.Clamp(selectedIndex, 0, visible.Count - 1);
            for (int index = 0; index < visible.Count; index++)
            {
                PaletteCommand command = visible[index];
                bool selected = index == selectedIndex;
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                StackPanel copy = new() { Spacing = 2 };
                copy.Children.Add(new TextBlock
                {
                    Text = command.Category,
                    Foreground = selected ? Accent : Muted,
                    FontSize = NexTypography.Metadata,
                    FontWeight = FontWeight.SemiBold
                });
                copy.Children.Add(new TextBlock
                {
                    Text = command.Label,
                    Foreground = TextForeground,
                    FontSize = NexTypography.BodyStrong,
                    FontWeight = FontWeight.SemiBold
                });
                copy.Children.Add(new TextBlock
                {
                    Text = command.Description,
                    Foreground = Muted,
                    FontSize = NexTypography.Body,
                    TextWrapping = TextWrapping.Wrap
                });
                row.Children.Add(copy);
                if (!string.IsNullOrWhiteSpace(command.Shortcut))
                {
                    TextBlock shortcut = new()
                    {
                        Text = command.Shortcut,
                        Foreground = Muted,
                        FontSize = NexTypography.Metadata,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(18, 0, 0, 0)
                    };
                    Grid.SetColumn(shortcut, 1);
                    row.Children.Add(shortcut);
                }

                Button item = new()
                {
                    Content = row,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = selected ? RaisedBackground : Brushes.Transparent,
                    Foreground = TextForeground,
                    BorderBrush = PanelBorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(10, 7),
                    Margin = new Thickness(0)
                };
                item.Click += async (_, _) => await ExecuteAsync(command);
                results.Children.Add(item);
            }
        }

        filter.TextChanged += (_, _) =>
        {
            selectedIndex = 0;
            RenderPalette();
        };
        filter.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                palette.Close();
                return;
            }
            if (e.Key == Key.Down && visible.Count > 0)
            {
                e.Handled = true;
                selectedIndex = (selectedIndex + 1) % visible.Count;
                RenderPalette();
                return;
            }
            if (e.Key == Key.Up && visible.Count > 0)
            {
                e.Handled = true;
                selectedIndex = (selectedIndex - 1 + visible.Count) % visible.Count;
                RenderPalette();
                return;
            }
            if (e.Key == Key.Enter && visible.Count > 0)
            {
                e.Handled = true;
                await ExecuteAsync(visible[selectedIndex]);
            }
        };

        StackPanel heading = new() { Spacing = 3 };
        heading.Children.Add(new TextBlock
        {
            Text = "COMMAND PALETTE",
            Foreground = Accent,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Navigate, inspect, and act without leaving the command line.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata
        });

        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(14)
        };
        root.Children.Add(heading);
        Grid.SetRow(filter, 1);
        filter.Margin = new Thickness(0, 10, 0, 8);
        root.Children.Add(filter);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        palette.Content = root;
        RenderPalette();
        palette.Opened += (_, _) => filter.Focus();
        _ = palette.ShowDialog(this);
    }

    private async Task SubmitUserInputAsync(string input, InputSourceKind source = InputSourceKind.Keyboard)
    {
        try
        {
            InputSubmissionResult submission = await _runtime.Interaction.Input.SubmitAsync(
                new InputRequest(source, input, DateTimeOffset.Now, Guid.NewGuid()),
                _snapshot.Session.InputMode,
                _cts.Token).ConfigureAwait(false);

            foreach (LocalCommandResult result in submission.Results)
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                    Dispatcher.UIThread.Post(() => ShowClientMessage(result.Message));
                if (result.ExitRequested)
                    Dispatcher.UIThread.Post(Close);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => ShowClientMessage($"Command failed: {exception.Message}", error: true));
        }
    }

    private async Task SubmitCommandAsync(string command)
    {
        try
        {
            InputSubmissionResult submission = await _runtime.Interaction.Input.SubmitAsync(
                new InputRequest(InputSourceKind.UiAction, command, DateTimeOffset.Now, Guid.NewGuid()),
                _snapshot.Session.InputMode,
                _cts.Token).ConfigureAwait(false);
            foreach (LocalCommandResult result in submission.Results)
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                    Dispatcher.UIThread.Post(() => ShowClientMessage(result.Message));
                if (result.ExitRequested)
                    Dispatcher.UIThread.Post(Close);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => ShowClientMessage($"Command failed: {exception.Message}", error: true));
        }
    }

    private async Task ToggleJevEnabledAsync()
    {
        bool enabled = !_runtime.Authority.Enabled;
        try
        {
            await _runtime.SetJevEnabledAsync(enabled, _cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                UpdateJevToggle();
                UpdateJevHud(_snapshot);
                RenderPersistentGameplay();
                if (_activeTool == ToolView.Jev) RefreshActiveWorkspace();
                ShowClientMessage(enabled
                    ? "Jev enabled. Configured domain authority is active."
                    : "Jev disabled. Domain authority settings were preserved.");
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => ShowClientMessage($"Could not change Jev state: {exception.Message}", error: true));
        }
    }

    private void UpdateJevToggle()
    {
        bool enabled = _runtime.Authority.Enabled;
        _jevToggle.Content = NexQuickAction.IconLabel(
            NexIconKind.JevState,
            enabled ? "ON" : "OFF",
            enabled ? NexMudTheme.Success : NexMudTheme.Muted,
            14);
        _jevToggle.Foreground = enabled ? NexMudTheme.Success : NexMudTheme.Muted;
        _jevToggle.BorderBrush = enabled ? NexMudTheme.Success : NexMudTheme.Divider;
        _jevToggle.Background = NexMudTheme.RaisedSurfaceGradient;
    }

    private void ScheduleTerminalSizeUpdate()
    {
        if (!_runtime.Transport.IsConnected) return;

        double width = _gameScroll.Bounds.Width;
        double height = _gameScroll.Bounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;

        double fontSize = NexTranscriptTypography.NormalizeFontSize(_runtime.Settings.TranscriptFontSize);
        double lineHeight = NexTranscriptTypography.LineHeight(fontSize);
        ushort columns = checked((ushort)Math.Clamp((int)Math.Floor(width / (fontSize * 0.62)), 20, 500));
        ushort rows = checked((ushort)Math.Clamp((int)Math.Floor(height / lineHeight), 5, 200));

        CancellationTokenSource next = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        CancellationToken token = next.Token;
        CancellationTokenSource? previous = Interlocked.Exchange(ref _nawsResizeCts, next);
        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                await _runtime.Transport.UpdateTerminalSizeAsync(columns, rows, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await _runtime.Events.PublishAsync(
                    new ProtocolError("NAWS", exception.Message),
                    "gui.naws",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    private async Task ToggleConnectionAsync()
    {
        if (_snapshot.Session.ConnectionStatus != ConnectionStatus.Disconnected)
        {
            await _runtime.Transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        ConnectionProfile profile = _runtime.ActiveConnectionProfile;
        string host = profile.Host;
        int port = profile.Port;
        bool tls = profile.UseTls;
        try
        {
            await _runtime.Transport.ConnectAsync(
                _runtime.CreateConnectionOptions(host, port, tls),
                _cts.Token).ConfigureAwait(false);
            await _runtime.SaveConnectionSettingsAsync(host, port, tls, _cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(ScheduleTerminalSizeUpdate);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => ShowClientMessage($"Connect failed: {exception.Message}", error: true));
        }
    }

    public Task ShowSettingsAsync()
    {
        if (_settingsOverlay.IsVisible)
        {
            _settingsWorkspace?.Focus();
            return Task.CompletedTask;
        }

        _settingsWorkspace = new SettingsWorkspace(
            _runtime,
            () => CloseSettingsOverlay(saved: false),
            () => CloseSettingsOverlay(saved: true));
        _settingsSheet.Child = _settingsWorkspace;
        _settingsOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => _settingsWorkspace?.Focus());
        return Task.CompletedTask;
    }

    private void CloseSettingsOverlay(bool saved)
    {
        _settingsOverlay.IsVisible = false;
        _settingsSheet.Child = null;
        _settingsWorkspace = null;
        if (saved)
        {
            ApplyUiSettings();
            ScheduleTerminalSizeUpdate();
            RenderState(_snapshot);
            ShowClientMessage("Settings saved.");
        }
        _command.Focus();
    }

    public void ShowAbout()
    {
        Window about = new()
        {
            Title = "About NexMUD",
            Width = 420,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBackground,
            Icon = Icon
        };
        StackPanel content = new()
        {
            Margin = new Thickness(26),
            Spacing = 10
        };
        content.Children.Add(new TextBlock
        {
            Text = "NexMUD",
            Foreground = TextForeground,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Version 0.30.0",
            Foreground = UiTheme.Faint,
            FontSize = UiTheme.TextSm
        });
        content.Children.Add(new TextBlock
        {
            Text = "A modern MUD client with optional Jev decision support.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "The MUD server sees a normal xterm-compatible Telnet client; Jev integration stays local to the client.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });
        Button close = GhostButton("Close");
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.Margin = new Thickness(0, 10, 0, 0);
        close.Click += (_, _) => about.Close();
        content.Children.Add(close);
        about.Content = content;
        about.ShowDialog(this);
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (envelope.Payload)
                {
                    case GameObservationReceived observation when
                        observation.Observation.Kind is ObservationKind.Text or ObservationKind.ReplayMarker:
                        _logWriter.Write(observation.Observation.RawText);
                        break;
                    case ConnectionStateChanged connection:
                        HandleLoggingConnectionState(connection);
                        break;
                    case JevDecisionProduced decision:
                        Dispatcher.UIThread.Post(() => RenderDecision(decision.Decision));
                        break;
                    case ItemIdentified identified:
                        _itemKnowledgeCache[identified.Item.Name] = new ItemKnowledge(
                            identified.Item.Name,
                            identified.Item.Flags,
                            identified.Item.Weight,
                            identified.Item.WearLocations,
                            identified.Item.Level,
                            identified.Item.Material,
                            identified.Item.ItemType,
                            identified.Item.WeaponType,
                            identified.Item.WeaponFlags,
                            identified.Item.DamageType,
                            identified.Item.DamageDice,
                            identified.Item.DamageAverage,
                            identified.Item.ExtraFields,
                            1,
                            envelope.Timestamp);
                        Dispatcher.UIThread.Post(() =>
                        {
                            _lastItemIdentification = identified.Item;
                            _codexWorkspace.NotifyKnowledgeChanged();
                            RenderPersistentGameplay();
                            ShowClientMessage($"Captured item reference: {identified.Item.Name}");
                        });
                        break;
                    case AbilityHelpObserved help:
                        _abilityKnowledgeCache[help.Help.Name] = new AbilityHelpKnowledge(
                            help.Help.Name,
                            help.Help.Kind,
                            help.Help.ActivationLagRounds,
                            help.Help.ActivationManaCost,
                            help.Help.Syntax,
                            help.Help.Description,
                            help.Help.Fields,
                            1,
                            envelope.Timestamp);
                        Dispatcher.UIThread.Post(() =>
                        {
                            _lastAbilityHelp = help.Help;
                            _codexWorkspace.NotifyKnowledgeChanged();
                            if (_activeTool == ToolView.Abilities)
                            {
                                RefreshActiveWorkspace();
                            }
                            ShowClientMessage($"Captured {help.Help.Kind.ToString().ToLowerInvariant()} help: {help.Help.Name}");
                        });
                        break;
                    case JevApprovalRequested approval:
                        Dispatcher.UIThread.Post(() =>
                        {
                            _pendingJevApprovalId = approval.DecisionId;
                            _pendingJevCommand = approval.Command;
                            _jevExecutionStatus = "Waiting for approval.";
                            ShowTool(ToolView.Jev);
                        });
                        break;
                    case JevApprovalResolved approval:
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_pendingJevApprovalId == approval.DecisionId)
                            {
                                _pendingJevApprovalId = null;
                                _pendingJevCommand = null;
                            }
                            _jevExecutionStatus = approval.Approved
                                ? "Approved and queued after revalidation."
                                : approval.Reason ?? "Approval rejected.";
                            if (_activeTool == ToolView.Jev) RefreshActiveWorkspace();
                        });
                        break;
                    case JevDecisionExecutionSkipped skipped:
                        Dispatcher.UIThread.Post(() =>
                        {
                            _jevExecutionStatus = skipped.Reason;
                            if (_activeTool == ToolView.Jev) RefreshActiveWorkspace();
                        });
                        break;
                    case JevEnabledChanged enabled:
                        Dispatcher.UIThread.Post(() =>
                        {
                            UpdateJevToggle();
                            UpdateJevHud(_snapshot);
                            RenderPersistentGameplay();
                            if (_activeTool == ToolView.Jev) RefreshActiveWorkspace();
                        });
                        break;
                    case AutoMoveStateChanged navigation:
                        Dispatcher.UIThread.Post(() =>
                        {
                            _mapperWorkspace.UpdateNavigation(navigation);
                            if (navigation.Status == AutoMoveStatus.Failed && !string.IsNullOrWhiteSpace(navigation.Reason))
                            {
                                ShowClientMessage($"Auto-move: {navigation.Reason}", error: true);
                            }
                            else if (navigation.Status == AutoMoveStatus.Completed)
                            {
                                ShowClientMessage("Auto-move: destination reached.");
                            }
                        });
                        break;
                    case AutomationKeybindingInvoked _:
                    case AutomationRuleMatched _:
                    case AutomationVariableChanged _:
                    case AutomationWorkflowStateChanged _:
                    case ScriptLogEmitted { ModuleId: "automation.profile" }:
                        _automationWorkspace.HandleEvent(envelope.Payload, envelope.Timestamp);
                        break;
                    case ScriptRuntimeTaskFaulted fault:
                        Dispatcher.UIThread.Post(() => ShowClientMessage(
                            $"{fault.OwnerName} / {fault.Operation}: {fault.Message}",
                            error: true));
                        break;
                    case ScriptUiNotificationRequested notification:
                        Dispatcher.UIThread.Post(() => ShowClientMessage(
                            $"{notification.Title}: {notification.Message}",
                            error: notification.Level.Equals("Error", StringComparison.OrdinalIgnoreCase)));
                        break;
                    case ComponentError error:
                        Dispatcher.UIThread.Post(() => ShowClientMessage($"{error.Component}: {error.Message}", error: true));
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConsumeStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateSnapshot snapshot in _snapshots.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _snapshot = snapshot;
                await WarmEquipmentKnowledgeAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await WarmAbilityKnowledgeAsync(snapshot, cancellationToken).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => RenderState(snapshot));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WarmEquipmentKnowledgeAsync(StateSnapshot snapshot, CancellationToken cancellationToken)
    {
        string[] names = snapshot.Character.Equipment.Slots
            .Where(slot => !slot.IsEmpty && !string.IsNullOrWhiteSpace(slot.Item))
            .Select(slot => slot.Item!.Trim())
            .Concat(snapshot.Character.CarriedItems.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !_itemKnowledgeCache.ContainsKey(name) && _itemKnowledgeLookups.TryAdd(name, 0))
            .ToArray();

        foreach (string name in names)
        {
            try
            {
                ItemKnowledge? item = await _runtime.Knowledge.GetItemAsync(name, cancellationToken).ConfigureAwait(false);
                if (item is not null)
                {
                    _itemKnowledgeCache[name] = item;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Knowledge lookup is enrichment only. Live MUD state must never depend on SQLite availability.
            }
        }
    }

    private async Task WarmAbilityKnowledgeAsync(StateSnapshot snapshot, CancellationToken cancellationToken)
    {
        string[] names = snapshot.Character.Skills
            .Where(skill => skill.Availability == SkillAvailability.Available)
            .Select(skill => skill.Name)
            .Concat(snapshot.Character.Spells
                .Where(spell => spell.Availability == SkillAvailability.Available)
                .Select(spell => spell.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !_abilityKnowledgeCache.ContainsKey(name) && _abilityKnowledgeLookups.TryAdd(name, 0))
            .ToArray();

        foreach (string name in names)
        {
            try
            {
                AbilityHelpKnowledge? help = await _runtime.Knowledge.GetAbilityHelpAsync(name, cancellationToken).ConfigureAwait(false);
                if (help is not null)
                {
                    _abilityKnowledgeCache[name] = help;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Durable help is optional enrichment and must not delay or break live state.
            }
        }
    }

    private void HandleWorldBufferAppended(WorldBufferEntry entry)
    {
        if (entry.IsGagged) return;
        Dispatcher.UIThread.Post(() =>
        {
            AppendWorldEntry(entry);
            if (!_followTail)
            {
                _unseenOutputCount++;
                _jumpLive.Content = $"Return to live • {_unseenOutputCount} new";
            }
            if (_activeTool == ToolView.Search && !string.IsNullOrWhiteSpace(_searchQuery.Text))
                ScheduleSearchResults();
        });
    }

    private void AppendWorldEntry(WorldBufferEntry entry)
    {
        AppendWorldEntry(_gameText, entry);
        TrimRenderedTranscriptIfNeeded();
        if (_livePane.IsVisible)
        {
            AppendWorldEntry(_liveText, entry);
            Dispatcher.UIThread.Post(_liveScroll.ScrollToEnd, DispatcherPriority.Background);
        }
        if (_followTail)
        {
            _newOutputIndicator.IsVisible = false;
            Dispatcher.UIThread.Post(_gameScroll.ScrollToEnd, DispatcherPriority.Background);
        }
        else
        {
            _newOutputIndicator.IsVisible = true;
        }
    }

    private void AppendWorldEntry(SelectableTextBlock target, WorldBufferEntry entry)
    {
        string timestamp = FormatTimestamp(entry.Timestamp);
        if (timestamp.Length > 0)
        {
            target.Inlines!.Add(new Run(timestamp)
            {
                Foreground = NexMudTheme.Faint,
                FontFamily = NexMudTheme.Terminal,
                FontWeight = FontWeight.Normal
            });
        }
        foreach (WorldStyledRun run in entry.StyledRuns)
        {
            if (run.Text.Length == 0) continue;
            target.Inlines!.Add(CreateTranscriptRun(run));
        }
    }

    private string FormatTimestamp(DateTimeOffset timestamp)
    {
        TimestampRenderMode mode = (_runtime.Settings.Output ?? new OutputPreferences()).TimestampMode;
        DateTimeOffset local = timestamp.ToLocalTime();
        return mode switch
        {
            TimestampRenderMode.Time => $"[{local:HH:mm:ss}] ",
            TimestampRenderMode.TimeWithMilliseconds => $"[{local:HH:mm:ss.fff}] ",
            TimestampRenderMode.DateTime => $"[{local:yyyy-MM-dd HH:mm:ss}] ",
            _ => string.Empty
        };
    }

    private static Run CreateTranscriptRun(WorldStyledRun segment)
    {
        AnsiTextStyle style = segment.AnsiStyle;
        IBrush foreground = SegmentForeground(style);
        IBrush? background = SegmentBackground(style);
        if (style.Reverse)
            (foreground, background) = (background ?? ConsoleBackground, foreground);

        OutputPresentationStyle? overlay = segment.Override;
        if (!string.IsNullOrWhiteSpace(overlay?.Foreground)) foreground = Brush(overlay.Foreground);
        if (!string.IsNullOrWhiteSpace(overlay?.Background)) background = Brush(overlay.Background);

        bool bold = style.Bold || overlay?.Bold == true;
        bool underline = style.Underline || overlay?.Underline == true;
        Run run = new(segment.Text)
        {
            FontFamily = NexMudTheme.Terminal,
            Foreground = foreground,
            Background = background,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.Italic || overlay?.Italic == true ? FontStyle.Italic : FontStyle.Normal
        };
        if (underline) run.TextDecorations = TextDecorations.Underline;
        return run;
    }

    private void RebuildTranscript()
    {
        _gameText.Inlines!.Clear();
        foreach (WorldBufferEntry entry in _runtime.Interaction.World.Tail(RenderedTranscriptSegmentLimit))
            AppendWorldEntry(_gameText, entry);
        if (_livePane.IsVisible) RebuildLiveTranscript();
        if (_followTail)
            Dispatcher.UIThread.Post(_gameScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void HandleOutputNotification(OutputNotification notification)
    {
        Dispatcher.UIThread.Post(() =>
            ShowClientMessage(notification.Beep ? $"Sound alert: {notification.Message}" : notification.Message));
    }

    private void HandleOutputRuleDiagnostic(OutputRuleDiagnostic diagnostic)
    {
        Dispatcher.UIThread.Post(() => ShowClientMessage(
            $"Output rule {diagnostic.RuleId}: {diagnostic.Message}",
            error: true));
    }

    private void HandleLoggingConnectionState(ConnectionStateChanged connection)
    {
        if (connection.Status == ConnectionStatus.Connected)
        {
            if (_runtime.Settings.AutoLogSessions && !_logWriter.IsActive)
            {
                StartLogging();
            }
            return;
        }

        if (connection.Status == ConnectionStatus.Disconnected && _logWriter.IsActive)
        {
            StopLogging();
        }
    }

    private void ToggleLogging()
    {
        if (_logWriter.IsActive)
        {
            StopLogging();
            return;
        }

        if (_snapshot.Session.ConnectionStatus != ConnectionStatus.Connected)
        {
            ShowClientMessage("Connect before starting a session log.");
            return;
        }
        StartLogging();
    }

    private void StartLogging()
    {
        string host = _snapshot.Session.Host ?? _runtime.ActiveConnectionProfile.Host;
        string path = _logWriter.Start(GetLogDirectory(), host, _runtime.Settings.LogFormat);
        Dispatcher.UIThread.Post(() =>
        {
            RenderLoggingState();
            ShowClientMessage($"Logging server output to {path}");
        });
    }

    private void StopLogging()
    {
        string? path = _logWriter.CurrentPath;
        _logWriter.Stop();
        Dispatcher.UIThread.Post(() =>
        {
            RenderLoggingState();
            if (!string.IsNullOrWhiteSpace(path))
            {
                ShowClientMessage($"Session log saved to {path}");
            }
        });
    }

    private void RenderLoggingState()
    {
        bool active = _logWriter.IsActive;
        _logButton.Content = NexMudIcons.Create(
            NexIconKind.Log,
            17,
            active ? NexMudTheme.Success : NexMudTheme.Muted);
        _logButton.Background = active ? NexMudTheme.InsetGradient : NexMudTheme.RaisedSurfaceGradient;
        _logButton.BorderBrush = active ? NexMudTheme.Success : NexMudTheme.Divider;
        ToolTip.SetTip(
            _logButton,
            active
                ? $"Logging to {_logWriter.CurrentPath}"
                : $"Start a {_runtime.Settings.LogFormat} server-output log");
    }

    private void RenderConnectionAction(bool connected, string host, int port)
    {
        string address = $"{host}:{port}";
        IBrush stateColor = connected ? NexMudTheme.Success : NexMudTheme.Muted;
        _connect.Content = NexQuickAction.IconLabel(
            NexIconKind.Connection,
            address,
            stateColor,
            14);
        _connect.Background = connected ? NexMudTheme.InsetGradient : NexMudTheme.RaisedSurfaceGradient;
        _connect.Foreground = connected ? NexMudTheme.Success : NexMudTheme.Parchment;
        _connect.BorderBrush = connected ? NexMudTheme.Success : NexMudTheme.Divider;
        ToolTip.SetTip(
            _connect,
            connected
                ? $"Connected to {address}. Click to disconnect."
                : $"Disconnected. Click to connect to {address}.");
    }

    private string GetLogDirectory()
    {
        string root = System.IO.Path.GetDirectoryName(_runtime.SettingsStore.Path)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(root, "logs");
    }

    private void ShowClientMessage(string message, bool error = false)
    {
        _notification.Text = message.Trim();
        _notification.Foreground = error ? Danger : Accent;
    }

    private void HandleKnowledgeChanged()
    {
        if (_cts.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_cts.IsCancellationRequested) return;
            EnsureRoomMetadata(_snapshot.Room.Id, force: true);
            RenderPersistentGameplay();
        }, DispatcherPriority.Background);
    }

    private void EnsureRoomMetadata(string? roomId, bool force = false)
    {
        bool sameRoom = string.Equals(_currentRoomMetadataId, roomId, StringComparison.Ordinal);
        if (sameRoom && !force) return;

        _roomMetadataCts?.Cancel();
        _roomMetadataCts?.Dispose();
        _roomMetadataCts = null;
        _currentRoomMetadataId = roomId;
        if (!sameRoom) _currentRoomMetadata = null;

        if (string.IsNullOrWhiteSpace(roomId)) return;
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _roomMetadataCts = source;
        _ = LoadRoomMetadataAsync(roomId, source.Token);
    }

    private async Task LoadRoomMetadataAsync(string roomId, CancellationToken cancellationToken)
    {
        try
        {
            MapperRoomMetadata? metadata = await _runtime.Knowledge
                .GetRoomMetadataAsync(roomId, cancellationToken)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    !string.Equals(_currentRoomMetadataId, roomId, StringComparison.Ordinal)) return;
                _currentRoomMetadata = metadata;
                RenderPersistentGameplay();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Room metadata enriches the HUD but is not required for gameplay rendering.
        }
    }

    private void RenderPersistentGameplay()
    {
        MapperRoomMetadata? metadata = string.Equals(_currentRoomMetadataId, _snapshot.Room.Id, StringComparison.Ordinal)
            ? _currentRoomMetadata
            : null;
        RoomKnowledge? memory = _runtime.Knowledge.CurrentRoom;
        if (!string.Equals(memory?.RoomId, _snapshot.Room.Id, StringComparison.Ordinal))
        {
            memory = null;
        }
        GameplayShellViewModel viewModel = GameplayShellViewModel.From(
            _snapshot,
            _runtime.Authority.Enabled,
            _jevHudLabel.Text ?? "-",
            memory,
            metadata,
            _runtime.ActiveConnectionProfile.Host,
            _runtime.ActiveConnectionProfile.Port);
        _gameplayHudPanel.Update(viewModel.Character);
        _characterHudPanel.Update(viewModel.Character);
        _roomContextPanel.Update(viewModel.Room);
        _characterWorkspace.Update(_snapshot, viewModel.Character);
    }

    private void RenderState(StateSnapshot state)
    {
        SessionInputMode previousInputMode = _snapshot.Session.InputMode;
        _snapshot = state;
        EnsureRoomMetadata(state.Room.Id);
        bool connected = state.Session.ConnectionStatus != ConnectionStatus.Disconnected;
        ConnectionProfile activeProfile = _runtime.ActiveConnectionProfile;
        string connectionHost = connected ? state.Session.Host ?? activeProfile.Host : activeProfile.Host;
        int connectionPort = connected ? state.Session.Port ?? activeProfile.Port : activeProfile.Port;
        RenderConnectionAction(connected, connectionHost, connectionPort);
        CommandInputPresentation inputPresentation = CommandInputPolicy.For(state.Session.InputMode);
        CommandInputTransitionPresentation inputTransition = CommandInputPolicy.Transition(previousInputMode, state.Session.InputMode);
        _command.PasswordChar = inputPresentation.PasswordChar;
        _command.PlaceholderText = inputPresentation.Placeholder;
        _command.IsVisible = true;
        if (inputTransition.ClearInput)
        {
            _command.Text = string.Empty;
        }
        if (inputTransition.Refocus)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _command.Focus();
                _command.CaretIndex = _command.Text?.Length ?? 0;
            }, DispatcherPriority.Background);
        }

        CharacterProfileState profile = state.Character.Profile;
        _characterLabel.Text = profile.Name is null
            ? string.Empty
            : $"{profile.Name}{(profile.Level is null ? string.Empty : $" · L{profile.Level}{(string.IsNullOrWhiteSpace(profile.ClassName) ? string.Empty : $" {profile.ClassName}")}")}";
        _locationLabel.Text = state.Room.Name ?? (connected ? "Entering the world…" : "Not connected");
        _subLocationLabel.Text = state.Room.Name is null
            ? string.Empty
            : string.Join(" · ", new[] { FormatExits(state.Room.Exits), state.Room.Terrain }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        bool vitalsKnown = state.Character.HitPoints.Maximum is not null ||
                           state.Character.Mana.Maximum is not null ||
                           state.Character.Movement.Maximum is not null;
        _gameplayHudPanel.IsVisible = connected &&
                                      (state.Session.InputMode == SessionInputMode.Normal || vitalsKnown || profile.Name is not null);
        _contextHud.IsVisible = connected && state.Room.Name is not null;
        _combatHud.IsVisible = connected && (state.Session.InputMode == SessionInputMode.Normal || vitalsKnown);
        SetVital(_hp, _hpLabel, "HP", state.Character.HitPoints);
        SetVital(_mana, _manaLabel, "MA", state.Character.Mana);
        SetVital(_move, _moveLabel, "MV", state.Character.Movement);
        _positionLabel.Text = state.Character.Position ?? "-";
        _combatLabel.Text = state.Combat.Active ? "ACTIVE" : "CLEAR";
        _combatLabel.Foreground = state.Combat.Active ? Danger : Success;
        _targetLabel.Text = FormatCombatTarget(state.Combat);
        _targetLabel.Foreground = state.Combat.Active ? Warning : Muted;
        UpdateJevHud(state);
        RenderPersistentGameplay();
        _xpLabel.Text = state.Character.Experience is null
            ? string.Empty
            : $"XP {state.Character.Experience:N0}" +
              (state.Character.ExperienceToLevel is null ? string.Empty : $"  ·  {state.Character.ExperienceToLevel:N0} to level");

        _mapperWorkspace.UpdateState(state);
        _codexWorkspace.UpdateState(state);
        RefreshActiveWorkspace();

        string mode = state.Session.InputMode == SessionInputMode.Normal ? string.Empty : $" · {state.Session.InputMode}";
        string capture = state.Session.ActiveCapture == ResponseCaptureKind.None ? string.Empty : $" · reading {state.Session.ActiveCapture}";
        AutoMoveStateChanged navState = _runtime.Navigator.Current;
        string navigationStatus = navState.Status is AutoMoveStatus.Moving or AutoMoveStatus.Paused or AutoMoveStatus.Planning or AutoMoveStatus.Recovering or AutoMoveStatus.Replanning
            ? $" · {AutoMoveDisplay.Format(navState)}"
            : string.Empty;
        _status.Text = $"{state.Session.ConnectionStatus}{mode}{capture}{navigationStatus} · state {state.Version}";
        Title = profile.Name is null ? "NexMUD" : $"{profile.Name} - NexMUD";
    }

    private static string FormatJevAction(string action)
    {
        if (action.StartsWith("move:", StringComparison.OrdinalIgnoreCase))
        {
            return action["move:".Length..];
        }
        if (action.StartsWith("engage:", StringComparison.OrdinalIgnoreCase))
        {
            return $"engage {Uri.UnescapeDataString(action["engage:".Length..])}";
        }
        return action.Replace('_', ' ');
    }

    private void UpdateJevHud(StateSnapshot state)
    {
        UpdateJevToggle();
        if (!_runtime.Authority.Enabled)
        {
            _jevHudLabel.Text = "OFF";
            _jevHudLabel.Foreground = Muted;
            return;
        }

        if (_jevEvaluationInProgress)
        {
            _jevHudLabel.Text = "BUSY";
            _jevHudLabel.Foreground = NexMudTheme.AccentBright;
            return;
        }

        if (_lastJevDecision is null)
        {
            _jevHudLabel.Text = "-";
            _jevHudLabel.Foreground = Muted;
            return;
        }

        JevDecisionTrace decision = _lastJevDecision;
        bool stale = decision.StateVersion != state.Version;
        string action = FormatJevAction(decision.Selected.Action);
        string domain = decision.Domain switch
        {
            JevDomain.Combat => "CMB",
            JevDomain.Recovery => "REC",
            JevDomain.Navigation => "NAV",
            _ => decision.Domain.ToString().ToUpperInvariant()
        };
        _jevHudLabel.Text = stale
            ? $"{domain} {action} · stale"
            : $"{domain} {action} · {decision.Selected.Probability:P0}";
        _jevHudLabel.Foreground = stale
            ? Muted
            : decision.Authority switch
            {
                JevAuthority.Auto => Danger,
                JevAuthority.Approve => Warning,
                JevAuthority.Suggest => Accent,
                _ => Muted
            };
    }

    private void RefreshActiveWorkspace()
    {
        ToolView active = NormalizeToolView(_activeTool);
        if (_dynamicWorkspaceHosts.TryGetValue(active, out ContentControl? host) && active != ToolView.Search)
        {
            host.Content = BuildDynamicToolContent(active);
        }
        if (active == ToolView.Automation)
        {
            _automationWorkspace.RefreshSnapshot();
        }
        else if (active == ToolView.Scripting)
        {
            _scriptingWorkspace.Refresh();
        }
        UpdateNavigationStyles();
    }

    private Control BuildDynamicToolContent(ToolView view) => view switch
    {
        ToolView.Abilities or ToolView.Skills or ToolView.Spells => BuildAbilitiesPanel(_snapshot.Character),
        ToolView.Jev => BuildJevPanel(_snapshot),
        ToolView.Search => BuildSearchPanel(),
        _ => new TextBlock { Text = "Tool surface unavailable.", Foreground = Muted, Margin = new Thickness(14) }
    };

    private Control BuildMapperPanel(StateSnapshot state)
    {
        _mapperWorkspace.UpdateState(state);
        return _mapperWorkspace;
    }

    private async Task ShowRoomMetadataEditorAsync(string roomId)
    {
        MapperRoomMetadata? existing = await Task.Run(
            async () => await _runtime.Knowledge.GetRoomMetadataAsync(roomId, _cts.Token).ConfigureAwait(false),
            _cts.Token).ConfigureAwait(true);
        Window dialog = new()
        {
            Title = "Room metadata",
            Width = 500,
            Height = 420,
            MinWidth = 420,
            MinHeight = 360,
            Background = WindowBackground,
            Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        StackPanel body = new() { Margin = new Thickness(18), Spacing = 8 };
        TextBox label = new() { Text = existing?.Label, PlaceholderText = "Friendly room label" };
        TextBox area = new() { Text = existing?.Area, PlaceholderText = "Area / zone" };
        TextBox notes = new() { Text = existing?.Notes, PlaceholderText = "Notes", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120 };
        CheckBox avoid = new() { Content = "Avoid this room while routing", Foreground = TextForeground, IsChecked = existing?.Avoid == true };
        body.Children.Add(FormLabel("Label")); body.Children.Add(label);
        body.Children.Add(FormLabel("Area")); body.Children.Add(area);
        body.Children.Add(FormLabel("Notes")); body.Children.Add(notes);
        body.Children.Add(avoid);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        Button cancel = GhostButton("Cancel"); cancel.Click += (_, _) => dialog.Close();
        Button save = AccentButton("Save");
        save.Click += async (_, _) =>
        {
            MapperRoomMetadata metadata = new(
                roomId, label.Text?.Trim(), area.Text?.Trim(), notes.Text?.Trim(), avoid.IsChecked == true);
            await Task.Run(
                async () => await _runtime.Knowledge.SaveRoomMetadataAsync(metadata, _cts.Token).ConfigureAwait(false),
                _cts.Token).ConfigureAwait(true);
            dialog.Close();
            _mapperWorkspace.Refresh();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); body.Children.Add(buttons);
        dialog.Content = body;
        await dialog.ShowDialog(this);
    }

    private async Task ShowSpecialExitEditorAsync(string currentRoomId)
    {
        Window dialog = new()
        {
            Title = "Special / corrected exit",
            Width = 520,
            Height = 290,
            MinWidth = 440,
            Background = WindowBackground,
            Icon = Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        StackPanel body = new() { Margin = new Thickness(18), Spacing = 8 };
        TextBox command = new() { PlaceholderText = "Movement command, e.g. enter portal" };
        TextBox target = new() { PlaceholderText = "Destination room name, label, or ID" };
        TextBlock status = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(FormLabel("Command")); body.Children.Add(command);
        body.Children.Add(FormLabel("Destination")); body.Children.Add(target);
        body.Children.Add(status);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        Button cancel = GhostButton("Cancel"); cancel.Click += (_, _) => dialog.Close();
        Button save = AccentButton("Save exit");
        save.Click += async (_, _) =>
        {
            string commandText = command.Text?.Trim() ?? string.Empty;
            string destinationText = target.Text?.Trim() ?? string.Empty;
            if (commandText.Length == 0 || destinationText.Length == 0)
            {
                status.Text = "Enter both the movement command and destination.";
                return;
            }
            IReadOnlyList<MapperRoomSearchResult> matches = await Task.Run(
                async () => await _runtime.Knowledge.FindRoomsAsync(destinationText, 8, _cts.Token).ConfigureAwait(false),
                _cts.Token).ConfigureAwait(true);
            MapperRoomSearchResult? destination = matches.FirstOrDefault();
            if (destination is null)
            {
                status.Text = "The destination room is not known yet.";
                return;
            }
            await Task.Run(
                async () => await _runtime.Knowledge.SaveMapExitAsync(currentRoomId, commandText, destination.RoomId, _cts.Token).ConfigureAwait(false),
                _cts.Token).ConfigureAwait(true);
            dialog.Close();
            _mapperWorkspace.Refresh();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); body.Children.Add(buttons);
        dialog.Content = body;
        await dialog.ShowDialog(this);
    }

    private static TextBlock FormLabel(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = NexTypography.Metadata,
        FontWeight = FontWeight.SemiBold
    };

    private Control BuildKnowledgePanel(StateSnapshot state)
    {
        _codexWorkspace.UpdateState(state);
        return _codexWorkspace;
    }

    private async Task RouteToCodexLocationAsync(CodexEntryDetail detail, CodexLocation location)
    {
        bool entityDestination = detail.Kind == CodexEntryKind.Entity || !string.IsNullOrWhiteSpace(location.EntityName);
        MapperDestinationKind destinationKind = detail.Kind == CodexEntryKind.Item && !string.IsNullOrWhiteSpace(location.EntityName)
            ? MapperDestinationKind.ItemSource
            : entityDestination ? MapperDestinationKind.Entity : MapperDestinationKind.Room;
        MapperDestinationSearchResult destination = new(
            destinationKind,
            location.EntityName ?? (detail.Kind == CodexEntryKind.Entity ? detail.Title : location.RoomName ?? detail.Title),
            location.RoomId,
            location.RoomName,
            location.Area,
            location.ObservationCount,
            location.LastSeenAt,
            false);
        ShowTool(ToolView.Map);
        await _mapperWorkspace.SetDestinationAsync(destination).ConfigureAwait(true);
    }

    private Control BuildContextPanel(StateSnapshot state)
    {
        StackPanel stack = ToolStack();
        if (state.Session.InputMode is SessionInputMode.LoginName or SessionInputMode.LoginPassword)
        {
            stack.Children.Add(SectionBlock(
                "Login",
                new TextBlock
                {
                    Text = state.Session.InputMode == SessionInputMode.LoginPassword
                        ? "Enter your password in the command line. It is masked and never kept in history."
                        : "Enter your character name in the command line.",
                    Foreground = Muted,
                    FontSize = NexTypography.Body,
                    TextWrapping = TextWrapping.Wrap
                }));
            return ToolScroll(stack);
        }

        if (state.Combat.Active)
        {
            stack.Children.Add(BuildCombatCard(state));
        }

        StackPanel roomHeading = new() { Spacing = 3 };
        roomHeading.Children.Add(new TextBlock
        {
            Text = state.Room.Name ?? "World",
            Foreground = TextForeground,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        List<string> roomMeta = [];
        if (!string.IsNullOrWhiteSpace(state.Room.Terrain)) roomMeta.Add(state.Room.Terrain);
        if (!string.IsNullOrWhiteSpace(state.Room.Light)) roomMeta.Add(state.Room.Light);
        if (state.Room.ContentsCompleteness != ObservationCompleteness.Unknown)
        {
            roomMeta.Add($"{state.Room.Contents.Count:N0} observed");
        }
        if (roomMeta.Count > 0)
        {
            roomHeading.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", roomMeta),
                Foreground = Muted,
                FontSize = NexTypography.Metadata
            });
        }
        stack.Children.Add(roomHeading);

        if (state.Room.Exits.IsKnown)
        {
            stack.Children.Add(BuildExitControls(state.Room.Exits));
        }

        List<(string Label, string Value)> roomCounts =
        [
            ("People", state.Room.Occupants.Count.ToString()),
            ("Objects", state.Room.Objects.Count.ToString()),
            ("Fixtures", state.Room.Interactables.Count.ToString()),
            ("Corpses", state.Room.Corpses.Count.ToString())
        ];
        stack.Children.Add(SectionBlock("At a glance", MetricGrid(roomCounts, 4)));

        if (state.Room.Contents.Count > 0)
        {
            stack.Children.Add(SectionBlock("Present", BuildRoomContentsTable(state.Room.Contents), state.Room.Contents.Count.ToString()));
        }
        else if (state.Room.ContentsCompleteness != ObservationCompleteness.Unknown)
        {
            stack.Children.Add(SectionBlock("Present", new TextBlock
            {
                Text = "No people, objects, or fixtures observed.",
                Foreground = Muted,
                FontSize = NexTypography.Body
            }, "0"));
        }

        RoomKnowledge? memory = _runtime.Knowledge.CurrentRoom;
        if (memory is not null && (state.Room.Id is null || string.Equals(memory.RoomId, state.Room.Id, StringComparison.Ordinal)))
        {
            List<(string Label, string Value)> memoryMetrics =
            [
                ("Visits", memory.VisitCount.ToString("N0")),
                ("Routes", memory.Exits.Count.ToString("N0")),
                ("Recurring", memory.FrequentEntities.Count.ToString("N0"))
            ];
            StackPanel roomMemory = new() { Spacing = 6 };
            roomMemory.Children.Add(MetricGrid(memoryMetrics, 3));

            if (memory.Exits.Count > 0)
            {
                roomMemory.Children.Add(BuildKnownRoutePreview(memory.Exits));
            }
            if (memory.LastSeenAt is DateTimeOffset lastSeen)
            {
                roomMemory.Children.Add(new TextBlock
                {
                    Text = $"Last observed {lastSeen.ToLocalTime():g}",
                    Foreground = UiTheme.Faint,
                    FontSize = NexTypography.Metadata
                });
            }
            stack.Children.Add(SectionBlock("Room memory", roomMemory));
        }

        StackPanel quick = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
        Button look = CommandButton("Look", "look");
        Button where = CommandButton("Where", "where");
        foreach (Button button in new[] { look, where })
        {
            button.MinHeight = 22;
            button.Padding = new Thickness(5, 2);
            button.FontSize = NexTypography.Metadata;
            quick.Children.Add(button);
        }
        stack.Children.Add(SectionBlock("Actions", quick));
        return ToolScroll(stack);
    }

    private Control BuildRoomContentsTable(IReadOnlyList<RoomContentObservation> entities)
    {
        StackPanel rows = new() { Spacing = 0 };
        foreach (RoomContentObservation entity in entities
                     .OrderBy(entity => entity.Kind)
                     .ThenBy(entity => entity.CanonicalName ?? entity.Description))
        {
            rows.Children.Add(BuildRoomContentRow(entity));
        }
        return rows;
    }

    private Control BuildRoomContentRow(RoomContentObservation entity)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("44,*,Auto"),
            ColumnSpacing = 6,
            MinHeight = 30
        };
        row.Children.Add(new TextBlock
        {
            Text = RoomEntityLabel(entity.Kind),
            Foreground = UiTheme.Faint,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        TextBlock description = new()
        {
            Text = entity.Description,
            Foreground = TextForeground,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(description, 1);
        row.Children.Add(description);

        string? target = EntityTarget(entity);
        if (!string.IsNullOrWhiteSpace(target))
        {
            StackPanel actions = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            void AddAction(string label, string command)
            {
                Button button = CommandButton(label, command);
                button.MinHeight = 20;
                button.Padding = new Thickness(4, 1);
                button.FontSize = NexTypography.Metadata;
                actions.Children.Add(button);
            }

            if (entity.Kind == RoomEntityKind.Occupant)
            {
                AddAction("Look", $"look {target}");
                AddAction("Consider", $"consider {target}");
            }
            else
            {
                AddAction("Examine", $"examine {target}");
            }
            if (entity.Traits.HasFlag(RoomEntityTraits.Readable)) AddAction("Read", $"read {target}");
            if (entity.Traits.HasFlag(RoomEntityTraits.Drinkable)) AddAction("Drink", $"drink {target}");
            if (entity.Traits.HasFlag(RoomEntityTraits.Sacrificable)) AddAction("Sacrifice", $"sacrifice {target}");

            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
        }

        return new Border
        {
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 2),
            Child = row
        };
    }

    private static string RoomEntityLabel(RoomEntityKind kind) => kind switch
    {
        RoomEntityKind.Occupant => "PERSON",
        RoomEntityKind.Object => "OBJECT",
        RoomEntityKind.Fixture => "FIXTURE",
        RoomEntityKind.Corpse => "CORPSE",
        _ => "OTHER"
    };

    private static Control BuildKnownRoutePreview(IReadOnlyList<KnowledgeExit> exits)
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10,
            RowSpacing = 2
        };
        int rowCount = (exits.Count + 1) / 2;
        for (int row = 0; row < rowCount; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (int index = 0; index < exits.Count; index++)
        {
            KnowledgeExit exit = exits[index];
            string destination = !string.IsNullOrWhiteSpace(exit.ToRoomName) ? exit.ToRoomName! : string.IsNullOrWhiteSpace(exit.ToRoomId) ? "unmapped" : "Mapped room";
            Grid route = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 5 };
            route.Children.Add(new TextBlock
            {
                Text = DirectionLabel(exit.Direction),
                Foreground = Accent,
                FontSize = NexTypography.Metadata,
                FontWeight = FontWeight.SemiBold
            });
            TextBlock destinationText = new()
            {
                Text = destination,
                Foreground = Muted,
                FontSize = NexTypography.Metadata,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(destinationText, 1);
            route.Children.Add(destinationText);
            Grid.SetColumn(route, index % 2);
            Grid.SetRow(route, index / 2);
            grid.Children.Add(route);
        }
        return grid;
    }

    private Control BuildCombatCard(StateSnapshot state)
    {
        string target = state.Combat.TargetName ?? state.Combat.TargetId ?? "Target";
        StackPanel content = new() { Spacing = 7 };
        content.Children.Add(new TextBlock
        {
            Text = target,
            Foreground = TextForeground,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = state.Combat.TargetCondition ?? "Condition unknown",
            Foreground = Warning,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap
        });
        if (state.Combat.LastPlayerDamage is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Last hit: {state.Combat.LastPlayerDamage.AbsoluteTerm ?? ""} {state.Combat.LastPlayerDamage.DamageType} · {state.Combat.LastPlayerDamage.RelativeTerm}".Trim(),
                Foreground = Muted,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap
            });
        }
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(CommandButton("Flee", "flee"));
        if (_runtime.DecisionEngine is not null)
        {
            Button evaluate = AccentButton("Evaluate with Jev");
            evaluate.Click += async (_, _) => await EvaluateCombatAsync();
            actions.Children.Add(evaluate);
        }
        content.Children.Add(actions);
        return Card(content, Danger);
    }

    private Control BuildAbilitiesPanel(CharacterState character)
    {
        StackPanel stack = ToolStack();

        SkillState[] skills = character.Skills
            .OrderBy(skill => skill.RequiredLevel)
            .ThenBy(skill => skill.Name)
            .ToArray();
        SpellState[] spells = character.Spells
            .OrderBy(spell => spell.RequiredLevel)
            .ThenBy(spell => spell.Name)
            .ToArray();

        int readySkills = skills.Count(skill => skill.Availability == SkillAvailability.Available);
        int readySpells = spells.Count(spell => spell.Availability == SkillAvailability.Available);
        int lockedSkills = skills.Length - readySkills;
        int lockedSpells = spells.Length - readySpells;

        Grid actions = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
        actions.Children.Add(new TextBlock
        {
            Text = "Character capability",
            Foreground = Muted,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center
        });
        Button refreshSkills = CommandButton("Refresh skills", "skills");
        refreshSkills.FontSize = NexTypography.Metadata;
        refreshSkills.Padding = new Thickness(5, 2);
        refreshSkills.MinHeight = 22;
        Grid.SetColumn(refreshSkills, 1);
        actions.Children.Add(refreshSkills);
        Button refreshSpells = CommandButton("Refresh spells", "spells");
        refreshSpells.FontSize = NexTypography.Metadata;
        refreshSpells.Padding = new Thickness(5, 2);
        refreshSpells.MinHeight = 22;
        Grid.SetColumn(refreshSpells, 2);
        actions.Children.Add(refreshSpells);
        stack.Children.Add(actions);

        List<(string Label, string Value)> summary =
        [
            ("Skills", skills.Length.ToString("N0")),
            ("Ready", (readySkills + readySpells).ToString("N0")),
            ("Locked", (lockedSkills + lockedSpells).ToString("N0")),
            ("Spells", spells.Length.ToString("N0"))
        ];
        stack.Children.Add(SectionBlock("Overview", MetricGrid(summary, 4)));

        if (skills.Length > 0)
        {
            List<(string Name, string Value, AbilityDomain Domain, bool Available, bool Fresh)> entries = skills
                .Select(skill => (
                    skill.Name,
                    AbilityDisplayValue(skill.Availability, skill.RequiredLevel, skill.ProficiencyPercent),
                    skill.Domain,
                    skill.Availability == SkillAvailability.Available,
                    skill.IsFresh))
                .ToList();
            stack.Children.Add(SectionBlock("Skills", BuildAbilityGrid(entries), skills.Length.ToString()));
        }
        else if (character.SkillsCompleteness == ObservationCompleteness.Complete)
        {
            stack.Children.Add(SectionBlock("Skills", new TextBlock
            {
                Text = "No skills found.",
                Foreground = Muted,
                FontSize = NexTypography.Body
            }, "0"));
        }

        if (spells.Length > 0)
        {
            List<(string Name, string Value, AbilityDomain Domain, bool Available, bool Fresh)> entries = spells
                .Select(spell => (
                    spell.Name,
                    AbilityDisplayValue(spell.Availability, spell.RequiredLevel, spell.ProficiencyPercent),
                    spell.Domain,
                    spell.Availability == SkillAvailability.Available,
                    spell.IsFresh))
                .ToList();
            stack.Children.Add(SectionBlock("Spells", BuildAbilityGrid(entries), spells.Length.ToString()));
        }
        else if (character.SpellsCompleteness == ObservationCompleteness.Complete)
        {
            stack.Children.Add(SectionBlock("Spells", new TextBlock
            {
                Text = "No spells available.",
                Foreground = Muted,
                FontSize = NexTypography.Body
            }, "0"));
        }

        if (_lastAbilityHelp is not null)
        {
            stack.Children.Add(SectionBlock(
                "Reference",
                BuildAbilityHelpReference(_lastAbilityHelp),
                _lastAbilityHelp.Name));
        }

        return ToolScroll(stack);
    }

    private static string AbilityDisplayValue(
        SkillAvailability availability,
        int requiredLevel,
        int? proficiencyPercent)
    {
        if (availability == SkillAvailability.Available)
        {
            return proficiencyPercent is null ? "N/A" : $"{proficiencyPercent}%";
        }

        return requiredLevel > 0 ? $"Level {requiredLevel}" : "N/A";
    }

    private Control BuildSkillsPanel(CharacterState character)
    {
        StackPanel stack = ToolStack();
        stack.Children.Add(PanelActionHeader(
            character.SkillsCompleteness == ObservationCompleteness.Unknown
                ? "Skills have not been observed yet."
                : $"{character.Skills.Count} skills · {character.SkillsCompleteness}",
            "Refresh",
            "skills"));

        IReadOnlyList<SkillState> available = character.Skills
            .Where(skill => skill.Availability == SkillAvailability.Available)
            .OrderBy(skill => skill.RequiredLevel)
            .ThenBy(skill => skill.Name)
            .ToArray();
        IReadOnlyList<SkillState> future = character.Skills
            .Where(skill => skill.Availability == SkillAvailability.Unavailable)
            .OrderBy(skill => skill.RequiredLevel)
            .ThenBy(skill => skill.Name)
            .ToArray();

        if (available.Count > 0)
        {
            StackPanel rows = new() { Spacing = 0 };
            foreach (SkillState skill in available)
            {
                string value = skill.ProficiencyPercent is null ? "n/a" : $"{skill.ProficiencyPercent}%";
                rows.Children.Add(AbilityRow(skill.Name, value, skill.Domain, true, skill.IsFresh));
            }
            stack.Children.Add(CollapsibleSection("Available", rows, expanded: true, badge: available.Count.ToString()));
        }
        if (future.Count > 0)
        {
            StackPanel rows = new() { Spacing = 0 };
            foreach (SkillState skill in future)
            {
                string value = skill.ProficiencyPercent is null ? "n/a" : $"{skill.ProficiencyPercent}%";
                rows.Children.Add(AbilityRow(skill.Name, $"Level {skill.RequiredLevel} · {value}", skill.Domain, false, skill.IsFresh));
            }
            stack.Children.Add(CollapsibleSection("Future", rows, badge: future.Count.ToString()));
        }
        if (_lastAbilityHelp?.Kind == AbilityHelpKind.Skill)
        {
            stack.Children.Add(CollapsibleSection(
                "Reference",
                BuildAbilityHelpReference(_lastAbilityHelp),
                expanded: true,
                badge: _lastAbilityHelp.Name));
        }
        return ToolScroll(stack);
    }

    private Control BuildSpellsPanel(CharacterState character)
    {
        StackPanel stack = ToolStack();
        stack.Children.Add(PanelActionHeader(
            character.SpellsCompleteness == ObservationCompleteness.Unknown
                ? "Spells have not been observed yet."
                : $"{character.Spells.Count} spells · {character.SpellsCompleteness}",
            "Refresh",
            "spells"));

        if (character.Spells.Count == 0 && character.SpellsCompleteness == ObservationCompleteness.Complete)
        {
            stack.Children.Add(Card("No spells", "This character currently has no spells in the Avendar catalog."));
            return ToolScroll(stack);
        }

        SpellState[] available = character.Spells
            .Where(spell => spell.Availability == SkillAvailability.Available)
            .OrderBy(spell => spell.RequiredLevel)
            .ThenBy(spell => spell.Name)
            .ToArray();
        SpellState[] future = character.Spells
            .Where(spell => spell.Availability == SkillAvailability.Unavailable)
            .OrderBy(spell => spell.RequiredLevel)
            .ThenBy(spell => spell.Name)
            .ToArray();

        if (available.Length > 0)
        {
            StackPanel rows = new() { Spacing = 0 };
            foreach (SpellState spell in available)
            {
                string value = spell.ProficiencyPercent is null ? "n/a" : $"{spell.ProficiencyPercent}%";
                rows.Children.Add(AbilityRow(spell.Name, value, spell.Domain, true, spell.IsFresh));
            }
            stack.Children.Add(CollapsibleSection("Available", rows, expanded: true, badge: available.Length.ToString()));
        }
        if (future.Length > 0)
        {
            StackPanel rows = new() { Spacing = 0 };
            foreach (SpellState spell in future)
            {
                string value = spell.ProficiencyPercent is null ? "n/a" : $"{spell.ProficiencyPercent}%";
                rows.Children.Add(AbilityRow(spell.Name, $"Level {spell.RequiredLevel} · {value}", spell.Domain, false, spell.IsFresh));
            }
            stack.Children.Add(CollapsibleSection("Future", rows, badge: future.Length.ToString()));
        }
        if (_lastAbilityHelp?.Kind == AbilityHelpKind.Spell)
        {
            stack.Children.Add(CollapsibleSection(
                "Reference",
                BuildAbilityHelpReference(_lastAbilityHelp),
                expanded: true,
                badge: _lastAbilityHelp.Name));
        }
        return ToolScroll(stack);
    }

    private Control BuildJevPanel(StateSnapshot state)
    {
        StackPanel root = new() { Spacing = 8 };
        StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach ((JevSection section, string label) in new[]
                 {
                     (JevSection.Status, "Status"),
                     (JevSection.Authority, "Authority"),
                     (JevSection.RecentDecisions, "Recent Decisions"),
                     (JevSection.ProviderSettings, "Provider Settings")
                 })
        {
            Button button = GhostButton(label);
            button.FontWeight = _jevSection == section ? FontWeight.SemiBold : FontWeight.Normal;
            button.Foreground = _jevSection == section ? Accent : TextForeground;
            button.Click += (_, _) =>
            {
                _jevSection = section;
                RefreshActiveWorkspace();
            };
            tabs.Children.Add(button);
        }
        root.Children.Add(tabs);
        root.Children.Add(_jevSection switch
        {
            JevSection.Authority => BuildJevAuthorityPanel(state),
            JevSection.RecentDecisions => BuildJevRecentDecisionsPanel(),
            JevSection.ProviderSettings => BuildJevProviderSettingsPanel(),
            _ => BuildJevStatusPanel(state)
        });
        return root;
    }

    private Control BuildJevAuthorityPanel(StateSnapshot state)
    {
        StackPanel root = ToolStack();
        root.Children.Add(new TextBlock
        {
            Text = "Authority",
            Foreground = TextForeground,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "Choose a preset or set authority independently for each Jev domain.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });

        ComboBox preset = new()
        {
            ItemsSource = Enum.GetValues<JevPreset>(),
            SelectedItem = state.JevAuthority.Preset,
            MinWidth = 180
        };
        root.Children.Add(KeyValueControl("Preset", preset));

        Dictionary<JevDomain, ComboBox> domains = [];
        foreach (JevDomain domain in Enum.GetValues<JevDomain>())
        {
            ComboBox authority = new()
            {
                ItemsSource = Enum.GetValues<JevAuthority>(),
                SelectedItem = state.JevAuthority.Domains[domain],
                MinWidth = 180
            };
            domains[domain] = authority;
            root.Children.Add(KeyValueControl(domain.ToString(), authority));
        }

        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedItem is not JevPreset selected || selected == JevPreset.Custom) return;
            JevAuthoritySnapshot snapshot = JevAuthorityService.CreatePreset(selected);
            foreach ((JevDomain domain, ComboBox combo) in domains)
                combo.SelectedItem = snapshot.Domains[domain];
        };

        TextBlock message = new() { Foreground = Muted, FontSize = NexTypography.Metadata };
        Button save = AccentButton("Save Authority");
        save.Click += async (_, _) =>
        {
            try
            {
                Dictionary<JevDomain, JevAuthority> values = domains.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.SelectedItem is JevAuthority authority ? authority : JevAuthority.Off);
                JevPreset selectedPreset = preset.SelectedItem is JevPreset p ? p : JevPreset.Custom;
                if (selectedPreset != JevPreset.Custom)
                {
                    JevAuthoritySnapshot template = JevAuthorityService.CreatePreset(selectedPreset);
                    bool unchanged = values.All(pair => template.Domains[pair.Key] == pair.Value);
                    if (!unchanged) selectedPreset = JevPreset.Custom;
                }
                await _runtime.ApplyAndSaveAuthorityAsync(
                    JevAuthoritySnapshot.Create(selectedPreset, values),
                    _cts.Token).ConfigureAwait(true);
                message.Text = "Authority saved.";
                message.Foreground = Success;
                RefreshActiveWorkspace();
            }
            catch (Exception exception)
            {
                message.Text = exception.Message;
                message.Foreground = Danger;
            }
        };
        root.Children.Add(save);
        root.Children.Add(message);
        return ToolScroll(root);
    }

    private Control BuildJevRecentDecisionsPanel()
    {
        StackPanel root = ToolStack();
        root.Children.Add(new TextBlock
        {
            Text = "Recent Decisions",
            Foreground = TextForeground,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        if (_jevDecisionHistory.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "No Jev decisions have been observed this session.",
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (JevDecisionTrace decision in _jevDecisionHistory.Take(50))
                root.Children.Add(BuildDecisionHistoryRow(decision));
        }
        return ToolScroll(root);
    }

    private Control BuildJevProviderSettingsPanel()
    {
        StackPanel root = ToolStack();
        root.Children.Add(new TextBlock
        {
            Text = "Provider Settings",
            Foreground = TextForeground,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "Provider credentials and model selection are owned by Jev. Credentials are stored in the platform secret store.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });

        TextBox model = new()
        {
            Text = _runtime.Settings.JevModel,
            Background = ConsoleBackground,
            Foreground = TextForeground,
            BorderBrush = PanelBorderBrush,
            MinHeight = 32
        };
        TextBox apiKey = new()
        {
            PasswordChar = '●',
            PlaceholderText = "Leave blank to keep the stored API key",
            Background = ConsoleBackground,
            Foreground = TextForeground,
            BorderBrush = PanelBorderBrush,
            MinHeight = 32
        };
        root.Children.Add(KeyValueControl("Model", model));
        root.Children.Add(KeyValueControl("TypeSafe API key", apiKey));
        root.Children.Add(new TextBlock
        {
            Text = _runtime.DecisionEngine is null ? "Provider status: OFFLINE" : "Provider status: ONLINE",
            Foreground = _runtime.DecisionEngine is null ? Muted : Success,
            FontSize = NexTypography.Metadata
        });
        TextBlock message = new() { Foreground = Muted, FontSize = NexTypography.Metadata, TextWrapping = TextWrapping.Wrap };
        Button save = AccentButton("Save Provider Settings");
        save.Click += async (_, _) =>
        {
            try
            {
                string? key = string.IsNullOrWhiteSpace(apiKey.Text)
                    ? await _runtime.GetStoredJevApiKeyAsync(_cts.Token).ConfigureAwait(true)
                    : apiKey.Text;
                await _runtime.SaveJevConfigurationAsync(key, model.Text ?? string.Empty, _cts.Token).ConfigureAwait(true);
                apiKey.Text = string.Empty;
                message.Text = "Provider settings saved.";
                message.Foreground = Success;
                RefreshActiveWorkspace();
            }
            catch (Exception exception)
            {
                message.Text = exception.Message;
                message.Foreground = Danger;
            }
        };
        root.Children.Add(save);
        root.Children.Add(message);
        return ToolScroll(root);
    }

    private static Control KeyValueControl(string label, Control value)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("150,*"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = NexTypography.Metadata
        });
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private Control BuildJevStatusPanel(StateSnapshot state)
    {
        StackPanel stack = ToolStack();

        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        StackPanel title = new() { Spacing = 1 };
        title.Children.Add(new TextBlock
        {
            Text = "System One decision engine",
            Foreground = TextForeground,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        title.Children.Add(new TextBlock
        {
            Text = "Typed choices over explicit state; application policy remains deterministic.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = NexTypography.Metadata
        });
        heading.Children.Add(title);
        TextBlock status = new()
        {
            Text = !_runtime.Authority.Enabled ? "DISABLED" : _runtime.DecisionEngine is null ? "OFFLINE" : "ONLINE",
            Foreground = !_runtime.Authority.Enabled || _runtime.DecisionEngine is null ? Muted : Success,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(status, 1);
        heading.Children.Add(status);
        stack.Children.Add(heading);

        JevAuthority combatAuthority = state.JevAuthority.Domains[JevDomain.Combat];
        JevAuthority recoveryAuthority = state.JevAuthority.Domains[JevDomain.Recovery];
        JevAuthority navigationAuthority = state.JevAuthority.Domains[JevDomain.Navigation];
        KnowledgeSummary memory = _runtime.Knowledge.Summary;
        StackPanel policy = new() { Spacing = 5 };
        policy.Children.Add(MetricGrid(
        [
            ("Combat", combatAuthority.ToString()),
            ("Recovery", recoveryAuthority.ToString()),
            ("Navigation", navigationAuthority.ToString())
        ], 3));
        policy.Children.Add(new TextBlock
        {
            Text = $"{state.JevAuthority.Preset} · {_lastJevDecision?.Model ?? _runtime.Settings.JevModel} · " +
                   $"{memory.Rooms:N0} known rooms · {memory.CombatEvents:N0} combat events",
            Foreground = UiTheme.Faint,
            TextWrapping = TextWrapping.Wrap,
            FontSize = NexTypography.Metadata
        });
        policy.Children.Add(new TextBlock
        {
            Text = "Auto grind priority: combat → recovery → navigation. Every selected command is revalidated against current state before dispatch.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = NexTypography.Body
        });
        stack.Children.Add(SectionBlock("Policy & context", policy));

        if (_lastJevDecision is null)
        {
            stack.Children.Add(SectionBlock("Current decision", new TextBlock
            {
                Text = "No decision yet. Combat, resource, room, and route changes can trigger typed evaluations when their domains are enabled.",
                Foreground = Muted,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap
            }));
        }
        else
        {
            JevDecisionTrace decision = _lastJevDecision;
            stack.Children.Add(SectionBlock("Current decision", BuildDecisionSummary(decision), decision.Selected.Probability.ToString("P0")));

            StackPanel distribution = new() { Spacing = 7 };
            foreach (DecisionCandidate candidate in decision.Candidates.OrderByDescending(candidate => candidate.Probability))
            {
                distribution.Children.Add(BuildCandidateProbability(candidate, decision.Selected.Action));
            }
            stack.Children.Add(CollapsibleSection("Action distribution", distribution, badge: decision.Candidates.Count.ToString()));

            if ((decision.Metrics?.Count ?? 0) > 0)
            {
                StackPanel metrics = new() { Spacing = 7 };
                foreach (JevDecisionMetric metric in decision.Metrics!)
                {
                    metrics.Children.Add(BuildDecisionMetric(metric));
                }
                stack.Children.Add(CollapsibleSection("Parallel evaluations", metrics, badge: decision.Metrics!.Count.ToString()));
            }

            if (_pendingJevApprovalId == decision.DecisionId)
            {
                StackPanel approval = new() { Spacing = 8 };
                approval.Children.Add(new TextBlock
                {
                    Text = "Approval required",
                    Foreground = Warning,
                    FontSize = NexTypography.BodyStrong,
                    FontWeight = FontWeight.SemiBold
                });
                approval.Children.Add(new TextBlock
                {
                    Text = _pendingJevCommand is null
                        ? $"Jev proposes {FormatJevAction(decision.Selected.Action)}. The action will be revalidated before execution."
                        : $"Command: {_pendingJevCommand}",
                    Foreground = TextForeground,
                    FontSize = NexTypography.Body,
                    TextWrapping = TextWrapping.Wrap
                });
                StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
                Button approve = AccentButton("Approve");
                approve.Click += async (_, _) => await ResolveJevApprovalAsync(decision.DecisionId, true);
                Button reject = GhostButton("Reject");
                reject.Click += async (_, _) => await ResolveJevApprovalAsync(decision.DecisionId, false);
                buttons.Children.Add(approve);
                buttons.Children.Add(reject);
                approval.Children.Add(buttons);
                stack.Children.Add(Card(approval, Warning));
            }

            if (!string.IsNullOrWhiteSpace(_jevExecutionStatus))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = _jevExecutionStatus,
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = NexTypography.Body
                });
            }
        }

        if (_jevDecisionHistory.Count > 1)
        {
            StackPanel history = new() { Spacing = 6 };
            foreach (JevDecisionTrace prior in _jevDecisionHistory.Skip(1).Take(10))
            {
                history.Children.Add(BuildDecisionHistoryRow(prior));
            }
            stack.Children.Add(CollapsibleSection("Recent decisions", history, badge: Math.Min(10, _jevDecisionHistory.Count - 1).ToString()));
        }

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        bool engineReady = !_jevEvaluationInProgress && _runtime.DecisionEngine is not null;
        Button combat = GhostButton("Combat");
        combat.FontSize = NexTypography.Metadata;
        combat.IsEnabled = engineReady && AvendarJevRequestFactory.TryCreateCombatRequest(state, out _);
        combat.Click += async (_, _) => await EvaluateCombatAsync();
        actions.Children.Add(combat);

        Button recovery = GhostButton("Recovery");
        recovery.FontSize = NexTypography.Metadata;
        recovery.IsEnabled = engineReady && AvendarJevRequestFactory.TryCreateRecoveryRequest(state, null, out _);
        recovery.Click += async (_, _) => await EvaluateRecoveryAsync();
        actions.Children.Add(recovery);

        Button navigation = GhostButton("Navigation");
        navigation.FontSize = NexTypography.Metadata;
        navigation.IsEnabled = engineReady && AvendarJevRequestFactory.TryCreateNavigationRequest(state, null, out _);
        navigation.Click += async (_, _) => await EvaluateNavigationAsync();
        actions.Children.Add(navigation);

        Button configure = GhostButton("Provider Settings");
        configure.FontSize = NexTypography.Metadata;
        configure.Click += (_, _) =>
        {
            _jevSection = JevSection.ProviderSettings;
            RefreshActiveWorkspace();
        };
        actions.Children.Add(configure);
        stack.Children.Add(actions);
        return ToolScroll(stack);
    }

    private static Control BuildDecisionSummary(JevDecisionTrace decision)
    {
        StackPanel content = new() { Spacing = 7 };
        content.Children.Add(new TextBlock
        {
            Text = FormatJevAction(decision.Selected.Action),
            Foreground = Accent,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(decision.Selected.Description))
        {
            content.Children.Add(new TextBlock
            {
                Text = decision.Selected.Description,
                Foreground = Muted,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap
            });
        }

        content.Children.Add(KeyValue("Selected probability", decision.Selected.Probability.ToString("P1")));
        ProgressBar confidence = ProbabilityBar(Accent);
        confidence.Value = Math.Clamp(decision.Confidence, 0, 1);
        content.Children.Add(new TextBlock
        {
            Text = $"Choice confidence  {decision.Confidence:P0}",
            Foreground = TextForeground,
            FontSize = NexTypography.Body
        });
        content.Children.Add(confidence);
        content.Children.Add(KeyValue("Domain", decision.Domain.ToString()));
        content.Children.Add(KeyValue("Authority", decision.Authority.ToString()));
        content.Children.Add(KeyValue("Source", decision.Source.ToString()));
        content.Children.Add(KeyValue("State", $"v{decision.StateVersion}"));
        content.Children.Add(KeyValue("Latency", $"{decision.Latency.TotalMilliseconds:0} ms"));
        if (decision.Usage is not null)
        {
            content.Children.Add(KeyValue("Usage", $"{decision.Usage.InputTokens} in · {decision.Usage.OutputTokens} out"));
        }
        return content;
    }

    private static Control BuildCandidateProbability(DecisionCandidate candidate, string selectedAction)
    {
        StackPanel content = new() { Spacing = 4 };
        Grid label = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        label.Children.Add(new TextBlock
        {
            Text = FormatJevAction(candidate.Action),
            Foreground = candidate.Action == selectedAction ? Accent : TextForeground,
            FontSize = NexTypography.Body,
            FontWeight = candidate.Action == selectedAction ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock probability = new()
        {
            Text = candidate.Probability.ToString("P1"),
            Foreground = candidate.Action == selectedAction ? Accent : Muted,
            FontSize = NexTypography.BodyStrong
        };
        Grid.SetColumn(probability, 1);
        label.Children.Add(probability);
        content.Children.Add(label);
        ProgressBar bar = ProbabilityBar(candidate.Action == selectedAction ? Accent : Muted);
        bar.Value = Math.Clamp(candidate.Probability, 0, 1);
        content.Children.Add(bar);
        if (!string.IsNullOrWhiteSpace(candidate.Description))
        {
            content.Children.Add(new TextBlock
            {
                Text = candidate.Description,
                Foreground = Muted,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap
            });
        }
        return content;
    }

    private static Control BuildDecisionMetric(JevDecisionMetric metric)
    {
        StackPanel content = new() { Spacing = 6 };
        string value = metric.Type switch
        {
            JevQuestionType.Noul => $"P(yes) {metric.Value:P1}",
            JevQuestionType.Score => FormatScoreMetric(metric),
            _ => metric.Value.ToString("0.###")
        };
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        Control type = Badge(metric.Type.ToString().ToUpperInvariant(), metric.Type == JevQuestionType.Noul ? Warning : Accent);
        heading.Children.Add(type);
        TextBlock name = new()
        {
            Text = metric.Id,
            Foreground = TextForeground,
            FontSize = NexTypography.Body,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        heading.Children.Add(name);
        TextBlock readout = new() { Text = value, Foreground = Accent, FontSize = NexTypography.BodyStrong, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(readout, 2);
        heading.Children.Add(readout);
        content.Children.Add(heading);
        content.Children.Add(new TextBlock
        {
            Text = metric.Instructions,
            Foreground = Muted,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap
        });

        if (metric.Type == JevQuestionType.Noul)
        {
            ProgressBar yes = ProbabilityBar(Accent);
            yes.Value = Math.Clamp(metric.Value, 0, 1);
            content.Children.Add(ProbabilityReadout("yes", metric.Value, yes));
            ProgressBar no = ProbabilityBar(Muted);
            no.Value = Math.Clamp(1 - metric.Value, 0, 1);
            content.Children.Add(ProbabilityReadout("no", 1 - metric.Value, no));
        }
        else if (metric.Type == JevQuestionType.Score && metric.Probabilities is not null)
        {
            foreach ((string level, double probability) in metric.Probabilities
                         .OrderBy(pair => ParseScoreLevel(pair.Key)))
            {
                string label = metric.Legend is not null && metric.Legend.TryGetValue(level, out string? legend)
                    ? $"{level} · {legend}"
                    : level;
                ProgressBar bar = ProbabilityBar(Accent);
                bar.Value = Math.Clamp(probability, 0, 1);
                content.Children.Add(ProbabilityReadout(label, probability, bar));
            }
            if (metric.Confidence is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"Score confidence  {metric.Confidence.Value:P0}",
                    Foreground = Muted,
                    FontSize = NexTypography.Metadata
                });
            }
        }
        return Card(content, PanelBorderBrush);
    }

    private static Control ProbabilityReadout(string label, double probability, ProgressBar bar)
    {
        StackPanel stack = new() { Spacing = 2 };
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = NexTypography.Metadata });
        TextBlock value = new() { Text = probability.ToString("P1"), Foreground = TextForeground, FontSize = NexTypography.Metadata };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        stack.Children.Add(row);
        stack.Children.Add(bar);
        return stack;
    }

    private static double ParseScoreLevel(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : double.MaxValue;

    private static Control BuildDecisionHistoryRow(JevDecisionTrace decision)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(Badge(decision.Domain.ToString().ToUpperInvariant(), Muted));
        StackPanel body = new() { Spacing = 1 };
        body.Children.Add(new TextBlock
        {
            Text = FormatJevAction(decision.Selected.Action),
            Foreground = TextForeground,
            FontSize = NexTypography.Body,
            FontWeight = FontWeight.SemiBold
        });
        body.Children.Add(new TextBlock
        {
            Text = $"v{decision.StateVersion} · {decision.Latency.TotalMilliseconds:0} ms · {decision.Model}",
            Foreground = Muted,
            FontSize = NexTypography.Metadata
        });
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        TextBlock probability = new()
        {
            Text = decision.Selected.Probability.ToString("P0"),
            Foreground = Accent,
            FontSize = NexTypography.BodyStrong,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(probability, 2);
        row.Children.Add(probability);
        return Card(row, PanelBorderBrush);
    }

    private static string FormatScoreMetric(JevDecisionMetric metric)
    {
        string score = metric.Value.ToString("0.00");
        if (metric.Legend is null || metric.Legend.Count == 0)
        {
            return score;
        }
        int nearest = (int)Math.Round(metric.Value, MidpointRounding.AwayFromZero);
        string key = nearest.ToString();
        return metric.Legend.TryGetValue(key, out string? label) ? $"{score} · {label}" : score;
    }

    private static ProgressBar ProbabilityBar(IBrush color) => new()
    {
        Minimum = 0,
        Maximum = 1,
        Height = 7,
        Foreground = color,
        Background = ConsoleBackground
    };

    private static string AuthorityDescription(JevAuthority authority) => authority switch
    {
        JevAuthority.Off => "No automatic Jev evaluations are requested for combat.",
        JevAuthority.Observe => "Jev evaluates semantic combat triggers quietly for diagnostics only.",
        JevAuthority.Suggest => "Jev evaluates semantic combat triggers and surfaces its selected action.",
        JevAuthority.Approve => "Jev may propose an action, but execution waits for explicit approval and revalidation.",
        JevAuthority.Auto => "Jev may execute a selected action after current-state revalidation.",
        _ => string.Empty
    };

    private Control BuildExitControls(ExitState exits)
    {
        StackPanel stack = new() { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = "Exits",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold
        });
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
        IReadOnlyList<RoomExitObservation> observations = exits.Details ?? exits.Directions
            .Select(direction => new RoomExitObservation(direction, true, ExitDoorState.Unknown, ExitTraversability.Traversable))
            .ToArray();
        foreach (RoomExitObservation exit in observations)
        {
            Button button = GhostButton(DirectionLabel(exit.Direction));
            button.MinWidth = 40;
            button.IsEnabled = exit.Traversability != ExitTraversability.Blocked;
            string command = exit.Direction;
            button.Click += async (_, _) => await SubmitCommandAsync(command);
            buttons.Children.Add(button);
        }
        stack.Children.Add(buttons);
        return stack;
    }

    private Control PanelActionHeader(string message, string buttonLabel, string command)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Muted,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        Button button = CommandButton(buttonLabel, command);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private Control BuildAbilityGrid(
        IReadOnlyList<(string Name, string Value, AbilityDomain Domain, bool Available, bool Fresh)> entries)
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10,
            RowSpacing = 0
        };
        int rows = (entries.Count + 1) / 2;
        for (int row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (int index = 0; index < entries.Count; index++)
        {
            (string name, string value, AbilityDomain domain, bool available, bool fresh) = entries[index];
            Control cell = AbilityRow(name, value, domain, available, fresh);
            Grid.SetColumn(cell, index % 2);
            Grid.SetRow(cell, index / 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static Control BuildProgressionPreview(IReadOnlyList<(int Level, string Name, AbilityDomain Domain)> future)
    {
        StackPanel stack = new() { Spacing = 0 };
        var groups = future
            .GroupBy(item => item.Level)
            .OrderBy(group => group.Key)
            .ToArray();
        var shown = groups.Take(8).ToArray();
        foreach (var group in shown)
        {
            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("34,*"),
                ColumnSpacing = 7,
                MinHeight = 23
            };
            row.Children.Add(new TextBlock
            {
                Text = $"L{group.Key}",
                Foreground = UiTheme.Faint,
                FontSize = NexTypography.Metadata,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            TextBlock names = new()
            {
                Text = string.Join("  ·  ", group.Select(item => item.Name)),
                Foreground = TextForeground,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(names, 1);
            row.Children.Add(names);
            stack.Children.Add(new Border
            {
                BorderBrush = PanelBorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 2),
                Child = row
            });
        }

        int shownCount = shown.Sum(group => group.Count());
        if (future.Count > shownCount)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"+ {future.Count - shownCount:N0} later abilities across {groups.Length - shown.Length:N0} levels",
                Foreground = Muted,
                FontSize = NexTypography.Metadata,
                Margin = new Thickness(0, 5, 0, 0)
            });
        }
        return stack;
    }

    private Control AbilityRow(string name, string value, AbilityDomain domain, bool available, bool fresh)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 7,
            MinHeight = 29
        };
        StackPanel left = new() { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = available ? TextForeground : Muted,
            FontSize = NexTypography.Body,
            FontWeight = available ? FontWeight.SemiBold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (domain != AbilityDomain.Unknown)
        {
            left.Children.Add(new TextBlock
            {
                Text = domain.ToString(),
                Foreground = UiTheme.Faint,
                FontSize = NexTypography.Metadata,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        row.Children.Add(left);
        TextBlock right = new()
        {
            Text = fresh ? value : $"{value} · stale",
            Foreground = available ? Accent : Muted,
            FontSize = NexTypography.Body,
            FontWeight = available ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(right, 1);
        row.Children.Add(right);

        Button button = new()
        {
            Content = row,
            Background = Brushes.Transparent,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(1, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Opacity = available ? 1 : 0.68
        };

        AbilityHelpDocument? live = _lastAbilityHelp is not null &&
                                    string.Equals(_lastAbilityHelp.Name, name, StringComparison.OrdinalIgnoreCase)
            ? _lastAbilityHelp
            : null;
        _abilityKnowledgeCache.TryGetValue(name, out AbilityHelpKnowledge? persisted);
        ToolTip.SetTip(button, BuildAbilityTooltip(name, live, persisted));
        button.Click += async (_, _) => await SubmitCommandAsync($"help {name}");
        return button;
    }

    private static Control BuildAbilityTooltip(
        string name,
        AbilityHelpDocument? live,
        AbilityHelpKnowledge? persisted)
    {
        StackPanel content = new() { Spacing = 5, Width = 300, Margin = new Thickness(2) };
        content.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = TextForeground,
            FontSize = NexTypography.BodyStrong,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (live is null && persisted is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No help data. Click to load server help.",
                Foreground = Muted,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap
            });
            return content;
        }

        decimal? lag = live?.ActivationLagRounds ?? persisted?.ActivationLagRounds;
        int? manaCost = live?.ActivationManaCost ?? persisted?.ActivationManaCost;
        string? syntax = live?.Syntax ?? persisted?.Syntax;
        string description = live?.Description ?? persisted?.Description ?? string.Empty;
        AbilityHelpKind kind = live?.Kind ?? persisted?.Kind ?? AbilityHelpKind.Unknown;
        IReadOnlyDictionary<string, string> fields = live?.Fields
            ?? persisted?.Fields
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        List<string> meta = [];
        if (kind != AbilityHelpKind.Unknown) meta.Add(kind.ToString());
        if (lag is not null) meta.Add($"{lag:0.##} rounds");
        if (manaCost is not null) meta.Add($"{manaCost} MA");
        if (meta.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", meta),
                Foreground = Muted,
                FontSize = NexTypography.Body
            });
        }
        if (!string.IsNullOrWhiteSpace(syntax))
        {
            content.Children.Add(new TextBlock
            {
                Text = syntax,
                Foreground = Accent,
                FontFamily = UiTheme.Mono,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (!string.IsNullOrWhiteSpace(description))
        {
            content.Children.Add(UiTheme.DividerLine(0.55));
            content.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = TextForeground,
                FontSize = NexTypography.Body,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 160
            });
        }
        foreach ((string key, string value) in fields.OrderBy(pair => pair.Key))
        {
            Grid fact = new() { ColumnDefinitions = new ColumnDefinitions("82,*"), ColumnSpacing = 7 };
            fact.Children.Add(new TextBlock { Text = key, Foreground = Muted, FontSize = NexTypography.Metadata });
            TextBlock fieldValue = new()
            {
                Text = value,
                Foreground = TextForeground,
                FontSize = NexTypography.Metadata,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(fieldValue, 1);
            fact.Children.Add(fieldValue);
            content.Children.Add(fact);
        }
        return content;
    }

    private static Control BuildAbilityHelpReference(AbilityHelpDocument help)
    {
        StackPanel stack = new() { Spacing = 5 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{help.Name} · {help.Kind}",
            Foreground = TextForeground,
            FontSize = NexTypography.BodyStrong,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (help.ActivationLagRounds is not null)
        {
            stack.Children.Add(KeyValue("Lag", $"{help.ActivationLagRounds:0.##} rounds"));
        }
        if (help.ActivationManaCost is not null)
        {
            stack.Children.Add(KeyValue("MA cost", help.ActivationManaCost.Value.ToString()));
        }
        if (!string.IsNullOrWhiteSpace(help.Syntax))
        {
            stack.Children.Add(KeyValue("Syntax", help.Syntax));
        }
        if (!string.IsNullOrWhiteSpace(help.Description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = help.Description,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                FontSize = NexTypography.Body
            });
        }
        return stack;
    }

    private Task EvaluateCombatAsync() => EvaluateJevDomainAsync(JevDomain.Combat);

    private Task EvaluateRecoveryAsync() => EvaluateJevDomainAsync(JevDomain.Recovery);

    private Task EvaluateNavigationAsync() => EvaluateJevDomainAsync(JevDomain.Navigation);

    private async Task EvaluateJevDomainAsync(JevDomain domain)
    {
        if (_runtime.DecisionEngine is null)
        {
            ShowClientMessage("Jev is not configured. Add a TypeSafe API key in Jev → Provider Settings.", error: true);
            return;
        }
        if (_snapshot.Session.InputMode != SessionInputMode.Normal)
        {
            ShowClientMessage("Jev evaluation is disabled outside normal game input mode.", error: true);
            return;
        }

        bool applicable = domain switch
        {
            JevDomain.Combat => AvendarJevRequestFactory.TryCreateCombatRequest(_snapshot, out _),
            JevDomain.Recovery => AvendarJevRequestFactory.TryCreateRecoveryRequest(_snapshot, null, out _),
            JevDomain.Navigation => AvendarJevRequestFactory.TryCreateNavigationRequest(_snapshot, null, out _),
            _ => false
        };
        if (!applicable)
        {
            ShowClientMessage($"No {domain.ToString().ToLowerInvariant()} decision is currently applicable.");
            return;
        }

        _jevEvaluationInProgress = true;
        UpdateJevHud(_snapshot);
        RenderPersistentGameplay();
        if (_activeTool == ToolView.Jev)
        {
            RefreshActiveWorkspace();
        }
        try
        {
            JevDecisionTrace? result = domain switch
            {
                JevDomain.Combat => await _runtime.EvaluateCombatWithJevAsync(_cts.Token).ConfigureAwait(false),
                JevDomain.Recovery => await _runtime.EvaluateRecoveryWithJevAsync(_cts.Token).ConfigureAwait(false),
                JevDomain.Navigation => await _runtime.EvaluateNavigationWithJevAsync(_cts.Token).ConfigureAwait(false),
                _ => null
            };
            if (result is null)
            {
                Dispatcher.UIThread.Post(() => ShowClientMessage($"Jev could not build a valid {domain.ToString().ToLowerInvariant()} decision request."));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => ShowClientMessage($"Jev {domain.ToString().ToLowerInvariant()} evaluation failed: {exception.Message}", error: true));
        }
        finally
        {
            _jevEvaluationInProgress = false;
            Dispatcher.UIThread.Post(() =>
            {
                UpdateJevHud(_snapshot);
                RenderPersistentGameplay();
                if (_activeTool == ToolView.Jev) RefreshActiveWorkspace();
            });
        }
    }

    private async Task ResolveJevApprovalAsync(Guid decisionId, bool approve)
    {
        bool resolved = approve
            ? await _runtime.ApproveJevDecisionAsync(decisionId, _cts.Token).ConfigureAwait(false)
            : await _runtime.RejectJevDecisionAsync(decisionId, _cts.Token).ConfigureAwait(false);
        if (!resolved)
        {
            Dispatcher.UIThread.Post(() => ShowClientMessage("This Jev approval is no longer pending.", error: true));
        }
    }

    private void RenderDecision(JevDecisionTrace decision)
    {
        _lastJevDecision = decision;
        _jevDecisionHistory.RemoveAll(item => item.DecisionId == decision.DecisionId);
        _jevDecisionHistory.Insert(0, decision);
        if (_jevDecisionHistory.Count > 20)
        {
            _jevDecisionHistory.RemoveRange(20, _jevDecisionHistory.Count - 20);
        }
        _jevExecutionStatus = null;
        UpdateJevHud(_snapshot);
        RenderPersistentGameplay();
        if (_activeTool == ToolView.Jev)
        {
            RefreshActiveWorkspace();
            return;
        }

        // Never steal the player's current reference context for passive suggestions or
        // automatic execution. Only an approval request requires immediate interaction.
        if (decision.Authority == JevAuthority.Approve)
        {
            ShowTool(ToolView.Jev);
        }
        else if (decision.Authority == JevAuthority.Suggest)
        {
            ShowClientMessage($"Jev {decision.Domain}: {FormatJevAction(decision.Selected.Action)} · {decision.Selected.Probability:P0}");
        }
    }

    private static ToolView NormalizeToolView(ToolView view) => view switch
    {
        ToolView.Skills or ToolView.Spells => ToolView.Abilities,
        _ => view
    };

    private void ShowTool(ToolView requestedView)
    {
        ToolView view = NormalizeToolView(requestedView);
        if (view == ToolView.Context)
        {
            _gameplayRailVisible = _activeTool == ToolView.Context
                ? !_gameplayRailVisible
                : true;
        }
        if (_activeTool != view)
        {
            DeactivateWorkspace(_activeTool);
        }

        _activeTool = view;
        ApplyWorkspaceLayout(view);

        switch (view)
        {
            case ToolView.Context:
                _workspaceHost.Content = _gameplayRail;
                RenderPersistentGameplay();
                Dispatcher.UIThread.Post(() => _command.Focus());
                break;
            case ToolView.Character:
                RenderPersistentGameplay();
                _workspaceHost.Content = _characterWorkspace;
                break;
            case ToolView.Map:
                _workspaceHost.Content = _mapperWorkspace;
                _mapperWorkspace.Activate();
                break;
            case ToolView.Knowledge:
                _workspaceHost.Content = _codexWorkspace;
                _codexWorkspace.Activate();
                break;
            case ToolView.Automation:
                _workspaceHost.Content = _automationWorkspace;
                _automationWorkspace.Activate();
                break;
            case ToolView.Scripting:
                _scriptingWorkspace.Refresh();
                _workspaceHost.Content = _scriptingWorkspace;
                break;
            case ToolView.Abilities:
            case ToolView.Jev:
            case ToolView.Search:
            {
                if (!_dynamicWorkspaceHosts.TryGetValue(view, out ContentControl? host))
                {
                    host = new ContentControl
                    {
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Stretch
                    };
                    _dynamicWorkspaceHosts[view] = host;
                }
                host.Content = BuildDynamicToolContent(view);
                _workspaceHost.Content = host;
                if (view == ToolView.Search)
                {
                    Dispatcher.UIThread.Post(() => _searchQuery.Focus());
                }
                break;
            }
        }

        UpdateNavigationStyles();
    }

    private void DeactivateWorkspace(ToolView view)
    {
        switch (NormalizeToolView(view))
        {
            case ToolView.Map:
                _mapperWorkspace.Deactivate();
                break;
            case ToolView.Knowledge:
                _codexWorkspace.Deactivate();
                break;
            case ToolView.Automation:
                _automationWorkspace.Deactivate();
                break;
        }
    }

    private void ApplyWorkspaceLayout(ToolView view)
    {
        _workspaceSplitter.IsVisible = true;
        _workspaceSplitterColumn.Width = new GridLength(2);
        _worldColumn.MinWidth = 500;
        _workspaceColumn.MaxWidth = double.PositiveInfinity;

        switch (NormalizeToolView(view))
        {
            case ToolView.Context:
                if (!_gameplayRailVisible)
                {
                    _workspaceSplitter.IsVisible = false;
                    _workspaceSplitterColumn.Width = new GridLength(0);
                    _worldColumn.Width = new GridLength(1, GridUnitType.Star);
                    _workspaceColumn.MinWidth = 0;
                    _workspaceColumn.Width = new GridLength(0);
                    break;
                }
                _worldColumn.Width = new GridLength(1, GridUnitType.Star);
                _workspaceColumn.MinWidth = 370;
                _workspaceColumn.MaxWidth = 445;
                _workspaceColumn.Width = new GridLength(Math.Clamp(_preferredRailWidth, 390, 425));
                break;
            case ToolView.Character:
                _worldColumn.Width = new GridLength(54, GridUnitType.Star);
                _workspaceColumn.MinWidth = 520;
                _workspaceColumn.Width = new GridLength(46, GridUnitType.Star);
                break;
            case ToolView.Map:
                _worldColumn.Width = new GridLength(50, GridUnitType.Star);
                _workspaceColumn.MinWidth = 500;
                _workspaceColumn.Width = new GridLength(50, GridUnitType.Star);
                break;
            case ToolView.Automation:
                _worldColumn.Width = new GridLength(47, GridUnitType.Star);
                _workspaceColumn.MinWidth = 500;
                _workspaceColumn.Width = new GridLength(53, GridUnitType.Star);
                break;
            case ToolView.Scripting:
                _worldColumn.Width = new GridLength(44, GridUnitType.Star);
                _workspaceColumn.MinWidth = 520;
                _workspaceColumn.Width = new GridLength(56, GridUnitType.Star);
                break;
            case ToolView.Knowledge:
                _worldColumn.Width = new GridLength(58, GridUnitType.Star);
                _workspaceColumn.MinWidth = 430;
                _workspaceColumn.Width = new GridLength(42, GridUnitType.Star);
                break;
            case ToolView.Jev:
                _worldColumn.Width = new GridLength(60, GridUnitType.Star);
                _workspaceColumn.MinWidth = 420;
                _workspaceColumn.Width = new GridLength(40, GridUnitType.Star);
                break;
            case ToolView.Abilities:
                _worldColumn.Width = new GridLength(55, GridUnitType.Star);
                _workspaceColumn.MinWidth = 460;
                _workspaceColumn.Width = new GridLength(45, GridUnitType.Star);
                break;
            case ToolView.Search:
                _worldColumn.Width = new GridLength(60, GridUnitType.Star);
                _workspaceColumn.MinWidth = 420;
                _workspaceColumn.Width = new GridLength(40, GridUnitType.Star);
                break;
        }
    }

    private void HideTool() => ShowTool(ToolView.Context);

    private void ApplyUiSettings()
    {
        _runtime.Interaction.Configure(_runtime.Settings);
        if (_activeTool == ToolView.Context)
        {
            _gameplayRailVisible = (_runtime.Settings.General ?? new GeneralPreferences()).ShowGameplayRailByDefault;
        }
        AppearancePreferences appearance = _runtime.Settings.Appearance ?? new AppearancePreferences();
        if (Enum.TryParse(appearance.AccentPalette, true, out NexAccentTheme accent) && Application.Current is not null)
        {
            NexMudTheme.ApplyAccent(Application.Current, accent);
        }
        FontFamily = string.IsNullOrWhiteSpace(appearance.InterfaceFont)
            ? NexMudTheme.Interface
            : new FontFamily(appearance.InterfaceFont);
        ConfigureTranscriptText(_gameText);
        ConfigureTranscriptText(_liveText);
        RebuildTranscript();
        RenderLoggingState();
        if (_runtime.Settings.AutoLogSessions &&
            _snapshot.Session.ConnectionStatus == ConnectionStatus.Connected &&
            !_logWriter.IsActive)
        {
            StartLogging();
        }

        WorkspacePreferences workspace = _runtime.Settings.Workspace ?? new WorkspacePreferences();
        _preferredRailWidth = Math.Clamp(workspace.DockWidth, 390, 425);
        _preferredLiveHeight = workspace.LiveSplitHeight;

        ApplyWorkspaceLayout(_activeTool);
        RenderPersistentGameplay();
        RefreshActiveWorkspace();
    }

    private static Button GhostButton(string label) => new()
    {
        Content = label,
        MinHeight = 26,
        Padding = new Thickness(5, 3),
        CornerRadius = new CornerRadius(0),
        Background = Brushes.Transparent,
        Foreground = TextForeground,
        BorderBrush = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        FontSize = NexTypography.Metadata
    };

    private static Button AccentButton(string label) => new()
    {
        Content = label,
        MinHeight = 29,
        Padding = new Thickness(10, 5),
        CornerRadius = new CornerRadius(1),
        Background = Accent,
        Foreground = Brushes.Black,
        BorderBrush = Accent,
        BorderThickness = new Thickness(1),
        FontSize = NexTypography.Metadata,
        FontWeight = FontWeight.SemiBold
    };

    private Button AppBarButton(string label, Action action)
    {
        Button button = GhostButton(label);
        button.Click += (_, _) => action();
        return button;
    }

    private Button CommandButton(string label, string command)
    {
        Button button = GhostButton(label);
        button.MinHeight = 30;
        button.Padding = new Thickness(8, 4);
        button.FontSize = NexTypography.Body;
        button.Click += async (_, _) => await SubmitCommandAsync(command);
        return button;
    }

    private static StackPanel ToolStack() => new()
    {
        Spacing = UiTheme.SpaceSm,
        Margin = new Thickness(UiTheme.SpaceMd, UiTheme.SpaceSm, UiTheme.SpaceMd, UiTheme.SpaceMd)
    };

    private static ScrollViewer ToolScroll(Control content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private Control CollapsibleSection(
        string title,
        Control content,
        bool expanded = false,
        string? badge = null,
        string? key = null)
    {
        // Disclosure state belongs to the information architecture, not to a transient
        // Control instance. RefreshActiveWorkspace rebuilds projections as state changes, so keep
        // the user's choice separately and reapply it on every render.
        string stateKey = key ?? $"{_activeTool}:{title}";
        if (!_sectionExpansion.TryGetValue(stateKey, out bool isExpanded))
        {
            isExpanded = expanded;
            _sectionExpansion[stateKey] = isExpanded;
        }

        StackPanel section = new()
        {
            Spacing = 0,
            Margin = new Thickness(0, 2, 0, UiTheme.SpaceMd)
        };

        Grid header = BuildSectionHeader(title, badge);
        TextBlock disclosure = new()
        {
            Text = isExpanded ? "−" : "+",
            Foreground = Muted,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(disclosure, 2);
        header.Children.Add(disclosure);

        Border body = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, UiTheme.SpaceSm, 0, 0),
            Child = content,
            IsVisible = isExpanded
        };

        Border hitTarget = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2, 0, 4),
            Child = header
        };
        hitTarget.PointerPressed += (_, args) =>
        {
            bool next = !body.IsVisible;
            body.IsVisible = next;
            disclosure.Text = next ? "−" : "+";
            _sectionExpansion[stateKey] = next;
            args.Handled = true;
        };

        section.Children.Add(hitTarget);
        section.Children.Add(UiTheme.DividerLine());
        section.Children.Add(body);
        return section;
    }

    private static Control SectionBlock(
        string title,
        Control content,
        string? badge = null,
        Control? trailing = null)
    {
        StackPanel section = new()
        {
            Spacing = 0,
            Margin = new Thickness(0, 2, 0, UiTheme.SpaceMd)
        };
        Grid header = BuildSectionHeader(title, badge, trailing);
        section.Children.Add(header);
        section.Children.Add(UiTheme.DividerLine());
        Border body = new()
        {
            Padding = new Thickness(0, UiTheme.SpaceSm, 0, 0),
            Child = content
        };
        section.Children.Add(body);
        return section;
    }

    private static Grid BuildSectionHeader(string title, string? badge = null, Control? trailing = null)
    {
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 7,
            MinHeight = 28
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TextForeground,
            FontWeight = FontWeight.SemiBold,
            FontSize = NexTypography.SectionTitle,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(badge))
        {
            TextBlock count = new()
            {
                Text = badge,
                Foreground = Muted,
                FontSize = NexTypography.Metadata,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(count, 1);
            header.Children.Add(count);
        }

        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            header.Children.Add(trailing);
        }
        return header;
    }

    private static Grid MetricGrid(IReadOnlyList<(string Label, string Value)> metrics, int columns = 2)
    {
        int safeColumns = Math.Max(1, columns);
        Grid grid = new()
        {
            ColumnSpacing = UiTheme.SpaceLg,
            RowSpacing = UiTheme.SpaceSm
        };
        for (int column = 0; column < safeColumns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        }
        int rowCount = (metrics.Count + safeColumns - 1) / safeColumns;
        for (int row = 0; row < rowCount; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (int index = 0; index < metrics.Count; index++)
        {
            (string label, string value) = metrics[index];
            StackPanel metric = new() { Spacing = 0 };
            metric.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Muted,
                FontSize = NexTypography.Metadata
            });
            metric.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = TextForeground,
                FontSize = NexTypography.Body,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(metric, index % safeColumns);
            Grid.SetRow(metric, index / safeColumns);
            grid.Children.Add(metric);
        }
        return grid;
    }

    private static TextBlock MutedText(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = NexTypography.Body,
        TextWrapping = TextWrapping.Wrap
    };

    private static Border Card(string title, string body)
    {
        StackPanel content = new() { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TextForeground,
            FontSize = NexTypography.BodyStrong,
            FontWeight = FontWeight.SemiBold
        });
        content.Children.Add(MutedText(body));
        return Card(content, PanelBorderBrush);
    }

    private static Border Card(Control content, IBrush border) => new()
    {
        Background = Brushes.Transparent,
        BorderBrush = border,
        BorderThickness = new Thickness(0, 0, 0, 1),
        CornerRadius = new CornerRadius(0),
        Padding = new Thickness(0, 4, 0, 5),
        Child = content
    };

    private static Control KeyValue(string key, string value)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 25
        };
        row.Children.Add(new TextBlock
        {
            Text = key,
            Foreground = Muted,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center
        });
        TextBlock right = new()
        {
            Text = value,
            Foreground = TextForeground,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    private ItemInspectionData ResolveItemInspection(string itemName, string? slot)
    {
        ItemIdentification? live = null;
        _snapshot.Character.Equipment.IdentifiedItems.TryGetValue(itemName, out live);
        _itemKnowledgeCache.TryGetValue(itemName, out ItemKnowledge? persisted);
        return new ItemInspectionData(itemName, slot, live, persisted);
    }

    private Control BuildEquipmentGrid(IReadOnlyList<EquipmentSlotState> slots)
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            RowSpacing = 0
        };
        int rows = (slots.Count + 1) / 2;
        for (int row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (int index = 0; index < slots.Count; index++)
        {
            Control cell = BuildEquipmentCell(slots[index]);
            Grid.SetColumn(cell, index % 2);
            Grid.SetRow(cell, index / 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Control BuildEquipmentCell(EquipmentSlotState slot)
    {
        string label = CleanSlot(slot.Slot);
        if (slot.Ordinal > 1)
        {
            label += $" {slot.Ordinal}";
        }

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("54,*"),
            ColumnSpacing = 6,
            MinHeight = 22
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        TextBlock item = new()
        {
            Text = slot.Item ?? "-",
            Foreground = slot.IsEmpty ? UiTheme.Faint : TextForeground,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(item, 1);
        row.Children.Add(item);

        Border line = new()
        {
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
        line.Opacity = slot.IsEmpty ? 0.55 : 1;

        if (slot.IsEmpty)
        {
            return line;
        }

        Button identify = new()
        {
            Content = line,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        ItemIdentification? liveIdentification = null;
        _snapshot.Character.Equipment.IdentifiedItems.TryGetValue(slot.Item!, out liveIdentification);
        _itemKnowledgeCache.TryGetValue(slot.Item!, out ItemKnowledge? persistedIdentification);
        ToolTip.SetTip(identify, BuildEquipmentTooltip(slot.Item!, liveIdentification, persistedIdentification));
        identify.Click += async (_, _) => await SubmitCommandAsync($"id {slot.Item}");
        return identify;
    }

    private static Control BuildEquipmentTooltip(
        string itemName,
        ItemIdentification? live,
        ItemKnowledge? persisted) =>
        ItemInspectionPopover.Build(new ItemInspectionData(itemName, null, live, persisted));

    private static Control EquipmentRow(string slot, string item)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("115,*") };
        row.Children.Add(new TextBlock
        {
            Text = slot,
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center
        });
        TextBlock value = new()
        {
            Text = item,
            Foreground = TextForeground,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return Card(row, PanelBorderBrush);
    }

    private static Control Badge(string text, IBrush brush) => new Border
    {
        BorderBrush = brush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2),
        Padding = new Thickness(6, 2),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, Foreground = brush, FontSize = NexTypography.Metadata }
    };

    private static Control BuildHudReadout(string name, TextBlock value, double minWidth)
    {
        StackPanel stack = new()
        {
            Spacing = 2,
            MinWidth = minWidth,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Muted,
            FontSize = NexTypography.Hud
        });
        value.Text = "-";
        value.Foreground = TextForeground;
        value.FontSize = NexTypography.HudStrong;
        value.FontWeight = FontWeight.SemiBold;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(value);
        return stack;
    }

    private static string FormatCombatTarget(CombatState combat)
    {
        if (!combat.Active)
        {
            return "-";
        }

        string target = combat.TargetName ?? combat.TargetId ?? "unknown";
        return string.IsNullOrWhiteSpace(combat.TargetCondition)
            ? target
            : $"{target} · {combat.TargetCondition}";
    }

    private static Control BuildMeter(TextBlock label, ProgressBar bar, string name)
    {
        StackPanel stack = new()
        {
            Spacing = 3,
            Margin = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.Text = name;
        label.Foreground = TextForeground;
        label.FontSize = NexTypography.Hud;
        stack.Children.Add(label);
        stack.Children.Add(bar);
        return stack;
    }

    private static Control CompactVital(string name, VitalState vital, IBrush color)
    {
        StackPanel stack = new() { Spacing = 3 };
        TextBlock label = new()
        {
            Text = $"{name}  {FormatVital(vital)}",
            Foreground = TextForeground,
            FontSize = NexTypography.Hud
        };
        ProgressBar bar = VitalBar(color);
        int maximum = Math.Max(vital.Maximum ?? 1, 1);
        bar.Maximum = maximum;
        bar.Value = Math.Clamp(vital.Current ?? 0, 0, maximum);
        stack.Children.Add(label);
        stack.Children.Add(bar);
        return stack;
    }

    private static ProgressBar VitalBar(IBrush color) => new()
    {
        Minimum = 0,
        Maximum = 1,
        Height = 10,
        MinWidth = 150,
        Foreground = color,
        Background = ConsoleBackground
    };

    private static void SetVital(ProgressBar bar, TextBlock label, string name, VitalState vital)
    {
        int maximum = Math.Max(vital.Maximum ?? 1, 1);
        int current = Math.Clamp(vital.Current ?? 0, 0, maximum);
        bar.Maximum = maximum;
        bar.Value = current;
        label.Text = $"{name}  {FormatVital(vital)}";
    }

    private static TextBox ReadOnlyBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Background = ConsoleBackground,
        Foreground = TextForeground,
        BorderBrush = PanelBorderBrush,
        FontFamily = UiTheme.Mono,
        FontSize = NexTypography.Monospace
    };

    private static string? EntityTarget(RoomContentObservation entity)
    {
        string? target = entity.TargetKeywords?.FirstOrDefault(keyword => !string.IsNullOrWhiteSpace(keyword));
        if (!string.IsNullOrWhiteSpace(target))
        {
            return target;
        }
        if (!string.IsNullOrWhiteSpace(entity.CanonicalName))
        {
            return entity.CanonicalName;
        }
        return null;
    }

    private static string CleanSlot(string slot)
    {
        string normalized = slot
            .Replace("<", string.Empty, StringComparison.Ordinal)
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Trim();
        return normalized.ToLowerInvariant() switch
        {
            "worn on finger" => "Finger",
            "worn around neck" => "Neck",
            "worn on torso" => "Torso",
            "worn on head" => "Head",
            "worn on legs" => "Legs",
            "worn on feet" => "Feet",
            "worn on hands" => "Hands",
            "worn on arms" => "Arms",
            "worn as shield" => "Shield",
            "worn about body" => "Body",
            "worn about waist" => "Waist",
            "worn around wrist" => "Wrist",
            "wielded" => "Wielded",
            "dual wielded" => "Dual wielded",
            "branded" => "Brand",
            _ => normalized
        };
    }

    private static string DirectionLabel(string direction) => direction.ToLowerInvariant() switch
    {
        "north" => "N",
        "northeast" => "NE",
        "east" => "E",
        "southeast" => "SE",
        "south" => "S",
        "southwest" => "SW",
        "west" => "W",
        "northwest" => "NW",
        "up" => "U",
        "down" => "D",
        _ => direction
    };

    private static string FormatVital(VitalState vital) =>
        $"{vital.Current?.ToString() ?? "-"}/{vital.Maximum?.ToString() ?? "-"}";

    private static string FormatExits(ExitState exits)
    {
        if (!exits.IsKnown)
        {
            return "Exits unknown";
        }
        if (exits.Details is null || exits.Details.Count == 0)
        {
            return exits.Directions.Count == 0 ? "No exits" : "Exits " + string.Join(" ", exits.Directions.Select(DirectionLabel));
        }
        return "Exits " + string.Join(" ", exits.Details.Select(exit =>
            exit.Traversability == ExitTraversability.Blocked
                ? $"({DirectionLabel(exit.Direction)})"
                : DirectionLabel(exit.Direction)));
    }

    private static string FormatCapacity(int? current, int? maximum)
    {
        if (current is null)
        {
            return "-";
        }
        return maximum is null
            ? current.Value.ToString("N0")
            : $"{current.Value:N0} / {maximum.Value:N0}";
    }

    private static string FormatWealth(InventorySummary inventory)
    {
        List<string> values = [];
        if (inventory.Gold is not null) values.Add($"{inventory.Gold}g");
        if (inventory.Silver is not null) values.Add($"{inventory.Silver}s");
        if (inventory.Copper is not null) values.Add($"{inventory.Copper}c");
        return values.Count == 0 ? "-" : string.Join(" ", values);
    }

    private static IBrush SegmentForeground(AnsiTextStyle style)
    {
        AnsiColor color = style.Foreground ?? new AnsiColor(232, 237, 242);
        return new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
    }

    private static IBrush? SegmentBackground(AnsiTextStyle style)
    {
        if (style.Background is null)
        {
            return null;
        }
        AnsiColor color = style.Background;
        return new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

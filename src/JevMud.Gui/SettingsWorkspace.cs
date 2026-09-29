using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.RegularExpressions;
using JevMud.Client.Runtime;
using JevMud.Client.Interaction;
using JevMud.Client.Settings;
using JevMud.Contracts.Jev;
using JevMud.Core.Jev;

namespace JevMud.Gui;

public sealed class SettingsWorkspace : UserControl
{
    private enum SettingsPage
    {
        General,
        Keybindings,
        Highlights,
        Automation,
        Protocols,
        Mapper,
        Connection,
        Jev
    }

    private static readonly IBrush WindowBackground = UiTheme.Window;
    private static readonly IBrush SidebarBackground = UiTheme.Surface;
    private static readonly IBrush RaisedBackground = UiTheme.Raised;
    private static readonly IBrush TextForeground = UiTheme.Text;
    private static readonly IBrush Muted = UiTheme.Muted;
    private static readonly IBrush Accent = UiTheme.Accent;
    private static readonly IBrush Success = UiTheme.Success;
    private static readonly IBrush BorderColor = UiTheme.Divider;
    private static readonly IBrush Error = UiTheme.Danger;

    private sealed class HighlightEditor
    {
        public required TextBox Pattern { get; init; }
        public required TextBox Foreground { get; init; }
        public required ComboBox MatchMode { get; init; }
        public required CheckBox Bold { get; init; }
        public required CheckBox Underline { get; init; }
        public required CheckBox CaseSensitive { get; init; }
        public required CheckBox Enabled { get; init; }
    }

    private sealed class AliasEditor
    {
        public required TextBox Name { get; init; }
        public required TextBox Expansion { get; init; }
        public required CheckBox Enabled { get; init; }
    }

    private sealed class TriggerEditor
    {
        public required TextBox Pattern { get; init; }
        public required TextBox Command { get; init; }
        public required ComboBox MatchMode { get; init; }
        public required ComboBox Scope { get; init; }
        public required TextBox Group { get; init; }
        public required TextBox Priority { get; init; }
        public required TextBox Cooldown { get; init; }
        public required CheckBox StopProcessing { get; init; }
        public required CheckBox CaseSensitive { get; init; }
        public required CheckBox Enabled { get; init; }
        public required CheckBox OneShot { get; init; }
    }

    private sealed class GameRuleEditor
    {
        public required TextBox Name { get; init; }
        public required TextBox Condition { get; init; }
        public required TextBox Command { get; init; }
        public required TextBox Group { get; init; }
        public required TextBox Priority { get; init; }
        public required TextBox Cooldown { get; init; }
        public required CheckBox StopProcessing { get; init; }
        public required CheckBox Enabled { get; init; }
        public required ComboBox Activation { get; init; }
        public required CheckBox OneShot { get; init; }
    }

    private sealed class WorkflowEditor
    {
        public required TextBox Name { get; init; }
        public required TextBox TriggerCondition { get; init; }
        public required TextBox TriggerEvent { get; init; }
        public required TextBox Steps { get; init; }
        public required TextBox Group { get; init; }
        public required TextBox Priority { get; init; }
        public required TextBox Cooldown { get; init; }
        public required ComboBox FailureMode { get; init; }
        public required CheckBox Enabled { get; init; }
        public required CheckBox OneShot { get; init; }
    }

    private sealed class TimerEditor
    {
        public required TextBox Name { get; init; }
        public required TextBox Interval { get; init; }
        public required TextBox Command { get; init; }
        public required TextBox Group { get; init; }
        public required CheckBox Repeat { get; init; }
        public required CheckBox Enabled { get; init; }
    }

    private sealed class KeyBindingEditor
    {
        public required TextBox Name { get; init; }
        public required TextBox Gesture { get; init; }
        public required TextBox Command { get; init; }
        public required ComboBox Context { get; init; }
        public required ComboBox Action { get; init; }
        public required TextBox Priority { get; init; }
        public required CheckBox Enabled { get; init; }
    }

    private sealed class OutputRuleEditor
    {
        public required string Id { get; init; }
        public required TextBox Name { get; init; }
        public required TextBox Pattern { get; init; }
        public required ComboBox MatchType { get; init; }
        public required ComboBox Action { get; init; }
        public required TextBox Value { get; init; }
        public required TextBox Priority { get; init; }
        public required CheckBox CaseSensitive { get; init; }
        public required CheckBox Enabled { get; init; }
        public required IReadOnlyList<OutputRuleAction> PreservedActions { get; init; }
    }

    private readonly JevMudRuntime _runtime;
    private readonly Action _cancel;
    private readonly Action _saved;
    private readonly TextBox _host = Field();
    private readonly TextBox _port = Field();
    private readonly CheckBox _tls = new();
    private readonly TextBox _terminalType = Field();
    private readonly ComboBox _fontSize = new();
    private readonly CheckBox _showContextDock = new();
    private readonly CheckBox _autoCombatContext = new();
    private readonly CheckBox _autoLogSessions = new();
    private readonly CheckBox _slurpTelemetryPrompt = new();
    private readonly TextBox _commandSeparator = Field();
    private readonly TextBox _historyMaximum = Field();
    private readonly CheckBox _persistHistory = new();
    private readonly CheckBox _deduplicateHistory = new();
    private readonly CheckBox _completionEnabled = new();
    private readonly TextBox _completionTokenLimit = Field();
    private readonly CheckBox _localEcho = new();
    private readonly ComboBox _timestampMode = new();
    private readonly TextBox _scrollbackMaximum = Field();
    private readonly CheckBox _splitOutputEnabled = new();
    private readonly CheckBox _notifyWhenUnfocused = new();
    private readonly ComboBox _logFormat = new();
    private readonly StackPanel _highlightRows = new() { Spacing = 0 };
    private readonly List<HighlightEditor> _highlightEditors = [];
    private readonly StackPanel _outputRuleRows = new() { Spacing = 0 };
    private readonly List<OutputRuleEditor> _outputRuleEditors = [];
    private readonly StackPanel _aliasRows = new() { Spacing = 0 };
    private readonly List<AliasEditor> _aliasEditors = [];
    private readonly StackPanel _triggerRows = new() { Spacing = 0 };
    private readonly List<TriggerEditor> _triggerEditors = [];
    private readonly StackPanel _gameRuleRows = new() { Spacing = 0 };
    private readonly List<GameRuleEditor> _gameRuleEditors = [];
    private readonly StackPanel _workflowRows = new() { Spacing = 0 };
    private readonly List<WorkflowEditor> _workflowEditors = [];
    private readonly StackPanel _timerRows = new() { Spacing = 0 };
    private readonly List<TimerEditor> _timerEditors = [];
    private readonly StackPanel _keyBindingRows = new() { Spacing = 0 };
    private readonly List<KeyBindingEditor> _keyBindingEditors = [];
    private readonly CheckBox _automationEnabled = new();
    private readonly TextBox _automationRate = Field();
    private readonly TextBox _rollingBufferCharacters = Field();
    private readonly TextBox _disabledAutomationGroups = Field();
    private readonly TextBox _automationHumanOverride = Field();
    private readonly TextBox _automationMaxWorkflows = Field();
    private readonly CheckBox _automationPersistVariables = new();
    private readonly CheckBox _protocolNaws = new();
    private readonly CheckBox _protocolGmcp = new();
    private readonly CheckBox _protocolMsdp = new();
    private readonly CheckBox _protocolMssp = new();
    private readonly CheckBox _protocolMccp2 = new();
    private readonly CheckBox _protocolCharset = new();
    private readonly CheckBox _protocolNewEnvironment = new();
    private readonly CheckBox _protocolMtts = new();
    private readonly CheckBox _protocolEor = new();
    private readonly CheckBox _mapperEnabled = new();
    private readonly CheckBox _mapperAutoMap = new();
    private readonly CheckBox _mapperPersistKnowledge = new();
    private readonly CheckBox _mapperAvoidBlocked = new();
    private readonly CheckBox _mapperAllowUnknown = new();
    private readonly TextBox _mapperMaximumDepth = Field();
    private readonly CheckBox _mapperAutoMoveEnabled = new();
    private readonly CheckBox _mapperStopOnCombat = new();
    private readonly CheckBox _mapperResumeAfterCombat = new();
    private readonly CheckBox _mapperReplanOnDeviation = new();
    private readonly TextBox _mapperStepDelay = Field();
    private readonly TextBox _mapperStepTimeout = Field();
    private readonly TextBox _mapperMaximumReplans = Field();
    private readonly TextBox _mapperVisualDepth = Field();
    private readonly TextBox _mapperVisualRooms = Field();
    private readonly TextBox _mapperAvoidAreas = Field();
    private readonly TextBox _mapperAvoidTerrains = Field();
    private readonly TextBox _mapperAvoidMobs = Field();
    private readonly CheckBox _mapperAvoidClosedDoors = new();
    private readonly CheckBox _mapperPreferKnownTraversable = new();
    private readonly CheckBox _mapperAutoOpenDoors = new();
    private readonly TextBox _mapperDoorOpenTemplate = Field();
    private readonly TextBox _apiKey = Field();
    private readonly TextBox _jevModel = Field();
    private readonly CheckBox _jevEnabled = new();
    private readonly TextBlock _secretStatus = new();
    private readonly ComboBox _preset = new();
    private readonly Dictionary<JevDomain, ComboBox> _authority = [];
    private readonly TextBlock _validation = new();
    private readonly ContentControl _pageContent = new();
    private readonly Dictionary<SettingsPage, Button> _navButtons = [];
    private readonly Dictionary<SettingsPage, Control> _pages = [];
    private bool _updatingAuthority;

    public SettingsWorkspace(JevMudRuntime runtime, Action cancel, Action saved)
    {
        _runtime = runtime;
        _cancel = cancel;
        _saved = saved;
        MinWidth = 780;
        MinHeight = 580;
        Focusable = true;
        Background = WindowBackground;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;

        _pages[SettingsPage.General] = BuildGeneralPage();
        _pages[SettingsPage.Keybindings] = BuildKeybindingsPage();
        _pages[SettingsPage.Highlights] = BuildHighlightsPage();
        _pages[SettingsPage.Automation] = BuildAutomationPage();
        _pages[SettingsPage.Protocols] = BuildProtocolsPage();
        _pages[SettingsPage.Mapper] = BuildMapperPage();
        _pages[SettingsPage.Connection] = BuildConnectionPage();
        _pages[SettingsPage.Jev] = BuildJevPage();
        Content = BuildLayout();
        LoadSettings();
        SelectPage(SettingsPage.General);
        _ = LoadSecretAsync();
    }

    public bool Saved { get; private set; }

    private Control BuildLayout()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            ColumnDefinitions = new ColumnDefinitions("210,*"),
            Background = WindowBackground
        };

        Border sidebar = new()
        {
            Background = SidebarBackground,
            BorderBrush = BorderColor,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 14)
        };
        StackPanel nav = new() { Spacing = 2 };
        nav.Children.Add(new TextBlock
        {
            Text = "Settings",
            Foreground = TextForeground,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 0, 8, 10)
        });
        nav.Children.Add(NavigationButton(SettingsPage.General, "Input & Output", "History, completion, transcript"));
        nav.Children.Add(NavigationButton(SettingsPage.Keybindings, "Keybindings", "Contexts, actions, conflicts"));
        nav.Children.Add(NavigationButton(SettingsPage.Highlights, "Rules", "Highlight, gag, substitute, notify"));
        nav.Children.Add(NavigationButton(SettingsPage.Automation, "Automation", "Rules, aliases, triggers, timers"));
        nav.Children.Add(NavigationButton(SettingsPage.Protocols, "Protocols", "Telnet capabilities and OOB data"));
        nav.Children.Add(NavigationButton(SettingsPage.Mapper, "Mapper", "World graph and route planning"));
        nav.Children.Add(NavigationButton(SettingsPage.Connection, "Connection", "Server and terminal identity"));
        nav.Children.Add(NavigationButton(SettingsPage.Jev, "Jev", "API key and authority"));
        sidebar.Child = nav;
        root.Children.Add(sidebar);

        _pageContent.Margin = new Thickness(22, 16, 22, 12);
        Grid.SetColumn(_pageContent, 1);
        root.Children.Add(_pageContent);

        Border footer = new()
        {
            Background = SidebarBackground,
            BorderBrush = BorderColor,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 12)
        };
        Grid footerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _validation.Foreground = Error;
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
        save.Click += async (_, _) => await SaveAsync();
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
            "Input & Output",
            "Configure first-class interaction behavior. Raw server data remains available to parsing, Automation, replay, and logging.");

        Border transcript = SectionCard("Game transcript", "Presentation only. Server text is preserved exactly as received.");
        StackPanel transcriptBody = (StackPanel)transcript.Child!;
        _fontSize.ItemsSource = new double[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24 };
        _fontSize.MinWidth = 150;
        transcriptBody.Children.Add(FormRow("Text size", _fontSize));
        ConfigureCheckBox(_slurpTelemetryPrompt, "Prompt slurp - hide Avendar [J|…] telemetry prompts from the visible transcript");
        transcriptBody.Children.Add(_slurpTelemetryPrompt);
        transcriptBody.Children.Add(new TextBlock
        {
            Text = "Display-only. Prompt telemetry is still parsed into state and retained by raw session logging.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        _timestampMode.ItemsSource = Enum.GetValues<TimestampRenderMode>();
        _timestampMode.MinWidth = 180;
        transcriptBody.Children.Add(FormRow("Timestamps", _timestampMode));
        _scrollbackMaximum.Width = 110;
        transcriptBody.Children.Add(FormRow("Scrollback entries", _scrollbackMaximum));
        ConfigureCheckBox(_splitOutputEnabled, "Enable split-output history/live view when scrolling away from the bottom");
        ConfigureCheckBox(_notifyWhenUnfocused, "Allow output rules to request attention when NexMUD is unfocused");
        transcriptBody.Children.Add(_splitOutputEnabled);
        transcriptBody.Children.Add(_notifyWhenUnfocused);
        stack.Children.Add(transcript);

        Border input = SectionCard("Command input", "History, completion, and ordered command batches are application services, not TextBox behavior.");
        StackPanel inputBody = (StackPanel)input.Child!;
        _commandSeparator.Width = 72;
        _commandSeparator.MaxLength = 1;
        _commandSeparator.PlaceholderText = "Off";
        inputBody.Children.Add(FormRow("Command separator", _commandSeparator));
        inputBody.Children.Add(new TextBlock
        {
            Text = "Default: ;  Example: n;n;n;w;open chest. Leave blank to disable. Prefix the separator with \\ to send it literally.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        _historyMaximum.Width = 100;
        _completionTokenLimit.Width = 110;
        inputBody.Children.Add(FormRow("History entries", _historyMaximum));
        inputBody.Children.Add(FormRow("Completion token index", _completionTokenLimit));
        ConfigureCheckBox(_persistHistory, "Persist manual command history");
        ConfigureCheckBox(_deduplicateHistory, "Collapse consecutive duplicate history entries");
        ConfigureCheckBox(_completionEnabled, "Enable Tab / Shift-Tab completion");
        ConfigureCheckBox(_localEcho, "Echo manual and generated commands in World with provenance styling");
        inputBody.Children.Add(_persistHistory);
        inputBody.Children.Add(_deduplicateHistory);
        inputBody.Children.Add(_completionEnabled);
        inputBody.Children.Add(_localEcho);
        stack.Children.Add(input);

        Border context = SectionCard("Gameplay rail", "Character telemetry and room context are persistent parts of the world-first gameplay surface.");
        StackPanel contextBody = (StackPanel)context.Child!;
        contextBody.Children.Add(new TextBlock
        {
            Text = "World remains visible while you play. Mapper, Codex, Automation, Scripting, and other deep tools share the in-app right workspace; selecting World restores the persistent Character and Room rail.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(context);

        Border logging = SectionCard("Session logging", "Logs contain the decoded server stream verbatim, including ANSI escape sequences. Client annotations are never injected.");
        StackPanel loggingBody = (StackPanel)logging.Child!;
        ConfigureCheckBox(_autoLogSessions, "Start logging automatically when a connection opens");
        loggingBody.Children.Add(_autoLogSessions);
        _logFormat.ItemsSource = Enum.GetValues<TranscriptLogFormat>();
        _logFormat.MinWidth = 170;
        loggingBody.Children.Add(FormRow("Log format", _logFormat));
        loggingBody.Children.Add(new TextBlock
        {
            Text = GetLogDirectory(),
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(logging);
        return PageScroll(stack);
    }

    private Control BuildKeybindingsPage()
    {
        StackPanel stack = PageStack(
            "Keybindings",
            "Typed, context-aware bindings invoke application actions. Equal-priority collisions in the same context are configuration errors.");

        Border bindings = SectionCard(
            "Bindings",
            "Use Primary for Command on macOS / Ctrl elsewhere. Input context wins over Global; Global is the fallback.");
        StackPanel body = (StackPanel)bindings.Child!;
        body.Children.Add(_keyBindingRows);
        StackPanel bindingActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button add = SecondaryButton("Add Key Binding");
        add.Click += (_, _) => AddKeyBindingEditor(new CommandKeyBinding(
            "Primary+1",
            "look",
            Context: KeybindingContext.Global,
            Action: KeybindingActionKind.SendCommand));
        bindingActions.Children.Add(add);
        Button reset = SecondaryButton("Clear Custom Bindings");
        reset.Click += (_, _) =>
        {
            _keyBindingRows.Children.Clear();
            _keyBindingEditors.Clear();
        };
        bindingActions.Children.Add(reset);
        body.Children.Add(bindingActions);
        body.Children.Add(new TextBlock
        {
            Text = "Conflicts are detected by gesture + context + priority and must be resolved before settings can be saved.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(bindings);
        return PageScroll(stack);
    }

    private Control BuildHighlightsPage()
    {
        StackPanel stack = PageStack(
            "Output Rules",
            "Presentation rules operate on immutable output frames and never rewrite semantic parsing input.");

        Border rules = SectionCard("Highlight rules", "Literal and regular-expression matches can override foreground color and add emphasis. ANSI styling remains intact outside the matched text.");
        StackPanel body = (StackPanel)rules.Child!;
        body.Children.Add(_highlightRows);

        Button add = SecondaryButton("Add Highlight");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => AddHighlightEditor(new TranscriptHighlightRule("", "#FFD166"));
        body.Children.Add(add);
        stack.Children.Add(rules);

        Border transformations = SectionCard(
            "Transformations",
            "Typed display-only rules. Gag hides presentation only; substitution never changes semantic source text; regex execution is bounded.");
        StackPanel transformationBody = (StackPanel)transformations.Child!;
        transformationBody.Children.Add(_outputRuleRows);
        Button addRule = SecondaryButton("Add Output Rule");
        addRule.HorizontalAlignment = HorizontalAlignment.Left;
        addRule.Click += (_, _) => AddOutputRuleEditor(new OutputTransformationRule(
            Guid.NewGuid().ToString("N"),
            "New rule",
            "",
            Actions: [new OutputRuleAction(OutputRuleActionKind.Highlight, Foreground: "#FFD166", Bold: true)]));
        transformationBody.Children.Add(addRule);
        stack.Children.Add(transformations);
        return PageScroll(stack);
    }

    private Control BuildAutomationPage()
    {
        StackPanel stack = PageStack(
            "Automation",
            "Traditional MUD-client conveniences. These rules never alter or suppress server output.");

        Border engine = SectionCard("Automation engine", "Rules-sourced automation shares NexMUD's semantic action pipeline and cannot bypass action validation.");
        StackPanel engineBody = (StackPanel)engine.Child!;
        ConfigureCheckBox(_automationEnabled, "Enable deterministic automation");
        engineBody.Children.Add(_automationEnabled);
        _automationRate.Width = 100;
        _rollingBufferCharacters.Width = 120;
        _automationHumanOverride.Width = 120;
        _automationMaxWorkflows.Width = 100;
        engineBody.Children.Add(FormRow("Max commands / second", _automationRate));
        engineBody.Children.Add(FormRow("Rolling trigger buffer", _rollingBufferCharacters));
        engineBody.Children.Add(FormRow("Human override window (ms)", _automationHumanOverride));
        engineBody.Children.Add(FormRow("Max running automations", _automationMaxWorkflows));
        ConfigureCheckBox(_automationPersistVariables, "Persist automation variables across sessions");
        engineBody.Children.Add(_automationPersistVariables);
        _disabledAutomationGroups.PlaceholderText = "Comma-separated groups, e.g. Grinding, Social";
        engineBody.Children.Add(FormRow("Disabled groups", _disabledAutomationGroups));
        stack.Children.Add(engine);

        Border aliases = SectionCard("Aliases", "Expand a short command before it is sent. Use $* for all arguments and $1 through $9 for positional arguments.");
        StackPanel aliasBody = (StackPanel)aliases.Child!;
        aliasBody.Children.Add(_aliasRows);
        Button addAlias = SecondaryButton("Add Alias");
        addAlias.HorizontalAlignment = HorizontalAlignment.Left;
        addAlias.Click += (_, _) => AddAliasEditor(new CommandAlias("", ""));
        aliasBody.Children.Add(addAlias);
        stack.Children.Add(aliases);

        Border triggers = SectionCard("Triggers", "Match complete server-output lines and send a command. Regex commands may use $1-$9 capture groups. Trigger matching never gags or rewrites output.");
        StackPanel triggerBody = (StackPanel)triggers.Child!;
        triggerBody.Children.Add(_triggerRows);
        Button addTrigger = SecondaryButton("Add Trigger");
        addTrigger.HorizontalAlignment = HorizontalAlignment.Left;
        addTrigger.Click += (_, _) => AddTriggerEditor(new TriggerRule("", ""));
        triggerBody.Children.Add(addTrigger);
        stack.Children.Add(triggers);

        Border gameRules = SectionCard("Game rules", "State-aware deterministic rules. Conditions use && and comparisons, for example: combat.active && hp.percent < 30.");
        StackPanel gameRuleBody = (StackPanel)gameRules.Child!;
        gameRuleBody.Children.Add(_gameRuleRows);
        Button addGameRule = SecondaryButton("Add Game Rule");
        addGameRule.HorizontalAlignment = HorizontalAlignment.Left;
        addGameRule.Click += (_, _) => AddGameRuleEditor(new GameRule("", "", ""));
        gameRuleBody.Children.Add(addGameRule);
        gameRuleBody.Children.Add(new TextBlock
        {
            Text = "State names: connected, combat.active, combat.target, hp, hp.max, hp.percent, mana, mana.max, mana.percent, move, move.max, move.percent, position, room.id, room.name, room.terrain, room.light, room.occupants, room.objects, room.corpses, level, xp, xp.tolevel.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(gameRules);

        Border workflows = SectionCard(
            "Multi-step automations",
            "Build named behaviors in terms of when they start and the actions Jev performs. Leave both start conditions empty for an automation you run manually.");
        StackPanel workflowBody = (StackPanel)workflows.Child!;
        workflowBody.Children.Add(_workflowRows);
        Button addWorkflow = SecondaryButton("Add Automation");
        addWorkflow.HorizontalAlignment = HorizontalAlignment.Left;
        addWorkflow.Click += (_, _) => AddWorkflowEditor(new AutomationWorkflow("", "send look"));
        workflowBody.Children.Add(addWorkflow);
        workflowBody.Children.Add(new TextBlock
        {
            Text = "Actions are one per line. Common actions: send <command>, wait <condition>, wait-event <event>, navigate <room/MOB/item>, jev <domain>, delay <ms>, and stop. Advanced actions also support if/unless, retry, assert, set/unset, ${state} values, and ${event.field} values.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(workflows);

        Border timers = SectionCard("Timers", "Send commands on a wall-clock interval while connected and in normal input mode.");
        StackPanel timerBody = (StackPanel)timers.Child!;
        timerBody.Children.Add(_timerRows);
        Button addTimer = SecondaryButton("Add Timer");
        addTimer.HorizontalAlignment = HorizontalAlignment.Left;
        addTimer.Click += (_, _) => AddTimerEditor(new CommandTimer("", 60, ""));
        timerBody.Children.Add(addTimer);
        stack.Children.Add(timers);

        return PageScroll(stack);
    }

    private Control BuildProtocolsPage()
    {
        StackPanel stack = PageStack(
            "Protocols",
            "Negotiate structured and transport capabilities explicitly. Changes apply on the next connection; disable a protocol when debugging server compatibility.");

        Border structured = SectionCard("Structured game data", "Prefer structured out-of-band state when the server provides it; text parsing remains the fallback.");
        StackPanel body = (StackPanel)structured.Child!;
        ConfigureCheckBox(_protocolGmcp, "GMCP - Generic MUD Communication Protocol");
        ConfigureCheckBox(_protocolMsdp, "MSDP - MUD Server Data Protocol");
        ConfigureCheckBox(_protocolMssp, "MSSP - MUD Server Status Protocol");
        body.Children.Add(_protocolGmcp);
        body.Children.Add(_protocolMsdp);
        body.Children.Add(_protocolMssp);
        stack.Children.Add(structured);

        Border transport = SectionCard("Telnet capabilities", "Compression, terminal sizing, charset negotiation, and terminal capability reporting.");
        StackPanel transportBody = (StackPanel)transport.Child!;
        ConfigureCheckBox(_protocolMccp2, "MCCP2 compression");
        ConfigureCheckBox(_protocolNaws, "NAWS terminal-size negotiation");
        ConfigureCheckBox(_protocolCharset, "CHARSET negotiation (UTF-8)");
        ConfigureCheckBox(_protocolNewEnvironment, "NEW-ENVIRON / MNES client metadata");
        ConfigureCheckBox(_protocolMtts, "MTTS terminal capability reporting");
        ConfigureCheckBox(_protocolEor, "EOR prompt boundaries");
        transportBody.Children.Add(_protocolMccp2);
        transportBody.Children.Add(_protocolNaws);
        transportBody.Children.Add(_protocolCharset);
        transportBody.Children.Add(_protocolNewEnvironment);
        transportBody.Children.Add(_protocolMtts);
        transportBody.Children.Add(_protocolEor);
        stack.Children.Add(transport);
        return PageScroll(stack);
    }

    private Control BuildMapperPage()
    {
        StackPanel stack = PageStack(
            "Mapper",
            "Build the world graph from observed movement and persistent room knowledge. Route planning never invents unknown topology.");

        Border graph = SectionCard("World graph", "Rooms and confirmed directional transitions are persisted in the local knowledge database.");
        StackPanel graphBody = (StackPanel)graph.Child!;
        ConfigureCheckBox(_mapperEnabled, "Enable mapper services");
        ConfigureCheckBox(_mapperAutoMap, "Automatically record observed rooms and movement edges");
        ConfigureCheckBox(_mapperPersistKnowledge, "Persist map knowledge across sessions");
        graphBody.Children.Add(_mapperEnabled);
        graphBody.Children.Add(_mapperAutoMap);
        graphBody.Children.Add(_mapperPersistKnowledge);
        stack.Children.Add(graph);

        Border routes = SectionCard("Route planning", "Route planning uses only known topology and honors blocked/avoided rooms.");
        StackPanel routeBody = (StackPanel)routes.Child!;
        ConfigureCheckBox(_mapperAvoidBlocked, "Avoid exits known to be blocked");
        ConfigureCheckBox(_mapperAllowUnknown, "Allow exits with unknown traversability");
        ConfigureCheckBox(_mapperAvoidClosedDoors, "Avoid exits known to be closed");
        ConfigureCheckBox(_mapperPreferKnownTraversable, "Prefer confirmed traversable exits");
        _mapperMaximumDepth.Width = 100;
        routeBody.Children.Add(_mapperAvoidBlocked);
        routeBody.Children.Add(_mapperAllowUnknown);
        routeBody.Children.Add(_mapperAvoidClosedDoors);
        routeBody.Children.Add(_mapperPreferKnownTraversable);
        routeBody.Children.Add(FormRow("Maximum route depth", _mapperMaximumDepth));
        _mapperAvoidAreas.PlaceholderText = "Comma-separated areas";
        _mapperAvoidTerrains.PlaceholderText = "Comma-separated terrain names";
        _mapperAvoidMobs.PlaceholderText = "Comma-separated MOB names";
        routeBody.Children.Add(FormRow("Avoid areas", _mapperAvoidAreas));
        routeBody.Children.Add(FormRow("Avoid terrain", _mapperAvoidTerrains));
        routeBody.Children.Add(FormRow("Avoid rooms containing MOBs", _mapperAvoidMobs));
        stack.Children.Add(routes);

        Border movement = SectionCard("Auto-move", "Execute known routes one room at a time. NexMUD waits for confirmed arrival before sending the next movement command.");
        StackPanel movementBody = (StackPanel)movement.Child!;
        ConfigureCheckBox(_mapperAutoMoveEnabled, "Enable mapper auto-move");
        ConfigureCheckBox(_mapperStopOnCombat, "Pause auto-move when combat starts");
        ConfigureCheckBox(_mapperResumeAfterCombat, "Resume automatically when combat ends");
        ConfigureCheckBox(_mapperReplanOnDeviation, "Re-plan when movement leaves the expected route");
        ConfigureCheckBox(_mapperAutoOpenDoors, "Open known closed doors during auto-move");
        movementBody.Children.Add(_mapperAutoMoveEnabled);
        movementBody.Children.Add(_mapperStopOnCombat);
        movementBody.Children.Add(_mapperResumeAfterCombat);
        movementBody.Children.Add(_mapperReplanOnDeviation);
        movementBody.Children.Add(_mapperAutoOpenDoors);
        _mapperDoorOpenTemplate.PlaceholderText = "open {direction}";
        movementBody.Children.Add(FormRow("Door open command", _mapperDoorOpenTemplate));
        _mapperStepDelay.Width = 100;
        _mapperStepTimeout.Width = 100;
        _mapperMaximumReplans.Width = 100;
        movementBody.Children.Add(FormRow("Delay between confirmed steps (ms)", _mapperStepDelay));
        movementBody.Children.Add(FormRow("Arrival confirmation timeout (ms)", _mapperStepTimeout));
        movementBody.Children.Add(FormRow("Maximum automatic re-plans", _mapperMaximumReplans));
        stack.Children.Add(movement);

        Border visual = SectionCard("Visual map", "Control how much of the observed graph is rendered around the current room.");
        StackPanel visualBody = (StackPanel)visual.Child!;
        _mapperVisualDepth.Width = 100;
        _mapperVisualRooms.Width = 100;
        visualBody.Children.Add(FormRow("Graph depth", _mapperVisualDepth));
        visualBody.Children.Add(FormRow("Maximum visible rooms", _mapperVisualRooms));
        stack.Children.Add(visual);
        return PageScroll(stack);
    }

    private Control BuildConnectionPage()
    {
        StackPanel stack = PageStack(
            "Connection",
            "Connection settings are local to this client. Jev integration is never advertised to the MUD server.");

        Border server = SectionCard("Server", "The saved profile is used by Connect and automatic startup connection.");
        StackPanel serverBody = (StackPanel)server.Child!;
        serverBody.Children.Add(FormRow("Host", _host));
        serverBody.Children.Add(FormRow("Port", _port));
        ConfigureCheckBox(_tls, "Use TLS");
        serverBody.Children.Add(_tls);
        stack.Children.Add(server);

        Border identity = SectionCard("Telnet identity", "The default identifies as a normal xterm-compatible terminal, not as NexMUD.");
        ((StackPanel)identity.Child!).Children.Add(FormRow("Terminal type", _terminalType));
        stack.Children.Add(identity);
        return PageScroll(stack);
    }

    private Control BuildJevPage()
    {
        StackPanel stack = PageStack(
            "Jev",
            "Jev is a System One decision model, not a chat LLM. NexMUD sends explicit state plus typed questions and consumes calibrated structured answers.");

        Border master = SectionCard("Master control", "Temporarily stop all Jev decisions and Jev-sourced execution without changing the authority matrix below.");
        StackPanel masterBody = (StackPanel)master.Child!;
        ConfigureCheckBox(_jevEnabled, "Enable Jev");
        masterBody.Children.Add(_jevEnabled);
        masterBody.Children.Add(new TextBlock
        {
            Text = "Turning Jev off is an operational kill switch. Turning it back on restores the configured per-domain authority unchanged.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(master);

        Border credentials = SectionCard("TypeSafe credentials", "On macOS, the API key is stored in your login Keychain and the field is masked.");
        StackPanel credentialsBody = (StackPanel)credentials.Child!;
        _apiKey.PasswordChar = '●';
        _apiKey.RevealPassword = false;
        _apiKey.PlaceholderText = "TypeSafe API key";
        credentialsBody.Children.Add(FormRow("API key", _apiKey));
        credentialsBody.Children.Add(FormRow("Model", _jevModel));
        _secretStatus.Foreground = Muted;
        _secretStatus.TextWrapping = TextWrapping.Wrap;
        credentialsBody.Children.Add(_secretStatus);
        stack.Children.Add(credentials);

        Border primitives = SectionCard("Decision primitives", "Questions in one request are evaluated against the same state. Jev returns typed values rather than generated prose.");
        StackPanel primitivesBody = (StackPanel)primitives.Child!;
        primitivesBody.Children.Add(KeyValueDescription("Choice", "Select one closed option with a probability distribution and confidence."));
        primitivesBody.Children.Add(KeyValueDescription("Score", "Place state on an ordered rubric; the result may be fractional and includes a distribution."));
        primitivesBody.Children.Add(KeyValueDescription("Noul", "Return P(true) for a yes/no judgment so NexMUD owns the threshold."));
        stack.Children.Add(primitives);

        Border authority = SectionCard("Authority", "Presets are shortcuts. Individual domains remain the source of truth; authority controls what happens after a typed decision returns.");
        StackPanel authorityBody = (StackPanel)authority.Child!;
        _preset.ItemsSource = Enum.GetValues<JevPreset>();
        _preset.MinWidth = 180;
        _preset.SelectionChanged += (_, _) => PresetChanged();
        authorityBody.Children.Add(FormRow("Preset", _preset));

        Grid matrix = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            RowSpacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        JevDomain[] domains = Enum.GetValues<JevDomain>();
        int matrixRows = (domains.Length + 1) / 2;
        for (int rowIndex = 0; rowIndex < matrixRows; rowIndex++)
        {
            matrix.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        int index = 0;
        foreach (JevDomain domain in domains)
        {
            ComboBox selector = new()
            {
                ItemsSource = Enum.GetValues<JevAuthority>(),
                MinWidth = 145
            };
            selector.SelectionChanged += (_, _) => DomainChanged();
            _authority[domain] = selector;
            Control row = CompactFormRow(domain.ToString(), selector);
            Grid.SetColumn(row, index % 2);
            Grid.SetRow(row, index / 2);
            matrix.Children.Add(row);
            index++;
        }
        authorityBody.Children.Add(matrix);
        authorityBody.Children.Add(new TextBlock
        {
            Text = "Combat, Navigation, and Recovery have executable Jev workflows. Auto mode is supervised: only one command may be in flight, command outcomes must settle before another decision, Recovery owns posture/resource transitions, and Navigation avoids immediate/recent-room loops when alternatives exist. Recent speakers are excluded from blind grind targeting. Other domains are observation-only until they have deterministic action projectors.",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        stack.Children.Add(authority);
        return PageScroll(stack);
    }

    private void LoadSettings()
    {
        _host.Text = _runtime.Settings.Host;
        _port.Text = _runtime.Settings.Port.ToString();
        _tls.IsChecked = _runtime.Settings.UseTls;
        _terminalType.Text = _runtime.Settings.TerminalType;
        _fontSize.SelectedItem = NearestFontSize(_runtime.Settings.TranscriptFontSize);
        _showContextDock.IsChecked = true;
        _autoCombatContext.IsChecked = false;
        _autoLogSessions.IsChecked = _runtime.Settings.AutoLogSessions;
        _slurpTelemetryPrompt.IsChecked = _runtime.Settings.SlurpTelemetryPrompt;
        _commandSeparator.Text = _runtime.Settings.CommandSeparator;
        InputPreferences inputPreferences = _runtime.Settings.Input ?? new InputPreferences();
        _historyMaximum.Text = inputPreferences.HistoryMaximumEntries.ToString();
        _persistHistory.IsChecked = inputPreferences.PersistHistory;
        _deduplicateHistory.IsChecked = inputPreferences.DeduplicateConsecutiveHistory;
        _completionEnabled.IsChecked = inputPreferences.CompletionEnabled;
        _completionTokenLimit.Text = inputPreferences.CompletionTokenLimit.ToString();
        _localEcho.IsChecked = inputPreferences.LocalEcho;
        OutputPreferences outputPreferences = _runtime.Settings.Output ?? new OutputPreferences();
        _timestampMode.SelectedItem = outputPreferences.TimestampMode;
        _scrollbackMaximum.Text = outputPreferences.ScrollbackMaximumEntries.ToString();
        _splitOutputEnabled.IsChecked = outputPreferences.SplitOutputEnabled;
        _notifyWhenUnfocused.IsChecked = outputPreferences.NotifyWhenUnfocused;
        _logFormat.SelectedItem = _runtime.Settings.LogFormat;
        _highlightRows.Children.Clear();
        _highlightEditors.Clear();
        foreach (TranscriptHighlightRule rule in _runtime.Settings.HighlightRules ?? Array.Empty<TranscriptHighlightRule>())
        {
            AddHighlightEditor(rule);
        }
        _outputRuleRows.Children.Clear();
        _outputRuleEditors.Clear();
        foreach (OutputTransformationRule rule in _runtime.Settings.OutputRules ?? Array.Empty<OutputTransformationRule>())
        {
            AddOutputRuleEditor(rule);
        }
        _aliasRows.Children.Clear();
        _aliasEditors.Clear();
        foreach (CommandAlias alias in _runtime.Settings.Aliases ?? Array.Empty<CommandAlias>())
        {
            AddAliasEditor(alias);
        }
        _triggerRows.Children.Clear();
        _triggerEditors.Clear();
        foreach (TriggerRule trigger in _runtime.Settings.Triggers ?? Array.Empty<TriggerRule>())
        {
            AddTriggerEditor(trigger);
        }
        _gameRuleRows.Children.Clear();
        _gameRuleEditors.Clear();
        foreach (GameRule rule in _runtime.Settings.GameRules ?? Array.Empty<GameRule>())
        {
            AddGameRuleEditor(rule);
        }
        _workflowRows.Children.Clear();
        _workflowEditors.Clear();
        foreach (AutomationWorkflow workflow in _runtime.Settings.Workflows ?? Array.Empty<AutomationWorkflow>())
        {
            AddWorkflowEditor(workflow);
        }
        _timerRows.Children.Clear();
        _timerEditors.Clear();
        foreach (CommandTimer timer in _runtime.Settings.Timers ?? Array.Empty<CommandTimer>())
        {
            AddTimerEditor(timer);
        }
        _keyBindingRows.Children.Clear();
        _keyBindingEditors.Clear();
        foreach (CommandKeyBinding binding in _runtime.Settings.KeyBindings ?? Array.Empty<CommandKeyBinding>())
        {
            AddKeyBindingEditor(binding);
        }
        AutomationPreferences automation = _runtime.Settings.Automation ?? new AutomationPreferences();
        _automationEnabled.IsChecked = automation.Enabled;
        _automationRate.Text = automation.MaxCommandsPerSecond.ToString();
        _rollingBufferCharacters.Text = automation.RollingBufferCharacters.ToString();
        _automationHumanOverride.Text = automation.HumanOverrideMilliseconds.ToString();
        _automationMaxWorkflows.Text = automation.MaxConcurrentWorkflows.ToString();
        _automationPersistVariables.IsChecked = automation.PersistVariables;
        _disabledAutomationGroups.Text = string.Join(", ", automation.DisabledGroups ?? Array.Empty<string>());

        ProtocolPreferences protocols = _runtime.Settings.Protocols ?? new ProtocolPreferences();
        _protocolNaws.IsChecked = protocols.Naws;
        _protocolGmcp.IsChecked = protocols.Gmcp;
        _protocolMsdp.IsChecked = protocols.Msdp;
        _protocolMssp.IsChecked = protocols.Mssp;
        _protocolMccp2.IsChecked = protocols.Mccp2;
        _protocolCharset.IsChecked = protocols.Charset;
        _protocolNewEnvironment.IsChecked = protocols.NewEnvironment;
        _protocolMtts.IsChecked = protocols.Mtts;
        _protocolEor.IsChecked = protocols.Eor;

        MapperPreferences mapper = _runtime.Settings.Mapper ?? new MapperPreferences();
        _mapperEnabled.IsChecked = mapper.Enabled;
        _mapperAutoMap.IsChecked = mapper.AutoMap;
        _mapperPersistKnowledge.IsChecked = mapper.PersistKnowledge;
        _mapperAvoidBlocked.IsChecked = mapper.AvoidBlockedExits;
        _mapperAllowUnknown.IsChecked = mapper.AllowUnknownTraversability;
        _mapperMaximumDepth.Text = mapper.MaximumRouteDepth.ToString();
        _mapperAutoMoveEnabled.IsChecked = mapper.AutoMoveEnabled;
        _mapperStopOnCombat.IsChecked = mapper.StopAutoMoveOnCombat;
        _mapperResumeAfterCombat.IsChecked = mapper.ResumeAutoMoveAfterCombat;
        _mapperReplanOnDeviation.IsChecked = mapper.ReplanAutoMoveOnDeviation;
        _mapperStepDelay.Text = mapper.AutoMoveStepDelayMilliseconds.ToString();
        _mapperStepTimeout.Text = mapper.AutoMoveStepTimeoutMilliseconds.ToString();
        _mapperMaximumReplans.Text = mapper.AutoMoveMaximumReplans.ToString();
        _mapperVisualDepth.Text = mapper.VisualMapDepth.ToString();
        _mapperVisualRooms.Text = mapper.VisualMapMaximumRooms.ToString();
        _mapperAvoidAreas.Text = string.Join(", ", mapper.AvoidAreas ?? Array.Empty<string>());
        _mapperAvoidTerrains.Text = string.Join(", ", mapper.AvoidTerrains ?? Array.Empty<string>());
        _mapperAvoidMobs.Text = string.Join(", ", mapper.AvoidMobNames ?? Array.Empty<string>());
        _mapperAvoidClosedDoors.IsChecked = mapper.AvoidClosedDoors;
        _mapperPreferKnownTraversable.IsChecked = mapper.PreferKnownTraversableExits;
        _mapperAutoOpenDoors.IsChecked = mapper.AutoOpenDoors;
        _mapperDoorOpenTemplate.Text = mapper.DoorOpenCommandTemplate;

        _jevModel.Text = _runtime.Settings.JevModel;
        _jevEnabled.IsChecked = _runtime.Authority.Enabled;
        ApplyAuthority(_runtime.Authority.Current);
    }

    private async Task LoadSecretAsync()
    {
        try
        {
            string? stored = await _runtime.GetStoredJevApiKeyAsync(_runtime.CancellationToken).ConfigureAwait(true);
            _apiKey.Text = stored ?? string.Empty;
            if (_runtime.EnvironmentJevApiKeyConfigured)
            {
                _secretStatus.Text = "TYPESAFE_API_KEY is currently overriding the stored Keychain value for this process.";
                _secretStatus.Foreground = Accent;
            }
            else if (!string.IsNullOrWhiteSpace(stored))
            {
                _secretStatus.Text = "API key is stored in macOS Keychain.";
                _secretStatus.Foreground = Success;
            }
            else if (_runtime.SecretStore.IsAvailable)
            {
                _secretStatus.Text = "No API key is stored yet.";
                _secretStatus.Foreground = Muted;
            }
            else
            {
                _secretStatus.Text = "Secure key storage is unavailable on this platform. Use TYPESAFE_API_KEY instead.";
                _secretStatus.Foreground = Muted;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _secretStatus.Text = $"Could not read secure credentials: {exception.Message}";
            _secretStatus.Foreground = Error;
        }
    }

    private async Task SaveAsync()
    {
        _validation.Text = string.Empty;
        string host = (_host.Text ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            _validation.Text = "Host is required.";
            SelectPage(SettingsPage.Connection);
            return;
        }
        if (!int.TryParse(_port.Text, out int port) || port is < 1 or > 65535)
        {
            _validation.Text = "Port must be between 1 and 65535.";
            SelectPage(SettingsPage.Connection);
            return;
        }
        string terminalType = (_terminalType.Text ?? string.Empty).Trim();
        if (terminalType.Length == 0)
        {
            _validation.Text = "Terminal type is required.";
            SelectPage(SettingsPage.Connection);
            return;
        }
        string commandSeparator = _commandSeparator.Text ?? string.Empty;
        if (commandSeparator.Length > 1 || commandSeparator.Any(char.IsWhiteSpace))
        {
            _validation.Text = "Command separator must be blank or one non-whitespace character.";
            SelectPage(SettingsPage.General);
            return;
        }
        if (_fontSize.SelectedItem is not double fontSize)
        {
            _validation.Text = "Choose a transcript text size.";
            SelectPage(SettingsPage.General);
            return;
        }
        IReadOnlyList<TranscriptHighlightRule>? highlightRules = ReadHighlightRules();
        IReadOnlyList<OutputTransformationRule>? outputRules = ReadOutputRules();
        if (highlightRules is null || outputRules is null)
        {
            SelectPage(SettingsPage.Highlights);
            return;
        }
        if (!int.TryParse(_historyMaximum.Text, out int historyMaximum) || historyMaximum is < 1 or > 10000)
        {
            _validation.Text = "History entries must be between 1 and 10,000.";
            SelectPage(SettingsPage.General);
            return;
        }
        if (!int.TryParse(_completionTokenLimit.Text, out int completionTokenLimit) || completionTokenLimit is < 100 or > 50000)
        {
            _validation.Text = "Completion token index must be between 100 and 50,000 entries.";
            SelectPage(SettingsPage.General);
            return;
        }
        if (!int.TryParse(_scrollbackMaximum.Text, out int scrollbackMaximum) || scrollbackMaximum is < 250 or > 100000)
        {
            _validation.Text = "Scrollback entries must be between 250 and 100,000.";
            SelectPage(SettingsPage.General);
            return;
        }
        InputPreferences inputPreferences = new(
            historyMaximum,
            _persistHistory.IsChecked == true,
            _deduplicateHistory.IsChecked == true,
            _completionEnabled.IsChecked == true,
            completionTokenLimit,
            _localEcho.IsChecked == true);
        OutputPreferences outputPreferences = new(
            _timestampMode.SelectedItem is TimestampRenderMode timestampMode ? timestampMode : TimestampRenderMode.Off,
            scrollbackMaximum,
            _splitOutputEnabled.IsChecked == true,
            _notifyWhenUnfocused.IsChecked == true);
        IReadOnlyList<CommandKeyBinding>? keyBindings = ReadKeyBindings();
        if (keyBindings is null)
        {
            SelectPage(SettingsPage.Keybindings);
            return;
        }
        IReadOnlyList<CommandAlias>? aliases = ReadAliases();
        IReadOnlyList<TriggerRule>? triggers = ReadTriggers();
        IReadOnlyList<GameRule>? gameRules = ReadGameRules();
        IReadOnlyList<AutomationWorkflow>? workflows = ReadWorkflows();
        IReadOnlyList<CommandTimer>? timers = ReadTimers();
        if (aliases is null || triggers is null || gameRules is null || workflows is null || timers is null)
        {
            SelectPage(SettingsPage.Automation);
            return;
        }
        if (!int.TryParse(_automationRate.Text, out int automationRate) || automationRate is < 1 or > 50)
        {
            _validation.Text = "Automation command rate must be between 1 and 50 commands per second.";
            SelectPage(SettingsPage.Automation);
            return;
        }
        if (!int.TryParse(_rollingBufferCharacters.Text, out int rollingCharacters) || rollingCharacters is < 1024 or > 65536)
        {
            _validation.Text = "Rolling trigger buffer must be between 1,024 and 65,536 characters.";
            SelectPage(SettingsPage.Automation);
            return;
        }
        if (!int.TryParse(_automationHumanOverride.Text, out int humanOverride) || humanOverride is < 0 or > 30000)
        {
            _validation.Text = "Human override window must be between 0 and 30,000 ms.";
            SelectPage(SettingsPage.Automation);
            return;
        }
        if (!int.TryParse(_automationMaxWorkflows.Text, out int maxWorkflows) || maxWorkflows is < 1 or > 16)
        {
            _validation.Text = "Maximum concurrent workflows must be between 1 and 16.";
            SelectPage(SettingsPage.Automation);
            return;
        }
        if (!int.TryParse(_mapperMaximumDepth.Text, out int routeDepth) || routeDepth is < 1 or > 5000)
        {
            _validation.Text = "Mapper maximum route depth must be between 1 and 5,000.";
            SelectPage(SettingsPage.Mapper);
            return;
        }
        if (!int.TryParse(_mapperStepDelay.Text, out int stepDelay) || stepDelay is < 0 or > 5000)
        {
            _validation.Text = "Mapper step delay must be between 0 and 5,000 ms.";
            SelectPage(SettingsPage.Mapper);
            return;
        }
        if (!int.TryParse(_mapperStepTimeout.Text, out int stepTimeout) || stepTimeout is < 1000 or > 30000)
        {
            _validation.Text = "Mapper arrival timeout must be between 1,000 and 30,000 ms.";
            SelectPage(SettingsPage.Mapper);
            return;
        }
        if (!int.TryParse(_mapperMaximumReplans.Text, out int maximumReplans) || maximumReplans is < 0 or > 25)
        {
            _validation.Text = "Mapper maximum automatic re-plans must be between 0 and 25.";
            SelectPage(SettingsPage.Mapper);
            return;
        }
        if (!int.TryParse(_mapperVisualDepth.Text, out int visualDepth) || visualDepth is < 1 or > 50)
        {
            _validation.Text = "Mapper visual graph depth must be between 1 and 50.";
            SelectPage(SettingsPage.Mapper);
            return;
        }
        if (!int.TryParse(_mapperVisualRooms.Text, out int visualRooms) || visualRooms is < 10 or > 250)
        {
            _validation.Text = "Mapper visible room limit must be between 10 and 250.";
            SelectPage(SettingsPage.Mapper);
            return;
        }
        AutomationPreferences automationPreferences = new(
            _automationEnabled.IsChecked == true,
            automationRate,
            rollingCharacters,
            (_disabledAutomationGroups.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            humanOverride,
            maxWorkflows,
            _automationPersistVariables.IsChecked == true);
        ProtocolPreferences protocolPreferences = new(
            Naws: _protocolNaws.IsChecked == true,
            Gmcp: _protocolGmcp.IsChecked == true,
            Msdp: _protocolMsdp.IsChecked == true,
            Mssp: _protocolMssp.IsChecked == true,
            Mccp2: _protocolMccp2.IsChecked == true,
            Charset: _protocolCharset.IsChecked == true,
            NewEnvironment: _protocolNewEnvironment.IsChecked == true,
            Mtts: _protocolMtts.IsChecked == true,
            Eor: _protocolEor.IsChecked == true);
        MapperPreferences mapperPreferences = new(
            _mapperEnabled.IsChecked == true,
            _mapperAutoMap.IsChecked == true,
            _mapperPersistKnowledge.IsChecked == true,
            routeDepth,
            _mapperAvoidBlocked.IsChecked == true,
            _mapperAllowUnknown.IsChecked == true,
            _mapperAutoMoveEnabled.IsChecked == true,
            _mapperStopOnCombat.IsChecked == true,
            _mapperResumeAfterCombat.IsChecked == true,
            _mapperReplanOnDeviation.IsChecked == true,
            stepDelay,
            stepTimeout,
            maximumReplans,
            visualDepth,
            visualRooms,
            SplitCsv(_mapperAvoidAreas.Text),
            SplitCsv(_mapperAvoidTerrains.Text),
            SplitCsv(_mapperAvoidMobs.Text),
            _mapperAvoidClosedDoors.IsChecked == true,
            _mapperPreferKnownTraversable.IsChecked == true,
            _mapperAutoOpenDoors.IsChecked == true,
            string.IsNullOrWhiteSpace(_mapperDoorOpenTemplate.Text) ? "open {direction}" : (_mapperDoorOpenTemplate.Text ?? string.Empty).Trim());

        TranscriptLogFormat logFormat = _logFormat.SelectedItem is TranscriptLogFormat selectedLogFormat
            ? selectedLogFormat
            : TranscriptLogFormat.AnsiText;

        string model = (_jevModel.Text ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            _validation.Text = "Jev model is required.";
            SelectPage(SettingsPage.Jev);
            return;
        }

        Dictionary<JevDomain, JevAuthority> domains = [];
        foreach (JevDomain domain in Enum.GetValues<JevDomain>())
        {
            domains[domain] = _authority[domain].SelectedItem is JevAuthority value
                ? value
                : JevAuthority.Off;
        }
        JevPreset preset = _preset.SelectedItem is JevPreset selected ? selected : JevPreset.Custom;
        JevAuthoritySnapshot authoritySnapshot = JevAuthoritySnapshot.Create(preset, domains);

        try
        {
            await _runtime.SaveJevConfigurationAsync(_apiKey.Text, model, _runtime.CancellationToken)
                .ConfigureAwait(true);
            await _runtime.SaveClientPreferencesAsync(
                host,
                port,
                _tls.IsChecked == true,
                terminalType,
                fontSize,
                true,
                false,
                _autoLogSessions.IsChecked == true,
                _slurpTelemetryPrompt.IsChecked == true,
                commandSeparator,
                highlightRules,
                _runtime.CancellationToken).ConfigureAwait(true);
            await _runtime.SaveInteractionSettingsAsync(
                inputPreferences,
                outputPreferences,
                outputRules,
                _runtime.CancellationToken).ConfigureAwait(true);
            await _runtime.SaveConvenienceSettingsAsync(
                logFormat,
                aliases,
                triggers,
                gameRules,
                workflows,
                timers,
                keyBindings,
                _runtime.CancellationToken).ConfigureAwait(true);
            await _runtime.SaveSubsystemSettingsAsync(
                automationPreferences,
                protocolPreferences,
                mapperPreferences,
                _runtime.CancellationToken).ConfigureAwait(true);
            await _runtime.ApplyAndSaveAuthorityAsync(authoritySnapshot, _runtime.CancellationToken)
                .ConfigureAwait(true);
            await _runtime.SetJevEnabledAsync(_jevEnabled.IsChecked == true, _runtime.CancellationToken)
                .ConfigureAwait(true);
            Saved = true;
            _saved();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _validation.Text = exception.Message;
        }
    }

    private static string[] SplitCsv(string? text) =>
        (text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void AddAliasEditor(CommandAlias alias)
    {
        TextBox name = Field();
        name.Text = alias.Name;
        name.PlaceholderText = "Alias, e.g. kk";
        TextBox expansion = Field();
        expansion.Text = alias.Expansion;
        expansion.PlaceholderText = "Expansion, e.g. kill $*";
        CheckBox enabled = new() { Content = "Enabled", IsChecked = alias.Enabled, Foreground = TextForeground };

        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("150,*,Auto,Auto"), ColumnSpacing = 8 };
        row.Children.Add(name);
        Grid.SetColumn(expansion, 1);
        row.Children.Add(expansion);
        Grid.SetColumn(enabled, 2);
        row.Children.Add(enabled);
        Button remove = SecondaryButton("Remove");
        Grid.SetColumn(remove, 3);
        row.Children.Add(remove);

        Border container = EditorContainer(row);
        AliasEditor editor = new() { Name = name, Expansion = expansion, Enabled = enabled };
        remove.Click += (_, _) => { _aliasRows.Children.Remove(container); _aliasEditors.Remove(editor); };
        _aliasEditors.Add(editor);
        _aliasRows.Children.Add(container);
    }

    private void AddTriggerEditor(TriggerRule rule)
    {
        TextBox pattern = Field(); pattern.Text = rule.Pattern; pattern.PlaceholderText = "Text or regex";
        TextBox command = Field(); command.Text = rule.Command; command.PlaceholderText = "Command to send";
        ComboBox mode = new() { ItemsSource = Enum.GetValues<HighlightMatchMode>(), SelectedItem = rule.MatchMode, MinWidth = 90 };
        ComboBox scope = new() { ItemsSource = Enum.GetValues<TriggerScope>(), SelectedItem = rule.Scope, MinWidth = 105 };
        TextBox group = Field(); group.Text = rule.Group; group.PlaceholderText = "Group"; group.Width = 120;
        TextBox priority = Field(); priority.Text = rule.Priority.ToString(); priority.PlaceholderText = "Priority"; priority.Width = 70;
        TextBox cooldown = Field(); cooldown.Text = rule.CooldownMilliseconds.ToString(); cooldown.PlaceholderText = "Cooldown ms"; cooldown.Width = 95;
        CheckBox enabled = new() { Content = "Enabled", IsChecked = rule.Enabled, Foreground = TextForeground };
        CheckBox caseSensitive = new() { Content = "Case sensitive", IsChecked = rule.CaseSensitive, Foreground = TextForeground };
        CheckBox stopProcessing = new() { Content = "Stop after match", IsChecked = rule.StopProcessing, Foreground = TextForeground };
        CheckBox oneShot = new() { Content = "One shot", IsChecked = rule.OneShot, Foreground = TextForeground };

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("*,*,Auto,Auto"), ColumnSpacing = 8 };
        top.Children.Add(pattern);
        Grid.SetColumn(command, 1); top.Children.Add(command);
        Grid.SetColumn(mode, 2); top.Children.Add(mode);
        Button remove = SecondaryButton("Remove"); Grid.SetColumn(remove, 3); top.Children.Add(remove);

        StackPanel detail = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        detail.Children.Add(group); detail.Children.Add(scope); detail.Children.Add(priority); detail.Children.Add(cooldown);
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        toggles.Children.Add(enabled); toggles.Children.Add(caseSensitive); toggles.Children.Add(stopProcessing); toggles.Children.Add(oneShot);
        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(top); content.Children.Add(detail); content.Children.Add(toggles);
        Border container = EditorContainer(content);
        TriggerEditor editor = new()
        {
            Pattern = pattern, Command = command, MatchMode = mode, Scope = scope, Group = group,
            Priority = priority, Cooldown = cooldown, StopProcessing = stopProcessing,
            Enabled = enabled, CaseSensitive = caseSensitive, OneShot = oneShot
        };
        remove.Click += (_, _) => { _triggerRows.Children.Remove(container); _triggerEditors.Remove(editor); };
        _triggerEditors.Add(editor); _triggerRows.Children.Add(container);
    }

    private void AddGameRuleEditor(GameRule rule)
    {
        TextBox name = Field(); name.Text = rule.Name; name.PlaceholderText = "Rule name"; name.Width = 145;
        TextBox condition = Field(); condition.Text = rule.Condition; condition.PlaceholderText = "combat.active && hp.percent < 30";
        TextBox command = Field(); command.Text = rule.Command; command.PlaceholderText = "Command to send";
        TextBox group = Field(); group.Text = rule.Group; group.PlaceholderText = "Group"; group.Width = 115;
        TextBox priority = Field(); priority.Text = rule.Priority.ToString(); priority.PlaceholderText = "Priority"; priority.Width = 70;
        TextBox cooldown = Field(); cooldown.Text = rule.CooldownMilliseconds.ToString(); cooldown.PlaceholderText = "Cooldown ms"; cooldown.Width = 95;
        CheckBox enabled = new() { Content = "Enabled", IsChecked = rule.Enabled, Foreground = TextForeground };
        CheckBox stop = new() { Content = "Stop after match", IsChecked = rule.StopProcessing, Foreground = TextForeground };
        ComboBox activation = new() { ItemsSource = Enum.GetValues<GameRuleActivation>(), SelectedItem = rule.Activation, MinWidth = 95 };
        CheckBox oneShot = new() { Content = "One shot", IsChecked = rule.OneShot, Foreground = TextForeground };

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,*,Auto"), ColumnSpacing = 8 };
        top.Children.Add(name); Grid.SetColumn(condition, 1); top.Children.Add(condition); Grid.SetColumn(command, 2); top.Children.Add(command);
        Button remove = SecondaryButton("Remove"); Grid.SetColumn(remove, 3); top.Children.Add(remove);
        StackPanel details = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        details.Children.Add(group); details.Children.Add(priority); details.Children.Add(cooldown); details.Children.Add(activation); details.Children.Add(enabled); details.Children.Add(stop); details.Children.Add(oneShot);
        StackPanel content = new() { Spacing = 8 }; content.Children.Add(top); content.Children.Add(details);
        Border container = EditorContainer(content);
        GameRuleEditor editor = new()
        {
            Name = name, Condition = condition, Command = command, Group = group, Priority = priority,
            Cooldown = cooldown, Enabled = enabled, StopProcessing = stop, Activation = activation, OneShot = oneShot
        };
        remove.Click += (_, _) => { _gameRuleRows.Children.Remove(container); _gameRuleEditors.Remove(editor); };
        _gameRuleEditors.Add(editor); _gameRuleRows.Children.Add(container);
    }

    private void AddWorkflowEditor(AutomationWorkflow workflow)
    {
        TextBox name = Field();
        name.Text = workflow.Name;
        name.PlaceholderText = "Automation name";

        TextBox condition = Field();
        condition.Text = workflow.TriggerCondition ?? string.Empty;
        condition.PlaceholderText = "Example: !combat.active && hp.percent > 50";

        TextBox triggerEvent = Field();
        triggerEvent.Text = workflow.TriggerEvent ?? string.Empty;
        triggerEvent.PlaceholderText = "Example: EnemyKilled (optional)";

        TextBox group = Field();
        group.Text = workflow.Group;
        group.PlaceholderText = "Default";

        TextBox priority = Field();
        priority.Text = workflow.Priority.ToString();
        priority.Width = 90;

        TextBox cooldown = Field();
        cooldown.Text = workflow.CooldownMilliseconds.ToString();
        cooldown.Width = 110;

        ComboBox failureMode = new()
        {
            ItemsSource = Enum.GetValues<AutomationWorkflowFailureMode>(),
            SelectedItem = workflow.FailureMode,
            MinWidth = 120
        };
        CheckBox enabled = new() { Content = "Enabled", IsChecked = workflow.Enabled, Foreground = TextForeground };
        CheckBox oneShot = new() { Content = "Run only once per session", IsChecked = workflow.OneShot, Foreground = TextForeground };

        TextBox steps = new()
        {
            Text = workflow.Steps,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 130,
            PlaceholderText = "send look\nwait !combat.active timeout=30000\nnavigate trainer",
            FontFamily = new FontFamily("Menlo, monospace"),
            FontSize = NexTypography.Monospace,
            Background = RaisedBackground,
            Foreground = TextForeground,
            BorderBrush = BorderColor,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8)
        };

        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        heading.Children.Add(name);
        Grid.SetColumn(enabled, 1);
        heading.Children.Add(enabled);
        Button remove = SecondaryButton("Remove");
        Grid.SetColumn(remove, 2);
        heading.Children.Add(remove);

        StackPanel start = new() { Spacing = 6 };
        start.Children.Add(new TextBlock
        {
            Text = "WHEN SHOULD THIS START?",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold
        });
        start.Children.Add(FormRow("Game event", triggerEvent));
        start.Children.Add(FormRow("Only while", condition));

        StackPanel actions = new() { Spacing = 6 };
        actions.Children.Add(new TextBlock
        {
            Text = "WHAT SHOULD JEV DO?",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold
        });
        actions.Children.Add(steps);

        StackPanel advanced = new() { Spacing = 6 };
        advanced.Children.Add(new TextBlock
        {
            Text = "ADVANCED BEHAVIOR",
            Foreground = Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold
        });
        advanced.Children.Add(FormRow("Group", group));
        advanced.Children.Add(FormRow("Priority", priority));
        advanced.Children.Add(FormRow("Cooldown (ms)", cooldown));
        advanced.Children.Add(FormRow("If an action fails", failureMode));
        advanced.Children.Add(oneShot);

        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(heading);
        content.Children.Add(start);
        content.Children.Add(actions);
        content.Children.Add(advanced);
        Border container = EditorContainer(content);
        WorkflowEditor editor = new()
        {
            Name = name, TriggerCondition = condition, TriggerEvent = triggerEvent, Steps = steps, Group = group,
            Priority = priority, Cooldown = cooldown, FailureMode = failureMode, Enabled = enabled, OneShot = oneShot
        };
        remove.Click += (_, _) => { _workflowRows.Children.Remove(container); _workflowEditors.Remove(editor); };
        _workflowEditors.Add(editor);
        _workflowRows.Children.Add(container);
    }

    private void AddTimerEditor(CommandTimer timer)
    {
        TextBox name = Field(); name.Text = timer.Name; name.PlaceholderText = "Name";
        TextBox interval = Field(); interval.Text = timer.IntervalSeconds.ToString(); interval.PlaceholderText = "Seconds"; interval.Width = 95;
        TextBox command = Field(); command.Text = timer.Command; command.PlaceholderText = "Command to send";
        TextBox group = Field(); group.Text = timer.Group; group.PlaceholderText = "Group"; group.Width = 120;
        CheckBox enabled = new() { Content = "Enabled", IsChecked = timer.Enabled, Foreground = TextForeground };
        CheckBox repeat = new() { Content = "Repeat", IsChecked = timer.Repeat, Foreground = TextForeground };

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("140,Auto,*,Auto"), ColumnSpacing = 8 };
        top.Children.Add(name); Grid.SetColumn(interval, 1); top.Children.Add(interval); Grid.SetColumn(command, 2); top.Children.Add(command);
        Button remove = SecondaryButton("Remove"); Grid.SetColumn(remove, 3); top.Children.Add(remove);
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        toggles.Children.Add(enabled); toggles.Children.Add(repeat); toggles.Children.Add(group);
        StackPanel content = new() { Spacing = 8 }; content.Children.Add(top); content.Children.Add(toggles);
        Border container = EditorContainer(content);
        TimerEditor editor = new() { Name = name, Interval = interval, Command = command, Group = group, Enabled = enabled, Repeat = repeat };
        remove.Click += (_, _) => { _timerRows.Children.Remove(container); _timerEditors.Remove(editor); };
        _timerEditors.Add(editor); _timerRows.Children.Add(container);
    }

    private void AddKeyBindingEditor(CommandKeyBinding binding)
    {
        TextBox name = Field();
        name.Text = binding.Name ?? string.Empty;
        name.PlaceholderText = "Name";
        name.Width = 130;
        TextBox gesture = Field();
        gesture.Text = binding.Gesture;
        gesture.PlaceholderText = "Primary+1, Command+K, F5";
        gesture.Width = 165;
        ComboBox context = new()
        {
            ItemsSource = Enum.GetValues<KeybindingContext>(),
            SelectedItem = binding.Context,
            MinWidth = 95
        };
        ComboBox action = new()
        {
            ItemsSource = Enum.GetValues<KeybindingActionKind>(),
            SelectedItem = binding.Action,
            MinWidth = 135
        };
        TextBox command = Field();
        command.Text = binding.Command;
        command.PlaceholderText = "Command / pane / action value";
        TextBox priority = Field();
        priority.Text = binding.Priority.ToString();
        priority.Width = 70;
        priority.PlaceholderText = "Priority";
        CheckBox enabled = new() { Content = "Enabled", IsChecked = binding.Enabled, Foreground = TextForeground };

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        top.Children.Add(name);
        Grid.SetColumn(gesture, 1); top.Children.Add(gesture);
        Grid.SetColumn(context, 2); top.Children.Add(context);
        Grid.SetColumn(action, 3); top.Children.Add(action);
        Button remove = SecondaryButton("Remove"); Grid.SetColumn(remove, 4); top.Children.Add(remove);
        Grid detail = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        detail.Children.Add(command);
        Grid.SetColumn(priority, 1); detail.Children.Add(priority);
        Grid.SetColumn(enabled, 2); detail.Children.Add(enabled);

        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(top); content.Children.Add(detail);
        Border container = EditorContainer(content);
        KeyBindingEditor editor = new()
        {
            Name = name,
            Gesture = gesture,
            Command = command,
            Context = context,
            Action = action,
            Priority = priority,
            Enabled = enabled
        };
        remove.Click += (_, _) =>
        {
            _keyBindingRows.Children.Remove(container);
            _keyBindingEditors.Remove(editor);
        };
        _keyBindingEditors.Add(editor);
        _keyBindingRows.Children.Add(container);
    }

    private IReadOnlyList<CommandAlias>? ReadAliases()
    {
        List<CommandAlias> aliases = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (AliasEditor editor in _aliasEditors)
        {
            string name = (editor.Name.Text ?? string.Empty).Trim();
            string expansion = (editor.Expansion.Text ?? string.Empty).Trim();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace) || expansion.Length == 0)
            {
                _validation.Text = "Each alias needs a single-token name and an expansion.";
                return null;
            }
            if (!names.Add(name))
            {
                _validation.Text = $"Alias '{name}' is duplicated.";
                return null;
            }
            aliases.Add(new CommandAlias(name, expansion, editor.Enabled.IsChecked == true));
        }
        return aliases;
    }

    private IReadOnlyList<CommandKeyBinding>? ReadKeyBindings()
    {
        List<CommandKeyBinding> bindings = [];
        HashSet<string> conflictKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyBindingEditor editor in _keyBindingEditors)
        {
            string gesture = (editor.Gesture.Text ?? string.Empty).Trim();
            string value = (editor.Command.Text ?? string.Empty).Trim();
            string? name = string.IsNullOrWhiteSpace(editor.Name.Text) ? null : editor.Name.Text.Trim();
            KeybindingContext context = editor.Context.SelectedItem is KeybindingContext selectedContext
                ? selectedContext
                : KeybindingContext.Global;
            if (gesture.Length == 0 || !CommandGesture.IsValid(gesture, context))
            {
                _validation.Text = "Each key binding needs a safe gesture. Bare navigation keys are allowed outside Global; character keys require a modifier.";
                return null;
            }
            if (!int.TryParse(editor.Priority.Text, out int priority) || priority is < -10000 or > 10000)
            {
                _validation.Text = $"Key binding '{gesture}' has an invalid priority.";
                return null;
            }
            KeybindingActionKind action = editor.Action.SelectedItem is KeybindingActionKind selectedAction
                ? selectedAction
                : KeybindingActionKind.SendCommand;
            bool requiresValue = action is KeybindingActionKind.SendCommand
                or KeybindingActionKind.RunAutomation
                or KeybindingActionKind.TogglePane;
            if (requiresValue && value.Length == 0)
            {
                _validation.Text = $"Key binding '{gesture}' needs a value for {action}.";
                return null;
            }
            bool enabled = editor.Enabled.IsChecked == true;
            string conflictKey = $"{context}:{priority}:{gesture}";
            if (enabled && !conflictKeys.Add(conflictKey))
            {
                _validation.Text = $"Key binding conflict: {gesture} in {context} with priority {priority}.";
                return null;
            }
            bindings.Add(new CommandKeyBinding(gesture, value, enabled, name, context, action, priority));
        }
        return bindings;
    }

    private IReadOnlyList<TriggerRule>? ReadTriggers()
    {
        List<TriggerRule> triggers = [];
        foreach (TriggerEditor editor in _triggerEditors)
        {
            string pattern = (editor.Pattern.Text ?? string.Empty).Trim();
            string command = (editor.Command.Text ?? string.Empty).Trim();
            if (pattern.Length == 0 || command.Length == 0)
            {
                _validation.Text = "Each trigger needs a pattern and command.";
                return null;
            }
            HighlightMatchMode mode = editor.MatchMode.SelectedItem is HighlightMatchMode selected ? selected : HighlightMatchMode.Literal;
            if (mode == HighlightMatchMode.Regex)
            {
                try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (ArgumentException exception) { _validation.Text = $"Invalid trigger regex '{pattern}': {exception.Message}"; return null; }
            }
            string group = string.IsNullOrWhiteSpace(editor.Group.Text) ? "Default" : editor.Group.Text.Trim();
            if (!int.TryParse(editor.Priority.Text, out int priority) || priority is < -10000 or > 10000)
            {
                _validation.Text = $"Trigger priority for '{pattern}' must be between -10000 and 10000.";
                return null;
            }
            if (!int.TryParse(editor.Cooldown.Text, out int cooldown) || cooldown is < 0 or > 600000)
            {
                _validation.Text = $"Trigger cooldown for '{pattern}' must be between 0 and 600000 ms.";
                return null;
            }
            TriggerScope scope = editor.Scope.SelectedItem is TriggerScope selectedScope ? selectedScope : TriggerScope.Line;
            triggers.Add(new TriggerRule(
                pattern,
                command,
                mode,
                editor.CaseSensitive.IsChecked == true,
                editor.Enabled.IsChecked == true,
                group,
                priority,
                cooldown,
                editor.StopProcessing.IsChecked == true,
                scope,
                editor.OneShot.IsChecked == true));
        }
        return triggers;
    }

    private IReadOnlyList<GameRule>? ReadGameRules()
    {
        List<GameRule> rules = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameRuleEditor editor in _gameRuleEditors)
        {
            string name = (editor.Name.Text ?? string.Empty).Trim();
            string condition = (editor.Condition.Text ?? string.Empty).Trim();
            string command = (editor.Command.Text ?? string.Empty).Trim();
            string group = string.IsNullOrWhiteSpace(editor.Group.Text) ? "Default" : editor.Group.Text.Trim();
            if (name.Length == 0 || condition.Length == 0 || command.Length == 0)
            {
                _validation.Text = "Each game rule needs a name, condition, and command.";
                return null;
            }
            if (!names.Add(name))
            {
                _validation.Text = $"Game rule '{name}' is duplicated.";
                return null;
            }
            if (!int.TryParse(editor.Priority.Text, out int priority) || priority is < -10000 or > 10000 ||
                !int.TryParse(editor.Cooldown.Text, out int cooldown) || cooldown is < 0 or > 600000)
            {
                _validation.Text = $"Game rule '{name}' has an invalid priority or cooldown.";
                return null;
            }
            GameRuleActivation activation = editor.Activation.SelectedItem is GameRuleActivation selectedActivation
                ? selectedActivation
                : GameRuleActivation.OnEnter;
            rules.Add(new GameRule(
                name, condition, command, editor.Enabled.IsChecked == true, group, cooldown, priority,
                editor.StopProcessing.IsChecked == true, activation, editor.OneShot.IsChecked == true));
        }
        return rules;
    }

    private IReadOnlyList<AutomationWorkflow>? ReadWorkflows()
    {
        List<AutomationWorkflow> workflows = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkflowEditor editor in _workflowEditors)
        {
            string name = (editor.Name.Text ?? string.Empty).Trim();
            string steps = (editor.Steps.Text ?? string.Empty).Trim();
            if (name.Length == 0 || steps.Length == 0)
            {
                _validation.Text = "Each automation needs a name and at least one action.";
                return null;
            }
            if (!names.Add(name))
            {
                _validation.Text = $"Automation '{name}' is duplicated.";
                return null;
            }
            string? condition = string.IsNullOrWhiteSpace(editor.TriggerCondition.Text) ? null : editor.TriggerCondition.Text.Trim();
            string? triggerEvent = string.IsNullOrWhiteSpace(editor.TriggerEvent.Text) ? null : editor.TriggerEvent.Text.Trim();
            if (!int.TryParse(editor.Priority.Text, out int priority) || priority is < -10000 or > 10000 ||
                !int.TryParse(editor.Cooldown.Text, out int cooldown) || cooldown is < 0 or > 600000)
            {
                _validation.Text = $"Automation '{name}' has an invalid priority or cooldown.";
                return null;
            }
            AutomationWorkflowFailureMode failureMode = editor.FailureMode.SelectedItem is AutomationWorkflowFailureMode selectedMode
                ? selectedMode
                : AutomationWorkflowFailureMode.Stop;
            workflows.Add(new AutomationWorkflow(
                name, steps, condition, triggerEvent, editor.Enabled.IsChecked == true,
                string.IsNullOrWhiteSpace(editor.Group.Text) ? "Default" : editor.Group.Text.Trim(),
                priority, cooldown, failureMode, editor.OneShot.IsChecked == true));
        }
        return workflows;
    }

    private IReadOnlyList<CommandTimer>? ReadTimers()
    {
        List<CommandTimer> timers = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (TimerEditor editor in _timerEditors)
        {
            string name = (editor.Name.Text ?? string.Empty).Trim();
            string command = (editor.Command.Text ?? string.Empty).Trim();
            if (name.Length == 0 || command.Length == 0 || !int.TryParse(editor.Interval.Text, out int seconds) || seconds is < 1 or > 86400)
            {
                _validation.Text = "Each timer needs a unique name, a command, and an interval from 1 to 86400 seconds.";
                return null;
            }
            if (!names.Add(name))
            {
                _validation.Text = $"Timer '{name}' is duplicated.";
                return null;
            }
            string group = string.IsNullOrWhiteSpace(editor.Group.Text) ? "Default" : editor.Group.Text.Trim();
            timers.Add(new CommandTimer(name, seconds, command, editor.Repeat.IsChecked == true, editor.Enabled.IsChecked == true, group));
        }
        return timers;
    }

    private void AddOutputRuleEditor(OutputTransformationRule rule)
    {
        TextBox name = Field();
        name.Text = rule.Name;
        name.PlaceholderText = "Rule name";
        name.Width = 150;
        TextBox pattern = Field();
        pattern.Text = rule.Pattern;
        pattern.PlaceholderText = "Text or regex";
        ComboBox matchType = new()
        {
            ItemsSource = Enum.GetValues<OutputRuleMatchType>(),
            SelectedItem = rule.MatchType,
            MinWidth = 100
        };
        OutputRuleAction initialAction = rule.EffectiveActions.FirstOrDefault()
            ?? new OutputRuleAction(OutputRuleActionKind.Highlight, Foreground: "#FFD166", Bold: true);
        ComboBox action = new()
        {
            ItemsSource = Enum.GetValues<OutputRuleActionKind>(),
            SelectedItem = initialAction.Kind,
            MinWidth = 110
        };
        TextBox value = Field();
        value.Text = initialAction.Kind == OutputRuleActionKind.Highlight
            ? initialAction.Foreground ?? "#FFD166"
            : initialAction.Text ?? string.Empty;
        value.PlaceholderText = "Color / replacement / message";
        TextBox priority = Field();
        priority.Text = rule.Priority.ToString();
        priority.Width = 70;
        priority.PlaceholderText = "Priority";
        CheckBox enabled = new() { Content = "Enabled", IsChecked = rule.Enabled, Foreground = TextForeground };
        CheckBox caseSensitive = new() { Content = "Case sensitive", IsChecked = rule.CaseSensitive, Foreground = TextForeground };

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        top.Children.Add(name);
        Grid.SetColumn(pattern, 1); top.Children.Add(pattern);
        Grid.SetColumn(matchType, 2); top.Children.Add(matchType);
        Button remove = SecondaryButton("Remove"); Grid.SetColumn(remove, 3); top.Children.Add(remove);

        Grid detail = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        detail.Children.Add(action);
        Grid.SetColumn(value, 1); detail.Children.Add(value);
        Grid.SetColumn(priority, 2); detail.Children.Add(priority);
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        toggles.Children.Add(enabled); toggles.Children.Add(caseSensitive);

        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(top); content.Children.Add(detail); content.Children.Add(toggles);
        Border container = EditorContainer(content);
        OutputRuleEditor editor = new()
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id,
            Name = name,
            Pattern = pattern,
            MatchType = matchType,
            Action = action,
            Value = value,
            Priority = priority,
            CaseSensitive = caseSensitive,
            Enabled = enabled,
            PreservedActions = rule.EffectiveActions.Skip(1).ToArray()
        };
        remove.Click += (_, _) =>
        {
            _outputRuleRows.Children.Remove(container);
            _outputRuleEditors.Remove(editor);
        };
        _outputRuleEditors.Add(editor);
        _outputRuleRows.Children.Add(container);
    }

    private IReadOnlyList<OutputTransformationRule>? ReadOutputRules()
    {
        List<OutputTransformationRule> rules = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (OutputRuleEditor editor in _outputRuleEditors)
        {
            string name = (editor.Name.Text ?? string.Empty).Trim();
            string pattern = (editor.Pattern.Text ?? string.Empty).Trim();
            if (name.Length == 0 || pattern.Length == 0)
            {
                _validation.Text = "Each output rule needs a name and a pattern.";
                return null;
            }
            if (!int.TryParse(editor.Priority.Text, out int priority) || priority is < -10000 or > 10000)
            {
                _validation.Text = $"Output rule '{name}' has an invalid priority.";
                return null;
            }
            OutputRuleMatchType matchType = editor.MatchType.SelectedItem is OutputRuleMatchType selectedMatch
                ? selectedMatch
                : OutputRuleMatchType.Substring;
            if (matchType == OutputRuleMatchType.Regex)
            {
                if (pattern.Length > 4096)
                {
                    _validation.Text = $"Output rule '{name}' regex is too large.";
                    return null;
                }
                try
                {
                    RegexOptions options = RegexOptions.CultureInvariant;
                    if (editor.CaseSensitive.IsChecked != true) options |= RegexOptions.IgnoreCase;
                    _ = new Regex(pattern, options, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException exception)
                {
                    _validation.Text = $"Invalid output regex '{pattern}': {exception.Message}";
                    return null;
                }
            }

            OutputRuleActionKind actionKind = editor.Action.SelectedItem is OutputRuleActionKind selectedAction
                ? selectedAction
                : OutputRuleActionKind.Highlight;
            string value = (editor.Value.Text ?? string.Empty).Trim();
            OutputRuleAction action;
            if (actionKind == OutputRuleActionKind.Highlight)
            {
                if (!IsHexColor(value))
                {
                    _validation.Text = $"Output highlight color '{value}' must be #RRGGBB.";
                    return null;
                }
                action = new OutputRuleAction(actionKind, Foreground: value.ToUpperInvariant(), Bold: true);
            }
            else
            {
                action = new OutputRuleAction(actionKind, Text: value);
            }

            string id = ids.Add(editor.Id) ? editor.Id : Guid.NewGuid().ToString("N");
            OutputRuleAction[] actions = [action, .. editor.PreservedActions];
            rules.Add(new OutputTransformationRule(
                id,
                name,
                pattern,
                matchType,
                editor.CaseSensitive.IsChecked == true,
                priority,
                editor.Enabled.IsChecked == true,
                actions));
        }
        return rules;
    }

    private void AddHighlightEditor(TranscriptHighlightRule rule)
    {
        TextBox pattern = Field();
        pattern.Text = rule.Pattern;
        pattern.PlaceholderText = "Text or regex";

        TextBox foreground = Field();
        foreground.Text = rule.Foreground;
        foreground.PlaceholderText = "#FFD166";
        foreground.Width = 105;

        ComboBox mode = new()
        {
            ItemsSource = Enum.GetValues<HighlightMatchMode>(),
            SelectedItem = rule.MatchMode,
            MinWidth = 95
        };
        CheckBox enabled = new() { Content = "Enabled", IsChecked = rule.Enabled, Foreground = TextForeground };
        CheckBox bold = new() { Content = "Bold", IsChecked = rule.Bold, Foreground = TextForeground };
        CheckBox underline = new() { Content = "Underline", IsChecked = rule.Underline, Foreground = TextForeground };
        CheckBox caseSensitive = new() { Content = "Case sensitive", IsChecked = rule.CaseSensitive, Foreground = TextForeground };

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        top.Children.Add(pattern);
        Grid.SetColumn(mode, 1);
        top.Children.Add(mode);
        Grid.SetColumn(foreground, 2);
        top.Children.Add(foreground);
        Button remove = SecondaryButton("Remove");
        Grid.SetColumn(remove, 3);
        top.Children.Add(remove);

        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        toggles.Children.Add(enabled);
        toggles.Children.Add(bold);
        toggles.Children.Add(underline);
        toggles.Children.Add(caseSensitive);

        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(top);
        content.Children.Add(toggles);
        Border container = EditorContainer(content);

        HighlightEditor editor = new()
        {
            Pattern = pattern,
            Foreground = foreground,
            MatchMode = mode,
            Bold = bold,
            Underline = underline,
            CaseSensitive = caseSensitive,
            Enabled = enabled
        };
        remove.Click += (_, _) =>
        {
            _highlightRows.Children.Remove(container);
            _highlightEditors.Remove(editor);
        };
        _highlightEditors.Add(editor);
        _highlightRows.Children.Add(container);
    }

    private IReadOnlyList<TranscriptHighlightRule>? ReadHighlightRules()
    {
        List<TranscriptHighlightRule> rules = [];
        foreach (HighlightEditor editor in _highlightEditors)
        {
            string pattern = (editor.Pattern.Text ?? string.Empty).Trim();
            string foreground = (editor.Foreground.Text ?? string.Empty).Trim();
            if (pattern.Length == 0)
            {
                _validation.Text = "Each highlight rule needs a pattern, or remove the empty rule.";
                return null;
            }
            if (!IsHexColor(foreground))
            {
                _validation.Text = $"Highlight color '{foreground}' must be #RRGGBB.";
                return null;
            }

            HighlightMatchMode mode = editor.MatchMode.SelectedItem is HighlightMatchMode selectedMode
                ? selectedMode
                : HighlightMatchMode.Literal;
            if (mode == HighlightMatchMode.Regex)
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException exception)
                {
                    _validation.Text = $"Invalid highlight regex '{pattern}': {exception.Message}";
                    return null;
                }
            }

            rules.Add(new TranscriptHighlightRule(
                pattern,
                foreground.ToUpperInvariant(),
                mode,
                editor.Bold.IsChecked == true,
                editor.Underline.IsChecked == true,
                editor.CaseSensitive.IsChecked == true,
                editor.Enabled.IsChecked == true));
        }
        return rules;
    }

    private static bool IsHexColor(string value)
    {
        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }
        return value.Skip(1).All(Uri.IsHexDigit);
    }

    private string GetLogDirectory()
    {
        string root = System.IO.Path.GetDirectoryName(_runtime.SettingsStore.Path)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(root, "logs");
    }

    private Button NavigationButton(SettingsPage page, string title, string subtitle)
    {
        TextBlock label = new()
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = NexTypography.Body,
            Foreground = TextForeground
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
            button.Background = selected ? RaisedBackground : Brushes.Transparent;
            button.BorderBrush = selected ? Accent : Brushes.Transparent;
            button.BorderThickness = selected ? new Thickness(2, 0, 0, 0) : new Thickness(0);
            if (button.Content is TextBlock label)
            {
                label.Foreground = selected ? Accent : TextForeground;
            }
        }
    }

    private void PresetChanged()
    {
        if (_updatingAuthority || _preset.SelectedItem is not JevPreset preset || preset == JevPreset.Custom)
        {
            return;
        }
        ApplyAuthority(JevAuthorityService.CreatePreset(preset));
    }

    private void DomainChanged()
    {
        if (_updatingAuthority)
        {
            return;
        }
        _updatingAuthority = true;
        _preset.SelectedItem = JevPreset.Custom;
        _updatingAuthority = false;
    }

    private void ApplyAuthority(JevAuthoritySnapshot authority)
    {
        _updatingAuthority = true;
        _preset.SelectedItem = authority.Preset;
        foreach (JevDomain domain in Enum.GetValues<JevDomain>())
        {
            _authority[domain].SelectedItem = authority.Domains[domain];
        }
        _updatingAuthority = false;
    }

    private static StackPanel PageStack(string title, string subtitle)
    {
        StackPanel stack = new() { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TextForeground,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = Muted,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 4)
        });
        return stack;
    }

    private static Border SectionCard(string title, string subtitle)
    {
        StackPanel body = new() { Spacing = 8, Margin = new Thickness(0, 2, 0, 8) };
        Grid heading = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        heading.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TextForeground,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        TextBlock description = new()
        {
            Text = subtitle,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            FontSize = NexTypography.Metadata,
            Margin = new Thickness(0, 2, 0, 5)
        };
        Grid.SetRow(description, 1);
        heading.Children.Add(description);
        Border divider = UiTheme.DividerLine();
        Grid.SetRow(divider, 2);
        heading.Children.Add(divider);
        body.Children.Add(heading);
        return new Border
        {
            Child = body,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0)
        };
    }

    private static Control KeyValueDescription(string label, string description)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("100,*") };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Accent,
            FontWeight = FontWeight.SemiBold
        });
        TextBlock body = new()
        {
            Text = description,
            Foreground = TextForeground,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    private static Control FormRow(string label, Control control)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("150,*") };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = TextForeground,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static Control CompactFormRow(string label, Control control)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = TextForeground,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static ScrollViewer PageScroll(Control content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private static void ConfigureCheckBox(CheckBox checkBox, string text)
    {
        checkBox.Content = text;
        checkBox.Foreground = TextForeground;
        checkBox.FontSize = NexTypography.Body;
    }

    private static Border EditorContainer(Control content) => new()
    {
        Child = content,
        Background = Brushes.Transparent,
        BorderBrush = BorderColor,
        BorderThickness = new Thickness(0, 0, 0, 1),
        CornerRadius = new CornerRadius(0),
        Padding = new Thickness(0, 6, 0, 7)
    };

    private static TextBox Field()
    {
        TextBox field = UiTheme.FieldBox();
        field.FontSize = NexTypography.Body;
        return field;
    }

    private static Button PrimaryButton(string text)
    {
        Button button = UiTheme.PrimaryButton(text);
        button.FontSize = NexTypography.Body;
        return button;
    }

    private static Button SecondaryButton(string text)
    {
        Button button = UiTheme.QuietButton(text);
        button.FontSize = NexTypography.Body;
        return button;
    }

    private static double NearestFontSize(double current)
    {
        double[] sizes = [8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];
        return sizes.OrderBy(size => Math.Abs(size - current)).First();
    }

}

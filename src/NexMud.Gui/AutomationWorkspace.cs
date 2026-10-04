using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NexMud.Client.Automation;
using NexMud.Client.Interaction;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;
using NexMud.Client.Scripting;
using NexMud.Contracts.Events;

namespace NexMud.Gui;

/// <summary>
/// Canonical workspace for executable user behavior. Aliases, keybindings, triggers,
/// state rules, workflows and timers are edited here; script-backed behavior deep-links
/// to Scripting rather than duplicating the source editor.
/// </summary>
internal sealed class AutomationWorkspace : UserControl
{
    private const int MaxHistory = 250;
    private readonly NexMudRuntime _runtime;
    private readonly Func<Task> _showScripting;
    private readonly ObservableCollection<AutomationEntry> _entries = [];
    private readonly ObservableCollection<string> _history = [];
    private readonly ListBox _library = new();
    private readonly ListBox _historyList = new();
    private readonly ContentControl _content = new();
    private readonly ComboBox _filter = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _status = new();
    private WorkspaceMode _mode = WorkspaceMode.Library;
    private bool _active;

    private static readonly string[] Filters =
        ["All", "Aliases", "Keybindings", "Triggers", "State Rules", "Workflows", "Timers", "Script-backed"];

    public AutomationWorkspace(NexMudRuntime runtime, Func<Task> showScripting)
    {
        _runtime = runtime;
        _showScripting = showScripting;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;

        // Initialize the filter before subscribing to SelectionChanged. Avalonia raises
        // selection changes synchronously, so configuring the initial selection while
        // BuildLibrary is constructing its visual tree can re-enter RefreshLibrary and
        // attempt to parent the same controls twice during application startup.
        _filter.ItemsSource = Filters;
        _filter.SelectedItem = Filters[0];
        _filter.SelectionChanged += (_, _) => RefreshLibrary();
        _library.SelectionChanged += (_, _) => UpdateDetail();
        Content = Build();
        RefreshSnapshot();
    }

    public void Activate()
    {
        _active = true;
        RefreshSnapshot();
    }

    public void Deactivate() => _active = false;

    public void HandleEvent(IMudEvent mudEvent, DateTimeOffset timestamp)
    {
        if (mudEvent is not AutomationRuleMatched &&
            mudEvent is not AutomationVariableChanged &&
            mudEvent is not AutomationWorkflowStateChanged &&
            mudEvent is not AutomationKeybindingInvoked &&
            mudEvent is not ScriptLogEmitted { ModuleId: "automation.profile" })
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (mudEvent is not AutomationVariableChanged)
            {
                string? message = DescribeActivity(mudEvent);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _history.Insert(0, $"{timestamp:HH:mm:ss}  {message}");
                    while (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);
                }
            }
            if (_active || mudEvent is AutomationWorkflowStateChanged) RefreshSnapshot();
        });
    }

    public void RefreshSnapshot()
    {
        AutomationPreferences preferences = _runtime.Settings.Automation ?? new AutomationPreferences();
        int enabled = (_runtime.Settings.Aliases ?? []).Count(x => x.Enabled) +
                      (_runtime.Settings.KeyBindings ?? []).Count(x => x.Enabled) +
                      (_runtime.Settings.Triggers ?? []).Count(x => x.Enabled) +
                      (_runtime.Settings.GameRules ?? []).Count(x => x.Enabled) +
                      (_runtime.Settings.Workflows ?? []).Count(x => x.Enabled) +
                      (_runtime.Settings.Timers ?? []).Count(x => x.Enabled);
        _summary.Text = preferences.Enabled
            ? $"Automation engine ON · {enabled} enabled behaviors · {_runtime.Automation.ActiveWorkflowNames.Count} running"
            : "Automation engine OFF · configured behavior remains available for editing.";
        _summary.Foreground = preferences.Enabled ? UiTheme.Success : UiTheme.Warning;
        if (_mode == WorkspaceMode.Library) RefreshLibrary();
        else if (_mode == WorkspaceMode.Preferences) _content.Content = BuildPreferences();
    }

    private Control Build()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(UiTheme.SpaceMd)
        };
        StackPanel header = new() { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = "AUTOMATION",
            Foreground = UiTheme.Accent,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.Bold
        });
        header.Children.Add(new TextBlock
        {
            Text = "Executable behavior: aliases, keybindings, triggers, state rules, workflows, timers and script-backed behavior.",
            Foreground = UiTheme.Text,
            TextWrapping = TextWrapping.Wrap
        });
        _summary.FontSize = NexTypography.Metadata;
        header.Children.Add(_summary);
        root.Children.Add(header);

        StackPanel modes = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 10, 0, 10) };
        foreach ((WorkspaceMode mode, string label) in new[]
                 {
                     (WorkspaceMode.Library, "Library"),
                     (WorkspaceMode.Activity, "Activity"),
                     (WorkspaceMode.Templates, "Templates"),
                     (WorkspaceMode.Preferences, "Preferences")
                 })
        {
            Button button = UiTheme.QuietButton(label);
            button.Click += (_, _) => SelectMode(mode);
            modes.Children.Add(button);
        }
        Grid.SetRow(modes, 1);
        root.Children.Add(modes);
        Grid.SetRow(_content, 2);
        root.Children.Add(_content);
        return root;
    }

    private void SelectMode(WorkspaceMode mode)
    {
        _mode = mode;
        _content.Content = mode switch
        {
            WorkspaceMode.Library => BuildLibrary(),
            WorkspaceMode.Activity => BuildActivity(),
            WorkspaceMode.Templates => BuildTemplates(),
            WorkspaceMode.Preferences => BuildPreferences(),
            _ => BuildLibrary()
        };
        if (mode == WorkspaceMode.Library) RefreshLibrary();
    }

    private Control BuildLibrary()
    {
        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid toolbar = new() { ColumnDefinitions = new ColumnDefinitions("180,Auto,Auto,*"), ColumnSpacing = 6 };
        toolbar.Children.Add(_filter);
        Button create = UiTheme.PrimaryButton("New Automation");
        create.Click += (_, _) => ShowNewAutomationMenu(create);
        Grid.SetColumn(create, 1);
        toolbar.Children.Add(create);
        Button stop = UiTheme.QuietButton("Stop all running");
        stop.Click += (_, _) =>
        {
            _runtime.Automation.CancelAllWorkflows();
            RefreshSnapshot();
        };
        Grid.SetColumn(stop, 2);
        toolbar.Children.Add(stop);
        root.Children.Add(toolbar);

        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("1*,1.5*"), ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _library.ItemsSource = _entries;
        _library.Background = UiTheme.Console;
        _library.BorderThickness = new Thickness(0);
        _library.ItemTemplate = new FuncDataTemplate<AutomationEntry>((entry, _) => BuildEntryRow(entry), true);
        body.Children.Add(Panel("LIBRARY", _library));
        Border detail = Panel("DETAIL / BUILDER", _status);
        _status.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(detail, 1);
        body.Children.Add(detail);
        root.Children.Add(body);
        Grid.SetRow(body, 1);
        return root;
    }

    private void RefreshLibrary()
    {
        if (_content.Content is null || _mode != WorkspaceMode.Library)
        {
            _content.Content = BuildLibrary();
        }
        string? selectedKey = (_library.SelectedItem as AutomationEntry)?.Key;
        string filter = _filter.SelectedItem as string ?? "All";
        IEnumerable<AutomationEntry> entries = EnumerateEntries();
        if (!string.Equals(filter, "All", StringComparison.Ordinal))
            entries = entries.Where(entry => string.Equals(entry.Category, filter, StringComparison.Ordinal));
        _entries.Clear();
        foreach (AutomationEntry entry in entries.OrderBy(e => e.Category).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            _entries.Add(entry);
        _library.SelectedItem = _entries.FirstOrDefault(e => e.Key == selectedKey) ?? _entries.FirstOrDefault();
        UpdateDetail();
    }

    private IEnumerable<AutomationEntry> EnumerateEntries()
    {
        int index = 0;
        foreach (CommandAlias value in _runtime.Settings.Aliases ?? [])
            yield return new($"alias:{index++}", "Aliases", "Alias", value.Name, value.Expansion, value.Enabled, value);
        index = 0;
        foreach (CommandKeyBinding value in _runtime.Settings.KeyBindings ?? [])
            yield return new($"key:{index++}", "Keybindings", "Keybinding", value.Name ?? value.Gesture,
                $"{value.Gesture} · {value.Context} → {DescribeKeybinding(value)}", value.Enabled, value);
        index = 0;
        foreach (TriggerRule value in _runtime.Settings.Triggers ?? [])
            yield return new($"trigger:{index++}", "Triggers", "Trigger", value.Pattern, $"→ {value.Command}", value.Enabled, value);
        index = 0;
        foreach (GameRule value in _runtime.Settings.GameRules ?? [])
            yield return new($"state:{index++}", "State Rules", "State Rule", value.Name, $"{value.Condition} → {value.Command}", value.Enabled, value);
        index = 0;
        foreach (AutomationWorkflow value in _runtime.Settings.Workflows ?? [])
            yield return new($"workflow:{index++}", "Workflows", "Workflow", value.Name,
                string.IsNullOrWhiteSpace(value.TriggerCondition) && string.IsNullOrWhiteSpace(value.TriggerEvent)
                    ? "Manual / referenced invocation"
                    : $"{value.TriggerEvent ?? "state"} · {value.TriggerCondition ?? "any"}", value.Enabled, value);
        index = 0;
        foreach (CommandTimer value in _runtime.Settings.Timers ?? [])
            yield return new($"timer:{index++}", "Timers", "Timer", value.Name, $"Every {value.IntervalSeconds}s → {value.Command}", value.Enabled, value);
        yield return new("scripts", "Script-backed", "Script-backed", "Script-backed behavior",
            "Author and diagnose Jint packages in the Scripting workspace.", true, null);
    }

    private void UpdateDetail()
    {
        AutomationEntry? entry = _library.SelectedItem as AutomationEntry;
        Control editor = entry?.Value switch
        {
            CommandAlias alias => BuildAliasEditor(entry, alias),
            CommandKeyBinding key => BuildKeybindingEditor(entry, key),
            TriggerRule trigger => BuildTriggerEditor(entry, trigger),
            GameRule rule => BuildStateRuleEditor(entry, rule),
            AutomationWorkflow workflow => BuildWorkflowEditor(entry, workflow),
            CommandTimer timer => BuildTimerEditor(entry, timer),
            _ when entry?.Category == "Script-backed" => BuildScriptsDetail(),
            _ => new TextBlock { Text = "Select an automation.", Foreground = UiTheme.Muted }
        };
        Border panel = Panel("DETAIL / BUILDER", new ScrollViewer
        {
            Content = editor,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        });
        if (_content.Content is Grid root && root.Children.OfType<Grid>().LastOrDefault() is Grid body && body.Children.Count > 1)
        {
            body.Children.RemoveAt(1);
            Grid.SetColumn(panel, 1);
            body.Children.Add(panel);
        }
    }

    private Control BuildAliasEditor(AutomationEntry entry, CommandAlias value)
    {
        TextBox name = Field(value.Name); TextBox expansion = Field(value.Expansion); CheckBox enabled = Toggle("Enabled", value.Enabled);
        StackPanel form = EditorHeader("Alias", "Expand one manual command into one or more commands.");
        form.Children.Add(FieldRow("Name", name)); form.Children.Add(FieldRow("Expansion", expansion)); form.Children.Add(enabled);
        form.Children.Add(EditorActions(entry, async () => await SaveEntryAsync(entry, value with
        { Name = name.Text ?? "", Expansion = expansion.Text ?? "", Enabled = enabled.IsChecked ?? false })));
        return form;
    }

    private Control BuildKeybindingEditor(AutomationEntry entry, CommandKeyBinding value)
    {
        TextBox name = Field(value.Name ?? ""); TextBox gesture = Field(value.Gesture);
        ComboBox context = EnumCombo(value.Context); ComboBox action = EnumCombo(value.Action);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000); TextBox command = Field(value.Command ?? "");
        CheckBox enabled = Toggle("Enabled", value.Enabled);
        StackPanel form = EditorHeader("Keybinding", "WHEN a gesture occurs IN a context, invoke an application or automation action.");
        form.Children.Add(FieldRow("Name", name)); form.Children.Add(FieldRow("Gesture", gesture));
        form.Children.Add(FieldRow("Context", context)); form.Children.Add(FieldRow("Priority", priority));
        form.Children.Add(FieldRow("Application action", action)); form.Children.Add(FieldRow("Command / workflow / pane", command)); form.Children.Add(enabled);
        bool conflict = _runtime.Interaction.Keybindings.Conflicts().Any(group => group.Any(binding => ReferenceEquals(binding, value) ||
            (binding.Gesture.Equals(value.Gesture, StringComparison.OrdinalIgnoreCase) && binding.Context == value.Context && binding.Priority == value.Priority)));
        form.Children.Add(new TextBlock { Text = conflict ? "CONFLICTS: one or more bindings share this gesture/context/priority." : "CONFLICTS: None", Foreground = conflict ? UiTheme.Warning : UiTheme.Muted });
        form.Children.Add(EditorActions(entry, async () => await SaveEntryAsync(entry, value with
        {
            Name = BlankToNull(name.Text), Gesture = gesture.Text ?? "", Context = (KeybindingContext)(context.SelectedItem ?? value.Context),
            Priority = (int)(priority.Value ?? 0), Action = (KeybindingActionKind)(action.SelectedItem ?? value.Action),
            Command = command.Text ?? "", Enabled = enabled.IsChecked ?? false
        })));
        return form;
    }

    private Control BuildTriggerEditor(AutomationEntry entry, TriggerRule value)
    {
        TextBox pattern = Field(value.Pattern); TextBox command = Field(value.Command); TextBox group = Field(value.Group);
        CheckBox enabled = Toggle("Enabled", value.Enabled); ComboBox mode = EnumCombo(value.MatchMode);
        CheckBox caseSensitive = Toggle("Case sensitive", value.CaseSensitive);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        NumericUpDown cooldown = Number(value.CooldownMilliseconds, 0, 600_000);
        CheckBox stopProcessing = Toggle("Stop processing after match", value.StopProcessing);
        ComboBox scope = EnumCombo(value.Scope);
        CheckBox oneShot = Toggle("One shot", value.OneShot);
        StackPanel form = EditorHeader("Trigger", "Match raw output and execute behavior. Semantic processing remains independent.");
        form.Children.Add(FieldRow("Pattern", pattern)); form.Children.Add(FieldRow("Match", mode));
        form.Children.Add(caseSensitive); form.Children.Add(FieldRow("Scope", scope));
        form.Children.Add(FieldRow("Command", command)); form.Children.Add(FieldRow("Group", group));
        form.Children.Add(FieldRow("Priority", priority)); form.Children.Add(FieldRow("Cooldown (ms)", cooldown));
        form.Children.Add(stopProcessing); form.Children.Add(oneShot); form.Children.Add(enabled);
        form.Children.Add(EditorActions(entry, async () => await SaveEntryAsync(entry, value with
        {
            Pattern = pattern.Text ?? "", Command = command.Text ?? "", Group = group.Text ?? "Default",
            MatchMode = (HighlightMatchMode)(mode.SelectedItem ?? value.MatchMode),
            CaseSensitive = caseSensitive.IsChecked ?? false, Scope = (TriggerScope)(scope.SelectedItem ?? value.Scope),
            Priority = (int)(priority.Value ?? value.Priority), CooldownMilliseconds = (int)(cooldown.Value ?? value.CooldownMilliseconds),
            StopProcessing = stopProcessing.IsChecked ?? false, OneShot = oneShot.IsChecked ?? false,
            Enabled = enabled.IsChecked ?? false
        })));
        return form;
    }

    private Control BuildStateRuleEditor(AutomationEntry entry, GameRule value)
    {
        TextBox name = Field(value.Name); TextBox condition = Field(value.Condition); TextBox command = Field(value.Command); TextBox group = Field(value.Group);
        CheckBox enabled = Toggle("Enabled", value.Enabled);
        ComboBox activation = EnumCombo(value.Activation);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        NumericUpDown cooldown = Number(value.CooldownMilliseconds, 0, 600_000);
        CheckBox stopProcessing = Toggle("Stop processing after match", value.StopProcessing);
        CheckBox oneShot = Toggle("One shot", value.OneShot);
        StackPanel form = EditorHeader("State Rule", "Evaluate typed state and execute an action. This is not a transcript Output Rule.");
        form.Children.Add(FieldRow("Name", name)); form.Children.Add(FieldRow("Condition", condition));
        form.Children.Add(FieldRow("Activation", activation)); form.Children.Add(FieldRow("Command", command));
        form.Children.Add(FieldRow("Group", group)); form.Children.Add(FieldRow("Priority", priority));
        form.Children.Add(FieldRow("Cooldown (ms)", cooldown)); form.Children.Add(stopProcessing);
        form.Children.Add(oneShot); form.Children.Add(enabled);
        form.Children.Add(EditorActions(entry, async () => await SaveEntryAsync(entry, value with
        {
            Name = name.Text ?? "", Condition = condition.Text ?? "", Command = command.Text ?? "", Group = group.Text ?? "Default",
            Activation = (GameRuleActivation)(activation.SelectedItem ?? value.Activation),
            Priority = (int)(priority.Value ?? value.Priority), CooldownMilliseconds = (int)(cooldown.Value ?? value.CooldownMilliseconds),
            StopProcessing = stopProcessing.IsChecked ?? false, OneShot = oneShot.IsChecked ?? false,
            Enabled = enabled.IsChecked ?? false
        })));
        return form;
    }

    private Control BuildWorkflowEditor(AutomationEntry entry, AutomationWorkflow value)
    {
        TextBox name = Field(value.Name); TextBox condition = Field(value.TriggerCondition ?? ""); TextBox eventName = Field(value.TriggerEvent ?? ""); TextBox group = Field(value.Group);
        TextBox steps = Field(value.Steps); steps.AcceptsReturn = true; steps.MinHeight = 180; CheckBox enabled = Toggle("Enabled", value.Enabled);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        NumericUpDown cooldown = Number(value.CooldownMilliseconds, 0, 600_000);
        ComboBox failureMode = EnumCombo(value.FailureMode);
        CheckBox oneShot = Toggle("One shot", value.OneShot);
        StackPanel form = EditorHeader("Workflow", "Deterministic multi-step behavior with optional state/event trigger.");
        form.Children.Add(FieldRow("Name", name)); form.Children.Add(FieldRow("Trigger event", eventName));
        form.Children.Add(FieldRow("Trigger condition", condition)); form.Children.Add(FieldRow("Group", group));
        form.Children.Add(FieldRow("Priority", priority)); form.Children.Add(FieldRow("Cooldown (ms)", cooldown));
        form.Children.Add(FieldRow("Failure mode", failureMode)); form.Children.Add(oneShot);
        form.Children.Add(FieldRow("Steps", steps)); form.Children.Add(enabled);
        StackPanel actions = EditorActions(entry, async () => await SaveEntryAsync(entry, value with
        {
            Name = name.Text ?? "", TriggerEvent = BlankToNull(eventName.Text), TriggerCondition = BlankToNull(condition.Text),
            Group = group.Text ?? "Default", Steps = steps.Text ?? "", Priority = (int)(priority.Value ?? value.Priority),
            CooldownMilliseconds = (int)(cooldown.Value ?? value.CooldownMilliseconds),
            FailureMode = (AutomationWorkflowFailureMode)(failureMode.SelectedItem ?? value.FailureMode),
            OneShot = oneShot.IsChecked ?? false, Enabled = enabled.IsChecked ?? false
        }));
        Button run = UiTheme.QuietButton("Run workflow");
        run.Click += async (_, _) => await _runtime.Automation.RunWorkflowAsync(value.Id ?? string.Empty, _runtime.CancellationToken).ConfigureAwait(true);
        actions.Children.Insert(0, run); form.Children.Add(actions); return form;
    }

    private Control BuildTimerEditor(AutomationEntry entry, CommandTimer value)
    {
        TextBox name = Field(value.Name); NumericUpDown interval = Number(value.IntervalSeconds, 1, 86_400); TextBox command = Field(value.Command); TextBox group = Field(value.Group);
        CheckBox repeat = Toggle("Repeat", value.Repeat); CheckBox enabled = Toggle("Enabled", value.Enabled);
        StackPanel form = EditorHeader("Timer", "Run an action on a deterministic interval.");
        form.Children.Add(FieldRow("Name", name)); form.Children.Add(FieldRow("Interval seconds", interval)); form.Children.Add(FieldRow("Command", command)); form.Children.Add(FieldRow("Group", group)); form.Children.Add(repeat); form.Children.Add(enabled);
        form.Children.Add(EditorActions(entry, async () => await SaveEntryAsync(entry, value with
        { Name = name.Text ?? "", IntervalSeconds = (int)(interval.Value ?? 1), Command = command.Text ?? "", Group = group.Text ?? "Default", Repeat = repeat.IsChecked ?? false, Enabled = enabled.IsChecked ?? false })));
        return form;
    }

    private Control BuildScriptsDetail()
    {
        StackPanel form = EditorHeader("Script-backed", "Automation may reference Jint-backed behavior; source editing and runtime diagnostics remain in Scripting.");
        Button open = UiTheme.PrimaryButton("Open Scripting");
        open.Click += async (_, _) => await RunUiTaskAsync(_showScripting, "Unable to open Scripting").ConfigureAwait(true);
        form.Children.Add(open);
        return form;
    }

    private async Task SaveEntryAsync<T>(AutomationEntry entry, T updated)
    {
        ClientSettings settings = _runtime.Settings;
        IReadOnlyList<CommandAlias> aliases = settings.Aliases ?? [];
        IReadOnlyList<TriggerRule> triggers = settings.Triggers ?? [];
        IReadOnlyList<GameRule> rules = settings.GameRules ?? [];
        IReadOnlyList<AutomationWorkflow> workflows = settings.Workflows ?? [];
        IReadOnlyList<CommandTimer> timers = settings.Timers ?? [];
        IReadOnlyList<CommandKeyBinding> keys = settings.KeyBindings ?? [];
        int index = EntryIndex(entry.Key);
        switch (updated)
        {
            case CommandAlias alias: aliases = Replace(aliases, index, alias); break;
            case TriggerRule trigger: triggers = Replace(triggers, index, trigger); break;
            case GameRule rule: rules = Replace(rules, index, rule); break;
            case AutomationWorkflow workflow: workflows = Replace(workflows, index, workflow); break;
            case CommandTimer timer: timers = Replace(timers, index, timer); break;
            case CommandKeyBinding key: keys = Replace(keys, index, key); break;
        }
        await _runtime.SaveConvenienceSettingsAsync(settings.LogFormat, aliases, triggers, rules, workflows, timers, keys, _runtime.CancellationToken).ConfigureAwait(true);
        RefreshSnapshot();
    }

    private async Task DeleteEntryAsync(AutomationEntry entry)
    {
        ClientSettings settings = _runtime.Settings;
        int index = EntryIndex(entry.Key);
        IReadOnlyList<CommandAlias> aliases = settings.Aliases ?? [];
        IReadOnlyList<TriggerRule> triggers = settings.Triggers ?? [];
        IReadOnlyList<GameRule> rules = settings.GameRules ?? [];
        IReadOnlyList<AutomationWorkflow> workflows = settings.Workflows ?? [];
        IReadOnlyList<CommandTimer> timers = settings.Timers ?? [];
        IReadOnlyList<CommandKeyBinding> keys = settings.KeyBindings ?? [];
        if (entry.Key.StartsWith("alias:")) aliases = RemoveAt(aliases, index);
        else if (entry.Key.StartsWith("trigger:")) triggers = RemoveAt(triggers, index);
        else if (entry.Key.StartsWith("state:")) rules = RemoveAt(rules, index);
        else if (entry.Key.StartsWith("workflow:")) workflows = RemoveAt(workflows, index);
        else if (entry.Key.StartsWith("timer:")) timers = RemoveAt(timers, index);
        else if (entry.Key.StartsWith("key:")) keys = RemoveAt(keys, index);
        await _runtime.SaveConvenienceSettingsAsync(settings.LogFormat, aliases, triggers, rules, workflows, timers, keys, _runtime.CancellationToken).ConfigureAwait(true);
        RefreshSnapshot();
    }

    private void ShowNewAutomationMenu(Control anchor)
    {
        ContextMenu menu = new();
        foreach (string kind in new[] { "Alias", "Keybinding", "Trigger", "State Rule", "Workflow", "Timer", "Script-backed" })
        {
            MenuItem item = new() { Header = kind };
            item.Click += async (_, _) => await CreateAsync(kind).ConfigureAwait(true);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    private async Task CreateAsync(string kind)
    {
        ClientSettings settings = _runtime.Settings;
        List<CommandAlias> aliases = [.. settings.Aliases ?? []];
        List<TriggerRule> triggers = [.. settings.Triggers ?? []];
        List<GameRule> rules = [.. settings.GameRules ?? []];
        List<AutomationWorkflow> workflows = [.. settings.Workflows ?? []];
        List<CommandTimer> timers = [.. settings.Timers ?? []];
        List<CommandKeyBinding> keys = [.. settings.KeyBindings ?? []];
        switch (kind)
        {
            case "Alias": aliases.Add(new CommandAlias("new-alias", "look")); _filter.SelectedItem = "Aliases"; break;
            case "Keybinding": keys.Add(new CommandKeyBinding("Cmd+1", "scan north", Name: "New keybinding", Context: KeybindingContext.Input)); _filter.SelectedItem = "Keybindings"; break;
            case "Trigger": triggers.Add(new TriggerRule("You are thirsty.", "drink fountain")); _filter.SelectedItem = "Triggers"; break;
            case "State Rule": rules.Add(new GameRule("New state rule", "hp.percent < 30", "look")); _filter.SelectedItem = "State Rules"; break;
            case "Workflow": workflows.Add(new AutomationWorkflow("New workflow", "", Actions: [new SendCommandAutomationAction("look")], Id: Guid.NewGuid().ToString("N"))); _filter.SelectedItem = "Workflows"; break;
            case "Timer": timers.Add(new CommandTimer("New timer", 60, "score")); _filter.SelectedItem = "Timers"; break;
            case "Script-backed": await _showScripting().ConfigureAwait(true); return;
        }
        await _runtime.SaveConvenienceSettingsAsync(settings.LogFormat, aliases, triggers, rules, workflows, timers, keys, _runtime.CancellationToken).ConfigureAwait(true);
        RefreshSnapshot();
    }

    private Control BuildActivity()
    {
        ConfigureHistoryList();
        StackPanel root = new() { Spacing = 8 };
        root.Children.Add(new TextBlock { Text = "AUTOMATION ACTIVITY", Foreground = UiTheme.Accent, FontWeight = FontWeight.Bold });
        root.Children.Add(new TextBlock { Text = "Keybindings participate in the same provenance stream as triggers, state rules and workflows.", Foreground = UiTheme.Muted, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_historyList);
        return root;
    }

    private Control BuildTemplates()
    {
        StackPanel root = EditorHeader(
            "Templates",
            "Reusable Automation templates live here. No bundled templates are defined in this release.");
        root.Children.Add(new TextBlock
        {
            Text = "Use New Automation to create an Alias, Keybinding, Trigger, State Rule, Workflow, Timer, or Script-backed behavior.",
            Foreground = UiTheme.Muted,
            TextWrapping = TextWrapping.Wrap
        });
        Button create = UiTheme.PrimaryButton("New Automation");
        create.HorizontalAlignment = HorizontalAlignment.Left;
        create.Click += (_, _) => ShowNewAutomationMenu(create);
        root.Children.Add(create);
        return root;
    }

    private Control BuildPreferences()
    {
        AutomationPreferences value = _runtime.Settings.Automation ?? new AutomationPreferences();
        CheckBox enabled = Toggle("Enable deterministic automation", value.Enabled);
        NumericUpDown maxCommands = Number(value.MaxCommandsPerSecond, 1, 100);
        NumericUpDown concurrent = Number(value.MaxConcurrentWorkflows, 1, 32);
        NumericUpDown rolling = Number(value.RollingBufferCharacters, 256, 100_000);
        NumericUpDown overrideMs = Number(value.HumanOverrideMilliseconds, 0, 60_000);
        CheckBox persist = Toggle("Persist automation variables", value.PersistVariables);
        ChipEditor disabled = new("Disabled group"); disabled.SetValues(value.DisabledGroups);
        TextBlock message = new() { Foreground = UiTheme.Muted };
        StackPanel root = EditorHeader("Preferences", "Runtime policy only. Behavior definitions live in the Library.");
        root.Children.Add(enabled); root.Children.Add(FieldRow("Max commands/sec", maxCommands)); root.Children.Add(FieldRow("Max concurrent runs", concurrent)); root.Children.Add(FieldRow("Rolling trigger buffer", rolling)); root.Children.Add(FieldRow("Human override window (ms)", overrideMs)); root.Children.Add(persist); root.Children.Add(FieldRow("Disabled groups", disabled)); root.Children.Add(message);
        Button save = UiTheme.PrimaryButton("Save Preferences");
        save.Click += async (_, _) =>
        {
            AutomationPreferences updated = value with
            {
                Enabled = enabled.IsChecked ?? false,
                MaxCommandsPerSecond = (int)(maxCommands.Value ?? 8),
                MaxConcurrentWorkflows = (int)(concurrent.Value ?? 2),
                RollingBufferCharacters = (int)(rolling.Value ?? 8192),
                HumanOverrideMilliseconds = (int)(overrideMs.Value ?? 1500),
                PersistVariables = persist.IsChecked ?? true,
                DisabledGroups = disabled.Values
            };
            try
            {
                await _runtime.SaveAutomationPreferencesAsync(updated, _runtime.CancellationToken).ConfigureAwait(true);
                message.Text = "Saved.";
                message.Foreground = UiTheme.Success;
                RefreshSnapshot();
            }
            catch (Exception exception)
            {
                message.Text = exception.Message;
                message.Foreground = UiTheme.Danger;
            }
        };
        root.Children.Add(save);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private StackPanel EditorActions(AutomationEntry entry, Func<Task> saveAction)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 8, 0, 0) };
        Button save = UiTheme.PrimaryButton("Save"); save.Click += async (_, _) => await RunUiTaskAsync(saveAction, "Unable to save automation").ConfigureAwait(true);
        Button delete = UiTheme.QuietButton("Delete"); delete.Click += async (_, _) => await RunUiTaskAsync(() => DeleteEntryAsync(entry), "Unable to delete automation").ConfigureAwait(true);
        row.Children.Add(save); row.Children.Add(delete); return row;
    }

    private static Control BuildEntryRow(AutomationEntry? entry)
    {
        if (entry is null) return new TextBlock();
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(8, 6) };
        TextBlock mark = new() { Text = entry.Enabled ? "✓" : "○", Foreground = entry.Enabled ? UiTheme.Success : UiTheme.Muted };
        row.Children.Add(mark);
        StackPanel identity = new() { Spacing = 1 };
        identity.Children.Add(new TextBlock { Text = entry.Name, Foreground = UiTheme.Text, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        identity.Children.Add(new TextBlock { Text = entry.Summary, Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(identity, 1); row.Children.Add(identity);
        TextBlock kind = new() { Text = entry.Kind, Foreground = UiTheme.Accent, FontSize = NexTypography.Metadata, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(kind, 2); row.Children.Add(kind); return row;
    }

    private void ConfigureHistoryList()
    {
        _historyList.ItemsSource = _history;
        _historyList.Background = UiTheme.Console;
        _historyList.BorderThickness = new Thickness(0);
        _historyList.ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock
        {
            Text = item ?? string.Empty,
            Foreground = UiTheme.Text,
            FontFamily = UiTheme.Mono,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 3)
        }, true);
    }

    private static string? DescribeActivity(IMudEvent evt) => evt switch
    {
        AutomationKeybindingInvoked key => $"Keybinding invoked  {key.Gesture} · {key.Context} → {key.Action}{(string.IsNullOrWhiteSpace(key.Value) ? "" : $" · {key.Value}")}",
        AutomationRuleMatched rule => $"{NormalizeRuleKind(rule.RuleKind)} matched  {rule.RuleName}{(string.IsNullOrWhiteSpace(rule.Command) ? "" : $" → {rule.Command}")}",
        AutomationWorkflowStateChanged workflow => $"Workflow {workflow.Status.ToString().ToLowerInvariant()}  {workflow.WorkflowName}{(string.IsNullOrWhiteSpace(workflow.Detail) ? "" : $" · {workflow.Detail}")}",
        ScriptLogEmitted log => $"Automation runtime  {log.Message}",
        _ => null
    };

    private static string NormalizeRuleKind(string kind) => kind.Equals("rule", StringComparison.OrdinalIgnoreCase) ? "State Rule" : kind;
    private static string DescribeKeybinding(CommandKeyBinding binding) => binding.Action is KeybindingActionKind.SendCommand or KeybindingActionKind.RunAutomation or KeybindingActionKind.TogglePane ? $"{binding.Action} {binding.Command}" : binding.Action.ToString();
    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int EntryIndex(string key) => int.TryParse(key[(key.IndexOf(':') + 1)..], out int value) ? value : -1;
    private static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> source, int index, T value) { List<T> list = [.. source]; if (index >= 0 && index < list.Count) list[index] = value; return list; }
    private static IReadOnlyList<T> RemoveAt<T>(IReadOnlyList<T> source, int index) { List<T> list = [.. source]; if (index >= 0 && index < list.Count) list.RemoveAt(index); return list; }
    private static TextBox Field(string value) { TextBox field = UiTheme.FieldBox(); field.Text = value; return field; }
    private static CheckBox Toggle(string label, bool value) => new() { Content = label, IsChecked = value, Foreground = UiTheme.Text };
    private static NumericUpDown Number(decimal value, decimal min, decimal max) => new() { Value = value, Minimum = min, Maximum = max, Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
    private static ComboBox EnumCombo<T>(T selected) where T : struct, Enum => new() { ItemsSource = Enum.GetValues<T>(), SelectedItem = selected, MinWidth = 180 };

    private static StackPanel EditorHeader(string title, string description)
    {
        StackPanel form = new() { Spacing = 8, Margin = new Thickness(8) };
        form.Children.Add(new TextBlock { Text = title, Foreground = UiTheme.Text, FontSize = NexTypography.SectionTitle, FontWeight = FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = description, Foreground = UiTheme.Muted, TextWrapping = TextWrapping.Wrap });
        return form;
    }

    private static Control FieldRow(string label, Control control)
    {
        StackPanel panel = new() { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata });
        panel.Children.Add(control); return panel;
    }

    private static Border Panel(string title, Control child)
    {
        Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock { Text = title, Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 6) });
        Border content = new() { Child = child, BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(content, 1); grid.Children.Add(content);
        return new Border { Background = UiTheme.Surface, BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(1), Child = grid };
    }

    private static async Task RunUiTaskAsync(Func<Task> action, string failure)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"{failure}: {exception}"); }
    }

    private sealed record AutomationEntry(string Key, string Category, string Kind, string Name, string Summary, bool Enabled, object? Value);
    private enum WorkspaceMode { Library, Activity, Templates, Preferences }

    private sealed class ChipEditor : UserControl
    {
        private readonly List<string> _values = [];
        private readonly WrapPanel _chips = new();
        private readonly TextBox _input = UiTheme.FieldBox();
        public ChipEditor(string placeholder)
        {
            _input.PlaceholderText = placeholder;
            Button add = UiTheme.QuietButton("Add");
            add.Click += (_, _) => { string v = (_input.Text ?? "").Trim(); if (v.Length > 0 && !_values.Contains(v, StringComparer.OrdinalIgnoreCase)) { _values.Add(v); _input.Text = ""; Render(); } };
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 }; row.Children.Add(_input); Grid.SetColumn(add, 1); row.Children.Add(add);
            StackPanel root = new(); root.Children.Add(_chips); root.Children.Add(row); Content = root;
        }
        public IReadOnlyList<string> Values => _values.ToArray();
        public void SetValues(IEnumerable<string>? values) { _values.Clear(); foreach (string value in values ?? []) { string v = value.Trim(); if (v.Length > 0 && !_values.Contains(v, StringComparer.OrdinalIgnoreCase)) _values.Add(v); } Render(); }
        private void Render() { _chips.Children.Clear(); foreach (string value in _values.ToArray()) { Button chip = UiTheme.QuietButton($"{value} ×"); chip.MinHeight = 24; chip.Click += (_, _) => { _values.Remove(value); Render(); }; _chips.Children.Add(chip); } }
    }
}

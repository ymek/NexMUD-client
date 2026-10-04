using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Automation;
using NexMud.Client.Interaction;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;
using NexMud.Scripting.Runtime;

namespace NexMud.Gui.AutomationStudio;

internal sealed record AutomationEditorServices(
    NexMudRuntime Runtime,
    Func<ScriptFunctionRef, Task> OpenDefinition,
    Func<ScriptFunctionRef?, Task<ScriptFunctionRef?>> ChooseFunction,
    Func<ScriptFunctionRef, ExportedScriptFunction?> ResolveExport,
    Func<StudioDocumentKind, string, int?> ResolveAutomationIndex,
    Func<StudioDocumentKind, string, Task> DuplicateAutomation,
    Func<StudioDocumentKind, string, Task> DeleteAutomation);

/// <summary>
/// Full-width visual designer for one Automation definition. Sections follow the workbench model:
/// MATCH / SCHEDULE / GESTURE (what starts it), WHEN (conditions) and DO (actions). Primary edit
/// fields live here alongside their automation content.
/// </summary>
internal sealed class AutomationDocumentEditor
{
    private const double AutomationFormMaxWidth = 1400;
    private const double WorkflowFormMaxWidth = 960;
    private const double ControlHeight = 30;

    private readonly StudioDocumentKind _kind;
    private readonly string _automationId;
    private readonly AutomationEditorServices _services;
    private readonly ToggleSwitch _enabled = new() { Content = "Enabled", Foreground = StudioShellChrome.Foreground, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _testPreview = new() { Foreground = StudioShellChrome.Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private Button? _workflowRunButton;
    private Action? _refreshWorkflowRunButton;
    private Func<object> _collect = () => throw new InvalidOperationException("Editor not built.");
    private bool _loading = true;

    private AutomationDocumentEditor(
        StudioDocumentKind kind,
        string automationId,
        AutomationEditorServices services)
    {
        _kind = kind;
        _automationId = automationId;
        _services = services;
    }

    public Control View { get; private set; } = new Border();
    public string Title { get; private set; } = string.Empty;
    public bool IsEnabled => _enabled.IsChecked ?? false;
    public event Action? Changed;
    public event Action<string>? Saved;

    public static AutomationDocumentEditor? Create(
        StudioDocumentKind kind,
        string automationId,
        AutomationEditorServices services)
    {
        int? index = services.ResolveAutomationIndex(kind, automationId);
        if (index is null) return null;

        AutomationCollections collections = AutomationCollections.From(services.Runtime.Settings);
        if (collections.Get(kind, index.Value) is not { } value) return null;
        AutomationDocumentEditor editor = new(kind, automationId, services);
        editor.Build(value, collections.NameOf(kind, index.Value) ?? kind.Label());
        editor._loading = false;
        return editor;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        int index = _services.ResolveAutomationIndex(_kind, _automationId)
            ?? throw new InvalidOperationException($"{_kind.Label()} '{_automationId}' no longer exists.");
        object updated = _collect();
        AutomationCollections collections = AutomationCollections.From(_services.Runtime.Settings).Replace(_kind, index, updated);
        await collections.SaveAsync(_services.Runtime, cancellationToken).ConfigureAwait(true);
        Title = collections.NameOf(_kind, index) ?? Title;
        Saved?.Invoke(Title);
    }

    public async Task ToggleEnabledAsync(CancellationToken cancellationToken = default)
    {
        _enabled.IsChecked = !IsEnabled;
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    private void MarkChanged()
    {
        if (!_loading) Changed?.Invoke();
    }

    // ───────────────────────────── Document shell ─────────────────────────────

    private void Build(object value, string name)
    {
        Title = name;
        bool automationSurface = _kind != StudioDocumentKind.Workflow;
        StackPanel page = new()
        {
            Spacing = automationSurface ? 0 : 4,
            MaxWidth = automationSurface ? AutomationFormMaxWidth : WorkflowFormMaxWidth,
            HorizontalAlignment = automationSurface ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Margin = automationSurface ? new Thickness(20, 10, 20, 28) : new Thickness(28, 20, 28, 40)
        };
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions(automationSurface ? "*,Auto,Auto,Auto,Auto" : value is AutomationWorkflow ? "*,Auto,Auto,Auto" : "*,Auto"),
            ColumnSpacing = automationSurface ? 12 : 0
        };
        if (automationSurface)
        {
            header.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = UiTheme.Text,
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            Border kindBadge = new()
            {
                Background = StudioShellChrome.Input,
                BorderBrush = StudioShellChrome.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = _kind.Label(), Foreground = StudioShellChrome.Secondary, FontSize = 12 }
            };
            Grid.SetColumn(kindBadge, 1);
            header.Children.Add(kindBadge);
            Grid.SetColumn(_enabled, 2);
            Button test = new()
            {
                Content = "▶  Test",
                MinWidth = 106,
                Height = 34,
                Padding = new Thickness(12, 4),
                Background = StudioShellChrome.Input,
                Foreground = StudioShellChrome.Foreground,
                BorderBrush = StudioShellChrome.Border,
                BorderThickness = new Thickness(1),
                IsVisible = automationSurface,
                VerticalAlignment = VerticalAlignment.Center
            };
            test.Click += (_, _) => PreviewAutomation();
            Grid.SetColumn(test, 3);
            header.Children.Add(test);

            ContextMenu menu = new();
            MenuItem duplicate = new() { Header = "Duplicate" };
            duplicate.Click += async (_, _) => await _services.DuplicateAutomation(_kind, _automationId).ConfigureAwait(true);
            MenuItem delete = new() { Header = "Delete…" };
            delete.Click += async (_, _) => await _services.DeleteAutomation(_kind, _automationId).ConfigureAwait(true);
            menu.Items.Add(duplicate);
            menu.Items.Add(delete);
            Button more = new()
            {
                Content = "⋮",
                Width = 38,
                Height = 34,
                Background = StudioShellChrome.Input,
                Foreground = StudioShellChrome.Foreground,
                BorderBrush = StudioShellChrome.Border,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            more.Click += (_, _) => menu.Open(more);
            Grid.SetColumn(more, 4);
            header.Children.Add(more);
        }
        else
        {
            header.Children.Add(new TextBlock
            {
                Text = value is AutomationWorkflow ? name : $"{_kind.Label()}: {name}",
                Foreground = UiTheme.Text,
                FontSize = value is AutomationWorkflow ? 22 : 16,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(_enabled, 1);
            if (value is AutomationWorkflow workflow)
            {
                Button run = Quiet("▶  Run");
                _workflowRunButton = run;
                run.MinWidth = 94;
                run.Background = StudioShellChrome.Selected;
                run.Foreground = Brushes.White;
                run.IsEnabled = workflow.Enabled && workflow.AllowManualRun && !string.IsNullOrWhiteSpace(workflow.Id);
                ToolTip.SetTip(run, run.IsEnabled ? "Run this workflow now" : "Enable the workflow and manual run permission to run it.");
                run.Click += async (_, _) =>
                {
                    try
                    {
                        await SaveAsync().ConfigureAwait(true);
                        bool started = await _services.Runtime.Automation.RunWorkflowAsync(workflow.Id!, CancellationToken.None).ConfigureAwait(true);
                        _testPreview.Text = started ? "Workflow started." : "Workflow could not start. Check connection, automation settings, group settings and concurrency.";
                        _testPreview.Foreground = started ? StudioShellChrome.Success : UiTheme.Warning;
                    }
                    catch (Exception exception)
                    {
                        _testPreview.Text = $"Could not save workflow before running: {exception.Message}";
                        _testPreview.Foreground = UiTheme.Warning;
                    }
                    _testPreview.IsVisible = true;
                };
                Grid.SetColumn(run, 2);
                header.Children.Add(run);
                ContextMenu workflowMenu = new();
                MenuItem duplicate = new() { Header = "Duplicate" };
                duplicate.Click += async (_, _) => await _services.DuplicateAutomation(_kind, _automationId).ConfigureAwait(true);
                MenuItem delete = new() { Header = "Delete…" };
                delete.Click += async (_, _) => await _services.DeleteAutomation(_kind, _automationId).ConfigureAwait(true);
                workflowMenu.Items.Add(duplicate);
                workflowMenu.Items.Add(delete);
                Button more = Quiet("⋮");
                more.Width = 34;
                more.Click += (_, _) => workflowMenu.Open(more);
                Grid.SetColumn(more, 3);
                header.Children.Add(more);
            }
        }
        header.Children.Add(_enabled);
        page.Children.Add(header);
        if (automationSurface || value is AutomationWorkflow) page.Children.Add(_testPreview);

        switch (value)
        {
            case CommandAlias alias: BuildAlias(page, alias); break;
            case TriggerRule trigger: BuildTrigger(page, trigger); break;
            case SemanticTriggerRule semantic: BuildSemantic(page, semantic); break;
            case CommandKeyBinding key: BuildKeybinding(page, key); break;
            case CommandTimer timer: BuildTimer(page, timer); break;
            case GameRule rule: BuildStateRule(page, rule); break;
            case AutomationWorkflow workflow: BuildWorkflow(page, workflow); break;
            case TranscriptHighlightRule highlight: BuildHighlight(page, highlight); break;
        }
        _enabled.IsCheckedChanged += (_, _) =>
        {
            MarkChanged();
            _refreshWorkflowRunButton?.Invoke();
        };
        View = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ───────────────────────────── Per-kind designers ─────────────────────────────

    private void PreviewAutomation()
    {
        object value = _collect();
        string detail = value switch
        {
            CommandAlias alias => alias.Enabled && CommandAliasExpander.TryExpand(alias.Name, [alias], out string expansion)
                ? $"No-argument expansion: {expansion}. Conditions and {ActionCount(alias.Actions)} configured actions are not executed."
                : "The alias is disabled or has no previewable expansion.",
            TriggerRule trigger => $"{trigger.MatchMode} match for {trigger.Scope}: {trigger.Pattern}. {CommandPreview(trigger.Enabled, trigger.Command)}. {ActionCount(trigger.Actions)} configured actions are not executed.",
            SemanticTriggerRule semantic => $"Event: {semantic.EventName}. {ConditionCount(semantic.Conditions)} conditions and {ActionCount(semantic.Actions)} actions are not evaluated or executed.",
            CommandKeyBinding binding => $"Gesture: {binding.Gesture}. {CommandPreview(binding.Enabled, binding.Command)}. {ActionCount(binding.Actions)} configured actions are not executed.",
            CommandTimer timer => $"Interval: {timer.IntervalSeconds}s. {CommandPreview(timer.Enabled, timer.Command)}. Repeat: {timer.Repeat}. {ActionCount(timer.Actions)} configured actions are not executed.",
            GameRule rule => $"Rule: {rule.Condition}. Activation: {rule.Activation}. {CommandPreview(rule.Enabled, rule.Command)}. {ActionCount(rule.Actions)} configured actions are not executed.",
            TranscriptHighlightRule highlight => $"Highlight pattern: {highlight.Pattern} ({highlight.MatchMode}). No transcript text is evaluated.",
            _ => "This automation type does not have a preview."
        };

        _testPreview.Text = $"Preview only — no MUD commands or actions are run. {(IsAutomationEnabled(value) ? "Enabled" : "Disabled")}: {detail}";
        _testPreview.IsVisible = true;
    }

    private static bool IsAutomationEnabled(object value) => value switch
    {
        CommandAlias item => item.Enabled,
        TriggerRule item => item.Enabled,
        SemanticTriggerRule item => item.Enabled,
        CommandKeyBinding item => item.Enabled,
        CommandTimer item => item.Enabled,
        GameRule item => item.Enabled,
        TranscriptHighlightRule item => item.Enabled,
        _ => false
    };

    private static string CommandPreview(bool enabled, string command) => enabled
        ? $"Would send: {command}"
        : $"Would send if enabled: {command}";

    private static int ActionCount(IReadOnlyList<AutomationAction>? actions) => actions?.Count ?? 0;

    private static int ConditionCount(IReadOnlyList<AutomationCondition>? conditions) => conditions?.Count ?? 0;

    private void BuildAlias(StackPanel page, CommandAlias value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox name = Text(value.Name);
        TextBox expansion = Text(value.Expansion);
        ConditionList conditions = new(this, value.Conditions);
        ActionList actions = new(this, value.Actions);
        page.Children.Add(Section("MATCH", Labeled("Alias name (typed by the player)", name)));
        page.Children.Add(Section("WHEN", conditions.View));
        page.Children.Add(Section("DO", Labeled("Expansion (sent first)", expansion), actions.View));
        _collect = () => value with
        {
            Name = name.Text ?? "", Expansion = expansion.Text ?? "", Enabled = IsEnabled,
            Conditions = conditions.Values, Actions = actions.Values
        };
    }

    private void BuildTrigger(StackPanel page, TriggerRule value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox pattern = Text(value.Pattern);
        ComboBox mode = Combo(value.MatchMode);
        CheckBox caseSensitive = Check("Case sensitive", value.CaseSensitive);
        ComboBox scope = Combo(value.Scope);
        TextBox command = Text(value.Command);
        TextBox group = Text(value.Group);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        NumericUpDown cooldown = Number(value.CooldownMilliseconds, 0, 600_000);
        CheckBox stop = Check("Stop processing after match", value.StopProcessing);
        CheckBox oneShot = Check("One shot", value.OneShot);
        ConditionList conditions = new(this, value.Conditions);
        ActionList actions = new(this, value.Actions);

        page.Children.Add(Section("MATCH",
            Labeled("Source", new TextBlock { Text = "Text (raw output line)", Foreground = UiTheme.Text }),
            Row(Labeled("Match mode", mode), Labeled("Scope", scope)),
            Labeled("Pattern", pattern),
            caseSensitive,
            new TextBlock { Text = "Regex captures are available to the actions below.", Foreground = UiTheme.Faint, FontSize = 12 }));
        page.Children.Add(Section("WHEN", conditions.View));
        page.Children.Add(Section("DO", Labeled("Send command", command), actions.View));
        page.Children.Add(Section("BEHAVIOR", Row(Labeled("Group", group), Labeled("Priority", priority), Labeled("Cooldown (ms)", cooldown)), stop, oneShot));
        _collect = () => value with
        {
            Pattern = pattern.Text ?? "", Command = command.Text ?? "", Group = NonBlank(group.Text, "Default"),
            MatchMode = (HighlightMatchMode)(mode.SelectedItem ?? value.MatchMode), CaseSensitive = caseSensitive.IsChecked ?? false,
            Scope = (TriggerScope)(scope.SelectedItem ?? value.Scope), Priority = (int)(priority.Value ?? value.Priority),
            CooldownMilliseconds = (int)(cooldown.Value ?? value.CooldownMilliseconds), StopProcessing = stop.IsChecked ?? false,
            OneShot = oneShot.IsChecked ?? false, Enabled = IsEnabled, Conditions = conditions.Values, Actions = actions.Values
        };
    }

    private void BuildSemantic(StackPanel page, SemanticTriggerRule value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox name = Text(value.Name);
        TextBox eventName = Text(value.EventName);
        TextBox group = Text(value.Group);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        ConditionList conditions = new(this, value.Conditions);
        ActionList actions = new(this, value.Actions);
        page.Children.Add(Section("MATCH",
            Labeled("Source", new TextBlock { Text = "Semantic event", Foreground = UiTheme.Text }),
            Labeled("Name", name),
            Labeled("Event (e.g. RoomChanged, VitalsChanged)", eventName)));
        page.Children.Add(Section("WHEN", conditions.View));
        page.Children.Add(Section("DO", actions.View));
        page.Children.Add(Section("BEHAVIOR", Row(Labeled("Group", group), Labeled("Priority", priority))));
        _collect = () => value with
        {
            Name = name.Text ?? "", EventName = eventName.Text ?? "", Group = NonBlank(group.Text, "Default"),
            Priority = (int)(priority.Value ?? value.Priority), Enabled = IsEnabled,
            Conditions = conditions.Values, Actions = actions.Values
        };
    }

    private void BuildKeybinding(StackPanel page, CommandKeyBinding value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox name = Text(value.Name ?? "");
        TextBox gesture = Text(value.Gesture);
        ComboBox context = Combo(value.Context);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        ComboBox action = Combo(value.Action);
        TextBox command = Text(value.Command);
        ConditionList conditions = new(this, value.Conditions);
        ActionList actions = new(this, value.Actions);
        RunScriptFunctionAutomationAction? script = value.ScriptAction;
        ContentControl scriptHost = new();

        void RenderScript()
        {
            bool scripted = (KeybindingActionKind)(action.SelectedItem ?? value.Action) == KeybindingActionKind.RunScriptFunction;
            command.IsVisible = !scripted;
            if (!scripted) { scriptHost.Content = null; return; }
            if (script is null)
            {
                Button choose = Quiet("Choose Function…");
                choose.HorizontalAlignment = HorizontalAlignment.Left;
                choose.Click += async (_, _) =>
                {
                    ScriptFunctionRef? picked = await _services.ChooseFunction(null).ConfigureAwait(true);
                    if (picked is null) return;
                    script = new RunScriptFunctionAutomationAction(picked, EmptyArguments());
                    MarkChanged();
                    RenderScript();
                };
                scriptHost.Content = choose;
                return;
            }
            scriptHost.Content = ScriptCard("Run Script Function", script.FunctionRef, script.Arguments,
                (function, arguments) => { script = script with { FunctionRef = function, Arguments = arguments }; MarkChanged(); RenderScript(); },
                arguments => { script = script with { Arguments = arguments }; MarkChanged(); }, removable: null);
        }
        action.SelectionChanged += (_, _) => { MarkChanged(); RenderScript(); };
        RenderScript();

        page.Children.Add(Section("GESTURE", Row(Labeled("Gesture (e.g. Cmd+Shift+E)", gesture), Labeled("Priority", priority))));
        page.Children.Add(Section("CONTEXT", Row(Labeled("Active in", context), Labeled("Name", name))));
        page.Children.Add(Section("WHEN", conditions.View));
        page.Children.Add(Section("DO", Row(Labeled("Application action", action)), Labeled("Command / workflow / pane", command), scriptHost, actions.View));
        _collect = () =>
        {
            KeybindingActionKind kind = (KeybindingActionKind)(action.SelectedItem ?? value.Action);
            if (kind == KeybindingActionKind.RunScriptFunction && script is null)
                throw new InvalidOperationException("Choose a script function for this keybinding.");
            return value with
            {
                Name = BlankToNull(name.Text), Gesture = gesture.Text ?? "", Context = (KeybindingContext)(context.SelectedItem ?? value.Context),
                Priority = (int)(priority.Value ?? 0), Action = kind, Command = command.Text ?? "",
                ScriptAction = kind == KeybindingActionKind.RunScriptFunction ? script : null, Enabled = IsEnabled,
                Conditions = conditions.Values, Actions = actions.Values
            };
        };
    }

    private void BuildTimer(StackPanel page, CommandTimer value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox name = Text(value.Name);
        NumericUpDown interval = Number(value.IntervalSeconds, 1, 86_400);
        CheckBox repeat = Check("Repeat", value.Repeat);
        TextBox command = Text(value.Command);
        TextBox group = Text(value.Group);
        ConditionList conditions = new(this, value.Conditions);
        ActionList actions = new(this, value.Actions);
        page.Children.Add(Section("SCHEDULE", Labeled("Name", name), Row(Labeled("Interval (seconds)", interval), Labeled("Group", group)), repeat));
        page.Children.Add(Section("WHEN", conditions.View));
        page.Children.Add(Section("DO", Labeled("Send command", command), actions.View));
        _collect = () => value with
        {
            Name = name.Text ?? "", IntervalSeconds = (int)(interval.Value ?? 1), Repeat = repeat.IsChecked ?? false,
            Command = command.Text ?? "", Group = NonBlank(group.Text, "Default"), Enabled = IsEnabled,
            Conditions = conditions.Values, Actions = actions.Values
        };
    }

    private void BuildStateRule(StackPanel page, GameRule value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox name = Text(value.Name);
        TextBox condition = Text(value.Condition);
        ComboBox activation = Combo(value.Activation);
        TextBox command = Text(value.Command);
        TextBox group = Text(value.Group);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        NumericUpDown cooldown = Number(value.CooldownMilliseconds, 0, 600_000);
        CheckBox stop = Check("Stop processing after match", value.StopProcessing);
        CheckBox oneShot = Check("One shot", value.OneShot);
        ConditionList conditions = new(this, value.Conditions);
        ActionList actions = new(this, value.Actions);
        page.Children.Add(Section("WHEN",
            Labeled("Name", name),
            Labeled("State expression (e.g. hp.percent < 30)", condition),
            Labeled("Activation", activation),
            conditions.View));
        page.Children.Add(Section("DO", Labeled("Send command", command), actions.View));
        page.Children.Add(Section("BEHAVIOR", Row(Labeled("Group", group), Labeled("Priority", priority), Labeled("Cooldown (ms)", cooldown)), stop, oneShot));
        _collect = () => value with
        {
            Name = name.Text ?? "", Condition = condition.Text ?? "", Command = command.Text ?? "", Group = NonBlank(group.Text, "Default"),
            Activation = (GameRuleActivation)(activation.SelectedItem ?? value.Activation), Priority = (int)(priority.Value ?? value.Priority),
            CooldownMilliseconds = (int)(cooldown.Value ?? value.CooldownMilliseconds), StopProcessing = stop.IsChecked ?? false,
            OneShot = oneShot.IsChecked ?? false, Enabled = IsEnabled, Conditions = conditions.Values, Actions = actions.Values
        };
    }

    private void BuildHighlight(StackPanel page, TranscriptHighlightRule value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox pattern = Text(value.Pattern);
        ComboBox mode = Combo(value.MatchMode);
        CheckBox caseSensitive = Check("Case sensitive", value.CaseSensitive);
        TextBox color = Text(value.Foreground);
        Border swatch = new() { Width = 30, Height = ControlHeight, CornerRadius = new CornerRadius(4), BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(1) };
        void UpdateSwatch() => swatch.Background = Color.TryParse(color.Text, out Color parsed) ? new SolidColorBrush(parsed) : Brushes.Transparent;
        color.TextChanged += (_, _) => UpdateSwatch();
        UpdateSwatch();
        CheckBox bold = Check("Bold", value.Bold);
        CheckBox underline = Check("Underline", value.Underline);
        TextBlock preview = new() { Text = "A goblin attacks you for 12 damage!", FontFamily = UiTheme.Mono, FontSize = 13, Foreground = UiTheme.Text };
        page.Children.Add(Section("MATCH", Row(Labeled("Match mode", mode), Labeled("Pattern", pattern)), caseSensitive));
        page.Children.Add(Section("STYLE", Row(Labeled("Colour (e.g. #F59E0B)", color), swatch), Row(bold, underline), Labeled("Preview", preview)));
        _collect = () =>
        {
            if (!Color.TryParse(color.Text, out _)) throw new InvalidOperationException("Colour must be a valid hex value such as #F59E0B.");
            return value with
            {
                Pattern = pattern.Text ?? "", Foreground = color.Text!.Trim(), MatchMode = (HighlightMatchMode)(mode.SelectedItem ?? value.MatchMode),
                CaseSensitive = caseSensitive.IsChecked ?? false, Bold = bold.IsChecked ?? false, Underline = underline.IsChecked ?? false, Enabled = IsEnabled
            };
        };
    }

    private void BuildWorkflow(StackPanel page, AutomationWorkflow value)
    {
        _enabled.IsChecked = value.Enabled;
        TextBox name = Text(value.Name);
        TextBox eventName = Text(value.TriggerEvent ?? "");
        ToolTip.SetTip(eventName, "Existing event type name; leave empty to run only on manual start.");
        TextBox triggerCondition = Text(value.TriggerCondition ?? "");
        ToolTip.SetTip(triggerCondition, "Evaluated by the existing GameRuleEvaluator. Leave empty for no condition.");
        TextBox hotkey = Text(value.Hotkey ?? "");
        Button clearHotkey = Quiet("×");
        clearHotkey.Width = 32;
        clearHotkey.Click += (_, _) => hotkey.Text = string.Empty;
        CheckBox allowManualRun = Check("Allow manual run", value.AllowManualRun);
        void UpdateRunButton()
        {
            if (_workflowRunButton is not { } runButton) return;
            runButton.IsEnabled = IsEnabled && (allowManualRun.IsChecked ?? true) && !string.IsNullOrWhiteSpace(value.Id);
            ToolTip.SetTip(runButton, runButton.IsEnabled
                ? "Save changes and run this workflow now."
                : "Enable the workflow and manual run permission to run it.");
        }
        _refreshWorkflowRunButton = UpdateRunButton;
        allowManualRun.IsCheckedChanged += (_, _) => UpdateRunButton();
        UpdateRunButton();
        CheckBox oneShot = Check("One-shot (run once)", value.OneShot);
        ToolTip.SetTip(oneShot, "Disable this workflow after it completes successfully.");
        TextBox group = Text(value.Group);
        NumericUpDown priority = Number(value.Priority, -10_000, 10_000);
        NumericUpDown cooldown = Number(value.CooldownMilliseconds, 0, 600_000);
        ComboBox failure = Combo(value.FailureMode);
        TextBox legacySteps = Text(value.Steps);
        bool legacyStepsEdited = false;
        legacySteps.TextChanged += (_, _) => legacyStepsEdited = true;
        legacySteps.AcceptsReturn = true;
        legacySteps.MinHeight = 58;
        legacySteps.FontFamily = new FontFamily("Menlo, monospace");
        ActionList actions = new(this, value.Actions);
        Button convert = Quiet("Convert supported legacy steps");
        TextBlock conversionStatus = new() { Foreground = UiTheme.Warning, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        TextBlock hotkeyStatus = new() { Foreground = UiTheme.Warning, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void ValidateHotkey()
        {
            string gesture = hotkey.Text?.Trim() ?? string.Empty;
            if (gesture.Length == 0)
            {
                hotkeyStatus.Text = "No hotkey assigned.";
                hotkeyStatus.Foreground = UiTheme.Muted;
            }
            else if (!CommandGesture.IsValid(gesture))
            {
                hotkeyStatus.Text = "Invalid global hotkey. Use a modifier or function key (for example, F1).";
                hotkeyStatus.Foreground = UiTheme.Danger;
            }
            else
            {
                bool explicitConflict = (_services.Runtime.Settings.KeyBindings ?? [])
                    .Any(binding => binding.Enabled &&
                                    binding.Gesture.Equals(gesture, StringComparison.OrdinalIgnoreCase));
                bool workflowConflict = (_services.Runtime.Settings.Workflows ?? [])
                    .Any(other => other.Enabled && other.AllowManualRun &&
                                  other.Id != value.Id && other.Hotkey?.Equals(gesture, StringComparison.OrdinalIgnoreCase) == true);
                hotkeyStatus.Text = explicitConflict || workflowConflict
                    ? "Hotkey conflict: context is checked first; highest priority wins, and equal-priority matches do not run."
                    : "Hotkey is valid.";
                hotkeyStatus.Foreground = explicitConflict || workflowConflict ? UiTheme.Danger : StudioShellChrome.Success;
            }
        }
        hotkey.TextChanged += (_, _) => ValidateHotkey();
        ValidateHotkey();
        convert.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(convert, "Only the supported legacy commands are converted; failure leaves the DSL and typed actions unchanged.");
        convert.Click += (_, _) =>
        {
            if (!LegacyWorkflowStepAdapter.TryConvert(legacySteps.Text, out IReadOnlyList<AutomationAction> converted, out string? error))
            {
                conversionStatus.Text = error;
                conversionStatus.IsVisible = true;
                return;
            }
            actions.Append(converted);
            legacySteps.Text = string.Empty;
            conversionStatus.IsVisible = false;
            MarkChanged();
        };
        page.Children.Add(Section("1  TRIGGER", new TextBlock
            {
                Text = "Choose when this workflow starts. Leave Event empty for manual-only behavior.",
                Foreground = StudioShellChrome.Secondary, TextWrapping = TextWrapping.Wrap, FontSize = 12
            },
            Row(Labeled("Event type", eventName), Labeled("Or hotkey", Row(hotkey, clearHotkey)), allowManualRun, hotkeyStatus)));
        Button always = Quiet("Always");
        always.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(always, "Clear the condition so the trigger always matches.");
        always.Click += (_, _) => triggerCondition.Text = string.Empty;
        page.Children.Add(Section("2  CONDITIONS", new TextBlock
            {
                Text = "Conditions use the existing GameRuleEvaluator expression syntax. Leave blank for Always.",
                Foreground = StudioShellChrome.Secondary, TextWrapping = TextWrapping.Wrap, FontSize = 12
            },
            Row(Labeled("Trigger condition", triggerCondition), always)));
        page.Children.Add(Section("3  STEPS", new TextBlock
            {
                Text = "Legacy DSL runs first; typed actions run afterward. These two formats are not interleaved.",
                Foreground = StudioShellChrome.Secondary, TextWrapping = TextWrapping.Wrap, FontSize = 12
            },
            Labeled("Legacy DSL (preserved verbatim)", legacySteps), convert, conversionStatus,
            new TextBlock { Text = "Typed actions", Foreground = StudioShellChrome.Foreground, FontWeight = FontWeight.SemiBold }, actions.View));
        page.Children.Add(Section("4  BEHAVIOR", Labeled("Workflow name", name), Row(
            Labeled("Group", group), Labeled("Priority", priority), Labeled("Cooldown (ms)", cooldown),
            Labeled("Failure mode", failure), oneShot)));
        _collect = () =>
        {
            if (actions.ValidationError is { } validationError)
                throw new InvalidOperationException(validationError);
            if (!string.IsNullOrWhiteSpace(hotkey.Text) && !CommandGesture.IsValid(hotkey.Text.Trim()))
                throw new InvalidOperationException("Workflow hotkey is not a valid global gesture.");
            return value with
            {
                Name = name.Text ?? "",
                TriggerEvent = BlankToNull(eventName.Text),
                TriggerCondition = BlankToNull(triggerCondition.Text),
                Group = NonBlank(group.Text, "Default"),
                Steps = legacyStepsEdited ? legacySteps.Text ?? string.Empty : value.Steps,
                Priority = (int)(priority.Value ?? value.Priority),
                CooldownMilliseconds = (int)(cooldown.Value ?? value.CooldownMilliseconds),
                FailureMode = (AutomationWorkflowFailureMode)(failure.SelectedItem ?? value.FailureMode),
                OneShot = oneShot.IsChecked ?? false,
                Enabled = IsEnabled,
                AllowManualRun = allowManualRun.IsChecked ?? true,
                Hotkey = BlankToNull(hotkey.Text),
                Actions = actions.Values
            };
        };
    }

    // ───────────────────────────── Script function card ─────────────────────────────

    /// <summary>Shared card for script actions and predicates: identity, Open Definition, Change Function, JSON arguments.</summary>
    private Control ScriptCard(
        string title,
        ScriptFunctionRef function,
        JsonElement arguments,
        Action<ScriptFunctionRef, JsonElement> changeFunction,
        Action<JsonElement> changeArguments,
        Action? removable)
    {
        ExportedScriptFunction? export = _services.ResolveExport(function);
        string signature = export is null
            ? $"{function.ExportName}(ctx)"
            : $"{export.ExportName}({string.Join(", ", export.Parameters.Select(p => p.Name))})";
        StackPanel card = new() { Spacing = 4 };
        card.Children.Add(new TextBlock { Text = title, Foreground = UiTheme.Text, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        card.Children.Add(new TextBlock { Text = $"{function.PackageId} / {function.ModulePath}", Foreground = UiTheme.Muted, FontSize = 12.5 });
        card.Children.Add(new TextBlock { Text = signature, Foreground = UiTheme.Cyan, FontFamily = UiTheme.Mono, FontSize = 12.5 });
        if (export is null)
            card.Children.Add(new TextBlock { Text = "Not found in the latest successful build.", Foreground = UiTheme.Warning, FontSize = 12 });

        TextBox args = Text(arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText());
        args.FontFamily = UiTheme.Mono;
        TextBlock argsError = new() { Foreground = UiTheme.Danger, FontSize = 12, IsVisible = false };
        args.TextChanged += (_, _) =>
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(args.Text) ? "{}" : args.Text);
                argsError.IsVisible = false;
                changeArguments(document.RootElement.Clone());
            }
            catch (JsonException exception)
            {
                argsError.Text = $"Invalid JSON: {exception.Message}";
                argsError.IsVisible = true;
            }
        };

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        Button open = Quiet("Open Definition");
        open.Click += async (_, _) => await Guard(() => _services.OpenDefinition(function)).ConfigureAwait(true);
        Button change = Quiet("Change Function");
        change.Click += async (_, _) =>
        {
            ScriptFunctionRef? picked = await Guard(() => _services.ChooseFunction(function)).ConfigureAwait(true);
            if (picked is not null) changeFunction(picked, arguments);
        };
        buttons.Children.Add(open);
        buttons.Children.Add(change);
        if (removable is not null)
        {
            Button remove = Quiet("Remove");
            remove.Click += (_, _) => removable();
            buttons.Children.Add(remove);
        }
        card.Children.Add(buttons);
        card.Children.Add(Labeled("Arguments (JSON)", args));
        card.Children.Add(argsError);
        return card;
    }

    private static async Task<T?> Guard<T>(Func<Task<T?>> action)
    {
        try { return await action().ConfigureAwait(true); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); return default; }
    }

    private static async Task Guard(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
    }

    // ───────────────────────────── Ordered list editors ─────────────────────────────

    /// <summary>Numbered vertical action list with add, remove and reorder.</summary>
    private sealed class ActionList
    {
        private readonly AutomationDocumentEditor _owner;
        private readonly List<AutomationAction> _actions;
        private readonly StackPanel _rows = new() { Spacing = 10 };

        public ActionList(AutomationDocumentEditor owner, IReadOnlyList<AutomationAction>? initial)
        {
            _owner = owner;
            _actions = [.. initial ?? []];
            Button add = Quiet("+ Add action");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Click += (_, _) => ShowAddMenu(add);
            View = new StackPanel { Spacing = 10, Children = { _rows, add } };
            Render();
        }

        public Control View { get; }
        public IReadOnlyList<AutomationAction> Values => [.. _actions];
        public string? ValidationError => _storageErrors.FirstOrDefault(error => error.IsVisible)?.Text;
        private readonly List<TextBlock> _storageErrors = [];

        public void Append(IEnumerable<AutomationAction> actions)
        {
            _actions.AddRange(actions);
            Changed();
        }

        private void ShowAddMenu(Control anchor)
        {
            ContextMenu menu = new();
            AddItem(menu, "Send Command", () => new SendCommandAutomationAction("look"));
            AddItem(menu, "Send Commands", () => new SendCommandsAutomationAction(["look", "score"]));
            AddItem(menu, "Wait", () => new DelayAutomationAction(500));
            AddItem(menu, "Log", () => new LogAutomationAction("Info", "message"));
            AddItem(menu, "Set Storage", () => new SetStorageAutomationAction("key", "value"));
            AddItem(menu, "Delete Storage", () => new DeleteStorageAutomationAction("key"));
            MenuItem script = new() { Header = "Run Script Function…" };
            script.Click += async (_, _) =>
            {
                ScriptFunctionRef? picked = await Guard(() => _owner._services.ChooseFunction(null)).ConfigureAwait(true);
                if (picked is null) return;
                _actions.Add(new RunScriptFunctionAutomationAction(picked, EmptyArguments()));
                Changed();
            };
            menu.Items.Add(script);
            menu.Open(anchor);
        }

        private void AddItem(ContextMenu menu, string header, Func<AutomationAction> create)
        {
            MenuItem item = new() { Header = header };
            item.Click += (_, _) => { _actions.Add(create()); Changed(); };
            menu.Items.Add(item);
        }

        private void Changed() { _owner.MarkChanged(); Render(); }

        private void Render()
        {
            _rows.Children.Clear();
            _storageErrors.Clear();
            if (_actions.Count == 0)
                _rows.Children.Add(new TextBlock { Text = "No actions", Foreground = UiTheme.Faint });
            for (int i = 0; i < _actions.Count; i++)
                _rows.Children.Add(Row(i));
        }

        private Control Row(int i)
        {
            AutomationAction action = _actions[i];
            StackPanel body = new() { Spacing = 4 };
            switch (action)
            {
                case SendCommandAutomationAction send:
                    body.Children.Add(Heading($"{i + 1}. Send Command"));
                    body.Children.Add(Bound(send.Command, text => _actions[i] = send with { Command = text }));
                    break;
                case SendCommandsAutomationAction many:
                    body.Children.Add(Heading($"{i + 1}. Send Commands (one per line)"));
                    TextBox lines = Bound(string.Join('\n', many.Commands), text =>
                        _actions[i] = many with { Commands = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
                    lines.AcceptsReturn = true;
                    lines.MinHeight = 60;
                    body.Children.Add(lines);
                    break;
                case DelayAutomationAction delay:
                    body.Children.Add(Heading($"{i + 1}. Wait (ms)"));
                    NumericUpDown ms = _owner.Number(delay.Milliseconds, 0, 600_000);
                    ms.ValueChanged += (_, _) => { _actions[i] = delay with { Milliseconds = (int)(ms.Value ?? 0) }; _owner.MarkChanged(); };
                    body.Children.Add(ms);
                    break;
                case LogAutomationAction log:
                    body.Children.Add(Heading($"{i + 1}. Log"));
                    body.Children.Add(Bound(log.Level, text => _actions[i] = log with { Level = text }));
                    body.Children.Add(Bound(log.Message, text => _actions[i] = log with { Message = text }));
                    break;
                case SetStorageAutomationAction set:
                    body.Children.Add(Heading($"{i + 1}. Set Storage"));
                    body.Children.Add(Bound(set.Key, text => _actions[i] = set with { Key = text }));
                    TextBlock storageError = new() { Text = "Enter a valid JSON value.", Foreground = UiTheme.Danger, FontSize = 12, IsVisible = false };
                    _storageErrors.Add(storageError);
                    TextBox storageValue = Bound(JsonSerializer.Serialize(set.Value), text =>
                    {
                        try
                        {
                            using JsonDocument parsed = JsonDocument.Parse(text);
                            _actions[i] = set with { Value = parsed.RootElement.Clone() };
                            storageError.IsVisible = false;
                        }
                        catch (JsonException) { storageError.IsVisible = true; }
                    });
                    body.Children.Add(Labeled("Value (JSON)", storageValue));
                    body.Children.Add(storageError);
                    break;
                case DeleteStorageAutomationAction delete:
                    body.Children.Add(Heading($"{i + 1}. Delete Storage"));
                    body.Children.Add(Bound(delete.Key, text => _actions[i] = delete with { Key = text }));
                    break;
                case RunScriptFunctionAutomationAction script:
                    body.Children.Add(_owner.ScriptCard($"{i + 1}. Run Script Function", script.FunctionRef, script.Arguments,
                        (function, arguments) => { _actions[i] = script with { FunctionRef = function, Arguments = arguments }; Changed(); },
                        arguments => { _actions[i] = script with { Arguments = arguments }; _owner.MarkChanged(); },
                        removable: null));
                    break;
            }
            return Frame(body, i, _actions.Count, Move, Remove, Duplicate, _owner._kind == StudioDocumentKind.Workflow);
        }

        private TextBox Bound(string initial, Action<string> update)
        {
            TextBox box = _owner.Text(initial);
            box.TextChanged += (_, _) => { update(box.Text ?? ""); _owner.MarkChanged(); };
            return box;
        }

        private void Move(int from, int delta)
        {
            int to = from + delta;
            if (to < 0 || to >= _actions.Count) return;
            (_actions[from], _actions[to]) = (_actions[to], _actions[from]);
            Changed();
        }

        private void Remove(int index) { _actions.RemoveAt(index); Changed(); }

        private void Duplicate(int index) { _actions.Insert(index + 1, _actions[index]); Changed(); }
    }

    /// <summary>Condition list. Editable: state expressions and script predicates; other shapes are shown and removable.</summary>
    private sealed class ConditionList
    {
        private readonly AutomationDocumentEditor _owner;
        private readonly List<AutomationCondition> _conditions;
        private readonly StackPanel _rows = new() { Spacing = 10 };

        public ConditionList(AutomationDocumentEditor owner, IReadOnlyList<AutomationCondition>? initial)
        {
            _owner = owner;
            _conditions = [.. initial ?? []];
            Button add = Quiet("+ Add condition");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Click += (_, _) => ShowAddMenu(add);
            View = new StackPanel { Spacing = 10, Children = { _rows, add } };
            Render();
        }

        public Control View { get; }
        public IReadOnlyList<AutomationCondition>? Values => _conditions.Count == 0 ? null : [.. _conditions];

        private void ShowAddMenu(Control anchor)
        {
            ContextMenu menu = new();
            MenuItem expression = new() { Header = "State expression" };
            expression.Click += (_, _) => { _conditions.Add(new StateExpressionAutomationCondition("hp.percent < 50")); Changed(); };
            MenuItem predicate = new() { Header = "Script predicate…" };
            predicate.Click += async (_, _) =>
            {
                ScriptFunctionRef? picked = await Guard(() => _owner._services.ChooseFunction(null)).ConfigureAwait(true);
                if (picked is null) return;
                _conditions.Add(new ScriptPredicateAutomationCondition(picked, EmptyArguments()));
                Changed();
            };
            menu.Items.Add(expression);
            menu.Items.Add(predicate);
            menu.Open(anchor);
        }

        private void Changed() { _owner.MarkChanged(); Render(); }

        private void Render()
        {
            _rows.Children.Clear();
            if (_conditions.Count == 0)
                _rows.Children.Add(new TextBlock { Text = "No conditions", Foreground = UiTheme.Faint });
            for (int i = 0; i < _conditions.Count; i++)
                _rows.Children.Add(Row(i));
        }

        private Control Row(int i)
        {
            AutomationCondition condition = _conditions[i];
            StackPanel body = new() { Spacing = 4 };
            switch (condition)
            {
                case StateExpressionAutomationCondition expression:
                    body.Children.Add(Heading($"{i + 1}. State expression"));
                    TextBox box = _owner.Text(expression.Expression);
                    box.TextChanged += (_, _) => { _conditions[i] = expression with { Expression = box.Text ?? "" }; _owner.MarkChanged(); };
                    body.Children.Add(box);
                    break;
                case ScriptPredicateAutomationCondition predicate:
                    body.Children.Add(_owner.ScriptCard($"{i + 1}. Script predicate", predicate.FunctionRef, predicate.Arguments,
                        (function, arguments) => { _conditions[i] = predicate with { FunctionRef = function, Arguments = arguments }; Changed(); },
                        arguments => { _conditions[i] = predicate with { Arguments = arguments }; _owner.MarkChanged(); },
                        removable: null));
                    break;
                default:
                    body.Children.Add(Heading($"{i + 1}. Condition"));
                    body.Children.Add(new TextBlock { Text = Describe(condition), Foreground = UiTheme.Muted, TextWrapping = TextWrapping.Wrap });
                    break;
            }
            return Frame(body, i, _conditions.Count, Move, Remove);
        }

        private void Move(int from, int delta)
        {
            int to = from + delta;
            if (to < 0 || to >= _conditions.Count) return;
            (_conditions[from], _conditions[to]) = (_conditions[to], _conditions[from]);
            Changed();
        }

        private void Remove(int index) { _conditions.RemoveAt(index); Changed(); }

        private static string Describe(AutomationCondition condition) => condition switch
        {
            RegexAutomationCondition r => $"{r.Field} matches /{r.Pattern}/",
            ContainsAutomationCondition c => $"{c.Field} contains \"{c.Value}\"",
            EventFieldComparisonAutomationCondition e => $"{e.Field} {e.Operator} {e.Value}",
            StorageValueAutomationCondition s => $"storage[{s.Key}] {s.Operator} {s.Value}",
            AllAutomationCondition all => $"All of {all.Conditions.Count} conditions",
            AnyAutomationCondition any => $"Any of {any.Conditions.Count} conditions",
            NotAutomationCondition => "Not (nested condition)",
            _ => condition.GetType().Name
        };
    }

    // ───────────────────────────── Layout and control helpers ─────────────────────────────

    private static Control Frame(Control body, int index, int count, Action<int, int> move, Action<int> remove, Action<int>? duplicate = null, bool explainBoundaryMoves = false)
    {
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(Tool("↑", index > 0 ? "Move up" : explainBoundaryMoves ? "Already first; cannot move up" : "Move up", index > 0, () => move(index, -1)));
        tools.Children.Add(Tool("↓", index < count - 1 ? "Move down" : explainBoundaryMoves ? "Already last; cannot move down" : "Move down", index < count - 1, () => move(index, 1)));
        if (duplicate is not null) tools.Children.Add(Tool("⧉", "Duplicate", true, () => duplicate(index)));
        tools.Children.Add(Tool("×", "Remove", true, () => remove(index)));
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(body);
        Grid.SetColumn(tools, 1);
        grid.Children.Add(tools);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(0, 0, 0, 10),
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    private static Button Tool(string glyph, string tip, bool enabled, Action click)
    {
        Button button = new()
        {
            Content = glyph, MinWidth = 26, MinHeight = 26, Padding = new Thickness(4, 0), IsEnabled = enabled,
            Background = Brushes.Transparent, Foreground = UiTheme.Muted, BorderThickness = new Thickness(0), FontSize = 14
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private Control Section(string title, params Control[] content)
    {
        StackPanel section = new() { Spacing = 10 };
        section.Children.Add(new TextBlock { Text = title, Foreground = UiTheme.Accent, FontSize = 11.5, FontWeight = FontWeight.SemiBold });
        foreach (Control control in content) section.Children.Add(control);
        if (_kind == StudioDocumentKind.Workflow)
        {
            return new Border
            {
                Background = StudioShellChrome.Card,
                BorderBrush = StudioShellChrome.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 8, 0, 0),
                Child = section
            };
        }
        return new Border
        {
            Background = StudioShellChrome.Card,
            BorderBrush = StudioShellChrome.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 14, 0, 0),
            Child = section
        };
    }

    private static Control Labeled(string label, Control control)
    {
        StackPanel panel = new() { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.Muted, FontSize = 12 });
        panel.Children.Add(control);
        return panel;
    }

    private static Control Row(params Control[] controls)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (Control control in controls) row.Children.Add(control);
        return row;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, Foreground = UiTheme.Text, FontWeight = FontWeight.SemiBold, FontSize = 13 };

    private TextBox Text(string value) => TextField(value, this);

    private static TextBox TextField(string value, AutomationDocumentEditor? owner)
    {
        TextBox box = UiTheme.FieldBox();
        box.MinHeight = ControlHeight;
        box.FontSize = 13;
        box.Text = value;
        if (owner is not null) box.TextChanged += (_, _) => owner.MarkChanged();
        return box;
    }

    private CheckBox Check(string label, bool value)
    {
        CheckBox box = new() { Content = label, IsChecked = value, Foreground = UiTheme.Text, FontSize = 13 };
        box.IsCheckedChanged += (_, _) => MarkChanged();
        return box;
    }

    private ComboBox Combo<T>(T selected) where T : struct, Enum
    {
        ComboBox box = new() { ItemsSource = Enum.GetValues<T>(), SelectedItem = selected, MinWidth = 180, MinHeight = ControlHeight };
        box.SelectionChanged += (_, _) => MarkChanged();
        return box;
    }

    private NumericUpDown Number(decimal value, decimal min, decimal max)
    {
        NumericUpDown box = new() { Value = value, Minimum = min, Maximum = max, Increment = 1, Width = 140, MinHeight = ControlHeight, HorizontalAlignment = HorizontalAlignment.Left };
        box.ValueChanged += (_, _) => MarkChanged();
        return box;
    }

    private static Button Quiet(string label)
    {
        Button button = UiTheme.QuietButton(label);
        button.MinHeight = ControlHeight;
        button.FontSize = 12.5;
        return button;
    }

    private static string NonBlank(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static JsonElement EmptyArguments() { using JsonDocument document = JsonDocument.Parse("{}"); return document.RootElement.Clone(); }
}

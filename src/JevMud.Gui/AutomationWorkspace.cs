using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JevMud.Client.Runtime;
using JevMud.Client.Scripting;
using JevMud.Client.Settings;
using JevMud.Contracts.Events;

namespace JevMud.Gui;

/// <summary>
/// User-facing control surface for deterministic automation. The execution engine calls these
/// workflows internally; the UI intentionally presents them as automations described in terms of
/// when they start and what Jev will do.
/// </summary>
internal sealed class AutomationWorkspace : UserControl
{
    private const int MaxHistory = 250;
    private readonly JevMudRuntime _runtime;
    private readonly Func<Task> _showSettings;
    private readonly ObservableCollection<AutomationListItem> _automations = [];
    private readonly ObservableCollection<string> _history = [];
    private readonly ListBox _automationList = new();
    private readonly ListBox _historyList = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _status = new();
    private readonly ContentControl _detail = new();
    private bool _active;

    public AutomationWorkspace(JevMudRuntime runtime, Func<Task> showSettings)
    {
        _runtime = runtime;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;
        _showSettings = showSettings;
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
            mudEvent is not ScriptLogEmitted { ModuleId: "automation.profile" })
        {
            return;
        }

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

            if (_active || mudEvent is AutomationWorkflowStateChanged)
            {
                RefreshSnapshot();
            }
        });
    }

    public void RefreshSnapshot()
    {
        string? selectedName = (_automationList.SelectedItem as AutomationListItem)?.Workflow.Name;
        AutomationPreferences preferences = _runtime.Settings.Automation ?? new AutomationPreferences();
        IReadOnlySet<string> disabledGroups = preferences.GetDisabledGroupSet();
        IReadOnlySet<string> activeNames = new HashSet<string>(
            _runtime.Automation.ActiveWorkflowNames,
            StringComparer.OrdinalIgnoreCase);

        AutomationListItem[] items = (_runtime.Settings.Workflows ?? Array.Empty<AutomationWorkflow>())
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(workflow => CreateListItem(workflow, preferences.Enabled, disabledGroups, activeNames))
            .ToArray();

        _automations.Clear();
        foreach (AutomationListItem item in items) _automations.Add(item);

        AutomationListItem? selected = selectedName is null
            ? _automations.FirstOrDefault()
            : _automations.FirstOrDefault(item => item.Workflow.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
              ?? _automations.FirstOrDefault();
        _automationList.SelectedItem = selected;

        int enabledCount = items.Count(item => item.Availability is AutomationAvailability.Ready or AutomationAvailability.Running);
        int runningCount = items.Count(item => item.Availability == AutomationAvailability.Running);
        int triggerCount = (_runtime.Settings.Triggers ?? Array.Empty<TriggerRule>()).Count(rule => rule.Enabled);
        int ruleCount = (_runtime.Settings.GameRules ?? Array.Empty<GameRule>()).Count(rule => rule.Enabled);
        int timerCount = (_runtime.Settings.Timers ?? Array.Empty<CommandTimer>()).Count(rule => rule.Enabled);

        _summary.Text = preferences.Enabled
            ? $"Jev automation is ON  ·  {enabledCount} automations available  ·  {runningCount} running"
            : "Jev automation is OFF. Nothing on this page will run until automation is enabled.";
        _summary.Foreground = preferences.Enabled ? UiTheme.Success : UiTheme.Warning;

        _status.Text = triggerCount + ruleCount + timerCount > 0
            ? $"Also active: {triggerCount} text triggers · {ruleCount} state rules · {timerCount} timers"
            : string.Empty;

        UpdateDetail();
    }

    private Control Build()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,180"),
            Margin = new Thickness(UiTheme.SpaceMd)
        };

        StackPanel header = new() { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = "AUTOMATIONS",
            Foreground = UiTheme.Accent,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.Bold
        });
        header.Children.Add(new TextBlock
        {
            Text = "Choose a behavior to see when it runs and exactly what Jev will do.",
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Body
        });
        _summary.FontSize = NexTypography.Body;
        _summary.Foreground = UiTheme.Muted;
        header.Children.Add(_summary);
        _status.FontSize = NexTypography.Metadata;
        _status.Foreground = UiTheme.Muted;
        header.Children.Add(_status);
        root.Children.Add(header);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 10)
        };
        Button stopAll = ActionButton("Stop all running");
        stopAll.Click += (_, _) =>
        {
            _runtime.Automation.CancelAllWorkflows();
            _status.Text = "Stopping all running automations…";
            RefreshSnapshot();
        };
        actions.Children.Add(stopAll);
        Button refresh = ActionButton("Refresh");
        refresh.Click += (_, _) => RefreshSnapshot();
        actions.Children.Add(refresh);
        Button settings = ActionButton("Automation settings");
        settings.Click += (_, _) => RunUiTaskAsync(_showSettings, "Unable to open automation settings");
        actions.Children.Add(settings);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);

        Grid body = new()
        {
            ColumnDefinitions = new ColumnDefinitions("1.1*,1.7*"),
            ColumnSpacing = 8
        };

        _automationList.ItemsSource = _automations;
        _automationList.Background = UiTheme.Console;
        _automationList.BorderThickness = new Thickness(0);
        _automationList.ItemTemplate = new FuncDataTemplate<AutomationListItem>((item, _) => BuildAutomationRow(item), true);
        _automationList.SelectionChanged += (_, _) => UpdateDetail();
        body.Children.Add(Panel("YOUR AUTOMATIONS", _automationList));

        Border detailPanel = Panel("WHAT THIS AUTOMATION DOES", _detail);
        Grid.SetColumn(detailPanel, 1);
        body.Children.Add(detailPanel);
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        ConfigureHistoryList();
        Border history = Panel("RECENT AUTOMATION ACTIVITY", _historyList);
        history.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(history, 3);
        root.Children.Add(history);
        return root;
    }

    private Control BuildAutomationRow(AutomationListItem? item)
    {
        if (item is null) return new TextBlock();

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 7)
        };
        StackPanel identity = new() { Spacing = 2 };
        identity.Children.Add(new TextBlock
        {
            Text = item.Workflow.Name,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Body,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        identity.Children.Add(new TextBlock
        {
            Text = item.StartsWhen,
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        row.Children.Add(identity);

        Border state = new()
        {
            Background = Brushes.Transparent,
            BorderBrush = item.Availability switch
            {
                AutomationAvailability.Running => UiTheme.Success,
                AutomationAvailability.Ready => UiTheme.Accent,
                _ => UiTheme.Divider
            },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = item.StatusLabel,
                Foreground = item.Availability switch
                {
                    AutomationAvailability.Running => UiTheme.Success,
                    AutomationAvailability.Ready => UiTheme.Text,
                    _ => UiTheme.Muted
                },
                FontSize = NexTypography.Metadata,
                FontWeight = FontWeight.Bold
            }
        };
        Grid.SetColumn(state, 1);
        row.Children.Add(state);
        return row;
    }

    private void UpdateDetail()
    {
        if (_automationList.SelectedItem is not AutomationListItem item)
        {
            _detail.Content = new TextBlock
            {
                Text = _automations.Count == 0
                    ? "No automations are configured yet. Use Automation settings to create one."
                    : "Select an automation to inspect it.",
                Foreground = UiTheme.Muted,
                Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap
            };
            return;
        }

        AutomationWorkflow workflow = item.Workflow;
        StackPanel content = new() { Spacing = 12, Margin = new Thickness(12) };

        Grid titleRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        StackPanel title = new() { Spacing = 2 };
        title.Children.Add(new TextBlock
        {
            Text = workflow.Name,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        title.Children.Add(new TextBlock
        {
            Text = item.StatusExplanation,
            Foreground = item.Availability == AutomationAvailability.Disabled ? UiTheme.Warning : UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        titleRow.Children.Add(title);

        StackPanel controls = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
        Button run = ActionButton("Run now");
        run.IsEnabled = item.Availability == AutomationAvailability.Ready;
        run.Click += (_, _) => RunUiTaskAsync(() => RunAutomationAsync(workflow.Name), $"Unable to start {workflow.Name}");
        controls.Children.Add(run);
        Button stop = ActionButton("Stop");
        stop.IsEnabled = item.Availability == AutomationAvailability.Running;
        stop.Click += (_, _) => StopAutomation(workflow.Name);
        controls.Children.Add(stop);
        Grid.SetColumn(controls, 1);
        titleRow.Children.Add(controls);
        content.Children.Add(titleRow);

        content.Children.Add(ExplanationBlock("STARTS WHEN", item.StartsWhen));

        StackPanel steps = new() { Spacing = 5 };
        steps.Children.Add(SectionHeading("JEV WILL"));
        string[] descriptions = DescribeSteps(workflow.Steps);
        if (descriptions.Length == 0)
        {
            steps.Children.Add(new TextBlock
            {
                Text = "No actions are configured.",
                Foreground = UiTheme.Muted,
                FontSize = NexTypography.Body
            });
        }
        else
        {
            for (int index = 0; index < descriptions.Length; index++)
            {
                Grid step = new() { ColumnDefinitions = new ColumnDefinitions("24,*"), ColumnSpacing = 4 };
                step.Children.Add(new TextBlock
                {
                    Text = (index + 1).ToString(),
                    Foreground = UiTheme.Faint,
                    FontSize = NexTypography.Metadata,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                TextBlock description = new()
                {
                    Text = descriptions[index],
                    Foreground = UiTheme.Text,
                    FontSize = NexTypography.Body,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(description, 1);
                step.Children.Add(description);
                steps.Children.Add(step);
            }
        }
        content.Children.Add(steps);

        string behavior = workflow.FailureMode == AutomationWorkflowFailureMode.Stop
            ? "Stops if any step fails."
            : "Continues to the next step if a step fails.";
        if (workflow.OneShot) behavior += " Runs only once per session.";
        if (workflow.CooldownMilliseconds > 0)
        {
            behavior += $" Can start at most once every {FormatDuration(workflow.CooldownMilliseconds)}.";
        }
        content.Children.Add(ExplanationBlock("SAFETY & REPEAT BEHAVIOR", behavior));

        _detail.Content = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private async Task RunAutomationAsync(string name)
    {
        bool started = await _runtime.Automation.RunWorkflowAsync(name).ConfigureAwait(true);
        _status.Text = started ? $"Started {name}." : $"{name} could not start in the current state.";
        RefreshSnapshot();
    }

    private void StopAutomation(string name)
    {
        bool cancelled = _runtime.Automation.CancelWorkflow(name);
        _status.Text = cancelled ? $"Stopping {name}…" : $"{name} is not running.";
        RefreshSnapshot();
    }

    private void RunUiTaskAsync(Func<Task> action, string failurePrefix)
    {
        _ = RunUiTaskCoreAsync(action, failurePrefix);
    }

    private async Task RunUiTaskCoreAsync(Func<Task> action, string failurePrefix)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _status.Text = $"{failurePrefix}: {exception.Message}";
        }
    }

    private static AutomationListItem CreateListItem(
        AutomationWorkflow workflow,
        bool automationEnabled,
        IReadOnlySet<string> disabledGroups,
        IReadOnlySet<string> activeNames)
    {
        if (activeNames.Contains(workflow.Name))
        {
            return new AutomationListItem(
                workflow,
                AutomationAvailability.Running,
                "RUNNING",
                DescribeStart(workflow),
                "Jev is currently executing this automation.");
        }

        if (!automationEnabled)
        {
            return new AutomationListItem(
                workflow,
                AutomationAvailability.Disabled,
                "OFF",
                DescribeStart(workflow),
                "Automation is globally disabled in Settings.");
        }

        if (!workflow.Enabled)
        {
            return new AutomationListItem(
                workflow,
                AutomationAvailability.Disabled,
                "OFF",
                DescribeStart(workflow),
                "This automation is disabled.");
        }

        if (disabledGroups.Contains(workflow.Group))
        {
            return new AutomationListItem(
                workflow,
                AutomationAvailability.Disabled,
                "OFF",
                DescribeStart(workflow),
                $"The {workflow.Group} automation group is disabled.");
        }

        return new AutomationListItem(
            workflow,
            AutomationAvailability.Ready,
            "READY",
            DescribeStart(workflow),
            "Ready to run when its start condition occurs, or you can run it now.");
    }

    private static string DescribeStart(AutomationWorkflow workflow)
    {
        List<string> conditions = [];
        if (!string.IsNullOrWhiteSpace(workflow.TriggerEvent) && workflow.TriggerEvent != "*")
        {
            conditions.Add($"the game reports {HumanizeIdentifier(workflow.TriggerEvent!)}");
        }
        else if (workflow.TriggerEvent == "*")
        {
            conditions.Add("a game event occurs");
        }

        if (!string.IsNullOrWhiteSpace(workflow.TriggerCondition))
        {
            conditions.Add($"game state matches “{workflow.TriggerCondition}”");
        }

        if (conditions.Count == 0) return "Only when you choose Run now";
        return "Automatically when " + string.Join(" and ", conditions);
    }

    private static string[] DescribeSteps(string steps) => steps
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(step => step.Length > 0 && !step.StartsWith('#'))
        .Select(DescribeStep)
        .ToArray();

    private static string DescribeStep(string step)
    {
        if (TryPrefix(step, "send ", out string value)) return $"Send “{value}” to the game";
        if (TryPrefix(step, "delay ", out value) && int.TryParse(value, out int milliseconds)) return $"Wait {FormatDuration(milliseconds)}";
        if (TryPrefix(step, "wait-event ", out value)) return $"Wait for {HumanizeIdentifier(StripOptions(value))}";
        if (TryPrefix(step, "wait ", out value)) return $"Wait until {StripOptions(value)}";
        if (TryPrefix(step, "navigate ", out value)) return $"Travel to the nearest known match for “{value}”";
        if (TryPrefix(step, "assert ", out value)) return $"Confirm {value}; stop if it is not true";
        if (TryPrefix(step, "set ", out value)) return $"Remember {value}";
        if (TryPrefix(step, "unset ", out value)) return $"Forget the saved value “{value}”";
        if (TryPrefix(step, "jev ", out value)) return $"Let Jev handle {value}";
        if (step.Equals("stop", StringComparison.OrdinalIgnoreCase)) return "Finish this automation";

        if (TryConditional(step, "if ", out string condition, out string nested))
            return $"If {condition}, {LowercaseFirst(DescribeStep(nested))}";
        if (TryConditional(step, "unless ", out condition, out nested))
            return $"Unless {condition}, {LowercaseFirst(DescribeStep(nested))}";
        if (TryConditional(step, "retry ", out condition, out nested))
            return $"Retry ({condition}): {LowercaseFirst(DescribeStep(nested))}";

        return step;
    }

    private static bool TryPrefix(string input, string prefix, out string value)
    {
        if (input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = input[prefix.Length..].Trim();
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryConditional(string input, string prefix, out string condition, out string nested)
    {
        condition = string.Empty;
        nested = string.Empty;
        if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        int separator = input.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0) return false;
        condition = input[prefix.Length..separator].Trim();
        nested = input[(separator + 2)..].Trim();
        return condition.Length > 0 && nested.Length > 0;
    }

    private static string StripOptions(string input)
    {
        int timeout = input.IndexOf(" timeout=", StringComparison.OrdinalIgnoreCase);
        return timeout < 0 ? input.Trim() : input[..timeout].Trim();
    }

    private static string LowercaseFirst(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0])) return value;
        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string HumanizeIdentifier(string value)
    {
        StringBuilder result = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 && char.IsUpper(current) && (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])))
            {
                result.Append(' ');
            }
            result.Append(current == '_' ? ' ' : char.ToLowerInvariant(current));
        }
        return result.ToString().Trim();
    }

    private static string FormatDuration(int milliseconds)
    {
        if (milliseconds < 1000) return $"{milliseconds} ms";
        double seconds = milliseconds / 1000d;
        if (seconds < 60) return seconds % 1 == 0 ? $"{seconds:0} seconds" : $"{seconds:0.#} seconds";
        double minutes = seconds / 60d;
        return minutes % 1 == 0 ? $"{minutes:0} minutes" : $"{minutes:0.#} minutes";
    }

    private static string? DescribeActivity(IMudEvent mudEvent) => mudEvent switch
    {
        AutomationRuleMatched matched => string.IsNullOrWhiteSpace(matched.Command)
            ? $"{matched.RuleName} matched"
            : $"{matched.RuleName} matched → sent “{matched.Command}”",
        ScriptLogEmitted { ModuleId: "automation.profile" } log => log.Message,
        AutomationWorkflowStateChanged changed => changed.Status switch
        {
            AutomationWorkflowStatus.Started => $"{changed.WorkflowName} started",
            AutomationWorkflowStatus.Waiting => $"{changed.WorkflowName} is waiting" + DetailSuffix(changed.Detail),
            AutomationWorkflowStatus.Running => $"{changed.WorkflowName}: step {Math.Min(changed.StepIndex + 1, changed.TotalSteps)}/{changed.TotalSteps}" + DetailSuffix(changed.Detail),
            AutomationWorkflowStatus.Completed => $"{changed.WorkflowName} finished",
            AutomationWorkflowStatus.Cancelled => $"{changed.WorkflowName} stopped",
            AutomationWorkflowStatus.Failed => $"{changed.WorkflowName} failed" + DetailSuffix(changed.Detail),
            _ => $"{changed.WorkflowName}: {changed.Status}"
        },
        _ => null
    };

    private static string DetailSuffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" · {detail}";

    private static Control ExplanationBlock(string title, string text)
    {
        StackPanel block = new() { Spacing = 4 };
        block.Children.Add(SectionHeading(title));
        block.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap
        });
        return block;
    }

    private static TextBlock SectionHeading(string title) => new()
    {
        Text = title,
        Foreground = UiTheme.Muted,
        FontSize = NexTypography.Metadata,
        FontWeight = FontWeight.Bold
    };

    private void ConfigureHistoryList()
    {
        _historyList.ItemsSource = _history;
        _historyList.Background = UiTheme.Console;
        _historyList.BorderThickness = new Thickness(0);
        _historyList.ItemTemplate = new FuncDataTemplate<string>((item, _) => new TextBlock
        {
            Text = item,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 3)
        }, true);
    }

    private static Border Panel(string title, Control content)
    {
        Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(8, 7, 8, 6)
        });
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return new Border
        {
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(1),
            Background = UiTheme.Surface,
            Child = grid
        };
    }

    private static Button ActionButton(string text) => new()
    {
        Content = text,
        MinHeight = 28,
        Padding = new Thickness(9, 4),
        CornerRadius = new CornerRadius(1),
        Background = Brushes.Transparent,
        Foreground = UiTheme.Text,
        BorderBrush = UiTheme.Divider,
        BorderThickness = new Thickness(1),
        FontSize = NexTypography.Body
    };

    private enum AutomationAvailability
    {
        Ready,
        Running,
        Disabled
    }

    private sealed record AutomationListItem(
        AutomationWorkflow Workflow,
        AutomationAvailability Availability,
        string StatusLabel,
        string StartsWhen,
        string StatusExplanation);
}

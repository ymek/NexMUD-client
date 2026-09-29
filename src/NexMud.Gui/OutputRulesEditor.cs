using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Settings;

namespace NexMud.Gui;

/// <summary>Presentation-only transcript rule editor. Semantic source text is never exposed here.</summary>
internal sealed class OutputRulesEditor : UserControl
{
    private sealed class HighlightDraft
    {
        public required TextBox Pattern { get; init; }
        public required TextBox Foreground { get; init; }
        public required ComboBox MatchMode { get; init; }
        public required CheckBox Bold { get; init; }
        public required CheckBox Underline { get; init; }
        public required CheckBox CaseSensitive { get; init; }
        public required CheckBox Enabled { get; init; }
        public required Border Container { get; init; }
    }

    private sealed class TransformDraft
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

    private readonly StackPanel _highlightRows = new() { Spacing = 0 };
    private readonly StackPanel _transformRows = new() { Spacing = 0 };
    private readonly List<HighlightDraft> _highlights = [];
    private readonly List<TransformDraft> _transforms = [];

    public OutputRulesEditor(
        IReadOnlyList<TranscriptHighlightRule>? highlights,
        IReadOnlyList<OutputTransformationRule>? transforms)
    {
        FontFamily = UiTheme.Sans;
        StackPanel root = new() { Spacing = NexSpacing.Section };
        root.Children.Add(BuildHighlightsSection());
        root.Children.Add(BuildTransformsSection());
        Content = root;

        foreach (TranscriptHighlightRule rule in highlights ?? Array.Empty<TranscriptHighlightRule>()) AddHighlight(rule);
        foreach (OutputTransformationRule rule in transforms ?? Array.Empty<OutputTransformationRule>()) AddTransform(rule);
    }

    public bool TryRead(
        out IReadOnlyList<TranscriptHighlightRule> highlights,
        out IReadOnlyList<OutputTransformationRule> transforms,
        out string? validation)
    {
        validation = null;
        List<TranscriptHighlightRule> highlightResults = [];
        foreach (HighlightDraft draft in _highlights)
        {
            string pattern = (draft.Pattern.Text ?? string.Empty).Trim();
            string color = (draft.Foreground.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (pattern.Length == 0)
            {
                validation = "Each highlight needs a pattern, or remove the empty highlight.";
                highlights = [];
                transforms = [];
                return false;
            }
            if (!IsHexColor(color))
            {
                validation = $"Highlight color '{color}' must be #RRGGBB.";
                highlights = [];
                transforms = [];
                return false;
            }

            HighlightMatchMode mode = draft.MatchMode.SelectedItem is HighlightMatchMode selected
                ? selected
                : HighlightMatchMode.Literal;
            if (mode == HighlightMatchMode.Regex && !TryValidateRegex(pattern, draft.CaseSensitive.IsChecked == true, out validation))
            {
                highlights = [];
                transforms = [];
                return false;
            }

            highlightResults.Add(new TranscriptHighlightRule(
                pattern,
                color,
                mode,
                draft.Bold.IsChecked == true,
                draft.Underline.IsChecked == true,
                draft.CaseSensitive.IsChecked == true,
                draft.Enabled.IsChecked == true));
        }

        List<OutputTransformationRule> transformResults = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (TransformDraft draft in _transforms)
        {
            string name = (draft.Name.Text ?? string.Empty).Trim();
            string pattern = (draft.Pattern.Text ?? string.Empty).Trim();
            if (name.Length == 0 || pattern.Length == 0)
            {
                validation = "Each output transformation needs a name and pattern.";
                highlights = [];
                transforms = [];
                return false;
            }
            if (!int.TryParse(draft.Priority.Text, out int priority) || priority is < -10_000 or > 10_000)
            {
                validation = $"Output transformation '{name}' has an invalid priority.";
                highlights = [];
                transforms = [];
                return false;
            }

            OutputRuleMatchType matchType = draft.MatchType.SelectedItem is OutputRuleMatchType selectedMatch
                ? selectedMatch
                : OutputRuleMatchType.Substring;
            if (matchType == OutputRuleMatchType.Regex && !TryValidateRegex(pattern, draft.CaseSensitive.IsChecked == true, out validation))
            {
                highlights = [];
                transforms = [];
                return false;
            }

            OutputRuleActionKind actionKind = draft.Action.SelectedItem is OutputRuleActionKind selectedAction
                ? selectedAction
                : OutputRuleActionKind.Gag;
            string value = (draft.Value.Text ?? string.Empty).Trim();
            OutputRuleAction action;
            if (actionKind == OutputRuleActionKind.Highlight)
            {
                if (!IsHexColor(value))
                {
                    validation = $"Output highlight color '{value}' must be #RRGGBB.";
                    highlights = [];
                    transforms = [];
                    return false;
                }
                action = new OutputRuleAction(actionKind, Foreground: value.ToUpperInvariant(), Bold: true);
            }
            else
            {
                action = new OutputRuleAction(actionKind, Text: value);
            }

            string id = ids.Add(draft.Id) ? draft.Id : Guid.NewGuid().ToString("N");
            transformResults.Add(new OutputTransformationRule(
                id,
                name,
                pattern,
                matchType,
                draft.CaseSensitive.IsChecked == true,
                priority,
                draft.Enabled.IsChecked == true,
                [action, .. draft.PreservedActions]));
        }

        highlights = highlightResults;
        transforms = transformResults;
        return true;
    }

    private Control BuildHighlightsSection()
    {
        StackPanel body = Section("Highlights", "Literal/regex foreground emphasis. These rules affect presentation only.");
        body.Children.Add(_highlightRows);
        Button add = QuietButton("Add highlight");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => AddHighlight(new TranscriptHighlightRule("", "#FFD166"));
        body.Children.Add(add);
        return body;
    }

    private Control BuildTransformsSection()
    {
        StackPanel body = Section("Transformations", "Gag/hide and substitutions operate only on the rendered player view.");
        body.Children.Add(_transformRows);
        Button add = QuietButton("Add transformation");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += (_, _) => AddTransform(new OutputTransformationRule(
            Guid.NewGuid().ToString("N"),
            "New transformation",
            "",
            Actions: [new OutputRuleAction(OutputRuleActionKind.Gag)]));
        body.Children.Add(add);
        return body;
    }

    private void AddHighlight(TranscriptHighlightRule rule)
    {
        TextBox pattern = Field(rule.Pattern, "Text or regex");
        TextBox foreground = Field(rule.Foreground, "#FFD166");
        foreground.Width = 105;
        ComboBox mode = EnumCombo<HighlightMatchMode>(rule.MatchMode, 95);
        CheckBox enabled = Check("Enabled", rule.Enabled);
        CheckBox bold = Check("Bold", rule.Bold);
        CheckBox underline = Check("Underline", rule.Underline);
        CheckBox caseSensitive = Check("Case sensitive", rule.CaseSensitive);

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        top.Children.Add(pattern);
        Grid.SetColumn(mode, 1); top.Children.Add(mode);
        Grid.SetColumn(foreground, 2); top.Children.Add(foreground);
        Button up = QuietButton("↑");
        Grid.SetColumn(up, 3); top.Children.Add(up);
        Button down = QuietButton("↓");
        Grid.SetColumn(down, 4); top.Children.Add(down);
        Button remove = QuietButton("Remove");
        Grid.SetColumn(remove, 5); top.Children.Add(remove);

        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        toggles.Children.Add(enabled);
        toggles.Children.Add(bold);
        toggles.Children.Add(underline);
        toggles.Children.Add(caseSensitive);
        StackPanel content = new() { Spacing = 7 };
        content.Children.Add(top);
        content.Children.Add(toggles);
        Border container = EditorContainer(content);

        HighlightDraft draft = new()
        {
            Pattern = pattern,
            Foreground = foreground,
            MatchMode = mode,
            Bold = bold,
            Underline = underline,
            CaseSensitive = caseSensitive,
            Enabled = enabled,
            Container = container
        };
        up.Click += (_, _) => MoveHighlight(draft, -1);
        down.Click += (_, _) => MoveHighlight(draft, 1);
        remove.Click += (_, _) =>
        {
            _highlightRows.Children.Remove(container);
            _highlights.Remove(draft);
        };
        _highlights.Add(draft);
        _highlightRows.Children.Add(container);
    }

    private void AddTransform(OutputTransformationRule rule)
    {
        OutputRuleAction primary = rule.EffectiveActions.FirstOrDefault()
            ?? new OutputRuleAction(OutputRuleActionKind.Gag);
        TextBox name = Field(rule.Name, "Rule name");
        TextBox pattern = Field(rule.Pattern, "Text or regex");
        ComboBox matchType = EnumCombo<OutputRuleMatchType>(rule.MatchType, 100);
        ComboBox action = new()
        {
            ItemsSource = Enum.GetValues<OutputRuleActionKind>(),
            SelectedItem = primary.Kind,
            MinWidth = 115
        };
        TextBox value = Field(
            primary.Kind == OutputRuleActionKind.Highlight ? primary.Foreground ?? string.Empty : primary.Text ?? string.Empty,
            "Replacement / color / message");
        TextBox priority = Field(rule.Priority.ToString(), "0");
        priority.Width = 70;
        CheckBox enabled = Check("Enabled", rule.Enabled);
        CheckBox caseSensitive = Check("Case sensitive", rule.CaseSensitive);
        Button remove = QuietButton("Remove");

        Grid first = new() { ColumnDefinitions = new ColumnDefinitions("*,*,Auto,Auto"), ColumnSpacing = 8 };
        first.Children.Add(name);
        Grid.SetColumn(pattern, 1); first.Children.Add(pattern);
        Grid.SetColumn(matchType, 2); first.Children.Add(matchType);
        Grid.SetColumn(remove, 3); first.Children.Add(remove);
        Grid second = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        second.Children.Add(action);
        Grid.SetColumn(value, 1); second.Children.Add(value);
        Grid.SetColumn(priority, 2); second.Children.Add(priority);
        StackPanel toggles = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
        toggles.Children.Add(enabled);
        toggles.Children.Add(caseSensitive);

        StackPanel content = new() { Spacing = 7 };
        content.Children.Add(first);
        content.Children.Add(second);
        content.Children.Add(toggles);
        Border container = EditorContainer(content);
        TransformDraft draft = new()
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
            _transformRows.Children.Remove(container);
            _transforms.Remove(draft);
        };
        _transforms.Add(draft);
        _transformRows.Children.Add(container);
    }

    private void MoveHighlight(HighlightDraft draft, int delta)
    {
        int current = _highlights.IndexOf(draft);
        if (current < 0) return;
        int target = current + delta;
        if (target < 0 || target >= _highlights.Count) return;

        _highlights.RemoveAt(current);
        _highlights.Insert(target, draft);
        _highlightRows.Children.Remove(draft.Container);
        _highlightRows.Children.Insert(target, draft.Container);
    }

    private static bool TryValidateRegex(string pattern, bool caseSensitive, out string? message)
    {
        try
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            _ = new Regex(pattern, options, TimeSpan.FromMilliseconds(100));
            message = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            message = $"Invalid regular expression '{pattern}': {exception.Message}";
            return false;
        }
    }

    private static bool IsHexColor(string value) =>
        value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    private static StackPanel Section(string title, string subtitle)
    {
        StackPanel body = new() { Spacing = 7 };
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
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(UiTheme.DividerLine());
        return body;
    }

    private static TextBox Field(string? value, string placeholder) => new()
    {
        Text = value,
        PlaceholderText = placeholder,
        Background = UiTheme.Field,
        Foreground = UiTheme.Text,
        BorderBrush = UiTheme.Divider,
        BorderThickness = new Thickness(1),
        MinHeight = 30,
        Padding = new Thickness(7, 3),
        FontSize = NexTypography.Body
    };

    private static ComboBox EnumCombo<T>(T selected, double minimumWidth) where T : struct, Enum => new()
    {
        ItemsSource = Enum.GetValues<T>(),
        SelectedItem = selected,
        MinWidth = minimumWidth
    };

    private static CheckBox Check(string text, bool value) => new()
    {
        Content = text,
        IsChecked = value,
        Foreground = UiTheme.Text,
        FontSize = NexTypography.Body
    };

    private static Button QuietButton(string text) => UiTheme.QuietButton(text);

    private static Border EditorContainer(Control content) => new()
    {
        Child = content,
        BorderBrush = UiTheme.Divider,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(0, 6, 0, 7)
    };
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NexMud.Gui.AutomationStudio;

/// <summary>What the user wants to create, with the quick-start values collected by the wizard.</summary>
internal sealed record NewItemRequest(NewItemTemplate Template, IReadOnlyList<string> Values)
{
    public static string? Validate(NewItemTemplate template, IReadOnlyList<string> values)
    {
        if (values.Count != template.Fields.Count || values.Any(string.IsNullOrWhiteSpace)) return "Fill in every field.";
        if (template.Kind == StudioDocumentKind.Timer && !(int.TryParse(values[1], out int seconds) && seconds >= 1))
            return "Interval must be a whole number of seconds, 1 or more.";
        if (template.Kind == StudioDocumentKind.Highlight && !Color.TryParse(values[1], out _))
            return "Colour must be a hex value such as #F59E0B.";
        return null;
    }
}

internal sealed record NewItemField(string Label, string Default, string Hint);

internal sealed record NewItemTemplate(string Title, string Description, StudioDocumentKind Kind, IReadOnlyList<NewItemField> Fields, string Example)
{
    public static readonly IReadOnlyList<NewItemTemplate> All =
    [
        new("Alias", "Type a short word; NexMUD sends the longer command.", StudioDocumentKind.Alias,
            [new("Alias name", "k", "What you type"), new("Expands to", "kill {target}", "What gets sent")], "k  →  kill goblin"),
        new("Trigger", "React automatically when matching text arrives from the game.", StudioDocumentKind.Trigger,
            [new("When the game says", "You are hungry", "Text to look for"), new("Send command", "eat bread", "What to do")], "\"You are hungry\"  →  eat bread"),
        new("Highlight", "Colour matching text in the output so it stands out.", StudioDocumentKind.Highlight,
            [new("Text to highlight", "tells you", "Text to look for"), new("Colour", "#22D4BF", "Hex colour such as #F59E0B")], "tells you  →  teal"),
        new("Timer", "Send a command on a repeating schedule.", StudioDocumentKind.Timer,
            [new("Name", "keep-alive", "Timer name"), new("Every (seconds)", "60", "Whole number"), new("Send command", "score", "What to do")], "every 60s  →  score"),
        new("Keybinding", "Press a key combination to run a command.", StudioDocumentKind.Keybinding,
            [new("Key combination", "Cmd+1", "e.g. Cmd+Shift+E"), new("Send command", "look", "What to do")], "Cmd+1  →  look"),
        new("State Rule", "Act when a vital or game value crosses a threshold.", StudioDocumentKind.StateRule,
            [new("Name", "low-hp", "Rule name"), new("When", "hp.percent < 30", "State expression"), new("Send command", "flee", "What to do")], "hp below 30%  →  flee"),
        new("Semantic Trigger", "React to a recognised game event such as entering a room.", StudioDocumentKind.SemanticTrigger,
            [new("Name", "on-room-change", "Trigger name"), new("Event", "RoomChanged", "Event name")], "RoomChanged  →  your actions"),
        new("Workflow", "Chain several steps into one reusable sequence.", StudioDocumentKind.Workflow,
            [new("Name", "buff-up", "Workflow name")], "Add structured actions after creation"),
        new("Script package", "Write TypeScript for logic the visual editors can't express.", StudioDocumentKind.Script,
            [new("Package name", "My scripts", "Shown in the Explorer")], "main.ts with full IntelliSense")
    ];
}

/// <summary>Gallery of things you can create. Pick a card, fill in one or two fields, press Create.</summary>
internal static class NewItemDialog
{
    public static async Task<NewItemRequest?> ShowAsync(Window owner, StudioDocumentKind? preselect = null)
    {
        Window dialog = new()
        {
            Title = "Create", Width = 760, Height = 460, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = UiTheme.Window
        };
        ListBox list = new() { ItemsSource = NewItemTemplate.All, Width = 230, Background = Brushes.Transparent };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<NewItemTemplate>((item, _) => new TextBlock
        {
            Text = item?.Title, Foreground = UiTheme.Text, FontSize = 14, Padding = new Thickness(6, 5)
        }, true);
        list.SelectedItem = NewItemTemplate.All.FirstOrDefault(t => t.Kind == preselect) ?? NewItemTemplate.All[0];

        StackPanel form = new() { Spacing = 10, Margin = new Thickness(20, 6, 8, 6) };
        List<TextBox> boxes = [];
        Button create = UiTheme.PrimaryButton("Create");
        Button cancel = UiTheme.QuietButton("Cancel");
        TextBlock error = new() { Foreground = UiTheme.Danger, FontSize = 12, IsVisible = false };

        void Render()
        {
            form.Children.Clear();
            boxes.Clear();
            if (list.SelectedItem is not NewItemTemplate template) return;
            form.Children.Add(new TextBlock { Text = template.Title, Foreground = UiTheme.Text, FontSize = 20, FontWeight = FontWeight.SemiBold });
            form.Children.Add(new TextBlock { Text = template.Description, Foreground = UiTheme.Muted, TextWrapping = TextWrapping.Wrap });
            form.Children.Add(new TextBlock { Text = $"Example: {template.Example}", Foreground = UiTheme.Faint, FontFamily = UiTheme.Mono, FontSize = 12 });
            foreach (NewItemField field in template.Fields)
            {
                TextBox box = UiTheme.FieldBox();
                box.Text = field.Default;
                box.MinHeight = 32;
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
                boxes.Add(box);
                form.Children.Add(new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = $"{field.Label}  —  {field.Hint}", Foreground = UiTheme.Muted, FontSize = 12 },
                        box
                    }
                });
            }
            form.Children.Add(error);
            boxes.FirstOrDefault()?.Focus();
        }

        void Accept()
        {
            if (list.SelectedItem is not NewItemTemplate template) return;
            string[] values = boxes.Select(box => box.Text?.Trim() ?? "").ToArray();
            if (NewItemRequest.Validate(template, values) is { } validationError)
            {
                error.Text = validationError;
                error.IsVisible = true;
                return;
            }
            dialog.Close(new NewItemRequest(template, values));
        }

        list.SelectionChanged += (_, _) => Render();
        create.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(null); };
        dialog.Opened += (_, _) => boxes.FirstOrDefault()?.Focus();
        Render();

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 16, 12) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        Border listFrame = new() { Child = list, Margin = new Thickness(12), BorderBrush = UiTheme.Divider, BorderThickness = new Thickness(0, 0, 1, 0) };
        Grid.SetRowSpan(listFrame, 2);
        layout.Children.Add(listFrame);
        Grid.SetColumn(form, 1);
        layout.Children.Add(form);
        Grid.SetColumn(buttons, 1); Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        return await dialog.ShowDialog<NewItemRequest?>(owner).ConfigureAwait(true);
    }
}

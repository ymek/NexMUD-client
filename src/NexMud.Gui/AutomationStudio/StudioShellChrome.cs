using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NexMud.Gui.AutomationStudio;

/// <summary>Visual tokens and chrome shared only by the Automation Studio shell.</summary>
internal static class StudioShellChrome
{
    public static readonly IBrush Header = Brush("#0B1E40");
    public static readonly IBrush Rail = Brush("#0D2D5A");
    public static readonly IBrush Explorer = Brush("#081624");
    public static readonly IBrush Canvas = Brush("#0B1C30");
    public static readonly IBrush Card = Brush("#0F2338");
    public static readonly IBrush Input = Brush("#122840");
    public static readonly IBrush Border = Brush("#1B3A59");
    public static readonly IBrush Foreground = Brush("#E7F0FC");
    public static readonly IBrush Secondary = Brush("#A9C3E0");
    public static readonly IBrush Selected = Brush("#0660FC");
    public static readonly IBrush Blue = Brush("#168BFF");
    public static readonly IBrush Success = Brush("#19D690");
    public static readonly IBrush Connecting = Brush("#168BFF");
    public static readonly FontFamily Font = new("Inter, SF Pro Text, Helvetica Neue, sans-serif");

    public static Control BuildHeader(
        Control profile,
        Control runtimeState,
        Func<string, Task> searchRequested,
        Func<Task> settingsRequested)
    {
        TextBox search = new()
        {
            PlaceholderText = "Search automations, scripts, workflows...",
            Background = Input,
            Foreground = Foreground,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            MinHeight = 34,
            Padding = new Thickness(10, 5),
            FontSize = 13
        };
        search.KeyDown += async (_, args) =>
        {
            if (args.Key != Avalonia.Input.Key.Enter) return;
            args.Handled = true;
            await searchRequested(search.Text?.Trim() ?? string.Empty).ConfigureAwait(true);
        };

        StackPanel brand = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "N", Foreground = Blue, FontSize = 32, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "NexMUD", Foreground = Foreground, FontSize = 19, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                new Border { Width = 1, Height = 34, Background = Border, Margin = new Thickness(3, 0) },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "Automation Studio", Foreground = Foreground, FontSize = 14, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "Next-generation MUD client", Foreground = Secondary, FontSize = 11 }
                    }
                }
            }
        };

        Button settings = new()
        {
            Content = "⚙",
            Width = 38,
            Height = 34,
            Background = Brushes.Transparent,
            Foreground = Foreground,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5)
        };
        settings.Click += async (_, _) => await settingsRequested().ConfigureAwait(true);

        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("390,*,240,190,40"),
            ColumnSpacing = 16,
            Margin = new Thickness(22, 8, 18, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(brand);
        Border searchFrame = new()
        {
            Child = search,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(searchFrame, 1);
        header.Children.Add(searchFrame);
        Grid.SetColumn(profile, 2);
        header.Children.Add(profile);
        Grid.SetColumn(runtimeState, 3);
        header.Children.Add(runtimeState);
        Grid.SetColumn(settings, 4);
        header.Children.Add(settings);
        return new Border
        {
            Background = Header,
            BorderBrush = Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            MinHeight = 58,
            Child = header
        };
    }

    public static Control BuildActivityButtons(StudioActivity selected, Func<StudioActivity, Task> activityRequested)
    {
        StackPanel items = new() { Spacing = 8, Margin = new Thickness(8, 12), Width = 92 };
        (string Icon, string Label, StudioActivity Activity)[] activities =
        [
            ("⌘", "Automations", StudioActivity.Automations),
            ("♧", "Workflows", StudioActivity.Workflows),
            ("</>", "Scripts", StudioActivity.Scripts),
            ("⌕", "Search", StudioActivity.Search),
            ("◉", "Runtime", StudioActivity.Runtime)
        ];
        foreach ((string icon, string label, StudioActivity activity) in activities)
        {
            bool active = selected == activity;
            Button button = new()
            {
                Content = new StackPanel
                {
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = icon, FontSize = 23, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }
                    }
                },
                Height = 62,
                Width = 92,
                Padding = new Thickness(3),
                Background = active ? Selected : Brushes.Transparent,
                Foreground = Foreground,
                BorderBrush = active ? Blue : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            Avalonia.Automation.AutomationProperties.SetName(button, label);
            button.Click += async (_, _) => await activityRequested(activity).ConfigureAwait(true);
            items.Children.Add(button);
        }
        return items;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

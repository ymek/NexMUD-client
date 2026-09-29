using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JevMud.Gui;

/// <summary>
/// NexMUD's shared visual language. Keep chrome quiet and reserve color for state,
/// selection, and action. Both the main client and settings surface use these tokens.
/// </summary>
internal static class UiTheme
{
    // World-first game palette: obsidian/midnight surfaces, parchment text, brass
    // structure, cyan interaction, and semantic resource/status colors.
    public static readonly IBrush Window = Brush("#0B0D10");
    public static readonly IBrush Surface = Brush("#0F1B2A");
    public static readonly IBrush Raised = Brush("#1E293B");
    public static readonly IBrush Console = Brush("#070A0D");
    public static readonly IBrush Field = Brush("#111827");
    public static readonly IBrush Text = Brush("#E6DDC6");
    public static readonly IBrush Muted = Brush("#9CA3AF");
    public static readonly IBrush Faint = Brush("#64748B");
    public static readonly Color AccentColor = Color.Parse("#D4A85B");
    public static readonly IBrush Accent = new SolidColorBrush(AccentColor);
    public static readonly IBrush Brass = Accent;
    public static readonly IBrush Cyan = Brush("#22D4BF");
    public static readonly IBrush Success = Brush("#22C55E");
    public static readonly IBrush Warning = Brush("#F59E0B");
    public static readonly IBrush Danger = Brush("#EF4444");
    public static readonly IBrush Mana = Brush("#38BDF8");
    public static readonly IBrush Movement = Brush("#22C55E");
    public static readonly IBrush Divider = Brush("#2B3A4D");

    public static readonly FontFamily Sans = new("Inter, SF Pro Text, Helvetica Neue, sans-serif");
    public static readonly FontFamily Mono = new("Menlo, SFMono-Regular, Consolas, monospace");

    public const double TextXs = NexTypography.Metadata;
    public const double TextSm = NexTypography.Metadata;
    public const double TextBody = NexTypography.Body;
    public const double TextSection = NexTypography.CompactData;
    public const double TextTitle = 20;
    public const double ControlHeight = 30;
    public const double DenseRowHeight = 24;
    public const double Radius = 3;
    public const double SpaceXs = 4;
    public const double SpaceSm = 6;
    public const double SpaceMd = 10;
    public const double SpaceLg = 14;

    public static Border DividerLine(double opacity = 0.7) => new()
    {
        Height = 1,
        Background = Divider,
        Opacity = opacity
    };

    public static TextBox FieldBox() => new()
    {
        Background = Field,
        Foreground = Text,
        BorderBrush = Divider,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Radius),
        MinHeight = 34,
        Padding = new Thickness(8, 4),
        FontSize = TextBody
    };

    public static Button QuietButton(string text) => new()
    {
        Content = text,
        Background = Brushes.Transparent,
        Foreground = Text,
        BorderBrush = Divider,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Radius),
        MinHeight = ControlHeight,
        Padding = new Thickness(10, 4),
        FontSize = TextSm
    };

    public static Button PrimaryButton(string text) => new()
    {
        Content = text,
        Background = Accent,
        Foreground = Brushes.Black,
        BorderBrush = Accent,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Radius),
        MinHeight = ControlHeight,
        Padding = new Thickness(12, 4),
        FontSize = TextSm,
        FontWeight = FontWeight.SemiBold
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

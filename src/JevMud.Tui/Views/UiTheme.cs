using Terminal.Gui.Drawing;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace JevMud.Tui.Views;

internal static class UiTheme
{
    // Deliberately neutral, high-contrast console palette. Avoid low-contrast blue/gray panels.
    public static Color Background { get; } = new(0, 0, 0);
    public static Color Foreground { get; } = new(245, 245, 245);
    public static Color Muted { get; } = new(176, 176, 176);
    public static Color Accent { get; } = new(120, 200, 255);
    public static Color InputBackground { get; } = new(12, 12, 12);

    public static Scheme Panel { get; } = new()
    {
        Normal = new TgAttribute(Foreground, Background),
        Focus = new TgAttribute(Foreground, Background),
        HotNormal = new TgAttribute(Accent, Background),
        HotFocus = new TgAttribute(Accent, Background),

        Active = new TgAttribute(Foreground, Background),
        HotActive = new TgAttribute(Accent, Background),
        Highlight = new TgAttribute(Foreground, Background),
        Editable = new TgAttribute(Foreground, Background),
        ReadOnly = new TgAttribute(Foreground, Background),

        Disabled = new TgAttribute(Muted, Background)
    };

    public static Scheme Input { get; } = new()
    {
        Normal = new TgAttribute(Foreground, InputBackground),
        Focus = new TgAttribute(Foreground, InputBackground),
        HotNormal = new TgAttribute(Accent, InputBackground),
        HotFocus = new TgAttribute(Accent, InputBackground),

        Active = new TgAttribute(Foreground, Background),
        HotActive = new TgAttribute(Accent, Background),
        Highlight = new TgAttribute(Foreground, Background),
        Editable = new TgAttribute(Foreground, Background),
        ReadOnly = new TgAttribute(Foreground, Background),

        Disabled = new TgAttribute(Muted, InputBackground)
    };

    public static Scheme Status { get; } = new()
    {
        Normal = new TgAttribute(Foreground, Background),
        Focus = new TgAttribute(Foreground, Background)
    };
}

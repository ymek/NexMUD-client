using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace JevMud.Tui.Views;

/// <summary>
/// Terminal.Gui editable input projection. History ownership remains in the shared interaction layer.
/// </summary>
public sealed class CommandInputView : TextField
{
    public Func<string, string?>? HistoryPrevious { get; init; }
    public Func<string?>? HistoryNext { get; init; }

    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.CursorUp)
        {
            Apply(HistoryPrevious?.Invoke(Text?.ToString() ?? string.Empty));
            return true;
        }

        if (key == Key.CursorDown)
        {
            Apply(HistoryNext?.Invoke());
            return true;
        }

        return base.OnKeyDown(key);
    }

    private void Apply(string? value)
    {
        if (value is not null)
            Text = value;
    }
}

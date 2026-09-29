using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace JevMud.Tui.Views;

public sealed class CommandInputView : TextField
{
    private const int MaxHistory = 500;
    private readonly List<string> _history = [];
    private int _historyIndex;
    private string _draft = string.Empty;

    public CommandInputView()
    {
        _historyIndex = 0;
    }

    public void Record(string command)
    {
        if (!string.IsNullOrWhiteSpace(command) &&
            (_history.Count == 0 || !string.Equals(_history[^1], command, StringComparison.Ordinal)))
        {
            _history.Add(command);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        _historyIndex = _history.Count;
        _draft = string.Empty;
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.CursorUp)
        {
            NavigateHistory(-1);
            return true;
        }

        if (key == Key.CursorDown)
        {
            NavigateHistory(1);
            return true;
        }

        return base.OnKeyDown(key);
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        if (_historyIndex == _history.Count)
        {
            _draft = Text?.ToString() ?? string.Empty;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        Text = _historyIndex == _history.Count
            ? _draft
            : _history[_historyIndex];
    }
}

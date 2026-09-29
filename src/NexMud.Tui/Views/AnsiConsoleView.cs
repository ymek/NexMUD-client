using System.Text;
using NexMud.Transport.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace NexMud.Tui.Views;

public sealed class AnsiConsoleView : View
{
    private sealed record StyledRune(Rune Rune, AnsiTextStyle Style);

    private const int MaxLogicalLines = 5_000;
    private readonly AnsiTextParser _ansi = new();
    private readonly List<List<StyledRune>> _logicalLines = [[]];
    private IReadOnlyList<IReadOnlyList<StyledRune>>? _cachedRows;
    private int _cachedWidth = -1;
    private int _topRow;
    private bool _followTail = true;

    public AnsiConsoleView()
    {
        CanFocus = false;
        SetScheme(UiTheme.Panel);

        AddCommand(Command.ScrollUp, () =>
        {
            ScrollLines(-3);
            return true;
        });
        AddCommand(Command.ScrollDown, () =>
        {
            ScrollLines(3);
            return true;
        });

        MouseBindings.Add(MouseFlags.WheeledUp, Command.ScrollUp);
        MouseBindings.Add(MouseFlags.WheeledDown, Command.ScrollDown);

        // Terminal.Gui v2 exposes drawing as a cancellable event. Using the event
        // avoids coupling this view to version-specific protected hook signatures.
        DrawingContent += (_, args) =>
        {
            if (args.DrawContext is DrawContext drawContext)
            {
                DrawAnsiContent(drawContext);
            }
            args.Cancel = true;
        };
    }

    public bool IsFollowingTail => _followTail;

    public int LinesBelow
    {
        get
        {
            IReadOnlyList<IReadOnlyList<StyledRune>> rows = GetRows(Math.Max(1, Viewport.Width));
            int height = Math.Max(1, Viewport.Height);
            return Math.Max(0, rows.Count - (_topRow + height));
        }
    }

    public void AppendAnsi(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        AppendSegments(_ansi.Process(text));
    }

    public void AppendSegments(IEnumerable<AnsiTextSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        bool appended = false;
        foreach (AnsiTextSegment segment in segments)
        {
            appended = true;
            foreach (Rune rune in segment.Text.EnumerateRunes())
            {
                if (rune.Value == '\r') continue;
                if (rune.Value == '\n')
                {
                    _logicalLines.Add([]);
                    continue;
                }
                if (rune.Value == '\b')
                {
                    RemoveLastRune();
                    continue;
                }

                _logicalLines[^1].Add(new StyledRune(rune, segment.Style));
            }
        }
        if (!appended) return;

        while (_logicalLines.Count > MaxLogicalLines)
        {
            if (!_followTail)
            {
                int removedRows = CountWrappedRows(_logicalLines[0], Math.Max(1, Viewport.Width));
                _topRow = Math.Max(0, _topRow - removedRows);
            }
            _logicalLines.RemoveAt(0);
        }

        InvalidateRows();
        SetNeedsDraw();
    }

    public void ScrollPage(int direction)
    {
        int page = Math.Max(1, Viewport.Height - 2);
        ScrollLines(direction * page);
    }

    public void ScrollLines(int delta)
    {
        IReadOnlyList<IReadOnlyList<StyledRune>> rows = GetRows(Math.Max(1, Viewport.Width));
        int height = Math.Max(1, Viewport.Height);
        int maximumTop = Math.Max(0, rows.Count - height);

        if (_followTail)
        {
            _topRow = maximumTop;
        }

        _topRow = Math.Clamp(_topRow + delta, 0, maximumTop);
        _followTail = _topRow >= maximumTop;
        SetNeedsDraw();
    }

    public void ScrollToBottom()
    {
        _followTail = true;
        SetNeedsDraw();
    }

    private void DrawAnsiContent(DrawContext drawContext)
    {
        int width = Math.Max(1, Viewport.Width);
        int height = Math.Max(1, Viewport.Height);
        IReadOnlyList<IReadOnlyList<StyledRune>> rows = GetRows(width);

        int maximumTop = Math.Max(0, rows.Count - height);
        if (_followTail)
        {
            _topRow = maximumTop;
        }
        else
        {
            _topRow = Math.Clamp(_topRow, 0, maximumTop);
        }

        SetAttributeForRole(VisualRole.Normal);
        ClearViewport(drawContext);

        int visibleCount = Math.Min(height, Math.Max(0, rows.Count - _topRow));
        for (int rowIndex = 0; rowIndex < visibleCount; rowIndex++)
        {
            IReadOnlyList<StyledRune> row = rows[_topRow + rowIndex];
            int column = 0;

            foreach (StyledRune cell in row)
            {
                int runeWidth = Math.Max(0, RuneExtensions.GetColumns(cell.Rune));
                if (runeWidth > 0 && column + runeWidth > width)
                {
                    break;
                }

                SetAttribute(ToAttribute(cell.Style));
                Move(column, rowIndex);
                AddRune(cell.Rune);
                column += runeWidth;
            }
        }

    }

    private IReadOnlyList<IReadOnlyList<StyledRune>> GetRows(int width)
    {
        if (_cachedRows is not null && _cachedWidth == width)
        {
            return _cachedRows;
        }

        List<IReadOnlyList<StyledRune>> rows = [];
        foreach (List<StyledRune> logicalLine in _logicalLines)
        {
            List<StyledRune> row = [];
            int columns = 0;

            if (logicalLine.Count == 0)
            {
                rows.Add(row);
                continue;
            }

            foreach (StyledRune cell in logicalLine)
            {
                int runeWidth = Math.Max(0, RuneExtensions.GetColumns(cell.Rune));

                if (runeWidth > 0 && columns > 0 && columns + runeWidth > width)
                {
                    rows.Add(row);
                    row = [];
                    columns = 0;
                }

                row.Add(cell);
                columns += runeWidth;
            }

            rows.Add(row);
        }

        _cachedRows = rows;
        _cachedWidth = width;
        return rows;
    }

    private TgAttribute ToAttribute(AnsiTextStyle style)
    {
        TgAttribute normal = GetAttributeForRole(VisualRole.Normal);
        Color foreground = style.Foreground is null
            ? normal.Foreground
            : ToReadableForeground(style.Foreground);
        Color background = style.Background is null
            ? normal.Background
            : ToColor(style.Background);

        if (style.Reverse)
        {
            (foreground, background) = (background, foreground);
        }

        TextStyle textStyle = TextStyle.None;
        if (style.Bold)
        {
            textStyle |= TextStyle.Bold;
        }
        if (style.Faint)
        {
            textStyle |= TextStyle.Faint;
        }
        if (style.Italic)
        {
            textStyle |= TextStyle.Italic;
        }
        if (style.Underline)
        {
            textStyle |= TextStyle.Underline;
        }
        if (style.Blink)
        {
            textStyle |= TextStyle.Blink;
        }
        if (style.Strikethrough)
        {
            textStyle |= TextStyle.Strikethrough;
        }

        return new TgAttribute(foreground, background, textStyle);
    }

    private static Color ToReadableForeground(AnsiColor color)
    {
        const double minimumLuminance = 100.0;
        double luminance = 0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue;
        if (luminance >= minimumLuminance)
        {
            return ToColor(color);
        }

        double blend = (minimumLuminance - luminance) / (255.0 - luminance);
        byte red = (byte)Math.Clamp((int)Math.Round(color.Red + (255 - color.Red) * blend), 0, 255);
        byte green = (byte)Math.Clamp((int)Math.Round(color.Green + (255 - color.Green) * blend), 0, 255);
        byte blue = (byte)Math.Clamp((int)Math.Round(color.Blue + (255 - color.Blue) * blend), 0, 255);
        return new Color(red, green, blue);
    }

    private static Color ToColor(AnsiColor color) =>
        new(color.Red, color.Green, color.Blue);

    private static int CountWrappedRows(IReadOnlyList<StyledRune> line, int width)
    {
        if (line.Count == 0)
        {
            return 1;
        }

        int rows = 1;
        int columns = 0;
        foreach (StyledRune cell in line)
        {
            int runeWidth = Math.Max(0, RuneExtensions.GetColumns(cell.Rune));
            if (runeWidth > 0 && columns > 0 && columns + runeWidth > width)
            {
                rows++;
                columns = 0;
            }
            columns += runeWidth;
        }
        return rows;
    }

    private void RemoveLastRune()
    {
        if (_logicalLines[^1].Count > 0)
        {
            _logicalLines[^1].RemoveAt(_logicalLines[^1].Count - 1);
            return;
        }

        if (_logicalLines.Count > 1)
        {
            _logicalLines.RemoveAt(_logicalLines.Count - 1);
        }
    }

    private void InvalidateRows()
    {
        _cachedRows = null;
        _cachedWidth = -1;
    }
}

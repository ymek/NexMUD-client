using System.Collections.ObjectModel;
using System.Text;

namespace NexMud.Transport.Text;

public sealed record AnsiColor(byte Red, byte Green, byte Blue);

public sealed record AnsiTextStyle(
    AnsiColor? Foreground,
    AnsiColor? Background,
    bool Bold,
    bool Faint,
    bool Italic,
    bool Underline,
    bool Blink,
    bool Reverse,
    bool Strikethrough)
{
    public static AnsiTextStyle Default { get; } =
        new(null, null, false, false, false, false, false, false, false);
}

public sealed record AnsiTextSegment(string Text, AnsiTextStyle Style);

public sealed class AnsiTextParser
{
    private enum State
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape
    }

    private readonly StringBuilder _text = new();
    private readonly StringBuilder _csi = new();
    private readonly StringBuilder _osc = new();
    private readonly List<AnsiTextSegment> _segments = [];
    private State _state;
    private AnsiTextStyle _style = AnsiTextStyle.Default;

    public void Reset()
    {
        _state = State.Text;
        _style = AnsiTextStyle.Default;
        _text.Clear();
        _csi.Clear();
        _osc.Clear();
        _segments.Clear();
    }

    public IReadOnlyList<AnsiTextSegment> Process(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _segments.Clear();

        foreach (char value in input)
        {
            switch (_state)
            {
                case State.Text:
                    if (value == '\u001b')
                    {
                        FlushText();
                        _state = State.Escape;
                    }
                    else if (value != '\0')
                    {
                        _text.Append(value);
                    }
                    break;

                case State.Escape:
                    if (value == '[')
                    {
                        _csi.Clear();
                        _state = State.Csi;
                    }
                    else if (value == ']')
                    {
                        _osc.Clear();
                        _state = State.Osc;
                    }
                    else
                    {
                        _state = State.Text;
                    }
                    break;

                case State.Csi:
                    if (value is >= '@' and <= '~')
                    {
                        if (value == 'm')
                        {
                            ApplySgr(_csi.ToString());
                        }
                        _csi.Clear();
                        _state = State.Text;
                    }
                    else
                    {
                        _csi.Append(value);
                    }
                    break;

                case State.Osc:
                    if (value == '\a')
                    {
                        _osc.Clear();
                        _state = State.Text;
                    }
                    else if (value == '\u001b')
                    {
                        _state = State.OscEscape;
                    }
                    else
                    {
                        _osc.Append(value);
                    }
                    break;

                case State.OscEscape:
                    if (value == '\\')
                    {
                        _osc.Clear();
                        _state = State.Text;
                    }
                    else
                    {
                        _state = State.Osc;
                    }
                    break;
            }
        }

        FlushText();
        return new ReadOnlyCollection<AnsiTextSegment>(_segments.ToArray());
    }

    private void FlushText()
    {
        if (_text.Length == 0)
        {
            return;
        }

        string value = _text.ToString();
        _text.Clear();

        if (_segments.Count > 0 && _segments[^1].Style == _style)
        {
            AnsiTextSegment previous = _segments[^1];
            _segments[^1] = previous with { Text = previous.Text + value };
        }
        else
        {
            _segments.Add(new AnsiTextSegment(value, _style));
        }
    }

    private void ApplySgr(string parameters)
    {
        FlushText();

        int[] values = ParseParameters(parameters);
        if (values.Length == 0)
        {
            values = [0];
        }

        for (int index = 0; index < values.Length; index++)
        {
            int value = values[index];
            switch (value)
            {
                case 0:
                    _style = AnsiTextStyle.Default;
                    break;
                case 1:
                    _style = _style with { Bold = true, Faint = false };
                    break;
                case 2:
                    _style = _style with { Faint = true, Bold = false };
                    break;
                case 3:
                    _style = _style with { Italic = true };
                    break;
                case 4:
                    _style = _style with { Underline = true };
                    break;
                case 5:
                    _style = _style with { Blink = true };
                    break;
                case 7:
                    _style = _style with { Reverse = true };
                    break;
                case 9:
                    _style = _style with { Strikethrough = true };
                    break;
                case 22:
                    _style = _style with { Bold = false, Faint = false };
                    break;
                case 23:
                    _style = _style with { Italic = false };
                    break;
                case 24:
                    _style = _style with { Underline = false };
                    break;
                case 25:
                    _style = _style with { Blink = false };
                    break;
                case 27:
                    _style = _style with { Reverse = false };
                    break;
                case 29:
                    _style = _style with { Strikethrough = false };
                    break;
                case >= 30 and <= 37:
                    _style = _style with { Foreground = StandardColor(value - 30, bright: false) };
                    break;
                case 39:
                    _style = _style with { Foreground = null };
                    break;
                case >= 40 and <= 47:
                    _style = _style with { Background = StandardColor(value - 40, bright: false) };
                    break;
                case 49:
                    _style = _style with { Background = null };
                    break;
                case >= 90 and <= 97:
                    _style = _style with { Foreground = StandardColor(value - 90, bright: true) };
                    break;
                case >= 100 and <= 107:
                    _style = _style with { Background = StandardColor(value - 100, bright: true) };
                    break;
                case 38:
                case 48:
                    bool foreground = value == 38;
                    if (TryParseExtendedColor(values, ref index, out AnsiColor? color))
                    {
                        _style = foreground
                            ? _style with { Foreground = color }
                            : _style with { Background = color };
                    }
                    break;
            }
        }
    }

    private static int[] ParseParameters(string parameters)
    {
        if (string.IsNullOrEmpty(parameters))
        {
            return [];
        }

        List<int> values = [];
        foreach (string part in parameters.Split(';'))
        {
            values.Add(int.TryParse(part, out int value) ? value : 0);
        }
        return values.ToArray();
    }

    private static bool TryParseExtendedColor(int[] values, ref int index, out AnsiColor? color)
    {
        color = null;
        if (index + 1 >= values.Length)
        {
            return false;
        }

        int mode = values[++index];
        if (mode == 5 && index + 1 < values.Length)
        {
            color = IndexedColor(values[++index]);
            return true;
        }

        if (mode == 2 && index + 3 < values.Length)
        {
            byte red = ClampByte(values[++index]);
            byte green = ClampByte(values[++index]);
            byte blue = ClampByte(values[++index]);
            color = new AnsiColor(red, green, blue);
            return true;
        }

        return false;
    }

    private static AnsiColor IndexedColor(int value)
    {
        int index = Math.Clamp(value, 0, 255);
        if (index < 16)
        {
            return StandardColor(index % 8, index >= 8);
        }

        if (index is >= 16 and <= 231)
        {
            int cube = index - 16;
            int red = cube / 36;
            int green = cube / 6 % 6;
            int blue = cube % 6;
            return new AnsiColor(
                CubeComponent(red),
                CubeComponent(green),
                CubeComponent(blue));
        }

        byte gray = (byte)(8 + (index - 232) * 10);
        return new AnsiColor(gray, gray, gray);
    }

    private static byte CubeComponent(int value) =>
        value == 0 ? (byte)0 : (byte)(55 + value * 40);

    private static byte ClampByte(int value) =>
        (byte)Math.Clamp(value, 0, 255);

    private static AnsiColor StandardColor(int index, bool bright)
    {
        (byte r, byte g, byte b)[] normal =
        [
            (0, 0, 0),
            (170, 0, 0),
            (0, 170, 0),
            (170, 85, 0),
            (0, 0, 170),
            (170, 0, 170),
            (0, 170, 170),
            (170, 170, 170)
        ];

        (byte r, byte g, byte b)[] brightValues =
        [
            (85, 85, 85),
            (255, 85, 85),
            (85, 255, 85),
            (255, 255, 85),
            (85, 85, 255),
            (255, 85, 255),
            (85, 255, 255),
            (255, 255, 255)
        ];

        (byte r, byte g, byte b) selected = (bright ? brightValues : normal)[Math.Clamp(index, 0, 7)];
        return new AnsiColor(selected.r, selected.g, selected.b);
    }
}

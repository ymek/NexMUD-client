using System.Text;

namespace NexMud.Transport.Text;

public sealed class AnsiStripper
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private enum State
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape
    }

    private State _state;

    public void Reset()
    {
        _decoder.Reset();
        _state = State.Text;
    }

    public string Process(ReadOnlySpan<byte> bytes)
    {
        List<byte> textBytes = [];

        foreach (byte value in bytes)
        {
            switch (_state)
            {
                case State.Text:
                    if (value == 0x1b)
                    {
                        _state = State.Escape;
                    }
                    else if (value != 0)
                    {
                        textBytes.Add(value);
                    }
                    break;
                case State.Escape:
                    _state = value switch
                    {
                        (byte)'[' => State.Csi,
                        (byte)']' => State.Osc,
                        _ => State.Text
                    };
                    break;
                case State.Csi:
                    if (value is >= 0x40 and <= 0x7e)
                    {
                        _state = State.Text;
                    }
                    break;
                case State.Osc:
                    if (value == 0x07)
                    {
                        _state = State.Text;
                    }
                    else if (value == 0x1b)
                    {
                        _state = State.OscEscape;
                    }
                    break;
                case State.OscEscape:
                    _state = value == (byte)'\\' ? State.Text : State.Osc;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown ANSI parser state {_state}.");
            }
        }

        if (textBytes.Count == 0)
        {
            return string.Empty;
        }

        byte[] input = textBytes.ToArray();
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(input.Length)];
        _decoder.Convert(input, chars, flush: false, out _, out int charsUsed, out _);
        return new string(chars, 0, charsUsed);
    }
}

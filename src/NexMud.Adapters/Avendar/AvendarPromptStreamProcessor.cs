using System.Collections.ObjectModel;
using System.Text;

namespace NexMud.Adapters.Avendar;

public abstract record AvendarStreamToken;
public sealed record AvendarDisplayToken(string Text) : AvendarStreamToken;
public sealed record AvendarPromptToken(string Text) : AvendarStreamToken;

public sealed class AvendarPromptStreamProcessor
{
    private const string Marker = "[J|";
    private const int MaxPromptLength = 4096;
    private readonly StringBuilder _probe = new();
    private readonly StringBuilder _prompt = new();
    private bool _insidePrompt;

    public void Reset()
    {
        _probe.Clear();
        _prompt.Clear();
        _insidePrompt = false;
    }

    public IReadOnlyList<AvendarStreamToken> Process(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<AvendarStreamToken> tokens = [];
        StringBuilder display = new();

        foreach (char value in text)
        {
            if (_insidePrompt)
            {
                _prompt.Append(value);
                if (value == ']')
                {
                    FlushDisplay(tokens, display);
                    tokens.Add(new AvendarPromptToken(_prompt.ToString()));
                    _prompt.Clear();
                    _insidePrompt = false;
                }
                else if (_prompt.Length > MaxPromptLength)
                {
                    display.Append(_prompt);
                    _prompt.Clear();
                    _insidePrompt = false;
                }
                continue;
            }

            _probe.Append(value);
            FlushProbe(display);

            if (_probe.ToString() == Marker)
            {
                FlushDisplay(tokens, display);
                _insidePrompt = true;
                _prompt.Append(_probe);
                _probe.Clear();
            }
        }

        FlushDisplay(tokens, display);
        return new ReadOnlyCollection<AvendarStreamToken>(tokens);
    }

    private void FlushProbe(StringBuilder display)
    {
        while (_probe.Length > 0 && !Marker.StartsWith(_probe.ToString(), StringComparison.Ordinal))
        {
            int retained = LongestMarkerPrefixSuffix(_probe);
            int emitCount = _probe.Length - retained;
            display.Append(_probe.ToString(0, emitCount));
            _probe.Remove(0, emitCount);
        }
    }

    private static int LongestMarkerPrefixSuffix(StringBuilder value)
    {
        string text = value.ToString();
        int maximum = Math.Min(text.Length, Marker.Length - 1);
        for (int length = maximum; length > 0; length--)
        {
            if (text.EndsWith(Marker[..length], StringComparison.Ordinal))
            {
                return length;
            }
        }
        return 0;
    }

    private static void FlushDisplay(List<AvendarStreamToken> tokens, StringBuilder display)
    {
        if (display.Length == 0)
        {
            return;
        }

        tokens.Add(new AvendarDisplayToken(display.ToString()));
        display.Clear();
    }
}

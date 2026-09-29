using System.Text;

namespace JevMud.Gui;

/// <summary>
/// Normalizes network CRLF pairs for terminal presentation without altering the raw
/// server stream used by parsing or logging. The state handles CR/LF split across
/// arbitrary transport chunks.
/// </summary>
internal sealed class TranscriptLineEndingNormalizer
{
    private bool _pendingCarriageReturn;
    private bool _lastOutputWasLineFeed;

    public string Process(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder output = new(text.Length);
        int index = 0;

        if (_pendingCarriageReturn)
        {
            if (text[0] == '\n')
            {
                AppendLineFeed(output);
                index = 1;
            }
            else if (text[0] == '\0')
            {
                index = 1;
                _lastOutputWasLineFeed = false;
            }
            else
            {
                _lastOutputWasLineFeed = false;
            }
            _pendingCarriageReturn = false;
        }

        for (; index < text.Length; index++)
        {
            char value = text[index];
            if (value == '\n')
            {
                AppendLineFeed(output);
                continue;
            }

            if (value != '\r')
            {
                output.Append(value);
                _lastOutputWasLineFeed = false;
                continue;
            }

            // Old Diku/Merc servers commonly emit LFCR. LF advances the terminal
            // row and CR only returns to column zero; rendering both as text line
            // breaks inserts a blank visual row between every server line.
            if (_lastOutputWasLineFeed)
            {
                _lastOutputWasLineFeed = false;
                continue;
            }

            if (index + 1 >= text.Length)
            {
                _pendingCarriageReturn = true;
                continue;
            }

            if (text[index + 1] == '\n')
            {
                AppendLineFeed(output);
                index++;
                continue;
            }

            if (text[index + 1] == '\0')
            {
                index++;
                _lastOutputWasLineFeed = false;
                continue;
            }

            _lastOutputWasLineFeed = false;
        }

        return output.ToString();
    }

    public void Reset()
    {
        _pendingCarriageReturn = false;
        _lastOutputWasLineFeed = false;
    }

    private void AppendLineFeed(StringBuilder output)
    {
        output.Append('\n');
        _lastOutputWasLineFeed = true;
    }
}

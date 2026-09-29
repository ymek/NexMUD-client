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
                output.Append('\n');
                index = 1;
            }
            else
            {
                output.Append('\r');
            }
            _pendingCarriageReturn = false;
        }

        for (; index < text.Length; index++)
        {
            char value = text[index];
            if (value != '\r')
            {
                output.Append(value);
                continue;
            }

            if (index + 1 >= text.Length)
            {
                _pendingCarriageReturn = true;
                continue;
            }

            if (text[index + 1] == '\n')
            {
                output.Append('\n');
                index++;
                continue;
            }

            output.Append('\r');
        }

        return output.ToString();
    }

    public void Reset() => _pendingCarriageReturn = false;
}

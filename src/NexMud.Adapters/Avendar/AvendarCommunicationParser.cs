using System.Text.RegularExpressions;
using NexMud.Contracts.Events;

namespace NexMud.Adapters.Avendar;

public sealed partial class AvendarCommunicationParser
{
    public bool TryParse(string line, out CommunicationObserved? observed)
    {
        observed = null;
        string text = line.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        Match tell = TellRegex().Match(text);
        if (tell.Success)
        {
            observed = new CommunicationObserved("tell", tell.Groups["speaker"].Value, tell.Groups["message"].Value);
            return true;
        }

        Match say = SayRegex().Match(text);
        if (say.Success)
        {
            observed = new CommunicationObserved("say", say.Groups["speaker"].Value, say.Groups["message"].Value);
            return true;
        }

        Match ooc = OocRegex().Match(text);
        if (ooc.Success)
        {
            observed = new CommunicationObserved("ooc", ooc.Groups["speaker"].Value, ooc.Groups["message"].Value);
            return true;
        }

        Match tip = TipRegex().Match(text);
        if (tip.Success)
        {
            observed = new CommunicationObserved("tip", null, tip.Groups["message"].Value);
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(?<speaker>.+?) tells you, '(?<message>.*)'$", RegexOptions.IgnoreCase)]
    private static partial Regex TellRegex();

    [GeneratedRegex(@"^(?<speaker>.+?) says, '(?<message>.*)'$", RegexOptions.IgnoreCase)]
    private static partial Regex SayRegex();

    [GeneratedRegex(@"^\[OOC\]\s*(?<speaker>[^:]+):\s*(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OocRegex();

    [GeneratedRegex(@"^\[TIPS\]\s*(?<message>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TipRegex();
}

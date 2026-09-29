using JevMud.Contracts.Events;
using JevMud.Contracts.State;

namespace JevMud.Adapters.Avendar;

public static class AvendarSessionParser
{
    public static SessionInputModeChanged? ParseLine(string line)
    {
        string text = line.Trim();
        if (text.Equals("Under what name shall your deeds be recorded?", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionInputModeChanged(SessionInputMode.LoginName);
        }

        if (text.StartsWith("Password:", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionInputModeChanged(SessionInputMode.LoginPassword);
        }

        if (text.Equals("[Hit Return to continue]", StringComparison.Ordinal))
        {
            return new SessionInputModeChanged(SessionInputMode.Pager);
        }

        return null;
    }

    public static bool IsEditorCommand(string command)
    {
        string normalized = command.Trim();
        return normalized.Equals("description edit", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("background edit", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("bg edit", StringComparison.OrdinalIgnoreCase);
    }
}

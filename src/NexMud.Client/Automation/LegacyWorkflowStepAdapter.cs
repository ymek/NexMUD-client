namespace NexMud.Client.Automation;

/// <summary>Converts only legacy workflow steps with a provably equivalent typed action.</summary>
public static class LegacyWorkflowStepAdapter
{
    public static bool TryConvert(string? legacySteps, out IReadOnlyList<AutomationAction> actions, out string? error)
    {
        List<AutomationAction> converted = [];
        string[] lines = (legacySteps ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("send ", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("${", StringComparison.Ordinal))
            {
                string command = line[5..].Trim();
                if (command.Length > 0)
                {
                    converted.Add(new SendCommandAutomationAction(command));
                    continue;
                }
            }

            if (line.StartsWith("delay ", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan(6), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int milliseconds) &&
                milliseconds is >= 0 and <= 600_000)
            {
                converted.Add(new DelayAutomationAction(milliseconds));
                continue;
            }

            actions = [];
            error = $"Line {index + 1} ('{line}') is not safely convertible. Only non-empty literal 'send <command>' without templates and 'delay <integer-ms>' from 0 to 600000 are supported. No changes were made.";
            return false;
        }

        if (converted.Count == 0)
        {
            actions = [];
            error = "There are no executable legacy steps to convert.";
            return false;
        }

        actions = converted;
        error = null;
        return true;
    }
}

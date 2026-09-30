using System.Text.RegularExpressions;
using NexMud.Adapters.Observation;

namespace NexMud.Adapters.Avendar;

public static partial class AvendarPromptBoundaryDetector
{
    public static GameFrameBoundary? FindPromptBoundary(string bufferedText)
    {
        if (string.IsNullOrEmpty(bufferedText))
        {
            return null;
        }

        foreach (string literal in new[]
                 {
                     "Under what name shall your deeds be recorded?",
                     "[Hit Return to continue]"
                 })
        {
            int start = bufferedText.IndexOf(literal, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                return new GameFrameBoundary(start, literal.Length);
            }
        }

        int passwordStart = bufferedText.IndexOf("Password:", StringComparison.OrdinalIgnoreCase);
        if (passwordStart >= 0)
        {
            return new GameFrameBoundary(passwordStart, "Password:".Length);
        }

        int telemetryStart = bufferedText.IndexOf("[J|", StringComparison.Ordinal);
        if (telemetryStart >= 0)
        {
            int end = bufferedText.IndexOf(']', telemetryStart + 3);
            return end < 0
                ? null
                : new GameFrameBoundary(telemetryStart, end - telemetryStart + 1);
        }

        Match clock = GameClockPromptRegex().Match(bufferedText);
        return clock.Success ? new GameFrameBoundary(clock.Index, clock.Length) : null;
    }

    [GeneratedRegex(@"^\s*\d{1,2}:\d{2}>")]
    private static partial Regex GameClockPromptRegex();
}

using NexMud.Client.Settings;

namespace NexMud.Client.Automation;

public static class CommandAliasExpander
{
    public static string Expand(string input, IEnumerable<CommandAlias>? aliases)
    {
        return TryExpand(input, aliases, out string expanded) ? expanded : input;
    }

    public static bool TryExpand(string input, IEnumerable<CommandAlias>? aliases, out string expanded)
    {
        ArgumentNullException.ThrowIfNull(input);
        expanded = input;
        if (input.Length == 0 || aliases is null)
        {
            return false;
        }

        int separator = input.IndexOf(' ');
        string head = separator < 0 ? input : input[..separator];
        string tail = separator < 0 ? string.Empty : input[(separator + 1)..];
        CommandAlias? alias = aliases.FirstOrDefault(candidate =>
            candidate.Enabled &&
            candidate.Name.Equals(head, StringComparison.OrdinalIgnoreCase));
        if (alias is null)
        {
            return false;
        }

        string[] args = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        expanded = alias.Expansion.Replace("$*", tail, StringComparison.Ordinal);
        for (int index = 0; index < 9; index++)
        {
            string replacement = index < args.Length ? args[index] : string.Empty;
            expanded = expanded.Replace($"${index + 1}", replacement, StringComparison.Ordinal);
        }
        return true;
    }
}

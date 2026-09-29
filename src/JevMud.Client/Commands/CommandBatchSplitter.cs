namespace JevMud.Client.Commands;

/// <summary>
/// Splits human-entered command batches without changing programmatic commands.
/// A backslash escapes either the configured separator or another backslash.
/// </summary>
public static class CommandBatchSplitter
{
    public static IReadOnlyList<string> Split(string input, string? separator)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0 || string.IsNullOrEmpty(separator))
        {
            return [input];
        }
        if (separator.Length != 1)
        {
            throw new ArgumentException("Command separator must be empty or a single character.", nameof(separator));
        }

        char delimiter = separator[0];
        List<string> commands = [];
        System.Text.StringBuilder current = new(input.Length);
        for (int index = 0; index < input.Length; index++)
        {
            char value = input[index];
            if (value == '\\' && index + 1 < input.Length &&
                (input[index + 1] == delimiter || input[index + 1] == '\\'))
            {
                current.Append(input[++index]);
                continue;
            }

            if (value == delimiter)
            {
                AddCommand(commands, current);
                continue;
            }

            current.Append(value);
        }
        AddCommand(commands, current);
        return commands.Count == 0 ? [string.Empty] : commands;
    }

    private static void AddCommand(List<string> commands, System.Text.StringBuilder current)
    {
        string command = current.ToString().Trim();
        current.Clear();
        if (command.Length > 0)
        {
            commands.Add(command);
        }
    }
}

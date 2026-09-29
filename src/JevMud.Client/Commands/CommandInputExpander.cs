using System.Text;
using JevMud.Client.Automation;
using JevMud.Client.Settings;

namespace JevMud.Client.Commands;

/// <summary>
/// Expands aliases and command batches in the same order a user expects from the command line.
/// Escaped separators remain literal even when they pass through alias arguments.
/// </summary>
public static class CommandInputExpander
{
    public static IReadOnlyList<string> Expand(
        string input,
        string? separator,
        IEnumerable<CommandAlias>? aliases)
    {
        ArgumentNullException.ThrowIfNull(input);

        IReadOnlyList<string> rawCommands = SplitPreservingEscapes(input, separator);
        List<string> commands = [];
        foreach (string rawCommand in rawCommands)
        {
            string command = CommandAliasExpander.TryExpand(rawCommand, aliases, out string aliasExpansion)
                ? aliasExpansion
                : rawCommand;

            commands.AddRange(CommandBatchSplitter.Split(command, separator));
        }

        return commands.Count == 0 ? [string.Empty] : commands;
    }

    private static IReadOnlyList<string> SplitPreservingEscapes(string input, string? separator)
    {
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
        StringBuilder current = new(input.Length);
        for (int index = 0; index < input.Length; index++)
        {
            char value = input[index];
            if (value == '\\' && index + 1 < input.Length &&
                (input[index + 1] == delimiter || input[index + 1] == '\\'))
            {
                current.Append(value);
                current.Append(input[++index]);
                continue;
            }

            if (value == delimiter)
            {
                AddRawCommand(commands, current);
                continue;
            }

            current.Append(value);
        }

        AddRawCommand(commands, current);
        return commands.Count == 0 ? [string.Empty] : commands;
    }

    private static void AddRawCommand(List<string> commands, StringBuilder current)
    {
        string command = current.ToString().Trim();
        current.Clear();
        if (command.Length > 0)
        {
            commands.Add(command);
        }
    }
}

using JevMud.Contracts.Jev;
using JevMud.Scripting.Host;
using JevMud.Contracts.Transport;
using JevMud.Contracts.State;
using JevMud.Client.Runtime;
using JevMud.Client.Interaction;
using JevMud.Core.Jev;

namespace JevMud.Client.Commands;

public sealed record LocalCommandResult(bool ExitRequested, string? Message = null);

public sealed class LocalCommandHandler : IInputCommandDispatcher
{
    private readonly JevMudRuntime _runtime;

    public LocalCommandHandler(JevMudRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<LocalCommandResult> HandleAsync(string input, CancellationToken cancellationToken = default) =>
        DispatchAsync(input, InputSourceKind.Keyboard, _runtime.State.Current.Session.InputMode, cancellationToken);

    public async Task<LocalCommandResult> DispatchAsync(
        string input,
        InputSourceKind source,
        SessionInputMode inputMode,
        CancellationToken cancellationToken = default)
    {
        bool directInput = inputMode is SessionInputMode.LoginName or SessionInputMode.LoginPassword or SessionInputMode.Editor;
        string command = directInput ? input : input.TrimEnd();

        if (directInput)
        {
            await QueueMudCommandAsync(command, source, cancellationToken).ConfigureAwait(false);
            return new LocalCommandResult(false);
        }

        if (!command.StartsWith(':'))
        {
            await QueueMudCommandAsync(command, source, cancellationToken).ConfigureAwait(false);
            return new LocalCommandResult(false);
        }

        string localCommand = command.Trim();
        string[] parts = localCommand[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return new LocalCommandResult(false);
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                return new LocalCommandResult(false, HelpText);
            case "quit":
            case "exit":
                return new LocalCommandResult(true);
            case "disconnect":
                await _runtime.Transport.DisconnectAsync().ConfigureAwait(false);
                return new LocalCommandResult(false, "Disconnected.");
            case "connect":
                return await ConnectAsync(parts, cancellationToken).ConfigureAwait(false);
            case "jev":
                return await ConfigureJevAsync(parts, cancellationToken).ConfigureAwait(false);
            case "avendar":
                return await ConfigureAvendarAsync(parts, cancellationToken).ConfigureAwait(false);
            default:
                return new LocalCommandResult(false, $"Unknown local command '{parts[0]}'. Use :help.");
        }
    }

    private async Task<LocalCommandResult> ConnectAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length is < 3 or > 4 || !int.TryParse(parts[2], out int port))
        {
            return new LocalCommandResult(false, "Usage: :connect <host> <port> [tls]");
        }

        bool tls = parts.Length == 4 && string.Equals(parts[3], "tls", StringComparison.OrdinalIgnoreCase);
        if (parts.Length == 4 && !tls)
        {
            return new LocalCommandResult(false, "Fourth argument must be 'tls' when present.");
        }

        try
        {
            await _runtime.Transport.ConnectAsync(_runtime.CreateConnectionOptions(parts[1], port, tls), cancellationToken)
                .ConfigureAwait(false);
            await _runtime.SaveConnectionSettingsAsync(parts[1], port, tls, cancellationToken).ConfigureAwait(false);
            return new LocalCommandResult(false, $"Connected to {parts[1]}:{port}{(tls ? " using TLS" : string.Empty)}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LocalCommandResult(false, $"Connect failed: {exception.Message}");
        }
    }

    private async Task<LocalCommandResult> ConfigureAvendarAsync(
        string[] parts,
        CancellationToken cancellationToken)
    {
        if (parts.Length == 2 && string.Equals(parts[1], "prompt", StringComparison.OrdinalIgnoreCase))
        {
            const string telemetryPrompt = "prompt [J|%h/%H|%m/%M|%v/%V|%x|%X|%s|%r|%e|%T|%d]";
            await QueueMudCommandAsync(telemetryPrompt, InputSourceKind.UiAction, cancellationToken).ConfigureAwait(false);
            return new LocalCommandResult(false, "Avendar telemetry prompt requested for this character.");
        }

        return new LocalCommandResult(false, "Usage: :avendar prompt");
    }

    private async Task QueueMudCommandAsync(string command, InputSourceKind source, CancellationToken cancellationToken)
    {
        StateSnapshot state = _runtime.State.Current;
        bool sensitive = state.Session.InputMode == SessionInputMode.LoginPassword;
        ScriptCommandOrigin origin = source switch
        {
            InputSourceKind.Keybinding => ScriptCommandOrigin.Keybinding,
            InputSourceKind.Automation => ScriptCommandOrigin.Automation,
            InputSourceKind.Script => ScriptCommandOrigin.Script,
            InputSourceKind.Mapper => ScriptCommandOrigin.Mapper,
            InputSourceKind.Jev => ScriptCommandOrigin.Jev,
            _ => ScriptCommandOrigin.User
        };
        string ownerId = origin switch
        {
            ScriptCommandOrigin.Keybinding => "keybinding",
            ScriptCommandOrigin.Automation => "automation-input",
            ScriptCommandOrigin.Script => "script-input",
            ScriptCommandOrigin.Mapper => "mapper-input",
            ScriptCommandOrigin.Jev => "jev-input",
            _ => "user"
        };
        string ownerName = origin switch
        {
            ScriptCommandOrigin.Keybinding => "Keybinding",
            ScriptCommandOrigin.Automation => "Automation",
            ScriptCommandOrigin.Script => "Script",
            ScriptCommandOrigin.Mapper => "Mapper",
            ScriptCommandOrigin.Jev => "Jev",
            _ => "User"
        };
        ScriptCommandResult result = await _runtime.ScriptCommands.SendAsync(
            new ScriptCommandRequest(
                command,
                origin,
                ownerId,
                ownerName,
                $"{ownerName} command",
                Sensitive: sensitive,
                ExpectedStateVersion: state.Version),
            cancellationToken).ConfigureAwait(false);
        if (!result.Accepted)
            throw new InvalidOperationException(result.Reason ?? "Command was rejected.");
    }

    private async Task<LocalCommandResult> ConfigureJevAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 3 && string.Equals(parts[1], "preset", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParsePreset(parts[2], out JevPreset preset))
            {
                return new LocalCommandResult(false, "Preset must be off, copilot, bot, or autonomous.");
            }

            JevAuthoritySnapshot profile = JevAuthorityService.CreatePreset(preset);
            await _runtime.ApplyAndSaveAuthorityAsync(profile, cancellationToken).ConfigureAwait(false);
            return new LocalCommandResult(false, $"Jev preset: {preset}.");
        }

        if (parts.Length == 3 &&
            Enum.TryParse(parts[1], ignoreCase: true, out JevDomain domain) &&
            Enum.TryParse(parts[2], ignoreCase: true, out JevAuthority authority))
        {
            Dictionary<JevDomain, JevAuthority> domains = _runtime.Authority.Current.Domains
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            domains[domain] = authority;
            JevAuthoritySnapshot profile = JevAuthoritySnapshot.Create(JevPreset.Custom, domains);
            await _runtime.ApplyAndSaveAuthorityAsync(profile, cancellationToken).ConfigureAwait(false);
            return new LocalCommandResult(false, $"Jev {domain}: {authority}.");
        }

        if (parts.Length == 2 && string.Equals(parts[1], "status", StringComparison.OrdinalIgnoreCase))
        {
            JevAuthoritySnapshot current = _runtime.Authority.Current;
            string matrix = string.Join(", ", current.Domains.Select(pair => $"{pair.Key}={pair.Value}"));
            return new LocalCommandResult(false, $"Preset={current.Preset}; {matrix}");
        }

        return new LocalCommandResult(false, "Usage: :jev preset <off|copilot|bot|autonomous> | :jev <domain> <off|observe|suggest|approve|auto> | :jev status");
    }

    private static bool TryParsePreset(string value, out JevPreset preset)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out preset) || preset == JevPreset.Custom)
        {
            preset = default;
            return false;
        }
        return true;
    }

    private const string HelpText = """
Local commands:
  :connect <host> <port> [tls]
  :disconnect
  :jev preset <off|copilot|bot|autonomous>
  :jev <domain> <off|observe|suggest|approve|auto>
  :jev status
  :avendar prompt
  :help
  :quit

Keys:
  Up/Down      command history
  Tab/Shift+Tab contextual completion (GUI)
  PageUp/Down  game scrollback
  Mouse wheel  game scrollback
  Primary+K    command palette (GUI)
  Primary+F    search transcript (GUI)
  Primary+G    return to live output (GUI)
  Primary+L    toggle logging (GUI)
  F4           Jev authority settings (TUI)
  Esc          cancel/close dialogs only
  Ctrl+Q       quit NexMUD (TUI); Command-Q on macOS GUI

Plain input is sent to the MUD. Blank input sends Enter for pagers.
""";
}

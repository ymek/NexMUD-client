using System.Text.Json;
using System.Text.Json.Serialization;
using JevMud.Contracts.Jev;

namespace JevMud.Client.Settings;

public sealed class ClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientSettingsStore(string? path = null)
    {
        Path = path ?? GetDefaultPath();
    }

    public string Path { get; }

    public async Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return ClientSettings.Default;
            }

            await using FileStream stream = File.OpenRead(Path);
            ClientSettings? loaded = await JsonSerializer.DeserializeAsync<ClientSettings>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return Normalize(loaded ?? ClientSettings.Default);
        }
        catch (JsonException)
        {
            return ClientSettings.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ClientSettings normalized = Normalize(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = Path + ".tmp";
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string GetDefaultPath() =>
        JevMud.Client.Paths.NexMudDataPaths.GetFilePath("settings.json");

    private static ClientSettings Normalize(ClientSettings settings)
    {
        Dictionary<JevDomain, JevAuthority> domains = Enum.GetValues<JevDomain>()
            .ToDictionary(
                domain => domain,
                domain => settings.JevDomains.TryGetValue(domain, out JevAuthority authority)
                    ? authority
                    : JevAuthority.Off);

        string host = string.IsNullOrWhiteSpace(settings.Host) ? "avendar.net" : settings.Host.Trim();
        int port = settings.Port is >= 1 and <= 65535 ? settings.Port : 9999;
        string terminalType = string.IsNullOrWhiteSpace(settings.TerminalType)
            ? "xterm-256color"
            : settings.TerminalType.Trim();
        double transcriptFontSize = Math.Clamp(settings.TranscriptFontSize, 8, 24);
        string jevModel = string.IsNullOrWhiteSpace(settings.JevModel) ? "jev-latest" : settings.JevModel.Trim();
        TranscriptHighlightRule[] highlights = (settings.HighlightRules ?? Array.Empty<TranscriptHighlightRule>())
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern) && IsHexColor(rule.Foreground))
            .Take(100)
            .Select(rule => rule with
            {
                Pattern = rule.Pattern.Trim(),
                Foreground = rule.Foreground.Trim().ToUpperInvariant()
            })
            .ToArray();
        CommandAlias[] aliases = (settings.Aliases ?? Array.Empty<CommandAlias>())
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Name) && !string.IsNullOrWhiteSpace(alias.Expansion))
            .Take(100)
            .Select(alias => alias with { Name = alias.Name.Trim(), Expansion = alias.Expansion.Trim() })
            .ToArray();
        TriggerRule[] triggers = (settings.Triggers ?? Array.Empty<TriggerRule>())
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern) && !string.IsNullOrWhiteSpace(rule.Command))
            .Take(100)
            .Select(rule => rule with
            {
                Pattern = rule.Pattern.Trim(),
                Command = rule.Command.Trim(),
                Group = string.IsNullOrWhiteSpace(rule.Group) ? "Default" : rule.Group.Trim(),
                Priority = Math.Clamp(rule.Priority, -10_000, 10_000),
                CooldownMilliseconds = Math.Clamp(rule.CooldownMilliseconds, 0, 600_000)
            })
            .ToArray();
        GameRule[] gameRules = (settings.GameRules ?? Array.Empty<GameRule>())
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Name) && !string.IsNullOrWhiteSpace(rule.Condition) && !string.IsNullOrWhiteSpace(rule.Command))
            .Take(200)
            .Select(rule => rule with
            {
                Name = rule.Name.Trim(),
                Condition = rule.Condition.Trim(),
                Command = rule.Command.Trim(),
                Group = string.IsNullOrWhiteSpace(rule.Group) ? "Default" : rule.Group.Trim(),
                CooldownMilliseconds = Math.Clamp(rule.CooldownMilliseconds, 0, 600_000),
                Priority = Math.Clamp(rule.Priority, -10_000, 10_000)
            })
            .ToArray();
        AutomationWorkflow[] workflows = (settings.Workflows ?? Array.Empty<AutomationWorkflow>())
            .Where(workflow => !string.IsNullOrWhiteSpace(workflow.Name) && !string.IsNullOrWhiteSpace(workflow.Steps))
            .Take(100)
            .Select(workflow => workflow with
            {
                Name = workflow.Name.Trim(),
                Steps = workflow.Steps.Trim(),
                TriggerCondition = string.IsNullOrWhiteSpace(workflow.TriggerCondition) ? null : workflow.TriggerCondition.Trim(),
                TriggerEvent = string.IsNullOrWhiteSpace(workflow.TriggerEvent) ? null : workflow.TriggerEvent.Trim(),
                Group = string.IsNullOrWhiteSpace(workflow.Group) ? "Default" : workflow.Group.Trim(),
                Priority = Math.Clamp(workflow.Priority, -10_000, 10_000),
                CooldownMilliseconds = Math.Clamp(workflow.CooldownMilliseconds, 0, 600_000)
            })
            .ToArray();
        CommandTimer[] timers = (settings.Timers ?? Array.Empty<CommandTimer>())
            .Where(timer => !string.IsNullOrWhiteSpace(timer.Name) && !string.IsNullOrWhiteSpace(timer.Command))
            .Take(100)
            .Select(timer => timer with
            {
                Name = timer.Name.Trim(),
                Command = timer.Command.Trim(),
                Group = string.IsNullOrWhiteSpace(timer.Group) ? "Default" : timer.Group.Trim(),
                IntervalSeconds = Math.Clamp(timer.IntervalSeconds, 1, 86_400)
            })
            .ToArray();
        CommandKeyBinding[] keyBindings = (settings.KeyBindings ?? Array.Empty<CommandKeyBinding>())
            .Where(binding => !string.IsNullOrWhiteSpace(binding.Gesture) && !string.IsNullOrWhiteSpace(binding.Command))
            .Take(100)
            .Select(binding => binding with
            {
                Gesture = binding.Gesture.Trim(),
                Command = binding.Command.Trim()
            })
            .ToArray();
        WorkspacePreferences workspace = settings.Workspace ?? new WorkspacePreferences();
        double dockWidth = Math.Abs(workspace.DockWidth - 390d) < 0.01 ? 360d : workspace.DockWidth;
        workspace = workspace with
        {
            WindowWidth = Math.Clamp(workspace.WindowWidth, 1080, 7680),
            WindowHeight = Math.Clamp(workspace.WindowHeight, 700, 4320),
            DockWidth = Math.Clamp(dockWidth, 280, 900),
            DockView = string.IsNullOrWhiteSpace(workspace.DockView) ? "Context" : workspace.DockView.Trim(),
            LiveSplitHeight = Math.Clamp(workspace.LiveSplitHeight, 110, 700)
        };
        AutomationPreferences automation = settings.Automation ?? new AutomationPreferences();
        automation = automation with
        {
            MaxCommandsPerSecond = Math.Clamp(automation.MaxCommandsPerSecond, 1, 50),
            RollingBufferCharacters = Math.Clamp(automation.RollingBufferCharacters, 1024, 65536),
            HumanOverrideMilliseconds = Math.Clamp(automation.HumanOverrideMilliseconds, 0, 30_000),
            MaxConcurrentWorkflows = Math.Clamp(automation.MaxConcurrentWorkflows, 1, 16),
            DisabledGroups = (automation.DisabledGroups ?? Array.Empty<string>())
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToArray()
        };
        ProtocolPreferences protocols = settings.Protocols ?? new ProtocolPreferences();
        string commandSeparator = settings.CommandSeparator ?? ";";
        if (commandSeparator.Length > 1 || commandSeparator.Any(char.IsWhiteSpace))
        {
            commandSeparator = ";";
        }
        MapperPreferences mapper = settings.Mapper ?? new MapperPreferences();
        mapper = mapper with
        {
            MaximumRouteDepth = Math.Clamp(mapper.MaximumRouteDepth, 1, 5000),
            AutoMoveStepDelayMilliseconds = Math.Clamp(mapper.AutoMoveStepDelayMilliseconds, 0, 5000),
            AutoMoveStepTimeoutMilliseconds = Math.Clamp(mapper.AutoMoveStepTimeoutMilliseconds, 1000, 30000),
            AutoMoveMaximumReplans = Math.Clamp(mapper.AutoMoveMaximumReplans, 0, 25),
            VisualMapDepth = Math.Clamp(mapper.VisualMapDepth, 1, 50),
            VisualMapMaximumRooms = Math.Clamp(mapper.VisualMapMaximumRooms, 10, 250),
            AvoidAreas = NormalizeList(mapper.AvoidAreas, 100),
            AvoidTerrains = NormalizeList(mapper.AvoidTerrains, 100),
            AvoidMobNames = NormalizeList(mapper.AvoidMobNames, 200),
            DoorOpenCommandTemplate = NormalizeDoorOpenTemplate(mapper.DoorOpenCommandTemplate)
        };

        return settings with
        {
            JevDomains = domains,
            Host = host,
            Port = port,
            TerminalType = terminalType,
            TranscriptFontSize = transcriptFontSize,
            JevModel = jevModel,
            HighlightRules = highlights,
            Aliases = aliases,
            Triggers = triggers,
            GameRules = gameRules,
            Workflows = workflows,
            Timers = timers,
            KeyBindings = keyBindings,
            Workspace = workspace,
            Automation = automation,
            Protocols = protocols,
            Mapper = mapper,
            CommandSeparator = commandSeparator
        };
    }


    private static string NormalizeDoorOpenTemplate(string? value)
    {
        string template = string.IsNullOrWhiteSpace(value) ? "open {direction}" : value.Trim();
        if (template.Length > 120) template = template[..120];
        return template.Contains("{direction}", StringComparison.OrdinalIgnoreCase) ? template : "open {direction}";
    }

    private static string[] NormalizeList(IReadOnlyList<string>? values, int maximum) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToArray();

    private static bool IsHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (text.Length != 7 || text[0] != '#')
        {
            return false;
        }

        return text.Skip(1).All(Uri.IsHexDigit);
    }

    public static ClientSettings WithAuthority(ClientSettings settings, JevAuthoritySnapshot authority) =>
        settings with
        {
            JevPreset = authority.Preset,
            JevDomains = authority.Domains.ToDictionary(pair => pair.Key, pair => pair.Value)
        };
}

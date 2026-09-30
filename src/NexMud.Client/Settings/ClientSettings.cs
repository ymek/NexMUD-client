using System.Text.Json.Serialization;
using NexMud.Contracts.Jev;

namespace NexMud.Client.Settings;

public sealed record ClientSettings(
    JevPreset JevPreset,
    Dictionary<JevDomain, JevAuthority> JevDomains,
    string Host,
    int Port,
    bool UseTls,
    string TerminalType = "xterm-256color",
    double TranscriptFontSize = 14,
    bool ShowContextDock = true,
    bool AutoOpenCombatContext = true,
    string JevModel = "jev-latest",
    bool AutoLogSessions = false,
    bool SlurpTelemetryPrompt = false,
    IReadOnlyList<TranscriptHighlightRule>? HighlightRules = null,
    TranscriptLogFormat LogFormat = TranscriptLogFormat.AnsiText,
    IReadOnlyList<CommandAlias>? Aliases = null,
    IReadOnlyList<TriggerRule>? Triggers = null,
    IReadOnlyList<GameRule>? GameRules = null,
    IReadOnlyList<AutomationWorkflow>? Workflows = null,
    IReadOnlyList<CommandTimer>? Timers = null,
    IReadOnlyList<CommandKeyBinding>? KeyBindings = null,
    WorkspacePreferences? Workspace = null,
    AutomationPreferences? Automation = null,
    ProtocolPreferences? Protocols = null,
    MapperPreferences? Mapper = null,
    bool JevEnabled = true,
    string CommandSeparator = ";",
    InputPreferences? Input = null,
    OutputPreferences? Output = null,
    IReadOnlyList<OutputTransformationRule>? OutputRules = null,
    IReadOnlyList<ConnectionProfile>? ConnectionProfiles = null,
    string? ActiveConnectionProfileId = null,
    GeneralPreferences? General = null,
    AppearancePreferences? Appearance = null,
    IReadOnlyList<SemanticTriggerRule>? SemanticTriggers = null)
{
    public static ClientSettings Default { get; } = new(
        JevPreset.Off,
        Enum.GetValues<JevDomain>().ToDictionary(domain => domain, _ => JevAuthority.Off),
        "avendar.net",
        9999,
        false,
        "xterm-256color",
        14,
        true,
        true,
        "jev-latest",
        false,
        false,
        [],
        TranscriptLogFormat.AnsiText,
        [],
        [],
        [],
        [],
        [],
        [],
        new WorkspacePreferences(),
        new AutomationPreferences(),
        new ProtocolPreferences(),
        new MapperPreferences(),
        true,
        ";",
        null,
        null,
        null,
        [ConnectionProfile.Default],
        ConnectionProfile.Default.Id,
        new GeneralPreferences(),
        new AppearancePreferences(),
        []);

    [JsonIgnore]
    public IReadOnlyList<ConnectionProfile> EffectiveConnectionProfiles =>
        ConnectionProfiles is { Count: > 0 } ? ConnectionProfiles : [ConnectionProfile.Default];

    [JsonIgnore]
    public ConnectionProfile ActiveConnectionProfile
    {
        get
        {
            IReadOnlyList<ConnectionProfile> profiles = EffectiveConnectionProfiles;
            return profiles.FirstOrDefault(profile =>
                       string.Equals(profile.Id, ActiveConnectionProfileId, StringComparison.Ordinal))
                   ?? profiles[0];
        }
    }
}

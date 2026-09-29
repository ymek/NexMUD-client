using System.Text.Json.Serialization;

namespace NexMud.Client.Settings;

public enum ProtocolPolicy
{
    Auto,
    Enabled,
    Disabled
}

public sealed record ConnectionProtocolPreferences(
    ProtocolPolicy Naws = ProtocolPolicy.Auto,
    ProtocolPolicy Gmcp = ProtocolPolicy.Auto,
    ProtocolPolicy Msdp = ProtocolPolicy.Auto,
    ProtocolPolicy Mssp = ProtocolPolicy.Auto,
    ProtocolPolicy Mccp2 = ProtocolPolicy.Auto,
    ProtocolPolicy Charset = ProtocolPolicy.Auto,
    ProtocolPolicy NewEnvironment = ProtocolPolicy.Auto,
    ProtocolPolicy Mtts = ProtocolPolicy.Auto,
    ProtocolPolicy Eor = ProtocolPolicy.Auto)
{
    public bool Allows(ProtocolPolicy policy) => policy != ProtocolPolicy.Disabled;

    public static ProtocolPolicy FromLegacy(bool enabled) =>
        enabled ? ProtocolPolicy.Auto : ProtocolPolicy.Disabled;
}

public sealed record ConnectionProfile(
    string Id,
    string Name,
    string Host,
    int Port,
    bool UseTls = false,
    string TerminalType = "xterm-256color",
    ConnectionProtocolPreferences? Protocols = null)
{
    public static ConnectionProfile Default { get; } = new(
        "avendar",
        "Avendar",
        "avendar.net",
        9999,
        false,
        "xterm-256color",
        new ConnectionProtocolPreferences());

    [JsonIgnore]
    public ConnectionProtocolPreferences EffectiveProtocols => Protocols ?? new ConnectionProtocolPreferences();
}

public sealed record GeneralPreferences(
    bool RestorePreviousWorkspace = true,
    bool ReconnectLastConnectionProfile = true,
    bool ShowGameplayRailByDefault = true);

public enum ClientDensity
{
    Compact,
    Comfortable
}

public sealed record AppearancePreferences(
    string Theme = "Dark",
    string AccentPalette = "EmberBrass",
    double UiScale = 1.0,
    ClientDensity Density = ClientDensity.Compact,
    string InterfaceFont = "Inter, SF Pro Text, Helvetica Neue, sans-serif",
    string TranscriptFont = "Menlo, SFMono-Regular, Consolas, monospace",
    double TranscriptSize = 14,
    double TranscriptLineSpacing = 1.1428571428571428);

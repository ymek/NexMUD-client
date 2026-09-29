namespace JevMud.Contracts.Transport;

public sealed record MudProtocolOptions(
    bool Naws = true,
    bool Gmcp = true,
    bool Msdp = true,
    bool Mssp = true,
    bool Mccp2 = true,
    bool Charset = true,
    bool NewEnvironment = true,
    bool Mtts = true,
    bool Eor = true);

public sealed record MudConnectionOptions(
    string Host,
    int Port,
    bool UseTls = false,
    string TerminalType = "xterm-256color",
    ushort Columns = 120,
    ushort Rows = 40,
    MudProtocolOptions? Protocols = null,
    string ClientName = "NexMUD",
    string ClientVersion = "0.28.0")
{
    public MudProtocolOptions EffectiveProtocols => Protocols ?? new MudProtocolOptions();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host is required.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(TerminalType))
        {
            throw new ArgumentException("Terminal type is required.", nameof(TerminalType));
        }

        if (Columns == 0 || Rows == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Columns), "Terminal dimensions must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(ClientName))
        {
            throw new ArgumentException("Client name is required.", nameof(ClientName));
        }

        if (string.IsNullOrWhiteSpace(ClientVersion))
        {
            throw new ArgumentException("Client version is required.", nameof(ClientVersion));
        }
    }
}

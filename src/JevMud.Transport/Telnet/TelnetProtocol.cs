namespace JevMud.Transport.Telnet;

internal static class TelnetProtocol
{
    public const byte EorCommand = 239;
    public const byte Se = 240;
    public const byte Ga = 249;
    public const byte Sb = 250;
    public const byte Will = 251;
    public const byte Wont = 252;
    public const byte Do = 253;
    public const byte Dont = 254;
    public const byte Iac = 255;

    public const byte Echo = 1;
    public const byte SuppressGoAhead = 3;
    public const byte TerminalType = 24;
    public const byte Eor = 25;
    public const byte Naws = 31;
    public const byte NewEnvironment = 39;
    public const byte Charset = 42;
    public const byte Msdp = 69;
    public const byte Mssp = 70;
    public const byte Mccp2 = 86;
    public const byte Gmcp = 201;

    public const byte TerminalTypeIs = 0;
    public const byte TerminalTypeSend = 1;

    public const byte CharsetRequest = 1;
    public const byte CharsetAccepted = 2;
    public const byte CharsetRejected = 3;
    public const byte CharsetTtableIs = 4;
    public const byte CharsetTtableRejected = 5;
    public const byte CharsetTtableAck = 6;
    public const byte CharsetTtableNak = 7;
    public const byte NewEnvironmentIs = 0;
    public const byte NewEnvironmentSend = 1;
    public const byte NewEnvironmentVar = 0;
    public const byte NewEnvironmentValue = 1;
    public const byte NewEnvironmentEsc = 2;
    public const byte NewEnvironmentUserVar = 3;

    public const byte MsdpVar = 1;
    public const byte MsdpVal = 2;
    public const byte MsdpTableOpen = 3;
    public const byte MsdpTableClose = 4;
    public const byte MsdpArrayOpen = 5;
    public const byte MsdpArrayClose = 6;
    public const byte MsspVar = 1;
    public const byte MsspVal = 2;
}

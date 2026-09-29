using System.Text;

namespace JevMud.Transport.Telnet;

internal static class TelnetFrameBuilder
{
    public static byte[] Negotiate(byte command, byte option) => [TelnetProtocol.Iac, command, option];

    public static byte[] Subnegotiation(byte option, IEnumerable<byte> payload)
    {
        List<byte> result = [TelnetProtocol.Iac, TelnetProtocol.Sb, option];
        foreach (byte value in payload)
        {
            result.Add(value);
            if (value == TelnetProtocol.Iac) result.Add(value);
        }
        result.Add(TelnetProtocol.Iac);
        result.Add(TelnetProtocol.Se);
        return result.ToArray();
    }

    public static byte[] Naws(ushort columns, ushort rows) => Subnegotiation(
        TelnetProtocol.Naws,
        [
            (byte)(columns >> 8), (byte)(columns & 0xff),
            (byte)(rows >> 8), (byte)(rows & 0xff)
        ]);

    public static byte[] Gmcp(string module, string? payload = null)
    {
        string message = string.IsNullOrWhiteSpace(payload) ? module : $"{module} {payload}";
        return Subnegotiation(TelnetProtocol.Gmcp, Encoding.UTF8.GetBytes(message));
    }
}

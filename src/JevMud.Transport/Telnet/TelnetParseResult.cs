using JevMud.Contracts.Transport;

namespace JevMud.Transport.Telnet;

public sealed record GmcpFrame(string Module, string Payload);
public sealed record MsdpFrame(IReadOnlyDictionary<string, MsdpValue> Values);
public sealed record MsspFrame(IReadOnlyDictionary<string, IReadOnlyList<string>> Values);
public sealed record TelnetProtocolTransition(string Protocol, bool Enabled, string? Detail = null);
public sealed record TelnetProtocolIssue(string Protocol, string Message, bool Fatal = false);

public sealed record TelnetParseResult(
    byte[] TextBytes,
    IReadOnlyList<byte[]> Responses,
    IReadOnlyList<GmcpFrame> GmcpFrames,
    IReadOnlyList<MsdpFrame> MsdpFrames,
    IReadOnlyList<MsspFrame> MsspFrames,
    IReadOnlyList<TelnetProtocolTransition> ProtocolTransitions,
    IReadOnlyList<TelnetProtocolIssue> Issues,
    IReadOnlyList<string> PromptBoundaries,
    bool Mccp2Started = false,
    byte[]? CompressedRemainder = null);

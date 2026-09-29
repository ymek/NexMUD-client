using System.Text;
using System.Text.Json;
using JevMud.Contracts.Transport;

namespace JevMud.Transport.Telnet;

/// <summary>
/// Stateful Telnet parser and capability negotiator. The parser deliberately keeps transport
/// semantics here: game-specific interpretation belongs in adapters consuming the emitted OOB events.
/// </summary>
public sealed class TelnetParser
{
    private const int MaximumSubnegotiationBytes = 64 * 1024;

    private enum ParserState
    {
        Data,
        Iac,
        NegotiationOption,
        SubnegotiationOption,
        SubnegotiationData,
        SubnegotiationIac,
        DiscardSubnegotiation,
        DiscardSubnegotiationIac
    }

    private readonly MudConnectionOptions _options;
    private readonly List<byte> _subnegotiation = [];
    private readonly HashSet<byte> _localEnabled = [];
    private readonly HashSet<byte> _remoteEnabled = [];
    private readonly HashSet<byte> _localRejected = [];
    private readonly HashSet<byte> _remoteRejected = [];
    private ParserState _state;
    private byte _negotiationCommand;
    private byte _subnegotiationOption;
    private int _terminalTypeRequests;
    private bool _subnegotiationOverflowReported;

    public TelnetParser(MudConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsLocalOptionEnabled(byte option) => _localEnabled.Contains(option);
    public bool IsRemoteOptionEnabled(byte option) => _remoteEnabled.Contains(option);

    public TelnetParseResult Process(ReadOnlySpan<byte> bytes)
    {
        List<byte> text = [];
        List<byte[]> responses = [];
        List<GmcpFrame> gmcp = [];
        List<MsdpFrame> msdp = [];
        List<MsspFrame> mssp = [];
        List<TelnetProtocolTransition> transitions = [];
        List<TelnetProtocolIssue> issues = [];
        List<string> promptBoundaries = [];
        bool mccp2Started = false;
        byte[]? compressedRemainder = null;

        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            switch (_state)
            {
                case ParserState.Data:
                    if (value == TelnetProtocol.Iac) _state = ParserState.Iac;
                    else text.Add(value);
                    break;

                case ParserState.Iac:
                    HandleIac(value, text, promptBoundaries);
                    break;

                case ParserState.NegotiationOption:
                    HandleNegotiation(_negotiationCommand, value, responses, transitions);
                    _state = ParserState.Data;
                    break;

                case ParserState.SubnegotiationOption:
                    _subnegotiationOption = value;
                    _subnegotiation.Clear();
                    _subnegotiationOverflowReported = false;
                    _state = ParserState.SubnegotiationData;
                    break;

                case ParserState.SubnegotiationData:
                    if (value == TelnetProtocol.Iac)
                    {
                        _state = ParserState.SubnegotiationIac;
                    }
                    else if (_subnegotiation.Count >= MaximumSubnegotiationBytes)
                    {
                        ReportOversizedSubnegotiation(issues);
                        _state = ParserState.DiscardSubnegotiation;
                    }
                    else
                    {
                        _subnegotiation.Add(value);
                    }
                    break;

                case ParserState.SubnegotiationIac:
                    if (value == TelnetProtocol.Iac)
                    {
                        if (_subnegotiation.Count >= MaximumSubnegotiationBytes)
                        {
                            ReportOversizedSubnegotiation(issues);
                            _state = ParserState.DiscardSubnegotiation;
                        }
                        else
                        {
                            _subnegotiation.Add(TelnetProtocol.Iac);
                            _state = ParserState.SubnegotiationData;
                        }
                    }
                    else if (value == TelnetProtocol.Se)
                    {
                        mccp2Started = HandleSubnegotiation(responses, gmcp, msdp, mssp, issues);
                        _state = ParserState.Data;
                        if (mccp2Started)
                        {
                            compressedRemainder = index + 1 < bytes.Length
                                ? bytes[(index + 1)..].ToArray()
                                : Array.Empty<byte>();
                            index = bytes.Length;
                        }
                    }
                    else
                    {
                        issues.Add(new TelnetProtocolIssue(
                            ProtocolName(_subnegotiationOption),
                            $"Invalid IAC {value} inside Telnet subnegotiation; frame discarded."));
                        _subnegotiation.Clear();
                        _state = ParserState.Data;
                    }
                    break;

                case ParserState.DiscardSubnegotiation:
                    if (value == TelnetProtocol.Iac) _state = ParserState.DiscardSubnegotiationIac;
                    break;

                case ParserState.DiscardSubnegotiationIac:
                    _state = value == TelnetProtocol.Se
                        ? ParserState.Data
                        : ParserState.DiscardSubnegotiation;
                    if (value == TelnetProtocol.Se) _subnegotiation.Clear();
                    break;

                default:
                    throw new InvalidOperationException($"Unknown Telnet parser state {_state}.");
            }
        }

        return new TelnetParseResult(
            text.ToArray(),
            responses,
            gmcp,
            msdp,
            mssp,
            transitions,
            issues,
            promptBoundaries,
            mccp2Started,
            compressedRemainder);
    }

    private void HandleIac(byte value, List<byte> text, List<string> promptBoundaries)
    {
        if (value == TelnetProtocol.Iac)
        {
            text.Add(value);
            _state = ParserState.Data;
            return;
        }

        if (value is TelnetProtocol.Will or TelnetProtocol.Wont or TelnetProtocol.Do or TelnetProtocol.Dont)
        {
            _negotiationCommand = value;
            _state = ParserState.NegotiationOption;
            return;
        }

        if (value == TelnetProtocol.Sb)
        {
            _state = ParserState.SubnegotiationOption;
            return;
        }

        if (value == TelnetProtocol.Ga)
        {
            promptBoundaries.Add("GA");
        }
        else if (value == TelnetProtocol.EorCommand &&
                 _options.EffectiveProtocols.Eor &&
                 _remoteEnabled.Contains(TelnetProtocol.Eor))
        {
            promptBoundaries.Add("EOR");
        }

        _state = ParserState.Data;
    }

    private void HandleNegotiation(
        byte command,
        byte option,
        List<byte[]> responses,
        List<TelnetProtocolTransition> transitions)
    {
        if (command == TelnetProtocol.Do)
        {
            bool supported = SupportsLocal(option);
            if (supported)
            {
                _localRejected.Remove(option);
                if (_localEnabled.Add(option))
                {
                    responses.Add(TelnetFrameBuilder.Negotiate(TelnetProtocol.Will, option));
                    transitions.Add(new TelnetProtocolTransition(ProtocolName(option), true, "Client capability enabled"));
                    if (option == TelnetProtocol.Naws)
                    {
                        responses.Add(TelnetFrameBuilder.Naws(_options.Columns, _options.Rows));
                    }
                }
            }
            else if (_localRejected.Add(option))
            {
                responses.Add(TelnetFrameBuilder.Negotiate(TelnetProtocol.Wont, option));
            }
            return;
        }

        if (command == TelnetProtocol.Dont)
        {
            _localRejected.Remove(option);
            if (_localEnabled.Remove(option))
            {
                if (option == TelnetProtocol.TerminalType) _terminalTypeRequests = 0;
                responses.Add(TelnetFrameBuilder.Negotiate(TelnetProtocol.Wont, option));
                transitions.Add(new TelnetProtocolTransition(ProtocolName(option), false, "Server disabled client capability"));
            }
            return;
        }

        if (command == TelnetProtocol.Will)
        {
            bool supported = SupportsRemote(option);
            if (supported)
            {
                _remoteRejected.Remove(option);
                if (_remoteEnabled.Add(option))
                {
                    responses.Add(TelnetFrameBuilder.Negotiate(TelnetProtocol.Do, option));
                    transitions.Add(new TelnetProtocolTransition(ProtocolName(option), true, "Server capability enabled"));
                    AddProtocolActivationRequests(option, responses);
                }
            }
            else if (_remoteRejected.Add(option))
            {
                responses.Add(TelnetFrameBuilder.Negotiate(TelnetProtocol.Dont, option));
            }
            return;
        }

        if (command == TelnetProtocol.Wont)
        {
            _remoteRejected.Remove(option);
            if (_remoteEnabled.Remove(option))
            {
                responses.Add(TelnetFrameBuilder.Negotiate(TelnetProtocol.Dont, option));
                transitions.Add(new TelnetProtocolTransition(ProtocolName(option), false, "Server capability disabled"));
            }
        }
    }

    private bool SupportsLocal(byte option)
    {
        MudProtocolOptions protocols = _options.EffectiveProtocols;
        return option switch
        {
            TelnetProtocol.TerminalType => true,
            TelnetProtocol.Naws => protocols.Naws,
            // GMCP/MSDP are server capabilities: the server offers WILL and NexMUD answers DO.
            // Do not advertise them in the inverse direction when a server sends DO.
            TelnetProtocol.Charset => protocols.Charset,
            TelnetProtocol.NewEnvironment => protocols.NewEnvironment,
            TelnetProtocol.SuppressGoAhead => true,
            _ => false
        };
    }

    private bool SupportsRemote(byte option)
    {
        MudProtocolOptions protocols = _options.EffectiveProtocols;
        return option switch
        {
            TelnetProtocol.Echo => true,
            TelnetProtocol.SuppressGoAhead => true,
            TelnetProtocol.Eor => protocols.Eor,
            TelnetProtocol.Gmcp => protocols.Gmcp,
            TelnetProtocol.Msdp => protocols.Msdp,
            TelnetProtocol.Mssp => protocols.Mssp,
            TelnetProtocol.Mccp2 => protocols.Mccp2,
            _ => false
        };
    }

    private void AddProtocolActivationRequests(byte option, List<byte[]> responses)
    {
        if (option == TelnetProtocol.Msdp)
        {
            responses.Add(BuildMsdpAssignment("CLIENT_NAME", _options.ClientName));
            responses.Add(BuildMsdpAssignment("CLIENT_VERSION", _options.ClientVersion));
            responses.Add(BuildMsdpAssignment("LIST", "REPORTABLE_VARIABLES"));
            return;
        }

        if (option == TelnetProtocol.Gmcp)
        {
            string hello = JsonSerializer.Serialize(new
            {
                client = _options.ClientName,
                version = _options.ClientVersion
            });
            responses.Add(TelnetFrameBuilder.Gmcp("Core.Hello", hello));
            // Core.Supports.Set enumerates optional GMCP packages NexMUD semantically implements.
            // Raw GMCP transport is generic, but we do not claim game-package support prematurely.
            responses.Add(TelnetFrameBuilder.Gmcp("Core.Supports.Set", "[]"));
        }
    }

    private bool HandleSubnegotiation(
        List<byte[]> responses,
        List<GmcpFrame> gmcp,
        List<MsdpFrame> msdp,
        List<MsspFrame> mssp,
        List<TelnetProtocolIssue> issues)
    {
        bool mccp2Started = false;
        try
        {
            if (_subnegotiationOption == TelnetProtocol.TerminalType &&
                _subnegotiation.Count > 0 && _subnegotiation[0] == TelnetProtocol.TerminalTypeSend)
            {
                responses.Add(BuildTerminalType());
            }
            else if (_subnegotiationOption == TelnetProtocol.Gmcp && _options.EffectiveProtocols.Gmcp)
            {
                string message = Encoding.UTF8.GetString(_subnegotiation.ToArray()).Trim();
                if (!string.IsNullOrEmpty(message))
                {
                    int separator = message.IndexOf(' ');
                    string module = separator < 0 ? message : message[..separator];
                    string payload = separator < 0 ? string.Empty : message[(separator + 1)..].Trim();
                    gmcp.Add(new GmcpFrame(module, payload));
                }
            }
            else if (_subnegotiationOption == TelnetProtocol.Msdp && _options.EffectiveProtocols.Msdp)
            {
                IReadOnlyDictionary<string, MsdpValue> values = MsdpCodec.Parse(_subnegotiation);
                if (values.Count > 0)
                {
                    msdp.Add(new MsdpFrame(values));
                    if (values.TryGetValue("REPORTABLE_VARIABLES", out MsdpValue? reportable))
                    {
                        responses.AddRange(BuildMsdpReports(reportable));
                    }
                }
            }
            else if (_subnegotiationOption == TelnetProtocol.Mssp && _options.EffectiveProtocols.Mssp)
            {
                IReadOnlyDictionary<string, IReadOnlyList<string>> values = ParseMsspPairs(_subnegotiation);
                if (values.Count > 0) mssp.Add(new MsspFrame(values));
            }
            else if (_subnegotiationOption == TelnetProtocol.Charset && _options.EffectiveProtocols.Charset)
            {
                byte[]? charset = BuildCharsetResponse();
                if (charset is not null) responses.Add(charset);
            }
            else if (_subnegotiationOption == TelnetProtocol.NewEnvironment && _options.EffectiveProtocols.NewEnvironment)
            {
                if (_subnegotiation.Count > 0 && _subnegotiation[0] == TelnetProtocol.NewEnvironmentSend)
                {
                    responses.Add(BuildNewEnvironment());
                }
            }
            else if (_subnegotiationOption == TelnetProtocol.Mccp2 &&
                     _options.EffectiveProtocols.Mccp2 &&
                     _remoteEnabled.Contains(TelnetProtocol.Mccp2))
            {
                // Compression is a stream-mode transition. Only honor it after WILL/DO negotiation;
                // accepting an unsolicited MCCP2 marker would reinterpret arbitrary following bytes.
                mccp2Started = true;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            issues.Add(new TelnetProtocolIssue(
                ProtocolName(_subnegotiationOption),
                $"Malformed subnegotiation frame ignored: {exception.Message}"));
        }
        finally
        {
            _subnegotiation.Clear();
        }
        return mccp2Started;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseMsspPairs(IReadOnlyList<byte> data)
    {
        Dictionary<string, List<string>> mutable = new(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        List<byte> buffer = [];
        byte mode = 0;

        void Flush()
        {
            if (mode == TelnetProtocol.MsspVar)
            {
                key = Encoding.UTF8.GetString(buffer.ToArray()).Trim();
            }
            else if (mode == TelnetProtocol.MsspVal && !string.IsNullOrWhiteSpace(key))
            {
                if (!mutable.TryGetValue(key, out List<string>? values)) mutable[key] = values = [];
                values.Add(Encoding.UTF8.GetString(buffer.ToArray()).Trim());
            }
            buffer.Clear();
        }

        foreach (byte b in data)
        {
            if (b == TelnetProtocol.MsspVar || b == TelnetProtocol.MsspVal)
            {
                Flush();
                mode = b;
            }
            else
            {
                buffer.Add(b);
            }
        }
        Flush();

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] DesiredMsdpVariables =
    [
        "HEALTH", "HEALTH_MAX", "MANA", "MANA_MAX", "MOVEMENT", "MOVEMENT_MAX",
        "ROOM_VNUM", "ROOM_NAME", "ROOM_EXITS", "OPPONENT_NAME", "OPPONENT_HEALTH", "OPPONENT_HEALTH_MAX"
    ];

    private static IEnumerable<byte[]> BuildMsdpReports(MsdpValue reportable)
    {
        HashSet<string> available = reportable switch
        {
            MsdpArray array => array.Values
                .OfType<MsdpScalar>()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            MsdpScalar scalar => scalar.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (string variable in DesiredMsdpVariables.Where(available.Contains))
        {
            yield return BuildMsdpAssignment("REPORT", variable);
        }
    }

    private static byte[] BuildMsdpAssignment(string variable, string value)
    {
        return TelnetFrameBuilder.Subnegotiation(
            TelnetProtocol.Msdp,
            [
                TelnetProtocol.MsdpVar,
                .. Encoding.UTF8.GetBytes(variable),
                TelnetProtocol.MsdpVal,
                .. Encoding.UTF8.GetBytes(value)
            ]);
    }

    private byte[] BuildTerminalType()
    {
        string terminal = _terminalTypeRequests++ switch
        {
            0 => $"{_options.ClientName} {_options.ClientVersion}",
            1 => _options.TerminalType,
            _ when _options.EffectiveProtocols.Mtts => $"MTTS {BuildMttsBitVector()}",
            _ => _options.TerminalType
        };
        return TelnetFrameBuilder.Subnegotiation(
            TelnetProtocol.TerminalType,
            [TelnetProtocol.TerminalTypeIs, .. Encoding.ASCII.GetBytes(terminal)]);
    }

    private int BuildMttsBitVector()
    {
        const int ansi = 1;
        const int utf8 = 4;
        const int colors256 = 8;
        const int trueColor = 256;
        const int mnes = 512;
        const int ssl = 2048;

        int bits = ansi | utf8 | colors256 | trueColor;
        if (_options.EffectiveProtocols.NewEnvironment) bits |= mnes;
        if (_options.UseTls) bits |= ssl;
        return bits;
    }

    private byte[]? BuildCharsetResponse()
    {
        if (_subnegotiation.Count == 0) return null;
        if (_subnegotiation[0] == TelnetProtocol.CharsetTtableIs)
        {
            return TelnetFrameBuilder.Subnegotiation(
                TelnetProtocol.Charset,
                [TelnetProtocol.CharsetTtableRejected]);
        }
        if (_subnegotiation[0] != TelnetProtocol.CharsetRequest || _subnegotiation.Count < 2) return null;

        int separatorIndex = 1;
        if (_subnegotiation.Count >= 11 &&
            Encoding.ASCII.GetString(_subnegotiation.Skip(1).Take(8).ToArray()).Equals("[TTABLE]", StringComparison.OrdinalIgnoreCase))
        {
            // RFC 2066: optional [TTABLE] marker is followed by one version octet, then the charset list separator.
            separatorIndex = 10;
        }
        if (separatorIndex >= _subnegotiation.Count) return null;

        byte separator = _subnegotiation[separatorIndex];
        if (separator == TelnetProtocol.Iac) return null;

        List<string> offered = [];
        int start = separatorIndex + 1;
        for (int index = start; index <= _subnegotiation.Count; index++)
        {
            if (index == _subnegotiation.Count || _subnegotiation[index] == separator)
            {
                if (index > start)
                {
                    offered.Add(Encoding.ASCII.GetString(_subnegotiation.Skip(start).Take(index - start).ToArray()).Trim());
                }
                start = index + 1;
            }
        }

        string? accepted = offered.FirstOrDefault(value =>
            value.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("UTF8", StringComparison.OrdinalIgnoreCase));
        if (accepted is not null)
        {
            return TelnetFrameBuilder.Subnegotiation(
                TelnetProtocol.Charset,
                [TelnetProtocol.CharsetAccepted, .. Encoding.ASCII.GetBytes(accepted)]);
        }

        return TelnetFrameBuilder.Subnegotiation(TelnetProtocol.Charset, [TelnetProtocol.CharsetRejected]);
    }

    private sealed record EnvironmentRequest(byte Kind, string? Name);

    private byte[] BuildNewEnvironment()
    {
        IReadOnlyList<EnvironmentRequest> requested = ParseEnvironmentRequest(_subnegotiation);
        List<byte> payload = [TelnetProtocol.NewEnvironmentIs];

        if (requested.Count == 0)
        {
            AddDefaultEnvironment(payload, TelnetProtocol.NewEnvironmentVar);
            AddDefaultEnvironment(payload, TelnetProtocol.NewEnvironmentUserVar);
            return TelnetFrameBuilder.Subnegotiation(TelnetProtocol.NewEnvironment, payload);
        }

        foreach (EnvironmentRequest request in requested)
        {
            if (string.IsNullOrEmpty(request.Name))
            {
                AddDefaultEnvironment(payload, request.Kind);
                continue;
            }

            if (TryGetEnvironmentValue(request.Kind, request.Name, out string? value))
            {
                AddEnvironmentValue(payload, request.Kind, request.Name, value);
            }
            else
            {
                // RFC 1572 requires a response for each explicitly named request; omitting VALUE marks it undefined.
                payload.Add(request.Kind);
                AddEnvironmentBytes(payload, Encoding.ASCII.GetBytes(request.Name));
            }
        }

        return TelnetFrameBuilder.Subnegotiation(TelnetProtocol.NewEnvironment, payload);
    }

    private IReadOnlyList<EnvironmentRequest> ParseEnvironmentRequest(IReadOnlyList<byte> data)
    {
        List<EnvironmentRequest> requested = [];
        if (data.Count <= 1) return requested;

        int index = 1;
        while (index < data.Count)
        {
            byte kind = data[index++];
            if (kind is not (TelnetProtocol.NewEnvironmentVar or TelnetProtocol.NewEnvironmentUserVar)) continue;

            List<byte> name = [];
            bool escaped = false;
            while (index < data.Count)
            {
                byte value = data[index];
                if (!escaped && value == TelnetProtocol.NewEnvironmentEsc)
                {
                    escaped = true;
                    index++;
                    continue;
                }
                if (!escaped && value is (TelnetProtocol.NewEnvironmentVar or TelnetProtocol.NewEnvironmentUserVar))
                {
                    break;
                }
                name.Add(value);
                escaped = false;
                index++;
            }

            requested.Add(new EnvironmentRequest(kind, Encoding.ASCII.GetString(name.ToArray()).Trim()));
        }
        return requested;
    }

    private void AddDefaultEnvironment(List<byte> payload, byte kind)
    {
        if (kind == TelnetProtocol.NewEnvironmentUserVar) return;

        (string Name, string Value)[] values =
        [
            ("CHARSET", "UTF-8"),
            ("CLIENT_NAME", _options.ClientName),
            ("CLIENT_VERSION", _options.ClientVersion),
            ("MTTS", BuildMttsBitVector().ToString()),
            ("TERMINAL_TYPE", _options.TerminalType),
            ("TERM", _options.TerminalType),
            ("COLORTERM", "truecolor")
        ];

        foreach ((string name, string value) in values)
        {
            AddEnvironmentValue(payload, kind, name, value);
        }
    }

    private bool TryGetEnvironmentValue(byte kind, string name, out string value)
    {
        if (kind == TelnetProtocol.NewEnvironmentVar)
        {
            if (name.Equals("CHARSET", StringComparison.OrdinalIgnoreCase))
            {
                value = "UTF-8";
                return true;
            }
            if (name.Equals("CLIENT_NAME", StringComparison.OrdinalIgnoreCase))
            {
                value = _options.ClientName;
                return true;
            }
            if (name.Equals("CLIENT_VERSION", StringComparison.OrdinalIgnoreCase))
            {
                value = _options.ClientVersion;
                return true;
            }
            if (name.Equals("MTTS", StringComparison.OrdinalIgnoreCase))
            {
                value = BuildMttsBitVector().ToString();
                return true;
            }
            if (name.Equals("TERMINAL_TYPE", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("TERM", StringComparison.OrdinalIgnoreCase))
            {
                value = _options.TerminalType;
                return true;
            }
            if (name.Equals("COLORTERM", StringComparison.OrdinalIgnoreCase))
            {
                value = "truecolor";
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static void AddEnvironmentValue(List<byte> payload, byte kind, string name, string value)
    {
        payload.Add(kind);
        AddEnvironmentBytes(payload, Encoding.ASCII.GetBytes(name));
        payload.Add(TelnetProtocol.NewEnvironmentValue);
        AddEnvironmentBytes(payload, Encoding.ASCII.GetBytes(value));
    }

    private static void AddEnvironmentBytes(List<byte> payload, IEnumerable<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is TelnetProtocol.NewEnvironmentVar or TelnetProtocol.NewEnvironmentValue or
                TelnetProtocol.NewEnvironmentEsc or TelnetProtocol.NewEnvironmentUserVar)
            {
                payload.Add(TelnetProtocol.NewEnvironmentEsc);
            }
            payload.Add(value);
        }
    }

    private void ReportOversizedSubnegotiation(List<TelnetProtocolIssue> issues)
    {
        if (_subnegotiationOverflowReported) return;
        _subnegotiationOverflowReported = true;
        issues.Add(new TelnetProtocolIssue(
            ProtocolName(_subnegotiationOption),
            $"Subnegotiation exceeded {MaximumSubnegotiationBytes:N0} bytes and was discarded."));
        _subnegotiation.Clear();
    }

    private static string ProtocolName(byte option) => option switch
    {
        TelnetProtocol.Echo => "ECHO",
        TelnetProtocol.SuppressGoAhead => "SGA",
        TelnetProtocol.TerminalType => "TTYPE",
        TelnetProtocol.Eor => "EOR",
        TelnetProtocol.Naws => "NAWS",
        TelnetProtocol.NewEnvironment => "NEW-ENVIRON",
        TelnetProtocol.Charset => "CHARSET",
        TelnetProtocol.Msdp => "MSDP",
        TelnetProtocol.Mssp => "MSSP",
        TelnetProtocol.Mccp2 => "MCCP2",
        TelnetProtocol.Gmcp => "GMCP",
        _ => $"TELNET-{option}"
    };
}

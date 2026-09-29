using System.Text;
using NexMud.Contracts.Transport;

namespace NexMud.Transport.Telnet;

internal static class MsdpCodec
{
    public static IReadOnlyDictionary<string, MsdpValue> Parse(IReadOnlyList<byte> data)
    {
        Parser parser = new(data);
        return parser.ParseVariables(stopToken: null);
    }

    private sealed class Parser
    {
        private const int MaximumNestingDepth = 32;
        private readonly IReadOnlyList<byte> _data;
        private int _index;

        public Parser(IReadOnlyList<byte> data)
        {
            _data = data;
        }

        public IReadOnlyDictionary<string, MsdpValue> ParseVariables(byte? stopToken)
        {
            Dictionary<string, MsdpValue> result = new(StringComparer.OrdinalIgnoreCase);
            while (_index < _data.Count)
            {
                byte token = _data[_index];
                if (stopToken.HasValue && token == stopToken.Value)
                {
                    _index++;
                    break;
                }

                if (token != TelnetProtocol.MsdpVar)
                {
                    _index++;
                    continue;
                }

                _index++;
                string key = ReadScalarUntilControl();
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (_index >= _data.Count || _data[_index] != TelnetProtocol.MsdpVal) continue;
                _index++;
                result[key] = ParseValue(depth: 0);
            }
            return result;
        }

        private MsdpValue ParseValue(int depth)
        {
            if (depth > MaximumNestingDepth)
            {
                throw new InvalidDataException($"MSDP nesting exceeds {MaximumNestingDepth} levels.");
            }
            if (_index >= _data.Count) return new MsdpScalar(string.Empty);

            return _data[_index] switch
            {
                TelnetProtocol.MsdpTableOpen => ParseTable(depth + 1),
                TelnetProtocol.MsdpArrayOpen => ParseArray(depth + 1),
                _ => ParseScalarValue()
            };
        }

        private MsdpValue ParseTable(int depth)
        {
            _index++; // TABLE_OPEN
            Dictionary<string, MsdpValue> values = new(StringComparer.OrdinalIgnoreCase);
            while (_index < _data.Count)
            {
                if (_data[_index] == TelnetProtocol.MsdpTableClose)
                {
                    _index++;
                    break;
                }
                if (_data[_index] != TelnetProtocol.MsdpVar)
                {
                    _index++;
                    continue;
                }

                _index++;
                string key = ReadScalarUntilControl();
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (_index >= _data.Count || _data[_index] != TelnetProtocol.MsdpVal) continue;
                _index++;
                values[key] = ParseValue(depth);
            }
            return new MsdpTable(values);
        }

        private MsdpValue ParseArray(int depth)
        {
            _index++; // ARRAY_OPEN
            List<MsdpValue> values = [];
            while (_index < _data.Count)
            {
                byte token = _data[_index];
                if (token == TelnetProtocol.MsdpArrayClose)
                {
                    _index++;
                    break;
                }

                if (token == TelnetProtocol.MsdpVal)
                {
                    _index++;
                    values.Add(ParseValue(depth));
                    continue;
                }

                // Tolerate scalar array elements from permissive servers while guaranteeing progress.
                if (token is TelnetProtocol.MsdpTableOpen or TelnetProtocol.MsdpArrayOpen)
                {
                    values.Add(ParseValue(depth));
                }
                else if (!IsControl(token))
                {
                    values.Add(ParseScalarValue());
                }
                else
                {
                    _index++;
                }
            }
            return new MsdpArray(values);
        }

        private MsdpValue ParseScalarValue()
        {
            int start = _index;
            string value = ReadScalarUntilValueBoundary();
            if (_index == start && _index < _data.Count) _index++;
            return new MsdpScalar(value);
        }

        private string ReadScalarUntilControl()
        {
            int start = _index;
            while (_index < _data.Count && !IsControl(_data[_index])) _index++;
            return Encoding.UTF8.GetString(_data.Skip(start).Take(_index - start).ToArray()).Trim();
        }

        private string ReadScalarUntilValueBoundary()
        {
            int start = _index;
            while (_index < _data.Count)
            {
                byte token = _data[_index];
                if (token is TelnetProtocol.MsdpVar or TelnetProtocol.MsdpVal or
                    TelnetProtocol.MsdpTableClose or TelnetProtocol.MsdpArrayClose)
                {
                    break;
                }
                _index++;
            }
            return Encoding.UTF8.GetString(_data.Skip(start).Take(_index - start).ToArray()).Trim();
        }

        private static bool IsControl(byte value) => value is
            TelnetProtocol.MsdpVar or TelnetProtocol.MsdpVal or
            TelnetProtocol.MsdpTableOpen or TelnetProtocol.MsdpTableClose or
            TelnetProtocol.MsdpArrayOpen or TelnetProtocol.MsdpArrayClose;
    }
}

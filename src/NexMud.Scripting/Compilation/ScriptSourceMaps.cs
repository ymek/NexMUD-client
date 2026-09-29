using System.Text.Json;
using NexMud.Scripting.Diagnostics;

namespace NexMud.Scripting.Compilation;

/// <summary>Minimal Source Map v3 lookup used only for runtime diagnostics.</summary>
public static class ScriptSourceMaps
{
    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    public static ScriptSourceLocation Generated(
        CompiledScriptModule module,
        int? generatedLine,
        int? generatedColumn) =>
        new(
            module.OriginalSourcePath,
            generatedLine,
            generatedColumn,
            module.Path,
            generatedLine,
            generatedColumn);

    public static bool TryMap(
        CompiledScriptModule module,
        int generatedLine,
        int generatedColumn,
        out ScriptSourceLocation location)
    {
        location = Generated(module, generatedLine, generatedColumn);
        if (string.IsNullOrWhiteSpace(module.SourceMap) || generatedLine < 1 || generatedColumn < 0) return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(module.SourceMap);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("mappings", out JsonElement mappingsElement) ||
                mappingsElement.ValueKind != JsonValueKind.String)
                return false;
            string mappings = mappingsElement.GetString() ?? string.Empty;
            string[] sources = root.TryGetProperty("sources", out JsonElement sourcesElement)
                ? sourcesElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
                : [];

            int previousSource = 0;
            int previousOriginalLine = 0;
            int previousOriginalColumn = 0;
            string[] lines = mappings.Split(';');
            int targetLine = generatedLine - 1;
            if (targetLine >= lines.Length) return false;

            for (int lineIndex = 0; lineIndex <= targetLine; lineIndex++)
            {
                int generated = 0;
                int bestSource = -1;
                int bestOriginalLine = -1;
                int bestOriginalColumn = -1;
                int index = 0;
                string line = lines[lineIndex];
                while (index < line.Length)
                {
                    if (line[index] == ',') { index++; continue; }
                    int[] segment = DecodeSegment(line, ref index);
                    if (segment.Length == 0) continue;
                    generated += segment[0];
                    if (segment.Length >= 4)
                    {
                        previousSource += segment[1];
                        previousOriginalLine += segment[2];
                        previousOriginalColumn += segment[3];
                        if (lineIndex == targetLine && generated <= generatedColumn)
                        {
                            bestSource = previousSource;
                            bestOriginalLine = previousOriginalLine;
                            bestOriginalColumn = previousOriginalColumn;
                        }
                    }
                }

                if (lineIndex == targetLine && bestSource >= 0)
                {
                    string source = bestSource < sources.Length ? NormalizeSource(sources[bestSource]) : module.OriginalSourcePath ?? module.Path;
                    if (!string.IsNullOrWhiteSpace(module.OriginalSourcePath) && sources.Length == 1)
                        source = module.OriginalSourcePath;
                    location = new ScriptSourceLocation(
                        source,
                        bestOriginalLine + 1,
                        bestOriginalColumn + 1,
                        module.Path,
                        generatedLine,
                        generatedColumn + 1);
                    return true;
                }
            }
        }
        catch (JsonException) { }
        catch (FormatException) { }

        return false;
    }

    private static int[] DecodeSegment(string mappings, ref int index)
    {
        List<int> values = [];
        while (index < mappings.Length && mappings[index] is not ',' and not ';')
            values.Add(DecodeVlq(mappings, ref index));
        return values.ToArray();
    }

    private static int DecodeVlq(string value, ref int index)
    {
        int result = 0;
        int shift = 0;
        bool continuation;
        do
        {
            if (index >= value.Length) throw new FormatException("Invalid source-map VLQ segment.");
            int digit = Base64.IndexOf(value[index++]);
            if (digit < 0) throw new FormatException("Invalid source-map base64 digit.");
            continuation = (digit & 32) != 0;
            digit &= 31;
            result += digit << shift;
            shift += 5;
        } while (continuation);

        bool negative = (result & 1) == 1;
        result >>= 1;
        return negative ? -result : result;
    }

    private static string NormalizeSource(string source)
    {
        string normalized = source.Replace('\\', '/');
        while (normalized.StartsWith("../", StringComparison.Ordinal)) normalized = normalized[3..];
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}

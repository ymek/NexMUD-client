using NexMud.Scripting.Compilation;
using NexMud.Scripting.Diagnostics;
using NexMud.Scripting.Runtime;

namespace NexMud.Scripting.TypeScript.Compiler;

/// <summary>
/// Reads declarations emitted by TypeScript itself. Export discovery therefore follows compiler
/// symbol output rather than regex-scanning authored TypeScript source.
/// </summary>
internal static class TypeScriptDeclarationExportReader
{
    private const string FunctionPrefix = "export declare function ";
    private const string ConstPrefix = "export declare const ";

    public static IReadOnlyList<ExportedScriptFunction> Discover(
        ScriptModuleId packageId,
        IReadOnlyList<ScriptSourceFile> sources,
        string outputRoot)
    {
        List<ExportedScriptFunction> exports = [];
        foreach (ScriptSourceFile source in sources)
        {
            string extension = Path.GetExtension(source.Path);
            if ((!extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
                 !extension.Equals(".js", StringComparison.OrdinalIgnoreCase)) ||
                source.Path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
                continue;

            string declarationRelative = Path.ChangeExtension(source.Path, ".d.ts")!.Replace('\\', '/');
            string declarationPath = Path.Combine(outputRoot, declarationRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(declarationPath)) continue;

            string[] lines = File.ReadAllLines(declarationPath);
            List<string> documentation = [];
            bool inDocumentation = false;
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string trimmed = lines[lineIndex].Trim();
                if (trimmed.StartsWith("/**", StringComparison.Ordinal))
                {
                    documentation.Clear();
                    inDocumentation = true;
                    AddDocumentationLine(documentation, trimmed);
                    if (trimmed.EndsWith("*/", StringComparison.Ordinal)) inDocumentation = false;
                    continue;
                }
                if (inDocumentation)
                {
                    AddDocumentationLine(documentation, trimmed);
                    if (trimmed.EndsWith("*/", StringComparison.Ordinal)) inDocumentation = false;
                    continue;
                }
                bool functionDeclaration = trimmed.StartsWith(FunctionPrefix, StringComparison.Ordinal);
                bool callableConst = trimmed.StartsWith(ConstPrefix, StringComparison.Ordinal);
                if (!functionDeclaration && !callableConst)
                {
                    if (trimmed.Length > 0) documentation.Clear();
                    continue;
                }

                string declaration = trimmed;
                while (!declaration.EndsWith(';') && lineIndex + 1 < lines.Length)
                    declaration += " " + lines[++lineIndex].Trim();

                bool parsed = functionDeclaration
                    ? TryParseFunction(declaration, out string exportName, out IReadOnlyList<ScriptFunctionParameter> parameters, out string returnType)
                    : TryParseCallableConst(declaration, out exportName, out parameters, out returnType);
                if (!parsed)
                {
                    documentation.Clear();
                    continue;
                }

                exports.Add(new ExportedScriptFunction(
                    new ScriptFunctionRef(packageId.Value, source.Path, exportName),
                    exportName,
                    source.Path,
                    exportName,
                    parameters,
                    returnType,
                    documentation.Count == 0 ? null : string.Join(' ', documentation),
                    new ScriptSourceLocation(source.Path, null, null, declarationRelative, lineIndex + 1, 1)));
                documentation.Clear();
            }
        }

        return exports
            .OrderBy(item => item.ModulePath, StringComparer.Ordinal)
            .ThenBy(item => item.ExportName, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryParseFunction(
        string declaration,
        out string exportName,
        out IReadOnlyList<ScriptFunctionParameter> parameters,
        out string returnType)
    {
        exportName = string.Empty;
        parameters = [];
        returnType = "unknown";
        int nameStart = FunctionPrefix.Length;
        int open = declaration.IndexOf('(', nameStart);
        if (open <= nameStart) return false;
        exportName = declaration[nameStart..open].Trim();
        if (exportName.Length == 0) return false;
        int close = FindMatchingParenthesis(declaration, open);
        if (close < 0) return false;
        int colon = declaration.IndexOf(':', close + 1);
        if (colon < 0) return false;
        int semicolon = declaration.LastIndexOf(';');
        if (semicolon < colon) semicolon = declaration.Length;
        returnType = declaration[(colon + 1)..semicolon].Trim();
        parameters = SplitTopLevel(declaration[(open + 1)..close])
            .Select(ParseParameter)
            .Where(parameter => parameter is not null)
            .Cast<ScriptFunctionParameter>()
            .ToArray();
        return true;
    }


    private static bool TryParseCallableConst(
        string declaration,
        out string exportName,
        out IReadOnlyList<ScriptFunctionParameter> parameters,
        out string returnType)
    {
        exportName = string.Empty;
        parameters = [];
        returnType = "unknown";
        int nameStart = ConstPrefix.Length;
        int colon = declaration.IndexOf(':', nameStart);
        if (colon <= nameStart) return false;
        exportName = declaration[nameStart..colon].Trim();
        if (exportName.Length == 0) return false;
        int open = declaration.IndexOf('(', colon + 1);
        if (open < 0) return false;
        int close = FindMatchingParenthesis(declaration, open);
        if (close < 0) return false;
        int arrow = declaration.IndexOf("=>", close + 1, StringComparison.Ordinal);
        if (arrow < 0) return false;
        int semicolon = declaration.LastIndexOf(';');
        if (semicolon < arrow) semicolon = declaration.Length;
        returnType = declaration[(arrow + 2)..semicolon].Trim();
        parameters = SplitTopLevel(declaration[(open + 1)..close])
            .Select(ParseParameter)
            .Where(parameter => parameter is not null)
            .Cast<ScriptFunctionParameter>()
            .ToArray();
        return true;
    }

    private static ScriptFunctionParameter? ParseParameter(string text)
    {
        string value = text.Trim();
        if (value.Length == 0) return null;
        int colon = FindTopLevelColon(value);
        if (colon <= 0) return new ScriptFunctionParameter(value.TrimEnd('?'), "unknown", value.EndsWith('?'));
        string name = value[..colon].Trim();
        bool optional = name.EndsWith('?');
        if (optional) name = name[..^1].Trim();
        return new ScriptFunctionParameter(name, value[(colon + 1)..].Trim(), optional);
    }

    private static int FindMatchingParenthesis(string value, int open)
    {
        int depth = 0;
        for (int index = open; index < value.Length; index++)
        {
            if (value[index] == '(') depth++;
            else if (value[index] == ')' && --depth == 0) return index;
        }
        return -1;
    }

    private static int FindTopLevelColon(string value)
    {
        int angle = 0;
        int square = 0;
        int round = 0;
        int curly = 0;
        for (int index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
                case '[': square++; break;
                case ']': if (square > 0) square--; break;
                case '(': round++; break;
                case ')': if (round > 0) round--; break;
                case '{': curly++; break;
                case '}': if (curly > 0) curly--; break;
                case ':' when angle == 0 && square == 0 && round == 0 && curly == 0: return index;
            }
        }
        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        List<string> items = [];
        int start = 0;
        int angle = 0;
        int square = 0;
        int round = 0;
        int curly = 0;
        for (int index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '<': angle++; break;
                case '>': if (angle > 0) angle--; break;
                case '[': square++; break;
                case ']': if (square > 0) square--; break;
                case '(': round++; break;
                case ')': if (round > 0) round--; break;
                case '{': curly++; break;
                case '}': if (curly > 0) curly--; break;
                case ',' when angle == 0 && square == 0 && round == 0 && curly == 0:
                    items.Add(value[start..index]);
                    start = index + 1;
                    break;
            }
        }
        items.Add(value[start..]);
        return items;
    }

    private static void AddDocumentationLine(List<string> target, string line)
    {
        string cleaned = line
            .Replace("/**", string.Empty, StringComparison.Ordinal)
            .Replace("*/", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimStart('*')
            .Trim();
        if (cleaned.Length > 0) target.Add(cleaned);
    }
}

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
        string outputRoot,
        string? entrypoint = null)
    {
        if (entrypoint is not null)
            return DiscoverEntrypoint(packageId, sources, outputRoot, entrypoint);

        List<ExportedScriptFunction> exports = [];
        foreach (ScriptSourceFile source in sources)
        {
            if (entrypoint is not null && !source.Path.Equals(entrypoint, StringComparison.Ordinal))
                continue;
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

    private static IReadOnlyList<ExportedScriptFunction> DiscoverEntrypoint(
        ScriptModuleId packageId,
        IReadOnlyList<ScriptSourceFile> sources,
        string outputRoot,
        string entrypoint)
    {
        Dictionary<string, ScriptSourceFile> sourceByPath = sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, ExportedScriptFunction>> cache = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);

        Dictionary<string, ExportedScriptFunction> ReadModule(string modulePath)
        {
            if (cache.TryGetValue(modulePath, out Dictionary<string, ExportedScriptFunction>? cached))
                return cached;
            if (!sourceByPath.TryGetValue(modulePath, out ScriptSourceFile? source) || !visiting.Add(modulePath))
                return new Dictionary<string, ExportedScriptFunction>(StringComparer.Ordinal);

            Dictionary<string, ExportedScriptFunction> exports = new(StringComparer.Ordinal);
            foreach (ExportedScriptFunction item in Discover(packageId, [source], outputRoot))
                exports.TryAdd(item.ExportName, item);
            string declarationRelative = Path.ChangeExtension(modulePath, ".d.ts")!.Replace('\\', '/');
            string declarationPath = Path.Combine(outputRoot, declarationRelative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(declarationPath))
            {
                string[] declarations = File.ReadAllLines(declarationPath);
                Dictionary<string, (string Specifier, string Imported)> imports = new(StringComparer.Ordinal);
                foreach (string declaration in declarations)
                {
                    if (!TryParseNamedImport(declaration, out string specifier, out var bindings)) continue;
                    foreach ((string imported, string local) in bindings)
                        imports.TryAdd(local, (specifier, imported));
                }

                foreach (string declaration in declarations)
                {
                    if (TryParseReExport(declaration, out string specifier, out bool star, out var names))
                    {
                        string? targetPath = ResolveRelativeModulePath(modulePath, specifier, sourceByPath);
                        if (targetPath is null) continue;
                        Dictionary<string, ExportedScriptFunction> targetExports = ReadModule(targetPath);
                        if (star)
                        {
                            foreach ((string name, ExportedScriptFunction item) in targetExports)
                                exports.TryAdd(name, item);
                            continue;
                        }

                        foreach ((string importedName, string exportedName) in names)
                        {
                            if (!targetExports.TryGetValue(importedName, out ExportedScriptFunction? item)) continue;
                            exports.TryAdd(exportedName, RenameExport(packageId, modulePath, exportedName, item));
                        }
                        continue;
                    }

                    if (!TryParseLocalExport(declaration, out var localExports)) continue;
                    foreach ((string localName, string exportedName) in localExports)
                    {
                        ExportedScriptFunction? item = null;
                        if (imports.TryGetValue(localName, out var import))
                        {
                            string? targetPath = ResolveRelativeModulePath(modulePath, import.Specifier, sourceByPath);
                            if (targetPath is null || !ReadModule(targetPath).TryGetValue(import.Imported, out item))
                                continue;
                        }
                        else if (!exports.TryGetValue(localName, out item))
                        {
                            continue;
                        }

                        exports.TryAdd(exportedName, RenameExport(packageId, modulePath, exportedName, item));
                    }
                }
            }

            visiting.Remove(modulePath);
            cache[modulePath] = exports;
            return exports;
        }

        return ReadModule(entrypoint).Values
            .Select(item => item with
            {
                FunctionRef = new ScriptFunctionRef(packageId.Value, entrypoint, item.ExportName),
                ModulePath = entrypoint
            })
            .OrderBy(item => item.ExportName, StringComparer.Ordinal)
            .ToArray();
    }

    private static ExportedScriptFunction RenameExport(
        ScriptModuleId packageId,
        string modulePath,
        string exportedName,
        ExportedScriptFunction item) =>
        item with
        {
            FunctionRef = new ScriptFunctionRef(packageId.Value, modulePath, exportedName),
            DisplayName = exportedName,
            ModulePath = modulePath,
            ExportName = exportedName
        };

    private static bool TryParseNamedImport(
        string declaration,
        out string specifier,
        out IReadOnlyList<(string Imported, string Local)> bindings)
    {
        specifier = string.Empty;
        bindings = Array.Empty<(string, string)>();
        string text = declaration.Trim().TrimEnd(';').TrimEnd();
        if (!text.StartsWith("import ", StringComparison.Ordinal)) return false;
        int fromIndex = text.LastIndexOf(" from ", StringComparison.Ordinal);
        if (fromIndex < 0) return false;

        string moduleSpecifier = text[(fromIndex + 6)..].Trim();
        if (moduleSpecifier.Length < 2 ||
            (moduleSpecifier[0] != '\'' && moduleSpecifier[0] != '"') ||
            moduleSpecifier[^1] != moduleSpecifier[0])
        {
            return false;
        }

        string clause = text["import ".Length..fromIndex].Trim();
        if (clause.StartsWith("type ", StringComparison.Ordinal))
            clause = clause["type ".Length..].Trim();
        int open = clause.IndexOf('{');
        int close = clause.IndexOf('}', open + 1);
        if (open < 0 || close < 0) return false;

        List<(string Imported, string Local)> parsed = [];
        foreach (string binding in clause[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (binding.StartsWith("type ", StringComparison.Ordinal)) continue;
            string[] parts = binding.Split(" as ", 2, StringSplitOptions.TrimEntries);
            string imported = parts[0];
            string local = parts.Length == 1 ? imported : parts[1];
            if (imported.Length > 0 && local.Length > 0)
                parsed.Add((imported, local));
        }

        if (parsed.Count == 0) return false;
        specifier = moduleSpecifier[1..^1];
        bindings = parsed;
        return true;
    }

    private static bool TryParseLocalExport(
        string declaration,
        out IReadOnlyList<(string Local, string Exported)> names)
    {
        names = Array.Empty<(string, string)>();
        string clause = declaration.Trim().TrimEnd(';').TrimEnd();
        if (!clause.StartsWith("export {", StringComparison.Ordinal) ||
            !clause.EndsWith('}') || clause.Contains(" from ", StringComparison.Ordinal))
        {
            return false;
        }

        List<(string Local, string Exported)> parsed = [];
        foreach (string name in clause["export {".Length..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = name.Split(" as ", 2, StringSplitOptions.TrimEntries);
            parsed.Add((parts[0], parts.Length == 1 ? parts[0] : parts[1]));
        }
        names = parsed;
        return parsed.Count > 0;
    }

    private static bool TryParseReExport(
        string declaration,
        out string specifier,
        out bool star,
        out IReadOnlyList<(string Imported, string Exported)> names)
    {
        specifier = string.Empty;
        star = false;
        names = Array.Empty<(string, string)>();
        string text = declaration.Trim().TrimEnd(';').TrimEnd();
        int fromIndex = text.LastIndexOf(" from ", StringComparison.Ordinal);
        if (fromIndex < 0) return false;

        string moduleSpecifier = text[(fromIndex + 6)..].Trim();
        if (moduleSpecifier.Length < 2 ||
            (moduleSpecifier[0] != '\'' && moduleSpecifier[0] != '"') ||
            moduleSpecifier[^1] != moduleSpecifier[0])
        {
            return false;
        }

        string clause = text[..fromIndex].Trim();
        specifier = moduleSpecifier[1..^1];
        if (clause.Equals("export *", StringComparison.Ordinal))
        {
            star = true;
            return true;
        }
        if (!clause.StartsWith("export {", StringComparison.Ordinal) || !clause.EndsWith('}'))
            return false;

        List<(string Imported, string Exported)> parsedNames = [];
        string namesText = clause["export {".Length..^1];
        foreach (string name in namesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = name.Split(" as ", 2, StringSplitOptions.TrimEntries);
            parsedNames.Add((parts[0], parts.Length == 1 ? parts[0] : parts[1]));
        }
        names = parsedNames;
        return true;
    }

    private static string? ResolveRelativeModulePath(
        string sourcePath,
        string specifier,
        IReadOnlyDictionary<string, ScriptSourceFile> sources)
    {
        if (!specifier.StartsWith(".", StringComparison.Ordinal)) return null;
        string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
        string combined = string.IsNullOrEmpty(directory) ? specifier : $"{directory}/{specifier}";
        List<string> segments = [];
        foreach (string segment in combined.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        string basePath = string.Join('/', segments);
        string extension = Path.GetExtension(basePath);
        IEnumerable<string> candidates = extension switch
        {
            ".js" => [Path.ChangeExtension(basePath, ".ts")!, Path.ChangeExtension(basePath, ".tsx")!, basePath],
            ".ts" or ".tsx" => [basePath],
            "" => [basePath + ".ts", basePath + ".tsx", basePath + ".js", basePath + "/index.ts", basePath + "/index.js"],
            _ => []
        };
        return candidates.FirstOrDefault(sources.ContainsKey);
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

using System.Text;
using System.Text.RegularExpressions;
using Jint;
using JevMud.Scripting.Compilation;
using JevMud.Scripting.Runtime;

namespace JevMud.Scripting.Jint.Modules;

public static partial class JintModuleLoader
{
    public static void Validate(CompiledScriptPackage package, ScriptResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(limits);
        if (!ScriptApiVersion.IsCompatible(package.Manifest.ApiVersion))
            throw new InvalidOperationException($"Script API '{package.Manifest.ApiVersion}' is not compatible with API '{ScriptApiVersion.Current}'.");
        if (package.Modules.Count > limits.MaximumModuleCount)
            throw new InvalidOperationException($"Script package contains {package.Modules.Count} modules; limit is {limits.MaximumModuleCount}.");

        long totalBytes = 0;
        Dictionary<string, string[]> graph = new(StringComparer.Ordinal);
        foreach ((string path, CompiledScriptModule module) in package.Modules)
        {
            _ = CompiledScriptPackage.NormalizeModulePath(path);
            if (module.JavaScript.Length > limits.MaximumSourceLength)
                throw new InvalidOperationException($"Module '{path}' exceeds the source-length limit.");
            totalBytes += Encoding.UTF8.GetByteCount(module.JavaScript);
            if (totalBytes > limits.MaximumTotalModuleSourceBytes)
                throw new InvalidOperationException("Script package exceeds the total module-source limit.");
            if (EstimateSyntaxNodes(module.JavaScript) > limits.MaximumAstNodes)
                throw new InvalidOperationException($"Module '{path}' exceeds the syntax-complexity limit.");

            graph[path] = ImportSpecifierRegex().Matches(module.JavaScript)
                .Select(match => ResolveSpecifier(path, match.Groups[1].Value, package))
                .Where(resolved => resolved is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        ValidateGraph(package.Manifest.Entrypoint, graph, limits);
    }

    public static void Register(Engine engine, CompiledScriptPackage package, string bootstrapSource)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.Modules.Add("@nexmud/api", bootstrapSource);
        foreach ((string path, CompiledScriptModule module) in package.Modules)
            engine.Modules.Add(path, module.JavaScript);
    }

    private static string? ResolveSpecifier(string fromPath, string specifier, CompiledScriptPackage package)
    {
        if (specifier == "@nexmud/api") return null;
        if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidOperationException($"Module '{fromPath}' imports unsupported module '{specifier}'.");

        List<string> segments = fromPath.Split('/').SkipLast(1).ToList();
        foreach (string segment in specifier.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new InvalidOperationException($"Module import '{specifier}' escapes the script package root.");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }

        string resolved = string.Join('/', segments);
        if (!package.Modules.ContainsKey(resolved))
            throw new InvalidOperationException($"Module '{fromPath}' imports missing package module '{specifier}'.");
        return resolved;
    }

    private static void ValidateGraph(
        string entrypoint,
        IReadOnlyDictionary<string, string[]> graph,
        ScriptResourceLimits limits)
    {
        HashSet<string> visiting = new(StringComparer.Ordinal);
        Dictionary<string, int> memo = new(StringComparer.Ordinal);
        int resolutionHops = 0;

        int Visit(string module)
        {
            if (memo.TryGetValue(module, out int known)) return known;
            if (!visiting.Add(module)) return 1; // cycles are legal ES module graphs and do not increase depth forever.
            int depth = 1;
            if (graph.TryGetValue(module, out string[]? dependencies))
            {
                resolutionHops += dependencies.Length;
                if (resolutionHops > limits.MaximumModuleResolutionHops)
                    throw new InvalidOperationException("Script module graph exceeds the module-resolution hop limit.");
                foreach (string dependency in dependencies)
                    depth = Math.Max(depth, 1 + Visit(dependency));
            }
            visiting.Remove(module);
            memo[module] = depth;
            return depth;
        }

        int graphDepth = Visit(CompiledScriptPackage.NormalizeModulePath(entrypoint));
        if (graphDepth > limits.MaximumModuleGraphDepth)
            throw new InvalidOperationException($"Script module graph depth {graphDepth} exceeds limit {limits.MaximumModuleGraphDepth}.");
    }

    private static int EstimateSyntaxNodes(string source)
    {
        // Parser-independent preflight guard. Jint still owns actual ECMAScript parsing; this bound
        // prevents obviously pathological token volume before engine parsing begins.
        int nodes = 0;
        bool inToken = false;
        foreach (char value in source)
        {
            bool tokenCharacter = char.IsLetterOrDigit(value) || value is '_' or '$';
            if (tokenCharacter)
            {
                if (!inToken) nodes++;
                inToken = true;
            }
            else
            {
                inToken = false;
                if (!char.IsWhiteSpace(value)) nodes++;
            }
        }
        return nodes;
    }

    [GeneratedRegex("(?m)^\\s*(?:import(?:\\s+[^;\\r\\n]*?\\s+from)?|export\\s+[^;\\r\\n]*?\\s+from)\\s*[\"']([^\"']+)[\"']")]
    private static partial Regex ImportSpecifierRegex();
}

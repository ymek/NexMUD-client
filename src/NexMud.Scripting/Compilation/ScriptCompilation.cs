using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;

namespace NexMud.Scripting.Compilation;

public enum ScriptSourceLanguage
{
    JavaScript,
    TypeScript
}

public enum ScriptDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record ScriptSourceFile(string Path, string Content)
{
    public string Path { get; } = NormalizePath(Path);
    public string Content { get; } = Content ?? throw new ArgumentNullException(nameof(Content));

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new ArgumentException("Script source paths must be package-relative and may not traverse directories.", nameof(path));
        return normalized;
    }
}

public sealed record ScriptCompilerDiagnostic(
    ScriptDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? SourceFile = null,
    int? Line = null,
    int? Column = null);

public sealed record ScriptCompilerOptions(
    string EcmaScriptTarget,
    bool EmitSourceMaps = true,
    bool UseEsModules = true,
    bool Strict = true)
{
    public static ScriptCompilerOptions Default { get; } = new("ES2022");
}

public sealed record ScriptManifest(
    ScriptModuleId Id,
    string Name,
    string Version,
    string ApiVersion,
    string Entrypoint,
    ScriptCapability Permissions,
    ScriptOwnerKind OwnerKind = ScriptOwnerKind.UserScript,
    ScriptRuntimeProfile RuntimeProfile = ScriptRuntimeProfile.UserScript)
{
    public string Entrypoint { get; } = NormalizeEntrypoint(Entrypoint);

    private static string NormalizeEntrypoint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new ArgumentException("Script entrypoint must be package-relative and may not traverse directories.", nameof(path));
        return normalized;
    }
}

public sealed record ScriptCompileRequest(
    ScriptManifest Manifest,
    ScriptSourceLanguage Language,
    IReadOnlyList<ScriptSourceFile> Sources,
    ScriptCompilerOptions? Options = null)
{
    public ScriptCompilerOptions EffectiveOptions => Options ?? ScriptCompilerOptions.Default;
}

public sealed record CompiledScriptModule(
    string Path,
    string JavaScript,
    string? SourceMap = null,
    string? OriginalSourcePath = null);

public sealed class CompiledScriptPackage
{
    private readonly ReadOnlyDictionary<string, CompiledScriptModule> _modules;

    public CompiledScriptPackage(
        ScriptManifest manifest,
        IEnumerable<CompiledScriptModule> modules,
        string compilerIdentity,
        string cacheKey,
        IReadOnlyList<ExportedScriptFunction>? exportedFunctions = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        CompilerIdentity = compilerIdentity;
        CacheKey = cacheKey;
        ExportedFunctions = exportedFunctions ?? Array.Empty<ExportedScriptFunction>();

        Dictionary<string, CompiledScriptModule> normalized = new(StringComparer.Ordinal);
        foreach (CompiledScriptModule module in modules)
        {
            string path = NormalizeModulePath(module.Path);
            if (!normalized.TryAdd(path, module with { Path = path }))
                throw new ArgumentException($"Duplicate compiled module path '{path}'.", nameof(modules));
        }
        if (!normalized.ContainsKey(NormalizeModulePath(manifest.Entrypoint)))
            throw new ArgumentException($"Compiled package does not contain entrypoint '{manifest.Entrypoint}'.", nameof(modules));
        _modules = new ReadOnlyDictionary<string, CompiledScriptModule>(normalized);
    }

    public ScriptManifest Manifest { get; }
    public string CompilerIdentity { get; }
    public string CacheKey { get; }
    public IReadOnlyDictionary<string, CompiledScriptModule> Modules => _modules;
    public IReadOnlyList<ExportedScriptFunction> ExportedFunctions { get; }

    public static string NormalizeModulePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new ArgumentException("Module paths must remain inside the script package.", nameof(path));
        return normalized;
    }
}

public sealed record ScriptCompileResult(
    bool Success,
    CompiledScriptPackage? Package,
    IReadOnlyList<ScriptCompilerDiagnostic> Diagnostics,
    IReadOnlyList<ExportedScriptFunction>? ExportedFunctions = null)
{
    public static ScriptCompileResult Failed(params ScriptCompilerDiagnostic[] diagnostics) =>
        new(false, null, diagnostics, Array.Empty<ExportedScriptFunction>());
}

public interface IScriptCompiler
{
    string CompilerIdentity { get; }
    Task<ScriptCompileResult> CompileAsync(ScriptCompileRequest request, CancellationToken cancellationToken = default);
}

public static class ScriptCompileCacheKey
{
    public static string Compute(ScriptCompileRequest request, string compilerIdentity, string scriptApiVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptApiVersion);
        StringBuilder canonical = new();
        canonical.AppendLine(compilerIdentity);
        canonical.AppendLine(scriptApiVersion);
        canonical.AppendLine(request.Manifest.Id.Value);
        canonical.AppendLine(request.Manifest.Name);
        canonical.AppendLine(request.Manifest.Version);
        canonical.AppendLine(request.Manifest.ApiVersion);
        canonical.AppendLine(request.Manifest.Entrypoint);
        canonical.AppendLine(((int)request.Manifest.Permissions).ToString(System.Globalization.CultureInfo.InvariantCulture));
        canonical.AppendLine(request.Manifest.OwnerKind.ToString());
        canonical.AppendLine(request.Manifest.RuntimeProfile.ToString());
        canonical.AppendLine(request.EffectiveOptions.EcmaScriptTarget);
        canonical.AppendLine(request.EffectiveOptions.EmitSourceMaps.ToString());
        canonical.AppendLine(request.EffectiveOptions.UseEsModules.ToString());
        canonical.AppendLine(request.EffectiveOptions.Strict.ToString());
        foreach (ScriptSourceFile source in request.Sources.OrderBy(source => source.Path, StringComparer.Ordinal))
        {
            canonical.AppendLine(source.Path);
            canonical.AppendLine(source.Content);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}

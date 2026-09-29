using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.TypeScript.Cache;
using NexMud.Scripting.TypeScript.Declarations;

namespace NexMud.Scripting.TypeScript.Compiler;

/// <summary>
/// Concrete TypeScript compiler boundary. The client does not embed Node or a JavaScript runtime;
/// authoring builds invoke an installed TypeScript compiler and return engine-neutral JavaScript.
/// </summary>
public sealed partial class TypeScriptCompiler : IScriptCompiler
{
    private readonly string _executable;
    private readonly IScriptCompileCache _cache;
    private string _compilerIdentity = "typescript-external";
    private readonly SemaphoreSlim _identityGate = new(1, 1);

    public TypeScriptCompiler(string executable = "tsc", IScriptCompileCache? cache = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = executable;
        _cache = cache ?? new InMemoryScriptCompileCache();
    }

    public string CompilerIdentity => _compilerIdentity;

    public async Task<ScriptCompileResult> CompileAsync(
        ScriptCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Language != ScriptSourceLanguage.TypeScript)
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0001",
                "The TypeScript compiler accepts only TypeScript source packages."));
        if (!ScriptApiVersion.IsCompatible(request.Manifest.ApiVersion))
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0002",
                $"Unsupported NexMUD script API version '{request.Manifest.ApiVersion}'."));
        if (request.Sources.Count == 0)
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0003",
                "The script package contains no TypeScript sources."));

        string compilerIdentity;
        try
        {
            compilerIdentity = await ResolveCompilerIdentityAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0005",
                $"TypeScript compiler executable '{_executable}' was not found."));
        }

        string cacheKey = ScriptCompileCacheKey.Compute(request, compilerIdentity, ScriptApiVersion.Current);
        if (_cache.TryGet(cacheKey, out CompiledScriptPackage? cached) && cached is not null)
            return new ScriptCompileResult(true, cached, Array.Empty<ScriptCompilerDiagnostic>());

        string root = Path.Combine(Path.GetTempPath(), $"nexmud-ts-{Guid.NewGuid():N}");
        string sourceRoot = Path.Combine(root, "src");
        string outputRoot = Path.Combine(root, "out");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            foreach (ScriptSourceFile source in request.Sources)
            {
                string path = Path.Combine(sourceRoot, source.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, source.Content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "nexmud-api.d.ts"),
                NexMudTypeDeclarations.Source,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            string configPath = Path.Combine(root, "tsconfig.json");
            await File.WriteAllTextAsync(
                configPath,
                BuildConfig(request.EffectiveOptions, sourceRoot, outputRoot),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            ProcessResult process = await RunAsync(_executable, $"--pretty false --project \"{configPath}\"", root, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ScriptCompilerDiagnostic> diagnostics = ParseDiagnostics(process.Output, root, sourceRoot);
            if (process.ExitCode != 0)
            {
                if (diagnostics.Count == 0)
                    diagnostics = [new ScriptCompilerDiagnostic(
                        ScriptDiagnosticSeverity.Error,
                        "NEXTS0004",
                        string.IsNullOrWhiteSpace(process.Output) ? "TypeScript compilation failed." : process.Output.Trim())];
                return new ScriptCompileResult(false, null, diagnostics);
            }

            List<CompiledScriptModule> modules = [];
            foreach (ScriptSourceFile source in request.Sources)
            {
                string sourceExtension = Path.GetExtension(source.Path);
                if (!sourceExtension.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
                    !sourceExtension.Equals(".tsx", StringComparison.OrdinalIgnoreCase))
                    continue;

                string emittedRelative = (Path.ChangeExtension(source.Path, ".js")
                    ?? throw new InvalidOperationException("Unable to derive emitted module path.")).Replace('\\', '/');
                string emittedPath = Path.Combine(outputRoot, emittedRelative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(emittedPath)) continue;
                string? sourceMap = null;
                string mapPath = emittedPath + ".map";
                if (request.EffectiveOptions.EmitSourceMaps && File.Exists(mapPath))
                    sourceMap = await File.ReadAllTextAsync(mapPath, cancellationToken).ConfigureAwait(false);
                modules.Add(new CompiledScriptModule(
                    emittedRelative,
                    await File.ReadAllTextAsync(emittedPath, cancellationToken).ConfigureAwait(false),
                    sourceMap,
                    source.Path));
            }

            string compiledEntrypoint = (Path.ChangeExtension(request.Manifest.Entrypoint, ".js")
                ?? throw new InvalidOperationException("Unable to derive compiled entrypoint.")).Replace('\\', '/');
            ScriptManifest compiledManifest = new(
                request.Manifest.Id,
                request.Manifest.Name,
                request.Manifest.Version,
                request.Manifest.ApiVersion,
                compiledEntrypoint,
                request.Manifest.Permissions,
                request.Manifest.OwnerKind,
                request.Manifest.RuntimeProfile);
            CompiledScriptPackage package = new(compiledManifest, modules, compilerIdentity, cacheKey);
            _cache.Put(package);
            return new ScriptCompileResult(true, package, diagnostics);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0005",
                $"TypeScript compiler executable '{_executable}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0006",
                $"TypeScript compiler boundary failed: {exception.Message}"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string BuildConfig(ScriptCompilerOptions options, string sourceRoot, string outputRoot)
    {
        var config = new
        {
            compilerOptions = new Dictionary<string, object?>
            {
                ["target"] = options.EcmaScriptTarget,
                ["module"] = "ES2022",
                ["moduleResolution"] = "Bundler",
                ["strict"] = options.Strict,
                ["sourceMap"] = options.EmitSourceMaps,
                ["inlineSources"] = options.EmitSourceMaps,
                ["noEmitOnError"] = true,
                ["skipLibCheck"] = true,
                ["rootDir"] = sourceRoot,
                ["outDir"] = outputRoot,
                ["lib"] = new[] { "ES2022" },
                ["types"] = Array.Empty<string>()
            },
            include = new[] { Path.Combine(sourceRoot, "**", "*.ts"), Path.Combine(sourceRoot, "**", "*.d.ts") }
        };
        return JsonSerializer.Serialize(config);
    }

    private async Task<string> ResolveCompilerIdentityAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_compilerIdentity, "typescript-external", StringComparison.Ordinal)) return _compilerIdentity;
        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_compilerIdentity, "typescript-external", StringComparison.Ordinal)) return _compilerIdentity;
            ProcessResult result = await RunAsync(_executable, "--version", Environment.CurrentDirectory, cancellationToken)
                .ConfigureAwait(false);
            string version = result.Output.Trim();
            if (result.ExitCode == 0 && version.Length > 0) _compilerIdentity = $"typescript-{version}";
            return _compilerIdentity;
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start TypeScript compiler.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
    }

    private static IReadOnlyList<ScriptCompilerDiagnostic> ParseDiagnostics(string output, string root, string sourceRoot)
    {
        List<ScriptCompilerDiagnostic> diagnostics = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match match = DiagnosticPattern().Match(line);
            if (!match.Success) continue;
            string file = match.Groups["file"].Value.Replace('\\', '/');
            string normalizedSourceRoot = sourceRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (file.StartsWith(normalizedSourceRoot, StringComparison.OrdinalIgnoreCase))
                file = file[normalizedSourceRoot.Length..];
            else if (file.StartsWith(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                file = Path.GetFileName(file);
            diagnostics.Add(new ScriptCompilerDiagnostic(
                match.Groups["severity"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase)
                    ? ScriptDiagnosticSeverity.Warning
                    : ScriptDiagnosticSeverity.Error,
                match.Groups["code"].Value,
                match.Groups["message"].Value,
                file,
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["column"].Value)));
        }
        return diagnostics;
    }

    [GeneratedRegex(@"^(?<file>.+)\((?<line>\d+),(?<column>\d+)\): (?<severity>error|warning) (?<code>TS\d+): (?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    private sealed record ProcessResult(int ExitCode, string Output);
}

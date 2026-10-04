using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.Tooling;
using NexMud.Scripting.TypeScript.Cache;
using NexMud.Scripting.TypeScript.Declarations;

namespace NexMud.Scripting.TypeScript.Compiler;

/// <summary>
/// Concrete TypeScript compiler boundary. Production authoring resolves only the NexMUD-owned
/// Node/TypeScript toolchain; an explicit executable remains available for tests and development.
/// </summary>
public sealed partial class TypeScriptCompiler : IScriptCompiler
{
    private readonly string? _explicitExecutable;
    private readonly IToolchainLocator? _toolchainLocator;
    private readonly IToolProcessRunner _processRunner;
    private readonly IScriptCompileCache _cache;
    private readonly ConcurrentDictionary<string, IReadOnlyList<ExportedScriptFunction>> _exportCache = new(StringComparer.Ordinal);
    private string _compilerIdentity = "typescript-unresolved";
    private readonly SemaphoreSlim _identityGate = new(1, 1);

    public TypeScriptCompiler(string? executable = null, IScriptCompileCache? cache = null)
    {
        if (executable is not null) ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _explicitExecutable = executable;
        _toolchainLocator = executable is null ? new ToolchainLocator() : null;
        _processRunner = new ToolProcessRunner();
        _cache = cache ?? new InMemoryScriptCompileCache();
    }

    public TypeScriptCompiler(
        IToolchainLocator toolchainLocator,
        IToolProcessRunner processRunner,
        IScriptCompileCache? cache = null)
    {
        _toolchainLocator = toolchainLocator ?? throw new ArgumentNullException(nameof(toolchainLocator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
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
            if (request.PackageContext is not null)
            {
                IToolchainLocator locator = _toolchainLocator
                    ?? throw new ToolchainUnavailableException("Bundled esbuild toolchain locator is unavailable.");
                ToolchainComponentLocation esbuild = locator.ResolveRequired(ToolchainComponentNames.Esbuild);
                compilerIdentity = $"{compilerIdentity}+esbuild-{esbuild.Version}";
            }
        }
        catch (Exception exception) when (IsToolchainUnavailable(exception))
        {
            return ToolchainUnavailableResult(exception);
        }

        string cacheKey = ScriptCompileCacheKey.Compute(request, compilerIdentity, ScriptApiVersion.Current);
        if (_cache.TryGet(cacheKey, out CompiledScriptPackage? cached) && cached is not null &&
            _exportCache.TryGetValue(cacheKey, out IReadOnlyList<ExportedScriptFunction>? cachedExports))
        {
            return new ScriptCompileResult(true, cached, Array.Empty<ScriptCompilerDiagnostic>(), cachedExports);
        }

        if (request.PackageContext is not null)
            return await CompilePackageAsync(request, compilerIdentity, cacheKey, cancellationToken).ConfigureAwait(false);

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

            ToolProcessResult process = await RunCompilerAsync(
                    ["--pretty", "false", "--project", configPath],
                    root,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ScriptCompilerDiagnostic> diagnostics = ParseDiagnostics(process.Output, root, sourceRoot);
            if (process.ExitCode != 0)
            {
                if (diagnostics.Count == 0)
                    diagnostics = [new ScriptCompilerDiagnostic(
                        ScriptDiagnosticSeverity.Error,
                        "NEXTS0004",
                        string.IsNullOrWhiteSpace(process.Output) ? "TypeScript compilation failed." : process.Output.Trim())];
                return new ScriptCompileResult(false, null, diagnostics, Array.Empty<ExportedScriptFunction>());
            }

            List<CompiledScriptModule> modules = [];
            foreach (ScriptSourceFile source in request.Sources)
            {
                string sourceExtension = Path.GetExtension(source.Path);
                if (!sourceExtension.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
                    !sourceExtension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) &&
                    !sourceExtension.Equals(".js", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (source.Path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)) continue;

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
            IReadOnlyList<ExportedScriptFunction> exports = TypeScriptDeclarationExportReader.Discover(
                request.Manifest.Id,
                request.Sources,
                outputRoot);
            CompiledScriptPackage package = new(compiledManifest, modules, compilerIdentity, cacheKey, exports);
            _cache.Put(package);
            _exportCache[cacheKey] = exports;
            return new ScriptCompileResult(true, package, diagnostics, exports);
        }
        catch (Exception exception) when (IsToolchainUnavailable(exception))
        {
            return ToolchainUnavailableResult(exception);
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

    private async Task<ScriptCompileResult> CompilePackageAsync(
        ScriptCompileRequest request,
        string compilerIdentity,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        ScriptCompilePackageContext context = request.PackageContext!;
        if (!File.Exists(context.ProjectConfigPath))
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0007",
                $"Package TypeScript configuration was not found: {context.ProjectConfigPath}"));
        if (!request.Sources.Any(source => source.Path.Equals(request.Manifest.Entrypoint, StringComparison.Ordinal)))
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0007",
                $"Package entrypoint '{request.Manifest.Entrypoint}' is not present in the source set."));

        string root = Path.Combine(context.PackageRoot, ".nexmud", "build", Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "src");
        string declarationRoot = Path.Combine(root, "types");
        string bundleRoot = Path.Combine(root, "bundle");
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(declarationRoot);
            Directory.CreateDirectory(bundleRoot);
            foreach (ScriptSourceFile source in request.Sources)
            {
                string path = Path.Combine(sourceRoot, source.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, source.Content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }

            string typecheckConfig = Path.Combine(root, "tsconfig.typecheck.json");
            await File.WriteAllTextAsync(
                typecheckConfig,
                BuildPackageConfig(context.ProjectConfigPath, request.EffectiveOptions, sourceRoot, declarationRoot, emitDeclarations: false),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            ToolProcessResult typecheck = await RunCompilerAsync(
                    ["--pretty", "false", "--project", typecheckConfig], root, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ScriptCompilerDiagnostic> diagnostics = ParseDiagnostics(typecheck.Output, root, sourceRoot);
            if (typecheck.ExitCode != 0)
            {
                if (diagnostics.Count == 0)
                    diagnostics = [new ScriptCompilerDiagnostic(
                        ScriptDiagnosticSeverity.Error,
                        "NEXTS0004",
                        string.IsNullOrWhiteSpace(typecheck.Output) ? "TypeScript project typecheck failed." : typecheck.Output.Trim())];
                return new ScriptCompileResult(false, null, diagnostics, Array.Empty<ExportedScriptFunction>());
            }

            string declarationConfig = Path.Combine(root, "tsconfig.declarations.json");
            await File.WriteAllTextAsync(
                declarationConfig,
                BuildPackageConfig(context.ProjectConfigPath, request.EffectiveOptions, sourceRoot, declarationRoot, emitDeclarations: true),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            ToolProcessResult declarationBuild = await RunCompilerAsync(
                    ["--pretty", "false", "--project", declarationConfig], root, cancellationToken)
                .ConfigureAwait(false);
            diagnostics = diagnostics.Concat(ParseDiagnostics(declarationBuild.Output, root, sourceRoot)).ToArray();
            if (declarationBuild.ExitCode != 0)
            {
                if (!diagnostics.Any(diagnostic => diagnostic.Severity == ScriptDiagnosticSeverity.Error))
                    diagnostics = diagnostics.Append(new ScriptCompilerDiagnostic(
                        ScriptDiagnosticSeverity.Error,
                        "NEXTS0004",
                        string.IsNullOrWhiteSpace(declarationBuild.Output) ? "TypeScript declaration generation failed." : declarationBuild.Output.Trim())).ToArray();
                return new ScriptCompileResult(false, null, diagnostics, Array.Empty<ExportedScriptFunction>());
            }

            string compiledEntrypoint = (Path.ChangeExtension(request.Manifest.Entrypoint, ".js")
                ?? throw new InvalidOperationException("Unable to derive compiled entrypoint.")).Replace('\\', '/');
            string bundlePath = Path.Combine(bundleRoot, compiledEntrypoint.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
            string metafilePath = Path.Combine(root, "esbuild-meta.json");
            string entrypointPath = Path.Combine(sourceRoot, request.Manifest.Entrypoint.Replace('/', Path.DirectorySeparatorChar));
            ToolProcessResult bundle = await RunEsbuildAsync(
                [
                    "--bundle",
                    "--format=esm",
                    "--platform=neutral",
                    $"--target={request.EffectiveOptions.EcmaScriptTarget.ToLowerInvariant()}",
                    "--external:@nexmud/api",
                    "--external:node:*",
                    "--sourcemap=external",
                    $"--metafile={metafilePath}",
                    $"--outfile={bundlePath}",
                    entrypointPath
                ],
                root,
                cancellationToken).ConfigureAwait(false);
            if (bundle.ExitCode != 0)
            {
                string message = string.IsNullOrWhiteSpace(bundle.Output) ? "esbuild could not bundle the package entrypoint." : bundle.Output.Trim();
                return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    "NEXTS0008",
                    message));
            }

            ScriptCompilerDiagnostic? incompatibleImport = FindIncompatibleExternalImport(metafilePath);
            if (incompatibleImport is not null)
                return ScriptCompileResult.Failed(incompatibleImport);
            if (!File.Exists(bundlePath))
                return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    "NEXTS0008",
                    "esbuild completed without producing the package entrypoint bundle."));

            string? sourceMap = null;
            string mapPath = bundlePath + ".map";
            if (request.EffectiveOptions.EmitSourceMaps && File.Exists(mapPath))
            {
                string rawSourceMap = await File.ReadAllTextAsync(mapPath, cancellationToken).ConfigureAwait(false);
                sourceMap = NormalizePackageSourceMap(rawSourceMap, mapPath, sourceRoot, context.PackageRoot);
            }
            IReadOnlyList<ExportedScriptFunction> exports = TypeScriptDeclarationExportReader.Discover(
                request.Manifest.Id,
                request.Sources,
                declarationRoot,
                request.Manifest.Entrypoint);
            ScriptManifest compiledManifest = new(
                request.Manifest.Id,
                request.Manifest.Name,
                request.Manifest.Version,
                request.Manifest.ApiVersion,
                compiledEntrypoint,
                request.Manifest.Permissions,
                request.Manifest.OwnerKind,
                request.Manifest.RuntimeProfile);
            CompiledScriptPackage package = new(
                compiledManifest,
                [new CompiledScriptModule(
                    compiledEntrypoint,
                    await File.ReadAllTextAsync(bundlePath, cancellationToken).ConfigureAwait(false),
                    sourceMap,
                    request.Manifest.Entrypoint)],
                compilerIdentity,
                cacheKey,
                exports);
            _cache.Put(package);
            _exportCache[cacheKey] = exports;
            return new ScriptCompileResult(true, package, diagnostics, exports);
        }
        catch (Exception exception) when (IsToolchainUnavailable(exception))
        {
            return ToolchainUnavailableResult(exception);
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
                $"Package build boundary failed: {exception.Message}"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string NormalizePackageSourceMap(
        string sourceMap,
        string mapPath,
        string sourceRoot,
        string packageRoot)
    {
        JsonObject map = JsonNode.Parse(sourceMap)?.AsObject()
            ?? throw new JsonException("esbuild returned an invalid source map.");
        if (map["sources"] is not JsonArray sources)
            return sourceMap;

        string mapDirectory = Path.GetDirectoryName(mapPath)!;
        string mapSourceRoot = map["sourceRoot"]?.GetValue<string>() ?? string.Empty;
        string sourceBase = string.IsNullOrWhiteSpace(mapSourceRoot)
            ? mapDirectory
            : Path.GetFullPath(Path.Combine(mapDirectory, mapSourceRoot));
        for (int index = 0; index < sources.Count; index++)
        {
            string? source = sources[index]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(source)) continue;
            string absoluteSource = Path.GetFullPath(Path.IsPathRooted(source)
                ? source
                : Path.Combine(sourceBase, source));
            string? relativeSource = TryGetRelativePath(sourceRoot, absoluteSource)
                ?? TryGetRelativePath(packageRoot, absoluteSource);
            if (relativeSource is null)
                throw new InvalidOperationException("esbuild source map references a file outside the package build roots.");
            sources[index] = relativeSource.Replace('\\', '/');
        }

        map["sourceRoot"] = string.Empty;
        return map.ToJsonString();
    }

    private static string? TryGetRelativePath(string root, string path)
    {
        string relative = Path.GetRelativePath(ResolvePhysicalPath(root), ResolvePhysicalPath(path));
        return relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? null
            : relative;
    }

    private static string ResolvePhysicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string current = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("A source-map path must be absolute.");
        foreach (string segment in fullPath[current.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return Path.GetFullPath(current);
    }

    private static string BuildPackageConfig(
        string projectConfigPath,
        ScriptCompilerOptions options,
        string sourceRoot,
        string outputRoot,
        bool emitDeclarations)
    {
        var config = new
        {
            extends = projectConfigPath,
            compilerOptions = new Dictionary<string, object?>
            {
                ["target"] = options.EcmaScriptTarget,
                ["module"] = "ESNext",
                ["moduleResolution"] = "Bundler",
                ["strict"] = options.Strict,
                ["allowJs"] = true,
                ["checkJs"] = true,
                ["sourceMap"] = false,
                ["declaration"] = emitDeclarations,
                ["emitDeclarationOnly"] = emitDeclarations,
                ["declarationMap"] = false,
                ["noEmit"] = !emitDeclarations,
                ["noEmitOnError"] = true,
                ["skipLibCheck"] = true,
                ["rootDir"] = sourceRoot,
                ["outDir"] = outputRoot,
                ["lib"] = new[] { "ES2022" },
                ["types"] = Array.Empty<string>(),
                ["resolveJsonModule"] = true
            },
            include = new[] { "src/**/*.ts", "src/**/*.tsx", "src/**/*.js", "src/**/*.jsx", "src/**/*.d.ts" },
            exclude = new[] { "node_modules", "dist", ".nexmud", "**/*.test.*", "**/*.spec.*" }
        };
        return JsonSerializer.Serialize(config);
    }

    private async Task<ToolProcessResult> RunEsbuildAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        IToolchainLocator locator = _toolchainLocator
            ?? throw new ToolchainUnavailableException("Bundled esbuild toolchain locator is unavailable.");
        ToolchainComponentLocation node = locator.ResolveRequired(ToolchainComponentNames.Node);
        ToolchainComponentLocation esbuild = locator.ResolveRequired(ToolchainComponentNames.Esbuild);
        return await _processRunner.RunAsync(
            new ToolProcessRequest(
                node.FullPath,
                [esbuild.FullPath, .. arguments],
                workingDirectory,
                [Path.GetDirectoryName(node.FullPath)!]),
            cancellationToken).ConfigureAwait(false);
    }

    private static ScriptCompilerDiagnostic? FindIncompatibleExternalImport(string metafilePath)
    {
        using JsonDocument metafile = JsonDocument.Parse(File.ReadAllText(metafilePath));
        if (!metafile.RootElement.TryGetProperty("outputs", out JsonElement outputs)) return null;
        foreach (JsonProperty output in outputs.EnumerateObject())
        {
            if (!output.Value.TryGetProperty("imports", out JsonElement imports)) continue;
            foreach (JsonElement import in imports.EnumerateArray())
            {
                if (!import.TryGetProperty("external", out JsonElement external) || !external.GetBoolean()) continue;
                string path = import.TryGetProperty("path", out JsonElement pathElement)
                    ? pathElement.GetString() ?? string.Empty
                    : string.Empty;
                if (path.Equals("@nexmud/api", StringComparison.Ordinal)) continue;
                string message = path.StartsWith("node:", StringComparison.Ordinal)
                    ? $"Node built-in '{path}' is not supported by the NexMUD runtime."
                    : $"External import '{path}' is not supported by the NexMUD runtime.";
                return new ScriptCompilerDiagnostic(ScriptDiagnosticSeverity.Error, "NEXTS0009", message);
            }
        }
        return null;
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
                ["allowJs"] = true,
                ["checkJs"] = true,
                ["sourceMap"] = options.EmitSourceMaps,
                ["inlineSources"] = options.EmitSourceMaps,
                ["declaration"] = true,
                ["declarationMap"] = options.EmitSourceMaps,
                ["noEmitOnError"] = true,
                ["skipLibCheck"] = true,
                ["rootDir"] = sourceRoot,
                ["outDir"] = outputRoot,
                ["lib"] = new[] { "ES2022" },
                ["types"] = Array.Empty<string>()
            },
            include = new[]
            {
                Path.Combine(sourceRoot, "**", "*.ts"),
                Path.Combine(sourceRoot, "**", "*.d.ts"),
                Path.Combine(sourceRoot, "**", "*.js")
            }
        };
        return JsonSerializer.Serialize(config);
    }

    private async Task<string> ResolveCompilerIdentityAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_compilerIdentity, "typescript-unresolved", StringComparison.Ordinal)) return _compilerIdentity;
        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_compilerIdentity, "typescript-unresolved", StringComparison.Ordinal)) return _compilerIdentity;
            ToolProcessResult result = await RunCompilerAsync(
                    ["--version"],
                    Environment.CurrentDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            string version = result.Output.Trim();
            if (result.ExitCode == 0 && version.Length > 0)
            {
                string source = _explicitExecutable is null ? "bundled" : "external";
                _compilerIdentity = $"typescript-{source}-{version}";
            }
            return _compilerIdentity;
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private Task<ToolProcessResult> RunCompilerAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (_explicitExecutable is not null)
        {
            return _processRunner.RunAsync(
                new ToolProcessRequest(
                    _explicitExecutable,
                    arguments,
                    workingDirectory,
                    ResolveExternalPathEntries(_explicitExecutable)),
                cancellationToken);
        }

        IToolchainLocator locator = _toolchainLocator
            ?? throw new ToolchainUnavailableException("Bundled TypeScript toolchain locator is unavailable.");
        ToolchainComponentLocation node = locator.ResolveRequired(ToolchainComponentNames.Node);
        ToolchainComponentLocation typeScript = locator.ResolveRequired(ToolchainComponentNames.TypeScript);
        List<string> invocationArguments = [typeScript.FullPath, .. arguments];
        return _processRunner.RunAsync(
            new ToolProcessRequest(
                node.FullPath,
                invocationArguments,
                workingDirectory,
                [Path.GetDirectoryName(node.FullPath)!]),
            cancellationToken);
    }

    private ScriptCompileResult ToolchainUnavailableResult(Exception exception) =>
        ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
            ScriptDiagnosticSeverity.Error,
            "NEXTS0005",
            _explicitExecutable is null
                ? $"Bundled TypeScript toolchain is unavailable: {exception.Message}"
                : $"TypeScript compiler executable '{_explicitExecutable}' was not found."));

    private static bool IsToolchainUnavailable(Exception exception) =>
        exception is ToolchainUnavailableException
            or FileNotFoundException
            or DirectoryNotFoundException
            or System.ComponentModel.Win32Exception;

    private static IReadOnlyList<string> ResolveExternalPathEntries(string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            string? directory = Path.GetDirectoryName(executable);
            return string.IsNullOrWhiteSpace(directory) ? [] : [directory];
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexMud.Client.Paths;
using NexMud.Contracts.Events;
using NexMud.Core.Events;
using NexMud.Scripting.Compilation;
using NexMud.Scripting.Host;
using NexMud.Scripting.Permissions;
using NexMud.Scripting.Runtime;
using NexMud.Scripting.TypeScript.Compiler;
using NexMud.Scripting.TypeScript.Declarations;

namespace NexMud.Client.Scripting;

public enum ScriptPackageBuildStatus
{
    NeverBuilt,
    Succeeded,
    Failed
}

public sealed record ScriptPackageDefinition(
    string PackageId,
    string Name,
    string Version,
    string Entrypoint,
    ScriptCapability Capabilities,
    bool Enabled = false)
{
    public ScriptPackageDefinition Normalize()
    {
        string packageId = ScriptWorkspacePath.NormalizeIdentifier(PackageId, nameof(PackageId));
        string name = string.IsNullOrWhiteSpace(Name) ? packageId : Name.Trim();
        string version = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim();
        string entrypoint = ScriptWorkspacePath.NormalizeRelativePath(Entrypoint);
        return this with
        {
            PackageId = packageId,
            Name = name,
            Version = version,
            Entrypoint = entrypoint
        };
    }
}

public sealed record ScriptPackageBuildResult(
    string PackageId,
    Guid BuildId,
    bool Success,
    IReadOnlyList<ScriptCompilerDiagnostic> Diagnostics,
    IReadOnlyList<ExportedScriptFunction> ExportedFunctions,
    string? OutputArtifactId,
    DateTimeOffset BuiltAt);

public sealed record ScriptPackageSnapshot(
    ScriptPackageDefinition Definition,
    ScriptPackageBuildStatus BuildStatus,
    ScriptStatus RuntimeStatus,
    IReadOnlyList<ExportedScriptFunction> Exports,
    DateTimeOffset? LastBuild,
    string? LastRuntimeFault = null);

public sealed record ScriptWorkspaceSourceFile(string RelativePath, long Size);

public sealed record ScriptSourceSearchResult(
    string PackageId,
    string RelativePath,
    int Line,
    int Column,
    string Preview);

public sealed record ScriptPackageBuildChanged(
    string ProfileId,
    string PackageId,
    Guid BuildId,
    bool Success,
    int ErrorCount,
    DateTimeOffset BuiltAt) : IMudEvent;

public sealed record ScriptPackageRuntimeChanged(
    string ProfileId,
    string PackageId,
    string Status,
    DateTimeOffset Timestamp,
    string? Message = null) : IMudEvent;

/// <summary>
/// C#-owned profile-scoped source workspace. Monaco never receives a host filesystem path and
/// never persists source directly. Successful builds replace the active Jint package; failed
/// builds leave the last-known-good runtime untouched.
/// </summary>
public sealed partial class ScriptWorkspaceService
{
    private const string ManifestFileName = "manifest.json";
    private static readonly string[] SupportedExtensions = [".ts", ".js", ".d.ts", ".json"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record BuildState(
        ScriptPackageBuildStatus Status,
        IReadOnlyList<ExportedScriptFunction> Exports,
        DateTimeOffset? BuiltAt,
        string? LastRuntimeFault = null);

    private readonly ClientScriptPlatform _platform;
    private readonly IEventSink _events;
    private readonly IScriptCompiler _compiler;
    private readonly IScriptPackageManager _packageManager;
    private readonly Func<string> _activeProfileId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, BuildState> _buildStates = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedRuntimeProfileId;
    private readonly HashSet<string> _loadedPackageIds = new(StringComparer.OrdinalIgnoreCase);

    public ScriptWorkspaceService(
        ClientScriptPlatform platform,
        IEventSink events,
        Func<string> activeProfileId,
        IScriptCompiler? compiler = null,
        IScriptPackageManager? packageManager = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _activeProfileId = activeProfileId ?? throw new ArgumentNullException(nameof(activeProfileId));
        _compiler = compiler ?? new TypeScriptCompiler();
        _packageManager = packageManager ?? new ScriptPackageManager();
    }

    public event EventHandler? WorkspaceChanged;

    public string SdkDeclarations => NexMudTypeDeclarations.Source;

    public static bool IsSupportedSourcePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        string normalized = relativePath.Replace('\\', '/');
        return SupportedExtensions.Any(extension => normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ScriptPackageSnapshot>> ListPackagesAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        ScriptPackageWorkspaceMigrationResult migration =
            await EnsurePackageWorkspaceAsync(profileId, cancellationToken).ConfigureAwait(false);

        List<ScriptPackageSnapshot> packages = [];
        foreach (ScriptPackageCatalogEntry package in migration.Packages
                     .OrderBy(value => value.Document.Name, StringComparer.OrdinalIgnoreCase))
        {
            ScriptPackageDefinition definition = package.Document.ToRuntimeDefinition(package.Enabled);
            string stateKey = StateKey(profileId, definition.PackageId);
            BuildState state = _buildStates.TryGetValue(stateKey, out BuildState? known)
                ? known
                : new BuildState(ScriptPackageBuildStatus.NeverBuilt, [], null);
            ScriptStatus runtimeStatus = ResolveRuntimeStatus(profileId, definition.PackageId);
            packages.Add(new ScriptPackageSnapshot(
                definition,
                state.Status,
                runtimeStatus,
                state.Exports,
                state.BuiltAt,
                state.LastRuntimeFault));
        }
        return packages;
    }

    public async Task<ScriptPackageDefinition> CreatePackageAsync(
        string profileId,
        string name,
        string? packageId = null,
        CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string id = ScriptWorkspacePath.NormalizeIdentifier(
            string.IsNullOrWhiteSpace(packageId) ? CreatePackageId(name) : packageId,
            nameof(packageId));
        string scriptsRoot = GetScriptsRoot(profileId);
        _ = await EnsurePackageWorkspaceAsync(profileId, cancellationToken).ConfigureAwait(false);
        ScriptPackageDocument document = ScriptPackageDocument.CreateNew(
            id,
            id,
            ScriptPackageWorkspaceMigrator.ManagedPnpmVersion,
            displayName: name.Trim());

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string packageRoot = Path.Combine(scriptsRoot, id);
            if (Directory.Exists(packageRoot))
                throw new InvalidOperationException($"Script package '{id}' already exists for profile '{profileId}'.");
            Directory.CreateDirectory(Path.Combine(packageRoot, "src"));
            await AtomicWriteAsync(
                Path.Combine(packageRoot, ScriptPackageWorkspaceMigrator.PackageJsonFileName),
                document.ToJson(),
                cancellationToken).ConfigureAwait(false);
            await AtomicWriteAsync(
                Path.Combine(packageRoot, "src", "main.ts"),
                "import { nex, type AutomationInvocationContext } from \"@nexmud/api\";\n\nexport async function run(ctx: AutomationInvocationContext) {\n  await nex.log.info(\"Script invoked\", { source: ctx.sourceKind });\n}\n",
                cancellationToken).ConfigureAwait(false);
            await ScriptPackageWorkspaceMigrator.WriteRuntimeStateAsync(
                scriptsRoot,
                id,
                enabled: false,
                cancellationToken).ConfigureAwait(false);
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
            return document.ToRuntimeDefinition(enabled: false);
        }
        finally
        {
            _gate.Release();
        }
    }


    public async Task<ScriptPackageSnapshot?> GetPackageAsync(
        string profileId,
        string packageId,
        CancellationToken cancellationToken = default) =>
        (await ListPackagesAsync(profileId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(package => package.Definition.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<ScriptWorkspaceSourceFile>> ListSourceFilesAsync(
        string profileId,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string packageRoot = GetPackageRoot(profileId, packageId);
        if (!Directory.Exists(packageRoot))
            return Task.FromResult<IReadOnlyList<ScriptWorkspaceSourceFile>>([]);
        ScriptWorkspaceSourceFile[] files = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsGeneratedPackagePath(packageRoot, path))
            .Where(IsSupportedSourcePath)
            .Select(path => new ScriptWorkspaceSourceFile(
                Path.GetRelativePath(packageRoot, path).Replace('\\', '/'),
                new FileInfo(path).Length))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ScriptWorkspaceSourceFile>>(files);
    }

    public async Task<string> ReadSourceAsync(
        string profileId,
        string packageId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        string path = GetSourcePath(profileId, packageId, relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Script source file was not found.", relativePath);
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSourceAsync(
        string profileId,
        string packageId,
        string relativePath,
        string content,
        bool build = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!IsSupportedSourcePath(relativePath))
            throw new InvalidOperationException("Only .ts, .js, .d.ts, and .json files are supported by the script workspace.");
        string path = GetSourcePath(profileId, packageId, relativePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicWriteAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        if (build && (relativePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                      relativePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
            _ = await BuildPackageAsync(profileId, packageId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateFolderAsync(
        string profileId,
        string packageId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string packageRoot = GetPackageRoot(profileId, packageId);
        string target = ScriptWorkspacePath.CombineInside(packageRoot, relativePath);
        Directory.CreateDirectory(target);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task CreateFileAsync(
        string profileId,
        string packageId,
        string relativePath,
        string content = "",
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedSourcePath(relativePath))
            throw new InvalidOperationException("Only .ts, .js, .d.ts, and .json files are supported by the script workspace.");
        string path = GetSourcePath(profileId, packageId, relativePath);
        if (File.Exists(path)) throw new InvalidOperationException($"Script source '{relativePath}' already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicWriteAsync(path, content, cancellationToken).ConfigureAwait(false);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DuplicateFileAsync(
        string profileId,
        string packageId,
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        string source = GetSourcePath(profileId, packageId, sourceRelativePath);
        string destination = GetSourcePath(profileId, packageId, destinationRelativePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Script source file was not found.", sourceRelativePath);
        if (File.Exists(destination)) throw new InvalidOperationException($"Script source '{destinationRelativePath}' already exists.");
        await AtomicWriteAsync(destination, await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RenamePathAsync(
        string profileId,
        string packageId,
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string packageRoot = GetPackageRoot(profileId, packageId);
        string source = ScriptWorkspacePath.CombineInside(packageRoot, sourceRelativePath);
        string destination = ScriptWorkspacePath.CombineInside(packageRoot, destinationRelativePath);
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new FileNotFoundException("Script workspace path was not found.", sourceRelativePath);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidOperationException($"Destination '{destinationRelativePath}' already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(source)) File.Move(source, destination);
        else Directory.Move(source, destination);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task DeletePathAsync(
        string profileId,
        string packageId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string packageRoot = GetPackageRoot(profileId, packageId);
        string target = ScriptWorkspacePath.CombineInside(packageRoot, relativePath);
        if (File.Exists(target)) File.Delete(target);
        else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task DeletePackageAsync(
        string profileId,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        packageId = ScriptWorkspacePath.NormalizeIdentifier(packageId, nameof(packageId));
        if (string.Equals(profileId, _activeProfileId(), StringComparison.Ordinal) && _loadedPackageIds.Contains(packageId))
        {
            ScriptModuleId moduleId = new(packageId);
            await _platform.JavaScriptRuntime.UnloadAsync(moduleId, cancellationToken).ConfigureAwait(false);
            _platform.UnregisterRuntimeProfile(moduleId, profileId);
            _loadedPackageIds.Remove(packageId);
        }
        string packageRoot = GetPackageRoot(profileId, packageId);
        if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, recursive: true);
        await ScriptPackageWorkspaceMigrator.RemoveRuntimeStateAsync(
            GetScriptsRoot(profileId),
            packageId,
            cancellationToken).ConfigureAwait(false);
        _buildStates.TryRemove(StateKey(profileId, packageId), out _);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ScriptPackageBuildResult> BuildPackageAsync(
        string profileId,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        packageId = ScriptWorkspacePath.NormalizeIdentifier(packageId, nameof(packageId));
        string packageRoot = GetPackageRoot(profileId, packageId);
        ScriptPackageDefinition definition =
            await RequirePackageDefinitionAsync(profileId, packageId, cancellationToken).ConfigureAwait(false);
        Guid buildId = Guid.NewGuid();
        DateTimeOffset builtAt = DateTimeOffset.UtcNow;

        List<ScriptSourceFile> sources = [];
        foreach (string sourcePath in EnumerateBuildInputFiles(packageRoot)
                     .Where(path => path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string relativePath = Path.GetRelativePath(packageRoot, sourcePath).Replace('\\', '/');
            sources.Add(new ScriptSourceFile(
                relativePath,
                await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false)));
        }

        ScriptManifest manifest = new(
            new ScriptModuleId(definition.PackageId),
            definition.Name,
            definition.Version,
            ScriptApiVersion.Current,
            definition.Entrypoint,
            definition.Capabilities,
            ScriptOwnerKind.UserScript,
            ScriptRuntimeProfile.UserScript);
        ScriptCompileResult compile = await CompilePackageAsync(
            profileId,
            packageRoot,
            manifest,
            sources,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ExportedScriptFunction> exports = compile.ExportedFunctions ?? Array.Empty<ExportedScriptFunction>();
        ScriptPackageBuildStatus status = compile.Success ? ScriptPackageBuildStatus.Succeeded : ScriptPackageBuildStatus.Failed;
        string stateKey = StateKey(profileId, packageId);
        BuildState previous = _buildStates.TryGetValue(stateKey, out BuildState? prior)
            ? prior
            : new BuildState(ScriptPackageBuildStatus.NeverBuilt, [], null);
        _buildStates[stateKey] = compile.Success
            ? new BuildState(status, exports, builtAt, previous.LastRuntimeFault)
            : previous with { Status = status, BuiltAt = builtAt };

        if (compile.Success && compile.Package is not null && definition.Enabled &&
            string.Equals(profileId, _activeProfileId(), StringComparison.Ordinal))
        {
            try
            {
                await LoadOrReloadAsync(profileId, definition, compile.Package, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _buildStates[stateKey] = _buildStates[stateKey] with { LastRuntimeFault = exception.Message };
                await PublishRuntimeAsync(profileId, packageId, ScriptStatus.Faulted, exception.Message, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }

        ScriptPackageBuildResult result = new(
            packageId,
            buildId,
            compile.Success,
            compile.Diagnostics,
            exports,
            compile.Package?.CacheKey,
            builtAt);
        await _events.PublishAsync(
            new ScriptPackageBuildChanged(
                profileId,
                packageId,
                buildId,
                compile.Success,
                compile.Diagnostics.Count(diagnostic => diagnostic.Severity == ScriptDiagnosticSeverity.Error),
                builtAt),
            "scripting.workspace",
            cancellationToken).ConfigureAwait(false);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private async Task<ScriptCompileResult> CompilePackageAsync(
        string profileId,
        string packageRoot,
        ScriptManifest manifest,
        IReadOnlyList<ScriptSourceFile> sources,
        CancellationToken cancellationToken)
    {
        ScriptPackageDocument package;
        try
        {
            package = await ScriptPackageDocument.LoadAsync(
                Path.Combine(packageRoot, ScriptPackageWorkspaceMigrator.PackageJsonFileName),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0010",
                $"Package metadata could not be loaded: {exception.Message}"));
        }

        if (ScriptPackageDependencyValidator.Validate(package).Count > 0)
        {
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0011",
                "Package metadata contains unsupported dependency sources. Only registry semver and profile-local workspace dependencies are allowed."));
        }

        if (package.Dependencies.Count > 0 || package.DevDependencies.Count > 0 ||
            package.OptionalDependencies.Count > 0 || package.PeerDependencies.Count > 0)
        {
            ScriptPackageOperationResult restore;
            try
            {
                restore = await _packageManager.RestoreAsync(profileId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    "NEXTS0012",
                    $"Package dependency restore failed: {exception.Message}"));
            }

            if (!restore.Success)
            {
                return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                    ScriptDiagnosticSeverity.Error,
                    "NEXTS0012",
                    $"Package dependencies are not ready: {restore.Message}"));
            }
        }

        string projectConfigPath = Path.Combine(packageRoot, "tsconfig.json");
        if (!File.Exists(projectConfigPath))
        {
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0013",
                "Package TypeScript project configuration is missing."));
        }

        string scriptsRoot = GetScriptsRoot(profileId);
        string dependencyGraphHash;
        try
        {
            dependencyGraphHash = await ComputeDependencyGraphHashAsync(scriptsRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ScriptCompileResult.Failed(new ScriptCompilerDiagnostic(
                ScriptDiagnosticSeverity.Error,
                "NEXTS0014",
                $"Package dependency state could not be fingerprinted: {exception.Message}"));
        }
        ScriptCompileRequest request = new(manifest, ScriptSourceLanguage.TypeScript, sources)
        {
            PackageContext = new ScriptCompilePackageContext(packageRoot, projectConfigPath, dependencyGraphHash)
        };
        return await _compiler.CompileAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeDependencyGraphHashAsync(
        string scriptsRoot,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(scriptsRoot);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] separator = [0];
        string[] explicitInputs =
        [
            Path.Combine(root, ScriptPackageWorkspaceMigrator.LockfileName),
            Path.Combine(root, "pnpm-workspace.yaml"),
            Path.Combine(root, ".npmrc"),
            Path.Combine(root, "node_modules", ".pnpm", "lock.yaml"),
            Path.Combine(root, ScriptPackageWorkspaceMigrator.NexMudDirectoryName, "tsconfig.base.json"),
            Path.Combine(root, ScriptPackageWorkspaceMigrator.NexMudDirectoryName, "sdk", "@nexmud", "api", "index.d.ts")
        ];
        IEnumerable<string> inputs = EnumerateBuildInputFiles(root)
            .Concat(explicitInputs.Where(File.Exists))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);
        foreach (string path in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(separator);
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            byte[] buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
            hash.AppendData(separator);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateBuildInputFiles(string root)
    {
        Stack<string> directories = new();
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    yield return file;
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(ScriptPackageWorkspaceMigrator.NexMudDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                directories.Push(child);
            }
        }
    }

    public async Task<IReadOnlyList<ScriptPackageBuildResult>> BuildAllAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ScriptPackageSnapshot> packages = await ListPackagesAsync(profileId, cancellationToken).ConfigureAwait(false);
        List<ScriptPackageBuildResult> results = [];
        foreach (ScriptPackageSnapshot package in packages)
            results.Add(await BuildPackageAsync(profileId, package.Definition.PackageId, cancellationToken).ConfigureAwait(false));
        return results;
    }

    public async Task SetEnabledAsync(
        string profileId,
        string packageId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        packageId = ScriptWorkspacePath.NormalizeIdentifier(packageId, nameof(packageId));
        ScriptPackageDefinition definition =
            await RequirePackageDefinitionAsync(profileId, packageId, cancellationToken).ConfigureAwait(false);
        ScriptPackageDefinition updated = definition with { Enabled = enabled };
        await ScriptPackageWorkspaceMigrator.WriteRuntimeStateAsync(
            GetScriptsRoot(profileId),
            updated.PackageId,
            enabled,
            cancellationToken).ConfigureAwait(false);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);

        if (!string.Equals(profileId, _activeProfileId(), StringComparison.Ordinal)) return;
        if (!enabled)
        {
            if (_loadedPackageIds.Remove(updated.PackageId))
            {
                ScriptModuleId moduleId = new(updated.PackageId);
                await _platform.JavaScriptRuntime.UnloadAsync(moduleId, cancellationToken).ConfigureAwait(false);
                _platform.UnregisterRuntimeProfile(moduleId, profileId);
                await PublishRuntimeAsync(profileId, updated.PackageId, ScriptStatus.Disabled, null, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        ScriptPackageBuildResult build = await BuildPackageAsync(profileId, packageId, cancellationToken).ConfigureAwait(false);
        if (!build.Success)
            throw new InvalidOperationException("A script package with no successful build cannot be enabled.");
    }

    public async Task ActivateProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        if (string.Equals(_loadedRuntimeProfileId, profileId, StringComparison.Ordinal)) return;

        string? previousProfileId = _loadedRuntimeProfileId;
        foreach (string packageId in _loadedPackageIds.ToArray())
        {
            ScriptModuleId moduleId = new(packageId);
            await _platform.JavaScriptRuntime.UnloadAsync(moduleId, cancellationToken).ConfigureAwait(false);
            if (previousProfileId is not null)
                _platform.UnregisterRuntimeProfile(moduleId, previousProfileId);
        }
        _loadedPackageIds.Clear();
        _loadedRuntimeProfileId = profileId;

        IReadOnlyList<ScriptPackageSnapshot> packages = await ListPackagesAsync(profileId, cancellationToken).ConfigureAwait(false);
        foreach (ScriptPackageSnapshot package in packages.Where(package => package.Definition.Enabled))
            _ = await BuildPackageAsync(profileId, package.Definition.PackageId, cancellationToken).ConfigureAwait(false);
    }


    public async Task<ScriptFunctionInvocationResult> RunFunctionAsync(
        string profileId,
        ScriptFunctionRef functionRef,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        if (!string.Equals(profileId, _activeProfileId(), StringComparison.Ordinal))
            return ScriptFunctionInvocationResult.Failed(
                "ScriptProfileInactive",
                "Manual execution is available only for the active Connection Profile runtime.");
        return await _platform.Functions.InvokeAsync(
            new ScriptFunctionInvocationDescriptor(
                functionRef,
                AutomationInvocationSourceKind.ManualScriptRun,
                $"studio:{functionRef}",
                arguments),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScriptSourceSearchResult>> SearchAsync(
        string profileId,
        string query,
        CancellationToken cancellationToken = default)
    {
        profileId = ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ScriptPackageWorkspaceMigrationResult migration =
            await EnsurePackageWorkspaceAsync(profileId, cancellationToken).ConfigureAwait(false);
        List<ScriptSourceSearchResult> results = [];
        foreach (ScriptPackageCatalogEntry package in migration.Packages)
        {
            foreach (string path in Directory.EnumerateFiles(package.PackageRoot, "*", SearchOption.AllDirectories)
                         .Where(path => !IsGeneratedPackagePath(package.PackageRoot, path))
                         .Where(path => !Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                         .Where(IsSupportedSourcePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    int column = lines[lineIndex].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (column < 0) continue;
                    string relative = Path.GetRelativePath(package.PackageRoot, path).Replace('\\', '/');
                    results.Add(new ScriptSourceSearchResult(
                        package.Document.NexMud.Id,
                        relative,
                        lineIndex + 1,
                        column + 1,
                        lines[lineIndex].Trim()));
                    if (results.Count >= 500) return results;
                }
            }
        }
        return results;
    }

    private async Task LoadOrReloadAsync(
        string profileId,
        ScriptPackageDefinition definition,
        CompiledScriptPackage package,
        CancellationToken cancellationToken)
    {
        string? previousProfileId = _platform.RegisterRuntimeProfile(package.Manifest.Id, profileId);
        try
        {
            string storagePath = Path.Combine(GetProfileRoot(profileId), "script-storage.db");
            IScriptHost host = _platform.CreateHost(
                package.Manifest.Id,
                new ScriptPermissionSet(package.Manifest.Permissions),
                ScriptCommandOrigin.Script,
                definition.Name,
                storagePath);
            bool loaded = _platform.JavaScriptRuntime.Snapshot().Any(snapshot =>
                snapshot.Id.Value.Equals(definition.PackageId, StringComparison.OrdinalIgnoreCase) &&
                snapshot.Status is ScriptStatus.Loading or ScriptStatus.Running);
            if (loaded)
                await _platform.JavaScriptRuntime.ReloadAsync(package, host, cancellationToken).ConfigureAwait(false);
            else
                await _platform.JavaScriptRuntime.LoadAsync(package, host, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _platform.RestoreRuntimeProfile(package.Manifest.Id, profileId, previousProfileId);
            throw;
        }

        _loadedPackageIds.Add(definition.PackageId);
        await PublishRuntimeAsync(profileId, definition.PackageId, ScriptStatus.Running, null, cancellationToken).ConfigureAwait(false);
    }

    private ScriptStatus ResolveRuntimeStatus(string profileId, string packageId)
    {
        if (!string.Equals(profileId, _loadedRuntimeProfileId, StringComparison.Ordinal)) return ScriptStatus.Disabled;
        return _platform.JavaScriptRuntime.Snapshot()
            .FirstOrDefault(snapshot => snapshot.Id.Value.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.Status
            ?? ScriptStatus.Disabled;
    }

    private Task PublishRuntimeAsync(
        string profileId,
        string packageId,
        ScriptStatus status,
        string? message,
        CancellationToken cancellationToken) =>
        _events.PublishAsync(
            new ScriptPackageRuntimeChanged(profileId, packageId, status.ToString(), DateTimeOffset.UtcNow, message),
            "scripting.workspace",
            cancellationToken).AsTask();

    private async Task<ScriptPackageWorkspaceMigrationResult> EnsurePackageWorkspaceAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        string root = GetScriptsRoot(profileId);
        return await ScriptPackageWorkspaceMigrator.EnsureAsync(
            root,
            ScriptPackageWorkspaceMigrator.ManagedPnpmVersion,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScriptPackageDefinition> RequirePackageDefinitionAsync(
        string profileId,
        string packageId,
        CancellationToken cancellationToken)
    {
        ScriptPackageWorkspaceMigrationResult migration =
            await EnsurePackageWorkspaceAsync(profileId, cancellationToken).ConfigureAwait(false);
        ScriptPackageCatalogEntry? package = migration.Packages.FirstOrDefault(candidate =>
            candidate.Document.NexMud.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        if (package is null)
            throw new InvalidOperationException($"Script package '{packageId}' is missing or invalid.");
        return package.Document.ToRuntimeDefinition(package.Enabled);
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        try { File.Move(temporary, path, overwrite: true); }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static string CreatePackageId(string name)
    {
        string candidate = new(name.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        candidate = string.Join('-', candidate.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(candidate) ? "script-" + Guid.NewGuid().ToString("N")[..8] : candidate;
    }

    private static string StateKey(string profileId, string packageId) => $"{profileId}\n{packageId}";

    private static string GetProfileRoot(string profileId)
    {
        string dataRoot = Path.GetDirectoryName(NexMudDataPaths.GetFilePath("settings.json"))
            ?? throw new InvalidOperationException("Unable to resolve the NexMUD application data directory.");
        return Path.Combine(dataRoot, "profiles", ScriptWorkspacePath.NormalizeIdentifier(profileId, nameof(profileId)));
    }

    private static string GetScriptsRoot(string profileId) => Path.Combine(GetProfileRoot(profileId), "scripts");

    internal static string ResolvePackageRoot(string profileId, string packageId) => GetPackageRoot(profileId, packageId);

    private static string GetPackageRoot(string profileId, string packageId)
    {
        string normalizedPackageId = ScriptWorkspacePath.NormalizeIdentifier(packageId, nameof(packageId));
        string scriptsRoot = GetScriptsRoot(profileId);
        string direct = Path.Combine(scriptsRoot, normalizedPackageId);
        if (Directory.Exists(direct))
        {
            string packageJson = Path.Combine(direct, ScriptPackageWorkspaceMigrator.PackageJsonFileName);
            if (!File.Exists(packageJson)) return direct;
            try
            {
                ScriptPackageDocument document = ScriptPackageDocument.Parse(File.ReadAllText(packageJson));
                if (document.NexMud.Id.Equals(normalizedPackageId, StringComparison.OrdinalIgnoreCase)) return direct;
            }
            catch
            {
                return direct;
            }
        }

        if (!Directory.Exists(scriptsRoot)) return direct;
        foreach (string directory in Directory.EnumerateDirectories(scriptsRoot))
        {
            string packageJson = Path.Combine(directory, ScriptPackageWorkspaceMigrator.PackageJsonFileName);
            if (!File.Exists(packageJson)) continue;
            try
            {
                ScriptPackageDocument document = ScriptPackageDocument.Parse(File.ReadAllText(packageJson));
                if (document.NexMud.Id.Equals(normalizedPackageId, StringComparison.OrdinalIgnoreCase)) return directory;
            }
            catch
            {
                // Invalid package metadata is reported by package discovery; path lookup skips it.
            }
        }
        return direct;
    }

    private static string ResolvePackageIdFromDirectory(string profileId, string directoryName)
    {
        string packageRoot = Path.Combine(
            GetScriptsRoot(profileId),
            ScriptWorkspacePath.NormalizeIdentifier(directoryName, nameof(directoryName)));
        string packageJson = Path.Combine(packageRoot, ScriptPackageWorkspaceMigrator.PackageJsonFileName);
        if (!File.Exists(packageJson)) return directoryName;
        try
        {
            return ScriptPackageDocument.Parse(File.ReadAllText(packageJson)).NexMud.Id;
        }
        catch
        {
            return directoryName;
        }
    }

    private static bool IsGeneratedPackagePath(string packageRoot, string path)
    {
        string relative = Path.GetRelativePath(packageRoot, path).Replace('\\', '/');
        return relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals(ScriptPackageWorkspaceMigrator.NexMudDirectoryName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetSourcePath(string profileId, string packageId, string relativePath) =>
        ScriptWorkspacePath.CombineInside(GetPackageRoot(profileId, packageId), relativePath);
}

internal static class ScriptWorkspacePath
{
    public static string NormalizeIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized is "." or ".." || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains('/') || normalized.Contains('\\'))
            throw new ArgumentException("Identifier contains invalid path characters.", parameterName);
        return normalized;
    }

    public static string NormalizeRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Script paths must remain inside their package.", nameof(value));
        return normalized;
    }

    public static string CombineInside(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string canonicalRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(canonicalRoot, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Script path escaped its package root.");
        return candidate;
    }
}
